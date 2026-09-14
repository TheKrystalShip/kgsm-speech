using System.Reflection;

using FluentAssertions;

using TheKrystalShip.KGSM.Speech.Daemon;
using TheKrystalShip.Speech.Engine;

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
    public void ThisHostAnswersOutLoud()
    {
        // Every KGSM surface speaks with this daemon's voice, so a host that silently stopped
        // registering a synthesiser would leave every one of them answering in text with nothing
        // saying why.
        new SpeechOptions().ForEngine().Synthesis.Should().BeTrue();
    }
}
