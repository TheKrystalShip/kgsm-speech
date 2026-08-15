using FluentAssertions;

using TheKrystalShip.KGSM.Speech;

using Xunit;

namespace KGSM.Speech.Tests;

/// <summary>
/// The prior context the recogniser is primed with, and the failure that priming introduces.
/// </summary>
/// <remarks>
/// The names here are this host's real ones. <c>Ketchup</c> is the measured case — whisper returned
/// "catch-up" for it — and <c>romestead</c> is the other kind, a word that is not a word.
/// </remarks>
public class SpokenVocabularyTests
{
    private static readonly string[] Triggers = ["hey assistant"];

    private static readonly string[] Instances =
        ["minecraft", "necesse", "Ketchup", "projectzomboid", "romestead", "stationeers"];

    private static readonly string[] Blueprints =
        ["factorio", "minecraft", "dontstarvetogether", "abioticfactor", "7dtd"];

    [Fact]
    public void EveryNameThisHostKnowsIsInTheContext()
    {
        string context = SpokenVocabulary.Compose(Triggers, Instances, Blueprints);

        foreach (string name in Instances.Concat(Blueprints))
            context.Should().Contain(name);
    }

    [Fact]
    public void TheTriggerLeadsBecauseItIsSaidEveryTime()
    {
        string context = SpokenVocabulary.Compose(Triggers, Instances, Blueprints);

        context.Should().StartWith("hey assistant");
    }

    [Fact]
    public void ANameIsNotRepeatedWhenItIsBothInstalledAndInstallable()
    {
        // "minecraft" is an instance here and a blueprint. Spending the budget on it twice buys
        // nothing and costs a name at the end of the list.
        string context = SpokenVocabulary.Compose(Triggers, Instances, Blueprints);

        Occurrences(context, "minecraft").Should().Be(1);
    }

    [Fact]
    public void TheContextStaysInsideItsBudget()
    {
        // Whisper truncates its prompt window from the front, so overshooting silently drops the
        // names written first — which are the instances, the ones that matter most.
        var many = Enumerable.Range(0, 400).Select(i => $"server{i}").ToArray();

        string context = SpokenVocabulary.Compose(Triggers, many, many, budget: 200);

        context.Length.Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public void InstancesAreWrittenBeforeBlueprints()
    {
        // What people name in a request is a server that exists; a blueprint is named once, when
        // something is installed. Under a budget the instances are the ones to keep.
        string context = SpokenVocabulary.Compose(Triggers, ["romestead"], ["factorio"]);

        context.IndexOf("romestead", StringComparison.Ordinal)
            .Should().BeLessThan(context.IndexOf("factorio", StringComparison.Ordinal));
    }

    [Fact]
    public void AHostWithNothingInstalledClaimsNoServers()
    {
        // A lead-in with nothing after it would be prior context asserting the host is empty, which
        // is worse than giving whisper no context at all.
        string context = SpokenVocabulary.Compose(Triggers, [], []);

        context.Should().Be("hey assistant.");
        context.Should().NotContain("servers");
    }

    [Fact]
    public void NothingAtAllComposesToNothing()
    {
        SpokenVocabulary.Compose([], [], []).Should().BeEmpty();
    }

    [Fact]
    public void AWholeRunOfTheContextComingBackIsAnEcho()
    {
        // Whisper's documented failure with a prompt: given audio it can make nothing of, it
        // sometimes continues the context instead of returning nothing. It arrives looking exactly
        // like somebody listing servers.
        string context = SpokenVocabulary.Compose(Triggers, Instances, Blueprints);

        SpokenVocabulary.IsEchoOf("minecraft, necesse, Ketchup, projectzomboid", context)
            .Should().BeTrue();
    }

    [Fact]
    public void OneServerNamedOnItsOwnIsNotAnEcho()
    {
        // The whole point of a follow-up window: "hey assistant" … "minecraft" is somebody answering
        // which server they meant, and it is a substring of the context every time.
        string context = SpokenVocabulary.Compose(Triggers, Instances, Blueprints);

        SpokenVocabulary.IsEchoOf("minecraft", context).Should().BeFalse();
        SpokenVocabulary.IsEchoOf("Ketchup", context).Should().BeFalse();
    }

    [Fact]
    public void ARealRequestThatNamesAServerIsNotAnEcho()
    {
        string context = SpokenVocabulary.Compose(Triggers, Instances, Blueprints);

        SpokenVocabulary.IsEchoOf("stop the minecraft server", context).Should().BeFalse();
        SpokenVocabulary.IsEchoOf("is Ketchup running right now", context).Should().BeFalse();
    }

    [Fact]
    public void PunctuationAndCasingCannotHideAnEcho()
    {
        string context = SpokenVocabulary.Compose(Triggers, Instances, Blueprints);

        SpokenVocabulary.IsEchoOf("Minecraft. Necesse. KETCHUP!", context).Should().BeTrue();
    }

    [Fact]
    public void WithNoContextNothingIsEverAnEcho()
    {
        SpokenVocabulary.IsEchoOf("minecraft necesse ketchup", "").Should().BeFalse();
        SpokenVocabulary.IsEchoOf("", "anything at all here").Should().BeFalse();
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.OrdinalIgnoreCase))
            count++;

        return count;
    }
}
