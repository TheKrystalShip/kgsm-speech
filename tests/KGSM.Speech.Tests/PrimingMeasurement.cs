using System.Diagnostics;
using System.Text;

using KokoroSharp;
using KokoroSharp.Core;

using TheKrystalShip.KGSM.Speech;

using Whisper.net;
using Whisper.net.LibraryLoader;

using Xunit;
using Xunit.Abstractions;

namespace KGSM.Speech.Tests;

/// <summary>
/// Measures whether priming the recogniser actually changes what it produces for this host's names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not part of the suite.</b> It needs both models on disk and a GPU to be quick, so it is skipped
/// unless <c>KGSM_VOICE_MEASURE=1</c> — a test that only passes on one machine is not a test. It is
/// kept because the claim it checks ("priming fixes misheard server names") is otherwise an assertion
/// about a model nobody can verify by reading the code.
/// </para>
/// <para>
/// <b>The speech is synthesised, so this measures the decision and not the hearing.</b> Kokoro reads
/// each phrase and whisper transcribes it; what changes between the two runs is only the prior
/// context. That isolates exactly the thing being tested — which of several plausible spellings of the
/// same sound gets chosen — and says nothing about how the recogniser copes with a real room.
/// </para>
/// </remarks>
public class PrimingMeasurement(ITestOutputHelper output)
{
    private const string ModelPath = "/var/lib/kgsm-speech/models/ggml-small.en.bin";
    private const string SpeechPath = "/var/lib/kgsm-speech/models/kokoro.onnx";
    private const string VoicePath = "/opt/kgsm-speech/voices";

    private static readonly string[] Triggers = ["hey assistant"];

    private static readonly string[] Instances =
        ["minecraft", "necesse", "Ketchup", "projectzomboid", "romestead", "stationeers"];

    private static readonly string[] Blueprints =
        ["factorio", "palworld", "satisfactory", "dontstarvetogether", "abioticfactor", "enshrouded"];

    /// <summary>The phrases, and the word each one is really asking about.</summary>
    private static readonly (string Said, string Wanted)[] Phrases =
    [
        ("Hey assistant, is Ketchup running?", "ketchup"),
        ("Hey assistant, restart Ketchup please.", "ketchup"),
        ("Hey assistant, how is romestead doing?", "romestead"),
        ("Hey assistant, stop romestead.", "romestead"),
        ("Hey assistant, is necesse online?", "necesse"),
        ("Hey assistant, start stationeers.", "stationeers"),
        ("Hey assistant, is projectzomboid up?", "projectzomboid"),
        ("Hey assistant, is minecraft running?", "minecraft"),
    ];

    [Fact]
    public void PrimingIsMeasuredAgainstThisHostsNames()
    {
        if (Environment.GetEnvironmentVariable("KGSM_VOICE_MEASURE") != "1")
        {
            output.WriteLine("NOT RUN — set KGSM_VOICE_MEASURE=1. This loads both models and wants a GPU.");
            return;
        }

        if (!File.Exists(ModelPath) || !File.Exists(SpeechPath))
        {
            output.WriteLine($"NOT RUN — needs {ModelPath} and {SpeechPath}.");
            return;
        }

        KokoroVoiceManager.LoadVoicesFromPath(VoicePath);
        using KokoroWavSynthesizer synth = KokoroWavSynthesizer.LoadModel(SpeechPath);
        KokoroVoice voice = KokoroVoiceManager.GetVoice("af_heart");

        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu];
        using WhisperFactory factory = WhisperFactory.FromPath(ModelPath);

        // Composed by the thing that composes it in production, so what is measured is the context a
        // host actually sends rather than a hand-written approximation of one.
        string context = SpokenVocabulary.Compose(Triggers, Instances, Blueprints);
        output.WriteLine($"context ({context.Length} chars): {context}");
        output.WriteLine("");

        // Synthesised once and recognised twice, so the audio is identical between the two runs and
        // the only variable is the prior context.
        var audio = Phrases.Select(p => (p.Wanted, p.Said, Pcm: Speak(synth, voice, p.Said))).ToList();

        int bare = Recognise(factory, audio, string.Empty, "without priming");
        int primed = Recognise(factory, audio, context, "with priming");

        output.WriteLine("");
        output.WriteLine($"RESULT  without priming {bare}/{Phrases.Length}   with priming {primed}/{Phrases.Length}");

        Assert.True(
            primed >= bare,
            $"priming made recognition worse: {primed}/{Phrases.Length} against {bare}/{Phrases.Length}");
    }

    private int Recognise(
        WhisperFactory factory, List<(string Wanted, string Said, byte[] Pcm)> audio, string context, string label)
    {
        WhisperProcessorBuilder builder = factory.CreateBuilder().WithLanguage("en");
        using WhisperProcessor processor =
            (context.Length == 0 ? builder : builder.WithPrompt(context)).Build();

        output.WriteLine($"--- {label} ---");
        var hits = 0;

        foreach ((string wanted, string said, byte[] pcm) in audio)
        {
            var timer = Stopwatch.StartNew();
            var heard = new StringBuilder();

            using var wav = new MemoryStream(pcm);
            foreach (SegmentData segment in processor.ProcessAsync(wav).ToBlockingEnumerable())
                heard.Append(segment.Text);

            timer.Stop();
            string transcript = heard.ToString().Trim();
            bool hit = transcript.Contains(wanted, StringComparison.OrdinalIgnoreCase);
            if (hit) hits++;

            output.WriteLine(
                $"  {(hit ? "OK  " : "MISS")} [{wanted}] {transcript}  ({timer.ElapsedMilliseconds}ms)");
        }

        output.WriteLine($"  {hits}/{audio.Count}");
        return hits;
    }

    /// <summary>Synthesises a phrase and hands back the 16 kHz mono WAV whisper wants.</summary>
    private static byte[] Speak(KokoroWavSynthesizer synth, KokoroVoice voice, string text)
    {
        byte[] mono24k = synth.Synthesize(text, voice);
        byte[] mono16k = Resample24kTo16k(mono24k);

        var stream = new MemoryStream(44 + mono16k.Length);
        var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + mono16k.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(16000 * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(mono16k.Length);
        writer.Write(mono16k);

        return stream.ToArray();
    }

    /// <summary>24 kHz to 16 kHz: three samples in, two out, averaged rather than dropped.</summary>
    private static byte[] Resample24kTo16k(ReadOnlySpan<byte> mono24k)
    {
        int inSamples = mono24k.Length / 2;
        int outSamples = inSamples * 2 / 3;
        var outBytes = new byte[outSamples * 2];

        for (var i = 0; i < outSamples; i++)
        {
            double at = i * 1.5;
            var low = (int)at;
            int high = Math.Min(low + 1, inSamples - 1);
            double drift = at - low;

            short a = BitConverter.ToInt16(mono24k[(low * 2)..]);
            short b = BitConverter.ToInt16(mono24k[(high * 2)..]);
            var blended = (short)(a + ((b - a) * drift));

            BitConverter.TryWriteBytes(outBytes.AsSpan(i * 2), blended);
        }

        return outBytes;
    }
}
