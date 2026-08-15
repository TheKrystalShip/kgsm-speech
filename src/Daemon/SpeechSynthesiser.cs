using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

using KokoroSharp;
using KokoroSharp.Core;

using Microsoft.Extensions.Logging;

namespace TheKrystalShip.KGSM.Speech.Daemon;

/// <summary>
/// Kokoro, loaded. Words in, audio out.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the reason this daemon is a separate process.</b> Measured
/// on this host: loading the model costs 400MB, and the <em>first sentence synthesised</em> costs
/// another 919MB as cuDNN pages in its kernels. Disposing the session returns the video memory and
/// about a quarter of the rest — the remainder belongs to the CUDA runtime until the process ends.
/// </para>
/// <para>
/// <b>What comes out is 24kHz mono</b>, exactly as Kokoro produces it. Converting to whatever a
/// surface plays — 48kHz stereo for a Discord connection — happens on that surface's own side,
/// because doing it here would put four times as many bytes on the wire.
/// </para>
/// <para>
/// <b>Cost scales with how much is said</b>, which is the opposite of recognition and worth knowing:
/// recognition pays a fixed price per utterance whatever its length, and this pays per character.
/// </para>
/// </remarks>
internal sealed class SpeechSynthesiser : IDisposable
{
    /// <summary>
    /// Teaches the ONNX Runtime's P/Invokes where their library actually is on this host.
    /// </summary>
    /// <remarks>
    /// ONNX imports the literal name <c>onnxruntime.dll</c> on every platform, and .NET's default
    /// probing appends rather than replaces: it tries <c>onnxruntime.dll.so</c> and
    /// <c>libonnxruntime.dll.so</c> and never <c>libonnxruntime.so</c>, which is what is actually
    /// shipped. An ordinary publish papers over that with the RID asset mapping out of deps.json; a
    /// single-file one does not, and the symptom is a daemon that reports having no synthesiser while
    /// the library sits beside it.
    /// </remarks>
    static SpeechSynthesiser()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(Microsoft.ML.OnnxRuntime.SessionOptions).Assembly,
            (name, _, _) =>
            {
                if (!name.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;

                string library = $"lib{Path.GetFileNameWithoutExtension(name)}.so";
                string beside = Path.Combine(AppContext.BaseDirectory, library);

                // Beside the binary first, then however the host resolves it — a packaged install may
                // well have it on the loader path instead.
                if (NativeLibrary.TryLoad(beside, out IntPtr handle)) return handle;
                return NativeLibrary.TryLoad(library, out handle) ? handle : IntPtr.Zero;
            });
    }

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly KokoroWavSynthesizer? _synth;

    /// <summary>
    /// The voices read off disk so far, by name.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Kept so a voice tried a second time is free, and never populated ahead of use.</b>
    /// <see cref="KokoroVoiceManager"/> is deliberately untouched: its accessor loads <em>every</em>
    /// voice in the directory the first time it is asked for one — 157 arrays including every other
    /// language's, measured at 78MB of float32 resident to speak in one of them, all of it over the
    /// large-object threshold and so never compacted away. <see cref="KokoroVoice.FromPath"/> reads
    /// exactly one, at about half a megabyte.
    /// </remarks>
    private readonly ConcurrentDictionary<string, KokoroVoice> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly LaneTally _tally = new();
    private readonly string _modelPath;

    private bool _disposed;

    public SpeechSynthesiser(string modelPath, bool useGpu, string warmVoice, ILogger logger)
    {
        _logger = logger;
        _modelPath = modelPath ?? string.Empty;

        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            Detail = $"no model at '{modelPath}'";
            _logger.LogError(
                "Speech: no synthesis model at '{Model}' — surfaces will answer in text and not out "
                + "loud. Run deploy/setup.sh to fetch it, or set Speech:SpeechModelPath.",
                modelPath);
            return;
        }

        try
        {
            var timer = Stopwatch.StartNew();
            (_synth, string runtime) = Load(modelPath, useGpu);
            timer.Stop();

            Runtime = runtime;
            Detail = $"{Path.GetFileName(modelPath)} on the {runtime.ToUpperInvariant()}";

            _logger.LogInformation(
                "Speech: synthesis ready — on the {Runtime}, loaded in {Elapsed}ms",
                runtime.ToUpperInvariant(), timer.ElapsedMilliseconds);

            Warm(warmVoice);
        }
        catch (Exception ex)
        {
            Detail = ex.Message;
            _logger.LogError(ex, "Speech: could not load synthesis ({Model}) — answers stay in text", modelPath);
            _synth = null;
        }
    }

    public bool IsAvailable => _synth is not null;

    /// <summary>
    /// Which runtime the model opened on — <c>gpu</c> or <c>cpu</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Not the setting that asked for one.</b> The CUDA path needs cuDNN, which plenty of hosts do
    /// not have, and falling back to the processor is eight times slower and otherwise invisible.
    /// </remarks>
    public string Runtime { get; private set; } = "unknown";

    /// <summary>What this lane is doing, or the reason it can do nothing.</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>How synthesis has been getting on since this process started.</summary>
    public SpeechLane Reported() => _tally.Reported(
        IsAvailable, Detail, Path.GetFileName(_modelPath), Bytes(_modelPath), Runtime);

    /// <summary>The model file's size, or zero when there is nothing there to measure.</summary>
    private static long Bytes(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Opens the model on the card when asked, and on the processor when the card cannot be had.
    /// </summary>
    /// <remarks>
    /// The CUDA path needs cuDNN present, which is a heavier dependency than the cuBLAS recognition
    /// uses and is genuinely absent on plenty of hosts. Failing over rather than refusing is what keeps
    /// one binary correct on a node with a card and on one without — and the fallback is reported,
    /// because it is an eightfold difference and otherwise invisible.
    /// </remarks>
    private (KokoroWavSynthesizer, string) Load(string model, bool useGpu)
    {
        if (useGpu)
        {
            try
            {
                var cuda = Microsoft.ML.OnnxRuntime.SessionOptions.MakeSessionOptionWithCudaProvider(0);
                return (KokoroWavSynthesizer.LoadModel(model, cuda), "gpu");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Speech: synthesis could not use the GPU ({Reason}) — falling back to the "
                    + "processor, which is around eight times slower", ex.Message);
            }
        }

        return (KokoroWavSynthesizer.LoadModel(model), "cpu");
    }

    /// <summary>
    /// Synthesises one throwaway phrase so the first real answer does not pay for the last of the
    /// loading.
    /// </summary>
    /// <remarks>
    /// Measured in a live channel: the first answer of a session took 1441ms to synthesise and every
    /// one after it took 340-600ms. Loading the model is not the whole cost of the first call — the
    /// provider allocates and the graph is built on first inference — and that difference lands
    /// entirely on the first person to ask a question. It is why a surface says speech is about to be
    /// wanted — joining a voice channel, opening a chat — rather than waiting to need it.
    /// </remarks>
    private void Warm(string voice) => _ = Task.Run(async () =>
    {
        try
        {
            var timer = Stopwatch.StartNew();
            await SayAsync("Ready.", voice);
            timer.Stop();
            _logger.LogInformation("Speech: synthesis warmed in {Elapsed}ms", timer.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // Nothing depends on this: a failure here costs the first answer its head start and no
            // more, and the real call will report its own problem properly.
            _logger.LogDebug(ex, "Speech: could not warm synthesis");
        }
    });

    /// <summary>
    /// Says <paramref name="text"/> in <paramref name="voiceName"/>, as 24kHz mono signed 16-bit PCM.
    /// </summary>
    /// <remarks>
    /// The voice travels with every request rather than being set: it is a small array handed to the
    /// synthesiser per sentence, so there is no session state to keep in step and swapping voices costs
    /// a dictionary lookup.
    /// </remarks>
    public async Task<(SpeechProtocol.Outcome Outcome, byte[] Audio)> SayAsync(
        string text, string voiceName, CancellationToken ct = default)
    {
        if (_synth is null || string.IsNullOrWhiteSpace(text))
            return (SpeechProtocol.Outcome.Unavailable, []);

        KokoroVoice? voice = Read(voiceName);
        if (voice is null)
        {
            // Named, with every alternative named beside it: the one thing somebody in this position
            // needs is the spelling of a voice that exists.
            _logger.LogError(
                "Speech: there is no voice called '{Voice}' in {Directory}. Installed: {Installed}",
                voiceName, InstalledVoices.Directory, string.Join(", ", InstalledVoices.All()));

            return (SpeechProtocol.Outcome.Unavailable, []);
        }

        _tally.Queued();
        try
        {
            await _one.WaitAsync(ct);
        }
        finally
        {
            _tally.Dequeued();
        }

        var timer = Stopwatch.StartNew();
        _tally.Started();
        try
        {
            // Synthesis is a blocking ONNX call, and this daemon's only other job is hearing: reading the
            // next sentence is happening on another thread and must not wait behind it.
            byte[] mono24k = await Task.Run(() => _synth.Synthesize(text, voice), ct);

            timer.Stop();
            double seconds = mono24k.Length / (24000.0 * 2);

            _tally.Finished(SpeechProtocol.Outcome.Done, (int)timer.ElapsedMilliseconds, seconds, text.Length);
            _logger.LogDebug(
                "Speech: synthesised {Characters} characters into {Seconds:F1}s of audio in {Elapsed}ms",
                text.Length, seconds, timer.ElapsedMilliseconds);

            return (SpeechProtocol.Outcome.Done, mono24k);
        }
        catch (OperationCanceledException)
        {
            _tally.Finished(SpeechProtocol.Outcome.Failed, (int)timer.ElapsedMilliseconds);
            return (SpeechProtocol.Outcome.Failed, []);
        }
        catch (Exception ex)
        {
            // Failing to say an answer out loud is not failing to answer: the text is already in the
            // channel, and the voice connection stays up for the next question.
            _tally.Finished(SpeechProtocol.Outcome.Failed, (int)timer.ElapsedMilliseconds);
            _logger.LogWarning(ex, "Speech: could not synthesise a reply — it stands in text only");
            return (SpeechProtocol.Outcome.Failed, []);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// One voice, read from disk the first time it is asked for. Null when this host does not have it.
    /// </summary>
    private KokoroVoice? Read(string name)
    {
        string wanted = (name ?? string.Empty).Trim();
        if (wanted.Length == 0) return null;

        if (_loaded.TryGetValue(wanted, out KokoroVoice? already)) return already;

        string? file = InstalledVoices.Find(wanted);
        if (file is null) return null;

        try
        {
            return _loaded.GetOrAdd(wanted, KokoroVoice.FromPath(file));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Speech: could not read the voice '{Voice}'", wanted);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _synth?.Dispose();
        _one.Dispose();
    }
}
