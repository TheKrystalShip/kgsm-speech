namespace TheKrystalShip.KGSM.Speech.Daemon;

/// <summary>
/// The parts of this host's voice that can stop working while the daemon answers normally.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>This daemon reports degradation only — never a start or a stop.</b> It is socket activated and
/// gives its memory back by exiting, so inactive is its resting state rather than a transition. That
/// is also why nothing on this host can health-poll it: connecting to the socket is what starts it,
/// and a probe would load 1.6GB of models to ask whether it is well. Self-reporting is the only way
/// anything learns this leaf is impaired.
/// </para>
/// <para>
/// Loading and running are two separate answers, so they are two separate components. A model that did
/// not load means the host cannot do that thing at all; a model that loaded on the processor means it
/// can, slowly, which every surface waiting on it feels and none of them can see.
/// </para>
/// </remarks>
internal static class SpeechComponents
{
    /// <summary>Recognition — whether this host can understand anything said to it.</summary>
    public const string Hearing = "hearing";

    /// <summary>Synthesis — whether this host can answer out loud.</summary>
    public const string Speaking = "speaking";

    /// <summary>
    /// Whether recognition got the GPU it asked for.
    /// </summary>
    /// <remarks>
    /// Whisper.net takes the first runtime that initialises, so a driver mid-upgrade silently yields
    /// the processor. The models are the reason this leaf exists on a machine with a card.
    /// </remarks>
    public const string HearingAccelerator = "hearing-accelerator";

    /// <summary>Whether synthesis got the GPU it asked for.</summary>
    public const string SpeakingAccelerator = "speaking-accelerator";
}
