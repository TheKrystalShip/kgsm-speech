using System.Collections.Concurrent;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TheKrystalShip.KGSM.Speech;

/// <summary>
/// Talks to the speech daemon. One per surface; safe to share across threads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Connecting is what starts it.</b> The daemon is socket-activated: systemd holds the socket and
/// launches it on the first connection, so a client never starts a process, never supervises one, and
/// never has to care whether it is running. A daemon that has idled out is replaced by the next
/// request, transparently.
/// </para>
/// <para>
/// <b>Speech is always an enhancement.</b> Every call here answers with an absence rather than an
/// exception when there is no daemon, no model, or no card: a host without kgsm-speech installed is
/// the ordinary case, and a surface that treats it as a failure is a surface that stops working when
/// an optional leaf is missing.
/// </para>
/// </remarks>
public sealed class SpeechClient : IDisposable, IAsyncDisposable
{
    /// <summary>Where the daemon's socket lives on a host that has it installed.</summary>
    public const string DefaultSocketPath = "/run/kgsm-speech/speech.sock";

    /// <summary>How long to wait for the models to load when asked to wake.</summary>
    /// <remarks>
    /// Generous on purpose: loading is around three seconds warm, and a first run after a deploy pages
    /// several hundred megabytes of CUDA libraries off the disk. What this guards is a daemon that has
    /// hung, not one that is slow.
    /// </remarks>
    private static readonly TimeSpan ReadyWithin = TimeSpan.FromSeconds(90);

    /// <summary>How long any one request may take before the caller is told it failed.</summary>
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(60);

    /// <summary>How long to leave a failed connection alone before trying again.</summary>
    /// <remarks>
    /// A host whose daemon cannot start fails every time, and the audio path asks on every utterance.
    /// This is what keeps that from becoming a connection attempt per sentence.
    /// </remarks>
    private static readonly TimeSpan BeforeRetrying = TimeSpan.FromSeconds(30);

    private readonly string _socketPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _connecting = new(1, 1);

    private Live? _live;
    private DateTimeOffset _failedAt = DateTimeOffset.MinValue;
    private uint _nextId;
    private bool _disposed;

    public SpeechClient(string? socketPath = null, ILogger? logger = null)
    {
        _socketPath = string.IsNullOrWhiteSpace(socketPath) ? DefaultSocketPath : socketPath;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Whether this host has a speech daemon to talk to at all.
    /// </summary>
    /// <remarks>
    /// The socket is bound by systemd whether or not the daemon is running, so its presence is the
    /// question "is kgsm-speech installed here" answered without starting anything. That matters
    /// because it is asked before every utterance and on every surface that draws a voice picker.
    /// </remarks>
    public bool IsProvisioned => File.Exists(_socketPath);

    /// <summary>Where this client is pointed.</summary>
    public string SocketPath => _socketPath;

    /// <summary>
    /// Asks the daemon to load its models, without making the caller wait.
    /// </summary>
    /// <remarks>
    /// For the moment a surface knows speech is about to be wanted — joining a voice channel, opening a
    /// chat — rather than the moment it is needed. Loading takes seconds and the first sentence after
    /// it is slower again; done here, that lands on nobody.
    /// </remarks>
    public void Wake()
    {
        if (!IsProvisioned || _disposed) return;

        _ = Task.Run(async () =>
        {
            try
            {
                (SpeechProtocol.Outcome outcome, byte[] payload) = await AskAsync(
                    SpeechProtocol.Kind.Wake, SpeechProtocol.Wake, ReadyWithin, CancellationToken.None)
                    .ConfigureAwait(false);

                if (outcome != SpeechProtocol.Outcome.Done) return;

                (_, bool canHear, bool canSpeak, string detail) = SpeechProtocol.ReadReady(payload);
                _logger.LogInformation(
                    "Speech: {Detail} — {Hearing}, {Speaking}", detail,
                    canHear ? "hearing" : "deaf", canSpeak ? "speaking" : "silent");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Speech: could not wake the speech daemon");
            }
        });
    }

    /// <summary>
    /// Reads one utterance — 16kHz mono signed 16-bit PCM — conditioned on <paramref name="vocabulary"/>.
    /// </summary>
    /// <param name="pcm16k">The utterance, 16kHz mono signed 16-bit — whisper's native input.</param>
    /// <param name="vocabulary">
    /// Names this host uses that a general recogniser will not know. Empty is fine and common.
    /// </param>
    /// <param name="ifIdle">
    /// Give up rather than queue when the recogniser is busy. For speculative work — a look ahead at an
    /// unfinished sentence must never delay a finished one.
    /// </param>
    /// <param name="ct">Abandons the request.</param>
    public async Task<(SpeechProtocol.Outcome Outcome, string Text)> TranscribeAsync(
        byte[] pcm16k, string vocabulary = "", bool ifIdle = false, CancellationToken ct = default)
    {
        (SpeechProtocol.Outcome outcome, byte[] payload) = await AskAsync(
            SpeechProtocol.Kind.Transcribe,
            id => SpeechProtocol.Transcribe(id, ifIdle, vocabulary, pcm16k),
            AnswerWithin, ct).ConfigureAwait(false);

        if (outcome != SpeechProtocol.Outcome.Done) return (outcome, string.Empty);

        (_, SpeechProtocol.Outcome said, string text) = SpeechProtocol.ReadTranscribed(payload);
        return (said, text);
    }

    /// <summary>
    /// Says <paramref name="text"/>, as 24kHz mono signed 16-bit PCM. Null when it could not be said.
    /// </summary>
    /// <param name="text">What to say. Plain text — markup is read out as the characters it is.</param>
    /// <param name="voice">
    /// Leave null to speak in this host's voice, which is what makes one person's assistant sound the
    /// same in Discord as it does in a browser. Naming one here overrides that for this sentence only.
    /// </param>
    /// <param name="ct">Abandons the request.</param>
    public async Task<byte[]?> SynthesizeAsync(
        string text, string? voice = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        (SpeechProtocol.Outcome outcome, byte[] payload) = await AskAsync(
            SpeechProtocol.Kind.Synthesize,
            id => SpeechProtocol.Synthesize(id, voice ?? string.Empty, text),
            AnswerWithin, ct).ConfigureAwait(false);

        if (outcome != SpeechProtocol.Outcome.Done) return null;

        (_, SpeechProtocol.Outcome said, byte[] audio) = SpeechProtocol.ReadSynthesized(payload);
        return said == SpeechProtocol.Outcome.Done && audio.Length > 0 ? audio : null;
    }

    /// <summary>
    /// Says <paramref name="text"/> wrapped in <paramref name="format"/>. Null when it could not be said.
    /// </summary>
    /// <remarks>
    /// For a caller that cannot play raw samples — a browser wants something
    /// <c>decodeAudioData</c> understands. The daemon synthesises once either way; a format is a
    /// wrapper applied on the way out.
    /// </remarks>
    /// <param name="text">What to say. Plain text — markup is read out as the characters it is.</param>
    /// <param name="format">The container to wrap the samples in.</param>
    /// <param name="voice">Null speaks in this host's voice, which is what a surface should ask for.</param>
    /// <param name="ct">Abandons the request.</param>
    public async Task<byte[]?> SynthesizeAsync(
        string text, SpeechProtocol.Format format, string? voice = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        (SpeechProtocol.Outcome outcome, byte[] payload) = await AskAsync(
            SpeechProtocol.Kind.SynthesizeAs,
            id => SpeechProtocol.SynthesizeAs(id, format, voice ?? string.Empty, text),
            AnswerWithin, ct).ConfigureAwait(false);

        if (outcome != SpeechProtocol.Outcome.Done) return null;

        (_, SpeechProtocol.Outcome said, byte[] audio) = SpeechProtocol.ReadSynthesized(payload);
        return said == SpeechProtocol.Outcome.Done && audio.Length > 0 ? audio : null;
    }

    /// <summary>
    /// The voices this host has, best-first, and the one it is speaking in. Empty when there is none.
    /// </summary>
    /// <remarks>
    /// Costs a connection and a directory read on the far side — the daemon answers this without
    /// loading a model, so drawing a voice picker never costs a gigabyte.
    /// </remarks>
    public Task<(string Speaking, IReadOnlyList<string> Voices)> VoicesAsync(CancellationToken ct = default) =>
        ListAsync(SpeechProtocol.Kind.Voices, SpeechProtocol.Voices, ct);

    /// <summary>
    /// What the daemon is doing right now. Null when this host has none, or it would not answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asking starts the daemon</b>, as every call here does — the socket is systemd's and
    /// connecting to it is the trigger. It loads no model, so the process it starts is a small one,
    /// but a caller that asks on a timer is a caller that keeps one running. Ask when somebody is
    /// looking.
    /// </para>
    /// <para>
    /// <b>It does not push the idle deadline out.</b> The daemon deliberately excludes this message
    /// from what counts as being used, so watching it never keeps it alive.
    /// </para>
    /// </remarks>
    public async Task<SpeechStatus?> StatusAsync(CancellationToken ct = default)
    {
        (SpeechProtocol.Outcome outcome, byte[] payload) = await AskAsync(
            SpeechProtocol.Kind.Status, SpeechProtocol.Status, AnswerWithin, ct).ConfigureAwait(false);

        if (outcome != SpeechProtocol.Outcome.Done) return null;

        (_, string report) = SpeechProtocol.ReadReported(payload);
        return report.Length == 0 ? null : SpeechStatus.Parse(report);
    }

    /// <summary>
    /// Speaks in <paramref name="voice"/> from the next sentence on, <b>on every surface this host has</b>.
    /// </summary>
    /// <remarks>
    /// The voice belongs to the host, not to the caller: this is the same setting the Control Panel
    /// shows, changed from somewhere else. A surface offering it has to say so — and it lasts until the
    /// daemon restarts, because the durable value is the leaf's own configuration.
    /// </remarks>
    public async Task<(bool Changed, string Speaking)> SpeakAsAsync(
        string voice, CancellationToken ct = default)
    {
        (string speaking, IReadOnlyList<string> _) = await ListAsync(
            SpeechProtocol.Kind.SpeakAs, id => SpeechProtocol.SpeakAs(id, voice), ct).ConfigureAwait(false);

        return (speaking.Equals(voice, StringComparison.OrdinalIgnoreCase), speaking);
    }

    private async Task<(string Speaking, IReadOnlyList<string> Voices)> ListAsync(
        SpeechProtocol.Kind kind, Func<uint, byte[]> compose, CancellationToken ct)
    {
        (SpeechProtocol.Outcome outcome, byte[] payload) =
            await AskAsync(kind, compose, AnswerWithin, ct).ConfigureAwait(false);

        if (outcome != SpeechProtocol.Outcome.Done) return (string.Empty, []);

        (_, SpeechProtocol.Outcome said, string speaking, IReadOnlyList<string> voices) =
            SpeechProtocol.ReadVoiceList(payload);

        return said == SpeechProtocol.Outcome.Done ? (speaking, voices) : (string.Empty, []);
    }

    /// <summary>
    /// Sends one request and waits for the answer with its id on it.
    /// </summary>
    /// <returns>
    /// <see cref="SpeechProtocol.Outcome.Unavailable"/> when there was no daemon to ask, which every
    /// caller treats the way it treats a host with no models at all.
    /// </returns>
    private async Task<(SpeechProtocol.Outcome, byte[])> AskAsync(
        SpeechProtocol.Kind kind, Func<uint, byte[]> compose, TimeSpan patience, CancellationToken ct)
    {
        Live? live;
        try
        {
            live = await ConnectedAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Speech: no daemon to ask");
            return (SpeechProtocol.Outcome.Unavailable, []);
        }

        if (live is null) return (SpeechProtocol.Outcome.Unavailable, []);

        uint id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        live.Pending[id] = answer;

        try
        {
            await live.SendAsync(kind, compose(id), ct).ConfigureAwait(false);

            using var late = CancellationTokenSource.CreateLinkedTokenSource(ct);
            late.CancelAfter(patience);

            return (SpeechProtocol.Outcome.Done, await answer.Task.WaitAsync(late.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Speech: the daemon did not answer a {Kind} request in time", kind);
            return (SpeechProtocol.Outcome.Failed, []);
        }
        catch (OperationCanceledException)
        {
            return (SpeechProtocol.Outcome.Failed, []);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Speech: could not put a {Kind} request to the daemon", kind);
            return (SpeechProtocol.Outcome.Failed, []);
        }
        finally
        {
            live.Pending.TryRemove(id, out _);
        }
    }

    /// <summary>The live connection, opening one if there is none. Null when this host has no daemon.</summary>
    private async Task<Live?> ConnectedAsync(CancellationToken ct)
    {
        Live? live = _live;
        if (live is { Healthy: true }) return live;

        if (!IsProvisioned || _disposed) return null;

        await _connecting.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            live = _live;
            if (live is { Healthy: true }) return live;

            if (DateTimeOffset.UtcNow - _failedAt < BeforeRetrying) return null;

            _live = null;
            live?.Dispose();

            try
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), ct).ConfigureAwait(false);

                var opened = new Live(socket, _logger);
                opened.Listen();

                _live = opened;
                _failedAt = DateTimeOffset.MinValue;
                return opened;
            }
            catch (Exception ex)
            {
                _failedAt = DateTimeOffset.UtcNow;
                _logger.LogWarning(
                    ex, "Speech: could not reach the speech daemon on {Socket} — carrying on without it",
                    _socketPath);

                return null;
            }
        }
        finally
        {
            _connecting.Release();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        Interlocked.Exchange(ref _live, null)?.Dispose();
        _connecting.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>One open connection and what is outstanding on it.</summary>
    private sealed class Live(Socket socket, ILogger logger) : IDisposable
    {
        private readonly NetworkStream _stream = new(socket, ownsSocket: false);
        private readonly SemaphoreSlim _writing = new(1, 1);
        private readonly CancellationTokenSource _reading = new();
        private volatile bool _gone;

        public ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> Pending { get; } = new();

        public bool Healthy => !_gone && socket.Connected;

        /// <summary>Reads answers until the daemon stops sending them.</summary>
        public void Listen() => _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    (SpeechProtocol.Kind Kind, byte[] Payload)? frame =
                        await SpeechProtocol.ReadAsync(_stream, _reading.Token).ConfigureAwait(false);

                    if (frame is null) break;

                    // Every answer carries its request's id in the same place, whatever it is an
                    // answer to — so this needs to know nothing about the kinds to route one.
                    uint id = SpeechProtocol.IdOf(frame.Value.Payload);
                    if (Pending.TryRemove(id, out TaskCompletionSource<byte[]>? waiting))
                        waiting.TrySetResult(frame.Value.Payload);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Speech: lost the connection to the speech daemon");
            }
            finally
            {
                _gone = true;

                // Everything outstanding is now never going to be answered. Failing them here is what
                // turns a daemon that went away into one slow answer rather than a surface where
                // nothing ever comes back.
                foreach (uint id in Pending.Keys)
                    if (Pending.TryRemove(id, out TaskCompletionSource<byte[]>? waiting))
                        waiting.TrySetCanceled();
            }
        });

        public async Task SendAsync(SpeechProtocol.Kind kind, byte[] payload, CancellationToken ct)
        {
            await _writing.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await SpeechProtocol.WriteAsync(_stream, kind, payload, ct).ConfigureAwait(false);
            }
            finally
            {
                _writing.Release();
            }
        }

        public void Dispose()
        {
            _gone = true;
            _reading.Cancel();
            _stream.Dispose();
            socket.Dispose();
            _reading.Dispose();
            _writing.Dispose();
        }
    }
}
