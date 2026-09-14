using TheKrystalShip.KGSM.Lifecycle;
using TheKrystalShip.Speech.Engine;

namespace TheKrystalShip.KGSM.Speech.Daemon;

/// <summary>
/// Puts what the engine reports about this host's voice into the KGSM event journal.
/// </summary>
/// <remarks>
/// <b>This is the whole of what makes the engine a KGSM leaf.</b> The engine knows that a model
/// failed to load, or that it loaded on the processor; it does not know that this host keeps a
/// journal, what a leaf is, or that anything reads either. Those are facts about the product that
/// installs it, so they meet the engine here and nowhere else.
/// </remarks>
internal sealed class KgsmSpeechHealth(LeafLifecycle lifecycle) : ISpeechHealth
{
    public void MarkDegraded(string component, string detail) =>
        lifecycle.MarkDegraded(component, detail);

    public void MarkRecovered(string component) => lifecycle.MarkRecovered(component);
}
