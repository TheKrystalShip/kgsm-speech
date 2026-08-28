namespace TheKrystalShip.KGSM.Speech.Daemon;

/// <summary>
/// The order Kokoro's English voices are worth offering in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordered by how much speech each was trained on, not alphabetically</b>, because that is the axis
/// a listener hears. Within an accent the difference between the top of a group and the bottom is not
/// timbre — it is how synthetic the voice sounds, and the range is an order of magnitude: hours of
/// recorded speech against minutes.
/// </para>
/// <para>
/// <b>A preference, never a permission.</b> Nothing consults this to decide whether a voice may be
/// used — a host has whatever voices are installed, including Kokoro's eight other languages, and any
/// of them can be set. This decides what gets suggested, and a name missing from it sorts last rather
/// than disappearing.
/// </para>
/// <para>
/// <b>The leaf descriptor carries this same list as a literal</b>, because an attribute argument has
/// to be a constant and cannot reference this. They are checked against each other by a test; edit
/// both or the test will say so.
/// </para>
/// </remarks>
public static class SpeechVoices
{
    /// <summary>English voices, best-trained first within each accent.</summary>
    public static readonly IReadOnlyList<string> Preferred =
    [
        // American. af_heart and af_bella are the best-trained voices Kokoro ships at all, and
        // af_heart is what this bot speaks in — a picker whose first entry is the current value reads
        // as the setting it is, rather than as a list somebody has to find their own answer in.
        "af_heart", "af_bella", "af_nicole", "af_aoede", "af_kore", "af_sarah",
        "af_alloy", "af_nova", "af_sky", "af_jessica", "af_river",
        "am_fenrir", "am_michael", "am_puck", "am_echo", "am_eric",
        "am_liam", "am_onyx", "am_santa", "am_adam",

        // British. bf_emma is the only one of these with hours of speech behind it.
        "bf_emma", "bf_isabella", "bf_alice", "bf_lily",
        "bm_george", "bm_fable", "bm_lewis", "bm_daniel",
    ];
}
