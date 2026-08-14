using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Whisper.net;
using Whisper.net.LibraryLoader;

namespace TheKrystalShip.KGSM.Speech.Daemon;

/// <summary>
/// Whisper, loaded. Audio in, words out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Loading this costs about 380MB that never comes back.</b> The weights go to the card, but the
/// CUDA runtime behind them is resident for the life of the process whatever is disposed — which is
/// the whole reason this lives in a daemon rather than in the surfaces that use it. A process can
/// give the memory back by ending, and nothing else can.
/// </para>
/// <para>
/// <b>It knows nothing about Discord, this host's servers, or what a trigger phrase is.</b> The names
/// to expect arrive with each utterance and the surface decides what the words mean. A recogniser
/// holding opinions about either would be a second place to fix them.
/// </para>
/// <para>
/// <b>One pass at a time.</b> A whisper processor is not safe to use from two threads, and four
/// people in a channel are four streams that can finish talking at the same moment.
/// </para>
/// </remarks>
internal sealed class SpeechRecogniser : IDisposable
{
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly WhisperFactory? _factory;

    private WhisperProcessor? _processor;
    private string _vocabulary = string.Empty;
    private bool _disposed;

    public SpeechRecogniser(string modelPath, bool useGpu, ILogger logger)
    {
        _logger = logger;

        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            _logger.LogError(
                "Speech: no recognition model at '{Model}' — nothing said in a voice channel will be "
                + "understood. Download one (ggml-small.en.bin) and set Speech:ModelPath.",
                modelPath);
            return;
        }

        // Ask for the GPU and accept the processor. Whisper.net picks the first runtime that
        // initialises, so a host whose driver is mid-upgrade, or which has no NVIDIA card at all, gets
        // a slower recogniser rather than no voice surface. Which one it settled on is logged, because
        // the difference is a factor of forty and is otherwise invisible.
        RuntimeOptions.RuntimeLibraryOrder = useGpu
            ? [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu]
            : [RuntimeLibrary.Cpu];

        try
        {
            var timer = Stopwatch.StartNew();
            _factory = WhisperFactory.FromPath(modelPath);
            _processor = Build(string.Empty);
            timer.Stop();

            _logger.LogInformation(
                "Speech: recognition ready — {Model}, {Runtime} preferred, loaded in {Elapsed}ms",
                Path.GetFileName(modelPath), useGpu ? "GPU" : "CPU", timer.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Speech: could not load the recognition model at '{Model}'", modelPath);
            _factory = null;
            _processor = null;
        }
    }

    public bool IsAvailable => _processor is not null;

    /// <summary>
    /// Reads one utterance — 16 kHz mono signed 16-bit PCM — conditioned on <paramref name="vocabulary"/>.
    /// </summary>
    /// <param name="pcm">The utterance, 16kHz mono signed 16-bit.</param>
    /// <param name="vocabulary">Names this host uses that a general recogniser would not know.</param>
    /// <param name="ifIdle">
    /// Give up rather than queue when a pass is already running. For work that is worth doing and never
    /// worth waiting for: anything speculative that queued would sit in front of somebody's finished
    /// sentence, spending the wait it was added to shorten.
    /// </param>
    /// <param name="ct">Abandons the pass.</param>
    public async Task<(SpeechProtocol.Outcome Outcome, string Text)> ReadAsync(
        byte[] pcm, string vocabulary, bool ifIdle, CancellationToken ct = default)
    {
        if (_processor is null) return (SpeechProtocol.Outcome.Unavailable, string.Empty);

        if (ifIdle)
        {
            if (!_one.Wait(0, CancellationToken.None)) return (SpeechProtocol.Outcome.Busy, string.Empty);
        }
        else
        {
            await _one.WaitAsync(ct);
        }

        try
        {
            Prime(vocabulary);
            if (_processor is null) return (SpeechProtocol.Outcome.Unavailable, string.Empty);

            var timer = Stopwatch.StartNew();
            using var wav = WavStream(pcm);

            var text = new System.Text.StringBuilder();
            await foreach (SegmentData segment in _processor.ProcessAsync(wav, ct))
                text.Append(segment.Text);

            timer.Stop();
            _logger.LogDebug(
                "Speech: read {Seconds:F1}s of audio in {Elapsed}ms",
                pcm.Length / (16000.0 * 2), timer.ElapsedMilliseconds);

            return (SpeechProtocol.Outcome.Done, text.ToString());
        }
        catch (OperationCanceledException)
        {
            return (SpeechProtocol.Outcome.Failed, string.Empty);
        }
        catch (Exception ex)
        {
            // One utterance failing to recognise is not the daemon failing. The person is still
            // talking and the next thing they say may well come through.
            _logger.LogWarning(ex, "Speech: could not recognise an utterance");
            return (SpeechProtocol.Outcome.Failed, string.Empty);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Rebuilds the processor when the names to expect have moved.
    /// </summary>
    /// <remarks>
    /// Called with the lock held and on the recognition path, so it does as little as it can: a surface
    /// sends the same string with every utterance and this compares it, so installing a server costs
    /// one rebuild and the hundred utterances either side of it cost nothing.
    /// </remarks>
    private void Prime(string vocabulary)
    {
        if (_factory is null || vocabulary == _vocabulary) return;

        try
        {
            WhisperProcessor rebuilt = Build(vocabulary);
            _processor?.Dispose();
            _processor = rebuilt;
            _vocabulary = vocabulary;

            _logger.LogInformation(
                "Speech: primed with {Characters} characters of this host's names", vocabulary.Length);
        }
        catch (Exception ex)
        {
            // The processor that was working is still in place, so this costs the priming and not the
            // ability to recognise anything.
            _logger.LogWarning(ex, "Speech: could not prime the recogniser — carrying on without it");
        }
    }

    private WhisperProcessor Build(string vocabulary)
    {
        WhisperProcessorBuilder builder = _factory!.CreateBuilder().WithLanguage("en");
        return (vocabulary.Length == 0 ? builder : builder.WithPrompt(vocabulary)).Build();
    }

    /// <summary>
    /// Wraps raw PCM in the WAV header whisper expects.
    /// </summary>
    /// <remarks>
    /// The audio is already 16 kHz mono signed 16-bit, which is whisper's native input, so this adds a
    /// 44-byte header and copies nothing else — there is no conversion here and there must not be one,
    /// because a resample at this point would be the second one applied to the same audio.
    /// </remarks>
    private static MemoryStream WavStream(byte[] pcm)
    {
        const int SampleRate = 16000;
        const short Channels = 1;
        const short BitsPerSample = 16;

        var stream = new MemoryStream(44 + pcm.Length);
        var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                                              // PCM header length
        writer.Write((short)1);                                        // uncompressed
        writer.Write(Channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * Channels * BitsPerSample / 8);       // byte rate
        writer.Write((short)(Channels * BitsPerSample / 8));           // block align
        writer.Write(BitsPerSample);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);

        stream.Position = 0;
        return stream;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _processor?.Dispose();
        _factory?.Dispose();
        _one.Dispose();
    }
}
