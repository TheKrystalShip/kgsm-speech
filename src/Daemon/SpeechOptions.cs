using TheKrystalShip.KGSM.ComponentConfig;
using TheKrystalShip.Speech.Engine;

namespace TheKrystalShip.KGSM.Speech.Daemon;

/// <summary>
/// This host's speech configuration — the models, the card, and the voice it answers in.
/// </summary>
/// <remarks>
/// <b>The voice belongs here, not to a surface.</b> Every surface that speaks asks this daemon, and
/// none of them names a voice unless it deliberately wants a different one — so a person hears the
/// same assistant in Discord as in a browser, and changing that is one setting in one place.
/// </remarks>
[ConfigSection(Section)]
public class SpeechOptions
{
    public const string Section = "Speech";

    /// <summary>
    /// The whisper model used to recognise speech.
    /// </summary>
    /// <remarks>
    /// Outside the install prefix, because the deploy syncs that prefix with <c>rsync --delete</c> and
    /// a 488MB download is not something to re-fetch on every deploy.
    /// </remarks>
    /// <panel>The speech recognition model file. Without it nothing said out loud is understood.</panel>
    [ConfigField("recognitionModel", "Recognition model", Group = "models", Type = ConfigType.Path)]
    public string ModelPath { get; set; } = "/var/lib/kgsm-speech/models/ggml-small.en.bin";

    /// <panel>Whether to recognise speech on the graphics card. Around forty times faster than the
    /// processor; a host without a usable card falls back on its own.</panel>
    [ConfigField("recognitionUseGpu", "Recognise on the GPU", Group = "models")]
    public bool UseGpu { get; set; } = true;

    /// <summary>The Kokoro model used to synthesise speech.</summary>
    /// <panel>The speech synthesis model file. Without it surfaces answer in text only.</panel>
    [ConfigField("synthesisModel", "Synthesis model", Group = "models", Type = ConfigType.Path)]
    public string SpeechModelPath { get; set; } = "/var/lib/kgsm-speech/models/kokoro.onnx";

    /// <panel>Whether to synthesise speech on the graphics card. Around eight times faster than the
    /// processor and worth roughly 700MB of video memory; a host without a usable card falls back on
    /// its own.</panel>
    [ConfigField("synthesisUseGpu", "Synthesise on the GPU", Group = "models")]
    public bool SpeakUseGpu { get; set; } = true;

    /// <summary>
    /// Which of Kokoro's voices this host speaks in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The English ones, listed best-first within each accent.</b> Kokoro ships voices for eight
    /// other languages and they are on disk beside these, but they expect text in those languages —
    /// offered here they would be twenty-odd ways to read an English answer badly. Anything Kokoro can
    /// load still works if it is set directly; what this list is, is the set worth choosing from.
    /// </para>
    /// <para>
    /// <b>Ordered by how much speech each was trained on, because that is what is audible.</b> The
    /// difference between the top of a group and the bottom is not accent or timbre — it is how
    /// synthetic the voice sounds, and it is not subtle.
    /// </para>
    /// </remarks>
    /// <panel>The voice every surface on this host speaks in — Discord, and anything else that asks.
    /// The first two letters are the accent and the speaker — <code>b</code> British,
    /// <code>a</code> American, then <code>f</code> or <code>m</code>. They are listed best-first
    /// within each accent, and the gap is worth hearing: the ones at the top of each group were
    /// trained on hours of speech and the ones at the bottom on minutes, which is the difference
    /// between a voice that sounds like a person and one that sounds like a synthesiser.</panel>
    [ConfigField("voice", "Speaking voice", Group = "voice", Type = ConfigType.Enum, Values = [
        // American — af_heart and af_bella are the best-trained voices Kokoro ships at all.
        "af_heart", "af_bella", "af_nicole", "af_aoede", "af_kore", "af_sarah",
        "af_alloy", "af_nova", "af_sky", "af_jessica", "af_river",
        "am_fenrir", "am_michael", "am_puck", "am_echo", "am_eric",
        "am_liam", "am_onyx", "am_santa", "am_adam",
        // British.
        "bf_emma", "bf_isabella", "bf_alice", "bf_lily",
        "bm_george", "bm_fable", "bm_lewis", "bm_daniel",
    ])]
    public string Voice { get; set; } = "af_heart";

    /// <summary>The slowest this host will speak, as a percentage of Kokoro's natural pace.</summary>
    public const int SlowestRate = SpeechEngineOptions.SlowestRate;

    /// <summary>The fastest this host will speak, as a percentage of Kokoro's natural pace.</summary>
    public const int FastestRate = SpeechEngineOptions.FastestRate;

    /// <summary>
    /// How fast this host speaks, as a percentage of the voice's natural pace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A percentage rather than Kokoro's own multiplier</b>, because the panel's bounds are whole
    /// numbers: 100 is the pace the voice was trained at, and <see cref="SlowestRate"/> and
    /// <see cref="FastestRate"/> are both the declared bounds and the clamp the daemon applies, so the
    /// slider cannot ask for a rate the synthesiser will not honour. Kokoro takes it as a float, and
    /// this is divided by a hundred on the way in.
    /// </para>
    /// <para>
    /// <b>It belongs to the host for the same reason the voice does.</b> Every surface asks this daemon,
    /// so one setting changes how the assistant sounds in Discord and in a browser at once, and there is
    /// nothing to keep in step.
    /// </para>
    /// <para>
    /// Rate does not change what synthesis costs — the work is per character, and a faster reading is
    /// the same characters in less audio.
    /// </para>
    /// </remarks>
    /// <panel>How fast this host speaks, against the voice's natural pace. 100 is the pace the voice
    /// was trained at; higher is faster. Worth moving in small steps and listening — the voices stay
    /// natural a little way either side of 100 and start to sound wrong well before the ends of this
    /// range.</panel>
    [ConfigField("speechRate", "Speaking rate", Group = "voice", Type = ConfigType.Int, Unit = "%",
        Min = SlowestRate, Max = FastestRate)]
    public int SpeechRate { get; set; } = 100;

    /// <summary>
    /// How long to stay loaded with nothing to say. Zero stays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ending is the only way to give the memory back.</b> The models cost this host about 1.6GB
    /// and a gigabyte of video memory, and neither is released by unloading a model inside a running
    /// process — the CUDA runtime behind them is resident for the life of whatever loaded it. So the
    /// lever is the process, and this is the lever.
    /// </para>
    /// <para>
    /// <b>Nothing is lost by exiting.</b> systemd holds the socket whether or not this is running, so
    /// the next request starts it again; a surface reconnects without noticing. What it costs is the
    /// few seconds of loading, paid by whoever speaks next.
    /// </para>
    /// </remarks>
    /// <panel>How many minutes to stay loaded after the last thing said or heard. The models cost
    /// around 1.6GB of memory and a gigabyte of video memory, all of which comes back when this
    /// exits — but loading them again takes a few seconds, which the next person to speak waits for.
    /// Zero stays loaded until the host restarts.</panel>
    [ConfigField("idleMinutes", "Unload after", Group = "lifetime", Unit = "minutes", Min = 0, Max = 1440)]
    public int IdleMinutes { get; set; } = 0;

    /// <summary>
    /// The socket surfaces reach this daemon on.
    /// </summary>
    /// <remarks>
    /// Must match the <c>ListenStream=</c> in <c>kgsm-speech.socket</c>: systemd binds that path and
    /// hands the listening socket over, so this value is only consulted when the daemon is started by
    /// hand. Changing one without the other leaves surfaces connecting to a socket nobody serves.
    /// </remarks>
    /// <panel>The unix socket other services reach this one on. It has to match the socket unit, so
    /// leave it alone unless you are moving both.</panel>
    [ConfigField("socketPath", "Control socket", Group = "wiring", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring)]
    public string SocketPath { get; set; } = "/run/kgsm-speech/speech.sock";

    /// <summary>
    /// This host's settings as the engine reads them.
    /// </summary>
    /// <remarks>
    /// The two shapes are deliberately separate. This one is what the Control Panel configures and
    /// what the descriptor is generated from, so it carries the vocabulary that describes a knob to a
    /// person. The engine's carries none of that, because the other product that embeds the engine
    /// describes its configuration a different way and neither should have to know about the other.
    /// </remarks>
    public SpeechEngineOptions ForEngine() => new()
    {
        ModelPath = ModelPath,
        UseGpu = UseGpu,
        SpeechModelPath = SpeechModelPath,
        SpeakUseGpu = SpeakUseGpu,

        Voice = Voice,
        SpeechRate = SpeechRate,
        IdleMinutes = IdleMinutes,
        SocketPath = SocketPath,
    };
}
