using System.Reflection;

using FluentAssertions;

using TheKrystalShip.KGSM.Speech.Daemon;

using Xunit;

namespace KGSM.Speech.Tests;

/// <summary>
/// The voices are files, and the name of one arrives from configuration, from a slash command on
/// some surface, or from the Control Panel. Reading the directory is what lets this daemon answer
/// "which voices are there" without loading a model — and what a name is checked against before it
/// is ever allowed near a path.
/// </summary>
/// <remarks>
/// These run against the voices the build copies beside the test binary, which are the same files
/// the deploy ships beside the daemon.
/// </remarks>
public class InstalledVoicesTests
{
    [Fact]
    public void TheVoicesShippedBesideTheBinaryAreFound() =>
        InstalledVoices.All().Should().Contain("af_heart");

    [Fact]
    public void OnlyTheEnglishOnesAreOffered()
    {
        // Kokoro's other languages sit in the same tree and expect text in those languages. Offering
        // them would be twenty-odd ways to read an English answer badly.
        InstalledVoices.Offered().Should().OnlyContain(v => v[0] == 'a' || v[0] == 'b');
        InstalledVoices.Offered().Should().NotContain(v => v.StartsWith("zf_") || v.StartsWith("zm_"));
    }

    [Fact]
    public void TheOfferedListLeadsWithThePreferredOrder() =>
        InstalledVoices.Offered().First().Should().Be(SpeechVoices.Preferred[0]);

    [Fact]
    public void TheDefaultVoiceLeadsThatList()
    {
        // Not cosmetic: this is the order the Control Panel's dropdown shows and the order a surface
        // suggests voices in. A picker opening on the value it is already set to reads as a setting;
        // one opening elsewhere reads as a list to go hunting through.
        SpeechVoices.Preferred[0].Should().Be(new SpeechOptions().Voice);
    }

    [Fact]
    public void EveryVoiceIsNamedOnce() =>
        SpeechVoices.Preferred.Should().OnlyHaveUniqueItems();

    [Fact]
    public void AccentsAreNotInterleaved()
    {
        // Grouped, because somebody choosing a voice has usually already chosen an accent.
        var accents = SpeechVoices.Preferred.Select(v => v[0]).Distinct();
        accents.Should().HaveCount(2, "each accent should appear as one contiguous run");
    }

    [Fact]
    public void TheDescriptorOffersExactlyTheVoicesInThePreferredOrder()
    {
        // The list exists twice — an attribute argument has to be a compile-time constant, so the leaf
        // descriptor carries its own literal copy. The Control Panel's dropdown and the order this
        // host suggests voices in are the same list, and a voice added to one and not the other is a
        // surface offering something another surface has never heard of.
        Attribute field = typeof(SpeechOptions)
            .GetProperty(nameof(SpeechOptions.Voice))!
            .GetCustomAttributes()
            .Single(a => a.GetType().Name == "ConfigFieldAttribute");

        string[] declared = (string[])field.GetType().GetProperty("Values")!.GetValue(field)!;
        declared.Should().Equal(SpeechVoices.Preferred);
    }

    [Fact]
    public void AVoiceThisHostHasResolvesToItsFile()
    {
        string? file = InstalledVoices.Find("af_heart");

        file.Should().NotBeNull();
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public void TheNameIsMatchedHoweverItIsSpelled() =>
        InstalledVoices.Find("AF_Heart").Should().Be(InstalledVoices.Find("af_heart"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no_such_voice")]
    public void AVoiceThisHostDoesNotHaveIsNotFound(string name) =>
        InstalledVoices.Find(name).Should().BeNull();

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("voices-zh/../af_heart")]
    public void ANameIsNeverComposedIntoAPath(string mischief) =>
        // A voice name is configuration — it arrives from a settings file, an environment variable or
        // a request from another process — and configuration that reaches the filesystem unchecked is
        // how ".." gets read as a voice. Matched against the listing, so only a real filename can ever
        // come back.
        InstalledVoices.Find(mischief).Should().BeNull();
}
