using System.Globalization;
using System.Text;

namespace TheKrystalShip.KGSM.Speech;

/// <summary>
/// What the daemon is doing right now, asked without loading anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is measured.</b> Which runtime a model actually loaded on, what has been heard
/// and said since this process started, which surfaces are attached, how long is left before the
/// models are unloaded. A field that could not be measured is null or empty rather than filled in
/// with what the configuration says it should be — the two come apart routinely, and the places they
/// do are exactly what somebody reads this to find out.
/// </para>
/// <para>
/// <b>The counters are since this process started, and the process ends on its own.</b> A daemon
/// that idled out has taken its tallies with it, so <see cref="StartedAt"/> travels beside them: a
/// surface that renders "12 utterances" without saying since when is describing a window nobody
/// chose.
/// </para>
/// <para>
/// <b>Nothing anybody said is in here.</b> A transcript is a person talking into a microphone, and it
/// belongs to the surface that asked for it. This carries counts, durations, timestamps and outcomes.
/// </para>
/// <para>
/// <b>Written as <c>key=value</c> lines</b>, one per line, UTF-8. A key this version does not know is
/// skipped rather than refused, so a surface built against an older contract keeps working against a
/// newer daemon and loses only the fields it was never going to render.
/// </para>
/// </remarks>
public sealed record SpeechStatus
{
    /// <summary>When this daemon process started — what every counter below is counted since.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Whether the models are in memory. False is this daemon at rest, not a fault.</summary>
    public bool Loaded { get; init; }

    /// <summary>When they were loaded. Null while nothing has needed them.</summary>
    public DateTimeOffset? LoadedAt { get; init; }

    /// <summary>How long loading took. Null while they are not loaded.</summary>
    public int? LoadMilliseconds { get; init; }

    /// <summary>Minutes of quiet before this daemon unloads and exits. Zero stays loaded.</summary>
    public int IdleMinutes { get; init; }

    /// <summary>
    /// When something was last asked of this daemon.
    /// </summary>
    /// <remarks>
    /// Asking for this status is deliberately not "asking" — a surface polling it would hold the
    /// idle window open forever and the memory would never come back.
    /// </remarks>
    public DateTimeOffset? LastAskedAt { get; init; }

    /// <summary>When the models are unloaded if nothing else is asked. Null when this daemon stays.</summary>
    public DateTimeOffset? UnloadsAt { get; init; }

    /// <summary>
    /// The processes connected right now, by name.
    /// </summary>
    /// <remarks>
    /// Read from the credentials the kernel puts on each connection, so it is what is actually
    /// attached rather than what claimed to be. Empty on a host whose surfaces have all gone away, and
    /// a connection whose process has since exited is left out rather than named as something it no
    /// longer is.
    /// </remarks>
    public IReadOnlyList<string> Surfaces { get; init; } = [];

    /// <summary>The voice this host is speaking in at this moment.</summary>
    public string SpeakingVoice { get; init; } = string.Empty;

    /// <summary>
    /// The voice this host's configuration names.
    /// </summary>
    /// <remarks>
    /// Differs from <see cref="SpeakingVoice"/> after somebody has changed the voice on the running
    /// daemon: that lasts until it restarts and is deliberately never written back, so this is the
    /// one the next process will speak in.
    /// </remarks>
    public string ConfiguredVoice { get; init; } = string.Empty;

    /// <summary>How many voices are installed on this host.</summary>
    public int InstalledVoices { get; init; }

    /// <summary>Recognition — audio in, words out.</summary>
    public SpeechLane Hearing { get; init; } = new();

    /// <summary>Synthesis — words in, audio out.</summary>
    public SpeechLane Speaking { get; init; } = new();

    /// <summary>Whether the voice being spoken in is the one the configuration names.</summary>
    /// <remarks>
    /// Both empty is not a mismatch — it is a daemon that has not been asked to say anything yet.
    /// </remarks>
    public bool VoiceOverridden =>
        SpeakingVoice.Length > 0
        && ConfiguredVoice.Length > 0
        && !SpeakingVoice.Equals(ConfiguredVoice, StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes the <c>key=value</c> form the daemon answers with.</summary>
    public string ToText()
    {
        var text = new StringBuilder();

        Write(text, "startedAt", StartedAt);
        Write(text, "loaded", Loaded);
        Write(text, "loadedAt", LoadedAt);
        Write(text, "loadMs", (long?)LoadMilliseconds);
        Write(text, "idleMinutes", (long?)IdleMinutes);
        Write(text, "lastAskedAt", LastAskedAt);
        Write(text, "unloadsAt", UnloadsAt);

        // One line each rather than a joined list: a process name is whatever somebody called their
        // binary, and a separator is a thing to escape.
        foreach (string surface in Surfaces) Write(text, "surface", surface);

        Write(text, "voice.speaking", SpeakingVoice);
        Write(text, "voice.configured", ConfiguredVoice);
        Write(text, "voice.installed", InstalledVoices);

        Hearing.Write(text, "hear");
        Speaking.Write(text, "speak");

        return text.ToString();
    }

    /// <summary>Reads that form back. Unknown keys are skipped.</summary>
    public static SpeechStatus Parse(string text)
    {
        var surfaces = new List<string>();
        var hearing = new SpeechLane.Builder();
        var speaking = new SpeechLane.Builder();
        var status = new SpeechStatus();

        foreach (string line in text.Split('\n'))
        {
            int split = line.IndexOf('=');
            if (split <= 0) continue;

            string key = line[..split];
            string value = line[(split + 1)..].TrimEnd('\r');

            if (key.StartsWith("hear.", StringComparison.Ordinal))
            {
                hearing.Take(key[5..], value);
                continue;
            }

            if (key.StartsWith("speak.", StringComparison.Ordinal))
            {
                speaking.Take(key[6..], value);
                continue;
            }

            switch (key)
            {
                case "startedAt": status = status with { StartedAt = Moment(value) ?? default }; break;
                case "loaded": status = status with { Loaded = value == "true" }; break;
                case "loadedAt": status = status with { LoadedAt = Moment(value) }; break;
                case "loadMs": status = status with { LoadMilliseconds = Count(value) }; break;
                case "idleMinutes": status = status with { IdleMinutes = Count(value) ?? 0 }; break;
                case "lastAskedAt": status = status with { LastAskedAt = Moment(value) }; break;
                case "unloadsAt": status = status with { UnloadsAt = Moment(value) }; break;
                case "surface": surfaces.Add(value); break;
                case "voice.speaking": status = status with { SpeakingVoice = value }; break;
                case "voice.configured": status = status with { ConfiguredVoice = value }; break;
                case "voice.installed": status = status with { InstalledVoices = Count(value) ?? 0 }; break;
            }
        }

        return status with
        {
            Surfaces = surfaces,
            Hearing = hearing.Built(),
            Speaking = speaking.Built(),
        };
    }

    // The encodings, shared by both ends so a key is spelled once. An empty or absent value is left
    // out entirely rather than written as an empty string: a reader cannot tell "" from "not measured"
    // and the difference is the whole point of this file.
    internal static void Write(StringBuilder text, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value)) text.Append(key).Append('=').Append(value).Append('\n');
    }

    internal static void Write(StringBuilder text, string key, bool value) =>
        text.Append(key).Append('=').Append(value ? "true" : "false").Append('\n');

    internal static void Write(StringBuilder text, string key, long? value)
    {
        if (value.HasValue)
            text.Append(key).Append('=').Append(value.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    // Named apart from the integer overloads rather than overloaded on double: `long` and `double` are
    // both reachable from an int by an implicit conversion, and a call that resolved to the wrong one
    // would round a count or print a duration as "0.123".
    internal static void WriteSeconds(StringBuilder text, string key, double value) =>
        text.Append(key).Append('=').Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');

    internal static void Write(StringBuilder text, string key, DateTimeOffset? value)
    {
        if (value.HasValue)
            text.Append(key).Append('=').Append(value.Value.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
    }

    internal static DateTimeOffset? Moment(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTimeOffset at) ? at : null;

    internal static int? Count(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;

    internal static long? Total(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : null;

    internal static double Seconds(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : 0;
}

/// <summary>
/// One half of the engine — hearing or speaking — and how it has been getting on.
/// </summary>
/// <remarks>
/// <para>
/// The two are separate everywhere it matters: each loads its own model, each runs one pass at a time
/// behind its own lock, and either can be unavailable while the other works. A host with no cuDNN
/// hears on the card and speaks on the processor, and a single "speech is fine" would hide that.
/// </para>
/// <para>
/// <b><see cref="Runtime"/> is what loaded, not what was asked for.</b> Both models fall back to the
/// processor when the card cannot be had, silently and by design — it is the difference between a
/// working host and no voice at all. It is also a factor of forty for recognition and eight for
/// synthesis, so it is reported rather than assumed from the setting that requested it.
/// </para>
/// </remarks>
public sealed record SpeechLane
{
    /// <summary>Whether this half can do anything at all.</summary>
    public bool Available { get; init; }

    /// <summary>What it is doing, or why it cannot. Empty while the models have not been loaded.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>The model file's name.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>The model file's size on disk. Zero when it is not there to measure.</summary>
    public long ModelBytes { get; init; }

    /// <summary><c>gpu</c>, <c>cpu</c>, or <c>unknown</c> before anything is loaded.</summary>
    public string Runtime { get; init; } = "unknown";

    /// <summary>Whether a pass is running right now.</summary>
    public bool Busy { get; init; }

    /// <summary>How many requests are queued behind it. This engine runs one pass at a time.</summary>
    public int Waiting { get; init; }

    /// <summary>Passes that ran and produced an answer.</summary>
    public long Done { get; init; }

    /// <summary>Requests turned away because a pass was running and the caller would not wait.</summary>
    public long Rejected { get; init; }

    /// <summary>Passes that were attempted and threw.</summary>
    public long Failed { get; init; }

    /// <summary>Seconds of audio read, or produced.</summary>
    public double AudioSeconds { get; init; }

    /// <summary>Characters said. Zero on the recognition side, which is paid per utterance.</summary>
    public long Characters { get; init; }

    /// <summary>How long the last pass took.</summary>
    public int? LastMilliseconds { get; init; }

    /// <summary>The mean over the passes still remembered.</summary>
    public int? MeanMilliseconds { get; init; }

    /// <summary>The 95th percentile over the passes still remembered — what a slow one costs.</summary>
    public int? P95Milliseconds { get; init; }

    /// <summary>When the last pass finished.</summary>
    public DateTimeOffset? LastAt { get; init; }

    /// <summary>How it went: <c>done</c>, <c>busy</c>, <c>unavailable</c> or <c>failed</c>.</summary>
    public string? LastOutcome { get; init; }

    /// <summary>
    /// Seconds of audio per second of work, over the passes still remembered.
    /// </summary>
    /// <remarks>
    /// The one number that says whether the card is really being used: recognition on a GPU runs many
    /// times faster than real time and on a processor barely faster than the person talking. Null
    /// until a pass has been timed.
    /// </remarks>
    public double? RealtimeFactor =>
        MeanMilliseconds is > 0 && Done > 0 ? AudioSeconds / Done / (MeanMilliseconds.Value / 1000.0) : null;

    internal void Write(StringBuilder text, string prefix)
    {
        SpeechStatus.Write(text, prefix + ".available", Available);
        SpeechStatus.Write(text, prefix + ".detail", Detail);
        SpeechStatus.Write(text, prefix + ".model", Model);
        SpeechStatus.Write(text, prefix + ".modelBytes", ModelBytes);
        SpeechStatus.Write(text, prefix + ".runtime", Runtime);
        SpeechStatus.Write(text, prefix + ".busy", Busy);
        SpeechStatus.Write(text, prefix + ".waiting", (long)Waiting);
        SpeechStatus.Write(text, prefix + ".done", Done);
        SpeechStatus.Write(text, prefix + ".rejected", Rejected);
        SpeechStatus.Write(text, prefix + ".failed", Failed);
        SpeechStatus.WriteSeconds(text, prefix + ".audioSeconds", AudioSeconds);
        SpeechStatus.Write(text, prefix + ".characters", Characters);
        SpeechStatus.Write(text, prefix + ".msLast", (long?)LastMilliseconds);
        SpeechStatus.Write(text, prefix + ".msMean", (long?)MeanMilliseconds);
        SpeechStatus.Write(text, prefix + ".msP95", (long?)P95Milliseconds);
        SpeechStatus.Write(text, prefix + ".lastAt", LastAt);
        SpeechStatus.Write(text, prefix + ".lastOutcome", LastOutcome);
    }

    /// <summary>Collects one lane's keys as they arrive, in any order.</summary>
    internal sealed class Builder
    {
        private SpeechLane _lane = new();

        internal void Take(string key, string value) => _lane = key switch
        {
            "available" => _lane with { Available = value == "true" },
            "detail" => _lane with { Detail = value },
            "model" => _lane with { Model = value },
            "modelBytes" => _lane with { ModelBytes = SpeechStatus.Total(value) ?? 0 },
            "runtime" => _lane with { Runtime = value },
            "busy" => _lane with { Busy = value == "true" },
            "waiting" => _lane with { Waiting = SpeechStatus.Count(value) ?? 0 },
            "done" => _lane with { Done = SpeechStatus.Total(value) ?? 0 },
            "rejected" => _lane with { Rejected = SpeechStatus.Total(value) ?? 0 },
            "failed" => _lane with { Failed = SpeechStatus.Total(value) ?? 0 },
            "audioSeconds" => _lane with { AudioSeconds = SpeechStatus.Seconds(value) },
            "characters" => _lane with { Characters = SpeechStatus.Total(value) ?? 0 },
            "msLast" => _lane with { LastMilliseconds = SpeechStatus.Count(value) },
            "msMean" => _lane with { MeanMilliseconds = SpeechStatus.Count(value) },
            "msP95" => _lane with { P95Milliseconds = SpeechStatus.Count(value) },
            "lastAt" => _lane with { LastAt = SpeechStatus.Moment(value) },
            "lastOutcome" => _lane with { LastOutcome = value },
            _ => _lane,
        };

        internal SpeechLane Built() => _lane;
    }
}
