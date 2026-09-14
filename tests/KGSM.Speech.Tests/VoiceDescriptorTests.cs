using System.Reflection;

using FluentAssertions;

using TheKrystalShip.KGSM.Speech.Daemon;
using TheKrystalShip.Speech.Engine;
using TheKrystalShip.Speech.Engine.Kokoro;

using Xunit;

namespace KGSM.Speech.Tests;

public class VoiceDescriptorTests
{
    [Fact]
    public void TheDescriptorOffersExactlyTheVoicesInThePreferredOrder()
    {
        // The list exists twice — an attribute argument has to be a compile-time constant, so the leaf
        // descriptor carries its own literal copy of what the engine holds. The Control Panel's
        // dropdown and the order this host suggests voices in are the same list, and a voice added to
        // one and not the other is a surface offering something another surface has never heard of.
        Attribute field = typeof(SpeechOptions)
            .GetProperty(nameof(SpeechOptions.Voice))!
            .GetCustomAttributes()
            .Single(a => a.GetType().Name == "ConfigFieldAttribute");

        var values = (string[])field.GetType().GetProperty("Values")!.GetValue(field)!;

        values.Should().Equal(SpeechVoices.Preferred);
    }

    [Fact]
    public void TheRateSliderTakesItsBoundsFromTheSynthesiser()
    {
        // The bound is the voice's, not this host's opinion: below the slowest the words stop being
        // intelligible. Declaring it separately here is how the two drift apart.
        SpeechOptions.SlowestRate.Should().Be(SpeechEngineOptions.SlowestRate);
        SpeechOptions.FastestRate.Should().Be(SpeechEngineOptions.FastestRate);
    }

    [Fact]
    public void ThisHostCarriesASynthesiserToRegister()
    {
        // Every KGSM surface speaks with this daemon's voice. Synthesis is a package reference
        // rather than a setting, so the way it silently disappears is the reference being dropped —
        // which would leave every surface answering in text with nothing saying why.
        typeof(KokoroSynthesis).Should().BeAssignableTo<ISynthesiserFactory>();
    }
}
