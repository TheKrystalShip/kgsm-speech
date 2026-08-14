using System.Diagnostics;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;

namespace TheKrystalShip.KGSM.Speech.Daemon;

/// <summary>
/// Serves speech to every surface on this host.
/// </summary>
/// <remarks>
/// <para>
/// <b>The models load on demand, not at startup.</b> Starting costs nothing, so systemd can activate
/// this daemon for a question as small as "which voices are there" without a gigabyte being read off
/// the disk. What loads them is a surface saying it is about to need them — <see
/// cref="SpeechProtocol.Kind.Wake"/>, sent when a bot joins a voice channel — or the first thing
/// actually asked.
/// </para>
/// <para>
/// <b>Many surfaces, one set of models.</b> Recognition and synthesis each run one at a time and each
/// has its own lock, so a long answer being spoken never delays a sentence being read. What it does
/// delay is the <em>next</em> answer, and that is the queue this daemon deliberately is: one card,
/// one voice, everyone in line.
/// </para>
/// <para>
/// <b>Idling out is not a failure.</b> A daemon that exits has given the host back 1.6GB and a
/// gigabyte of video memory, and systemd still holds the socket — the next request brings it back.
/// </para>
/// </remarks>
internal sealed class SpeechServer(Socket listener, SpeechOptions options, ILogger logger)
{
    private readonly SemaphoreSlim _loading = new(1, 1);

    private SpeechRecogniser? _ears;
    private SpeechSynthesiser? _mouth;
    private string _voice = options.Voice;
    private DateTimeOffset _lastAsked = DateTimeOffset.UtcNow;

    /// <summary>Accepts surfaces until the host stops this, or until nobody has asked for long enough.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var idle = new CancellationTokenSource();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);

        Task watching = IdleAsync(idle, stopping.Token);

        logger.LogInformation(
            "Speech: serving on {Socket} — models load on demand, {Idle}",
            options.SocketPath,
            options.IdleMinutes > 0
                ? $"unloading after {options.IdleMinutes} idle minutes"
                : "staying loaded once they are");

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                Socket surface = await listener.AcceptAsync(stopping.Token);
                _ = ServeAsync(surface, stopping.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Speech: stopped accepting connections");
        }

        await watching;

        _ears?.Dispose();
        _mouth?.Dispose();
    }

    /// <summary>Reads one surface's requests until it goes away.</summary>
    /// <remarks>
    /// A connection ending is the ordinary case — a bot restarting, a deploy, this daemon idling out
    /// from under it — and costs nothing here: everything a surface needs travels with each request,
    /// so there is no session to lose.
    /// </remarks>
    private async Task ServeAsync(Socket surface, CancellationToken ct)
    {
        await using var stream = new NetworkStream(surface, ownsSocket: true);
        var writing = new SemaphoreSlim(1, 1);

        try
        {
            while (true)
            {
                (SpeechProtocol.Kind Kind, byte[] Payload)? frame =
                    await SpeechProtocol.ReadAsync(stream, ct);

                if (frame is null) break;

                // Off the read loop: recognition and synthesis are both blocking work of hundreds of
                // milliseconds, and answering here would stop this surface being heard until it
                // finished. Two surfaces asking at once are two of these.
                _ = Task.Run(() => AnswerAsync(stream, writing, frame.Value.Kind, frame.Value.Payload, ct), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Speech: a surface's connection failed");
        }
        finally
        {
            writing.Dispose();
        }
    }

    private async Task AnswerAsync(
        Stream stream, SemaphoreSlim writing, SpeechProtocol.Kind kind, byte[] payload, CancellationToken ct)
    {
        try
        {
            _lastAsked = DateTimeOffset.UtcNow;

            switch (kind)
            {
                case SpeechProtocol.Kind.Wake:
                {
                    uint id = SpeechProtocol.IdOf(payload);
                    await LoadedAsync(ct);

                    await SendAsync(stream, writing, SpeechProtocol.Kind.Ready,
                        SpeechProtocol.Ready(id, _ears?.IsAvailable == true, _mouth?.IsAvailable == true,
                            Describe()), ct);
                    break;
                }

                case SpeechProtocol.Kind.Transcribe:
                {
                    (uint id, bool ifIdle, string vocabulary, byte[] audio) =
                        SpeechProtocol.ReadTranscribe(payload);

                    await LoadedAsync(ct);

                    (SpeechProtocol.Outcome outcome, string text) = _ears is null
                        ? (SpeechProtocol.Outcome.Unavailable, string.Empty)
                        : await _ears.ReadAsync(audio, vocabulary, ifIdle, ct);

                    await SendAsync(stream, writing, SpeechProtocol.Kind.Transcribed,
                        SpeechProtocol.Transcribed(id, outcome, text), ct);
                    break;
                }

                case SpeechProtocol.Kind.Synthesize:
                {
                    (uint id, string named, string text) = SpeechProtocol.ReadSynthesize(payload);
                    await SaidAsync(stream, writing, id, SpeechProtocol.Format.Pcm, named, text, ct);
                    break;
                }

                case SpeechProtocol.Kind.SynthesizeAs:
                {
                    (uint id, SpeechProtocol.Format format, string named, string text) =
                        SpeechProtocol.ReadSynthesizeAs(payload);

                    await SaidAsync(stream, writing, id, format, named, text, ct);
                    break;
                }

                case SpeechProtocol.Kind.Voices:
                {
                    // Answered from the directory, so a surface drawing a voice picker never loads a
                    // model to fill it in.
                    uint id = SpeechProtocol.IdOf(payload);

                    await SendAsync(stream, writing, SpeechProtocol.Kind.VoiceList,
                        SpeechProtocol.VoiceList(
                            id, SpeechProtocol.Outcome.Done, _voice, InstalledVoices.Offered()), ct);
                    break;
                }

                case SpeechProtocol.Kind.SpeakAs:
                {
                    (uint id, string wanted) = SpeechProtocol.ReadSpeakAs(payload);
                    SpeechProtocol.Outcome outcome = SpeakAs(wanted);

                    await SendAsync(stream, writing, SpeechProtocol.Kind.VoiceList,
                        SpeechProtocol.VoiceList(id, outcome, _voice, InstalledVoices.Offered()), ct);
                    break;
                }

                default:
                    // A message this daemon does not know is a surface built against a newer contract.
                    // Dropping the request beats ending the connection it arrived on.
                    logger.LogWarning("Speech: ignored a {Kind} message", kind);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // The request is lost and the caller's own timeout reports it. Ending a connection over one
            // bad frame would take a whole voice session with it.
            logger.LogWarning(ex, "Speech: could not answer a {Kind} request", kind);
        }
    }

    /// <summary>
    /// Synthesises one request and answers it, whichever message asked.
    /// </summary>
    /// <remarks>
    /// The format is applied on the way out, so the two messages differ in their wrapper and in
    /// nothing else — one synthesis, one queue, one answer shape.
    /// </remarks>
    private async Task SaidAsync(
        Stream stream, SemaphoreSlim writing, uint id, SpeechProtocol.Format format,
        string named, string text, CancellationToken ct)
    {
        await LoadedAsync(ct);

        // An empty name is a surface asking for this host's voice, which is what they all should do —
        // the point of the setting is that they agree without coordinating.
        string voice = string.IsNullOrWhiteSpace(named) ? _voice : named;

        (SpeechProtocol.Outcome outcome, byte[] audio) = _mouth is null
            ? (SpeechProtocol.Outcome.Unavailable, [])
            : await _mouth.SayAsync(text, voice, ct);

        if (outcome == SpeechProtocol.Outcome.Done && format == SpeechProtocol.Format.Wav)
            audio = Wave.Mono16(audio);

        await SendAsync(stream, writing, SpeechProtocol.Kind.Synthesized,
            SpeechProtocol.Synthesized(id, outcome, audio), ct);
    }

    /// <summary>
    /// Changes the voice this host speaks in, for every surface, until this daemon restarts.
    /// </summary>
    /// <remarks>
    /// Deliberately not written back to the settings file: the durable value is the leaf's own
    /// configuration, and a daemon writing its own config is a second source of truth that wins
    /// silently. This is for hearing a voice before choosing it.
    /// </remarks>
    private SpeechProtocol.Outcome SpeakAs(string wanted)
    {
        string? file = InstalledVoices.Find(wanted);
        if (file is null)
        {
            logger.LogWarning(
                "Speech: asked to speak as '{Voice}', which this host does not have. Installed: {Installed}",
                wanted, string.Join(", ", InstalledVoices.All()));

            return SpeechProtocol.Outcome.Unavailable;
        }

        string named = Path.GetFileNameWithoutExtension(file);
        if (!named.Equals(_voice, StringComparison.OrdinalIgnoreCase))
        {
            _voice = named;
            logger.LogInformation("Speech: now speaking as {Voice}", named);
        }

        return SpeechProtocol.Outcome.Done;
    }

    /// <summary>Loads the models, once, however many surfaces arrive at the same moment.</summary>
    private async Task LoadedAsync(CancellationToken ct)
    {
        if (_ears is not null) return;

        await _loading.WaitAsync(ct);
        try
        {
            if (_ears is not null) return;

            var timer = Stopwatch.StartNew();
            var ears = new SpeechRecogniser(options.ModelPath, options.UseGpu, logger);
            SpeechSynthesiser mouth = new(options.SpeechModelPath, options.SpeakUseGpu, _voice, logger);
            timer.Stop();

            _mouth = mouth;
            _ears = ears;

            logger.LogInformation(
                "Speech: loaded in {Elapsed}ms — {Hearing}, {Speaking}", timer.ElapsedMilliseconds,
                ears.IsAvailable ? "hearing" : "deaf", mouth.IsAvailable ? "speaking" : "silent");
        }
        finally
        {
            _loading.Release();
        }
    }

    /// <summary>Ends the daemon once nothing has been asked of it for long enough.</summary>
    /// <remarks>
    /// Open connections are deliberately not consulted. A surface holds one for as long as it is
    /// running, so waiting for them all to go would mean never exiting on a host whose bot is always
    /// up — and the memory would never come back. What matters is whether anybody is <em>using</em>
    /// this, which is what the last request says.
    /// </remarks>
    private async Task IdleAsync(CancellationTokenSource idle, CancellationToken ct)
    {
        if (options.IdleMinutes <= 0) return;

        var window = TimeSpan.FromMinutes(options.IdleMinutes);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);

                if (DateTimeOffset.UtcNow - _lastAsked < window) continue;

                logger.LogInformation(
                    "Speech: nothing asked for {Minutes} minutes — unloading and exiting. "
                    + "The next request starts this again.", options.IdleMinutes);

                await idle.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private string Describe() =>
        $"{Path.GetFileName(options.ModelPath)} on the {(options.UseGpu ? "GPU" : "CPU")}, "
        + $"speaking as {_voice} on the {(options.SpeakUseGpu ? "GPU" : "CPU")}";

    /// <summary>
    /// Writes one message, one at a time.
    /// </summary>
    /// <remarks>
    /// Answers are written from whichever thread finished the work, so without this a recognition
    /// finishing mid-write of a synthesised answer would interleave its bytes into it and desynchronise
    /// that surface's stream for good.
    /// </remarks>
    private static async Task SendAsync(
        Stream stream, SemaphoreSlim writing, SpeechProtocol.Kind kind, byte[] payload, CancellationToken ct)
    {
        await writing.WaitAsync(ct);
        try
        {
            await SpeechProtocol.WriteAsync(stream, kind, payload, ct);
        }
        finally
        {
            writing.Release();
        }
    }
}
