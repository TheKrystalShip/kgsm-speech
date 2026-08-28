using FluentAssertions;

using TheKrystalShip.KGSM.Speech;

using Xunit;

namespace KGSM.Speech.Tests;

/// <summary>
/// The cut points decide what a listener hears and when. Getting them wrong is not a crash — it is an
/// answer read out with the punctuation pronounced, or a code block recited line by line, or a
/// sentence that never arrives because nothing after it ever terminated.
/// </summary>
/// <remarks>
/// Every surface that speaks reads this class, so these cases are the union of what each of them
/// needed rather than what any one of them thought of: a Discord voice channel and a browser streaming
/// audio break in different ways, and both sets of breakages are pinned here.
/// </remarks>
public class SpokenSentencesTests
{
    /// <summary>Feeds a reply through as a series of deltas and collects everything sayable.</summary>
    private static List<string> Say(params string?[] deltas)
    {
        var sentences = new SpokenSentences();
        var said = new List<string>();

        foreach (string? delta in deltas)
            said.AddRange(sentences.Take(delta));

        if (sentences.Flush() is { } last) said.Add(last);

        return said;
    }

    /// <summary>What a listener hears, as one string.</summary>
    private static string Heard(params string?[] deltas) => string.Join(" ", Say(deltas));

    /// <summary>A reply cut the way tokens actually arrive — mid-word, at no boundary anybody chose.</summary>
    private static string[] Chunks(string text, int size)
    {
        var chunks = new List<string>();
        for (int i = 0; i < text.Length; i += size)
            chunks.Add(text.Substring(i, Math.Min(size, text.Length - i)));
        return [.. chunks];
    }

    // --- when a sentence is ready -----------------------------------------------------------

    [Fact]
    public void ASentenceIsSaidWhenItEnds()
    {
        Say("Factorio is running with four players online. ", "Terraria is stopped.")
            .Should().SatisfyRespectively(
                first => first.Should().Be("Factorio is running with four players online."),
                second => second.Should().Be("Terraria is stopped."));
    }

    [Fact]
    public void TokenFragmentsAssembleIntoOneSentence()
    {
        // What the stream actually delivers: a delta is a piece of a token, not a word.
        Say("Fact", "orio", " is", " run", "ning right", " now.")
            .Should().ContainSingle()
            .Which.Should().Be("Factorio is running right now.");
    }

    [Fact]
    public void NothingIsSaidUntilThereIsAWholeSentence()
    {
        var sentences = new SpokenSentences();

        sentences.Take("Factorio").Should().BeEmpty();
        sentences.Take(" is ru").Should().BeEmpty();
        sentences.Take("nning.").Should().BeEmpty();
    }

    [Fact]
    public void ATerminatorAtTheVeryEndOfWhatHasArrivedIsNotYetABoundary()
    {
        // Nothing after it says whether it ended a sentence or sits inside a number. The next slice
        // decides, and the flush covers a reply that simply stopped there.
        var sentences = new SpokenSentences();

        sentences.Take("Factorio is running and has been up for eleven days.").Should().BeEmpty();
        sentences.Take(" ").Should().ContainSingle();
    }

    [Fact]
    public void TwoSentencesOnOneLineAreTwoOfThem()
    {
        Say("The server is running normally now. It was restarted about an hour ago.")
            .Should().SatisfyRespectively(
                first => first.Should().Be("The server is running normally now."),
                second => second.Should().Be("It was restarted about an hour ago."));
    }

    [Fact]
    public void SentencesComeOutInTheOrderTheyWereWritten()
    {
        Say("First the server was stopped by the scheduler. ",
            "Then it was started again a minute later. ",
            "It has been up ever since that restart.")
            .Should().SatisfyRespectively(
                first => first.Should().StartWith("First"),
                second => second.Should().StartWith("Then"),
                third => third.Should().StartWith("It has been up"));
    }

    [Fact]
    public void ALineEndingIsABoundaryToo()
    {
        // A list item carries no full stop, and waiting for one would hold the whole list back.
        Say("Here is what is on this host at the moment:\n",
            "- factorio, which is running and has eleven players on it\n",
            "- terraria, which is stopped\n")
            .Should().SatisfyRespectively(
                first => first.Should().Be("Here is what is on this host at the moment:"),
                second => second.Should().Be("factorio, which is running and has eleven players on it"),
                third => third.Should().Be("terraria, which is stopped"));
    }

    // --- what does NOT end a sentence -------------------------------------------------------

    [Theory]
    [InlineData("The model file is ggml-small.en.bin and it lives in the state directory.")]
    [InlineData("This host is running kgsm.sh version 1.2.3 with everything current.")]
    [InlineData("Reach the panel at panel.example.invalid whenever you need to look.")]
    [InlineData("The host is running version 2.0.58 of the engine and everything looks fine.")]
    public void ADotInsideAWordDoesNotEndTheSentence(string reply)
    {
        // Version numbers, filenames and hostnames are full of dots, and this domain is full of all
        // three. Cutting at one splits a sentence mid-word and spends a synthesis request saying half
        // of it — so a sentence ends at punctuation FOLLOWED BY WHITESPACE, decided one character late.
        Say(reply).Should().ContainSingle();
    }

    // --- how short is too short --------------------------------------------------------------

    [Fact]
    public void AShortSentenceWaitsForTheNextOneRatherThanBeingClipped()
    {
        // "Yes." alone is a syllable and a whole round trip. It rides along with what follows.
        Say("Yes. It has been running since Tuesday afternoon.")
            .Should().ContainSingle()
            .Which.Should().Be("Yes. It has been running since Tuesday afternoon.");
    }

    [Fact]
    public void AShortAnswerIsStillSaidWhenNothingFollowsIt()
    {
        // The minimum exists to avoid a clipped fragment mid-answer, not to swallow a short reply.
        Say("Yes, it is.").Should().ContainSingle().Which.Should().Be("Yes, it is.");
    }

    [Fact]
    public void AnAnswerThatNeverTerminatesIsStillSaid()
    {
        Say("The server is starting and should be up shortly")
            .Should().ContainSingle()
            .Which.Should().Be("The server is starting and should be up shortly");
    }

    [Fact]
    public void ACompleteShortSentenceIsWorthHearingImmediately()
    {
        // The floor is low on purpose. A sentence of six words is the answer, and holding it back to
        // pair with the next one trades away the latency this class exists to remove.
        Say("Factorio is running normally right now. ", "It has eleven players on it at the moment.")
            .Should().HaveCount(2);
    }

    // --- fenced code blocks -------------------------------------------------------------------

    [Fact]
    public void AFencedCodeBlockIsNotRead()
    {
        // The trap this class exists for. Segmenting first and stripping second reads the contents of
        // a fence out one line at a time — thirty seconds of punctuation.
        List<string> said = Say(
            "Here is the config you asked about.\n",
            "```ini\n",
            "name = factorio\n",
            "port = 34197\n",
            "restart = always\n",
            "```\n",
            "Change the port and restart it.");

        said.Should().NotContain(s => s.Contains("34197", StringComparison.Ordinal));
        said.Should().NotContain(s => s.Contains('='));
        said.Should().Contain(s => s.Contains("Here is the config", StringComparison.Ordinal));
        said.Should().Contain(s => s.Contains("Change the port", StringComparison.Ordinal));
    }

    [Fact]
    public void AFenceOpenedAndNeverClosedSwallowsTheRest()
    {
        // An answer that opened a fence and stopped IS a code block, whatever arrives next.
        List<string> said = Say(
            "Try this:\n", "```bash\n", "kgsm --start factorio\n", "kgsm --status factorio\n");

        said.Should().NotContain(s => s.Contains("kgsm", StringComparison.Ordinal));
        said.Should().Contain(s => s.Contains("Try this", StringComparison.Ordinal));
    }

    [Fact]
    public void AFenceSplitAcrossDeltasIsStillRecognised()
    {
        // The fence marker arrives in pieces like everything else does.
        List<string> said = Say(
            "Run this.\n", "`", "``", "\n", "rm -rf /tmp/x\n", "``", "`\n", "That clears the cache.");

        said.Should().NotContain(s => s.Contains("rm -rf", StringComparison.Ordinal));
        said.Should().Contain(s => s.Contains("That clears the cache", StringComparison.Ordinal));
    }

    [Fact]
    public void NothingIsSaidWhileAFenceIsStillOpen()
    {
        var sentences = new SpokenSentences();

        sentences.Take("Here is the configuration it is running with right now, on disk:\n```\n");
        sentences.Take("enabled=true. port=34197. name=heisen's server. seed=0.\n")
            .Should().BeEmpty("a boundary inside a fence would leave its contents to be read out");
    }

    [Fact]
    public void AFencedBlockArrivingMidWordIsStillDropped()
    {
        // Fence state carries across deltas, so anything that re-reads text it has already seen
        // toggles it twice and starts reading the code aloud. Sliced small enough that a re-reading
        // implementation would.
        const string reply =
            "It crashed on start, and here is the tail of the log for you to look at:\n"
            + "```\n"
            + "Segfault at 0x0.\n"
            + "at main()!\n"
            + "at start()?\n"
            + "```\n"
            + "I would restart it and see whether it happens again.";

        List<string> said = Say(Chunks(reply, 5));

        said.Should().NotContain(s => s.Contains("Segfault", StringComparison.Ordinal));
        said.Should().NotContain(s => s.Contains("main()", StringComparison.Ordinal));
        string.Join(" ", said).Should().Be(
            "It crashed on start, and here is the tail of the log for you to look at: "
            + "I would restart it and see whether it happens again.");
    }

    [Fact]
    public void AFenceTheReplyNeverClosesIsDroppedAtTheFlush()
    {
        const string reply =
            "Here is the configuration it is running with right now, as it stands on disk:\n"
            + "```ini\n"
            + "enabled=true\n"
            + "port=34197\n";

        List<string> said = Say(Chunks(reply, 6));

        said.Should().NotContain(s => s.Contains("34197", StringComparison.Ordinal));
        string.Join(" ", said).Should().Be(
            "Here is the configuration it is running with right now, as it stands on disk:");
    }

    [Fact]
    public void AFenceMarkerPartWayAlongALineDoesNotOpenOne()
    {
        // A sentence ending mid-line leaves the rest of that line in a fresh buffer. Reading that as
        // a line start would take "Done. ```yaml" for a fence and silently swallow every word after
        // it, for the rest of the answer.
        Heard("Done. ```yaml``` and the rest of this answer is still worth hearing.")
            .Should().Contain("the rest of this answer is still worth hearing");
    }

    // --- markup -------------------------------------------------------------------------------

    [Fact]
    public void MarkupIsDroppedAndTheWordsAreKept()
    {
        Say("The **factorio** server is `running` on port 34197 right now.")
            .Should().ContainSingle()
            .Which.Should().Be("The factorio server is running on port 34197 right now.");
    }

    [Fact]
    public void MarkupIsStrippedFromEveryPieceNotOnlyTheFirst()
    {
        Heard("**Factorio** is running on `port 34197` and it has been up for eleven days. ",
              "*Terraria* is stopped and nothing is wrong with it.")
            .Should().Be(
                "Factorio is running on port 34197 and it has been up for eleven days. "
                + "Terraria is stopped and nothing is wrong with it.");
    }

    [Fact]
    public void AListItemIsItsOwnBreathWithoutItsBullet()
    {
        Say("- factorio is running with four players\n", "- terraria has been stopped since Monday\n")
            .Should().SatisfyRespectively(
                first => first.Should().Be("factorio is running with four players"),
                second => second.Should().Be("terraria has been stopped since Monday"));
    }

    [Fact]
    public void APlusIsAListMarkerToo()
    {
        Say("+ factorio is running with four players online right now\n")
            .Should().ContainSingle()
            .Which.Should().Be("factorio is running with four players online right now");
    }

    [Fact]
    public void AHeadingLosesItsHashes()
    {
        List<string> said = Say("## Server status\n", "Everything on this host is running normally.");

        said[0].Should().Contain("Server status").And.NotContain("#");
    }

    [Fact]
    public void ALinkIsReadAsItsText()
    {
        // An address read aloud is noise, and its dots would cut the sentence in the middle.
        List<string> said = Say(
            "The panel is at [the control panel](https://example.invalid) whenever you need it.");

        said.Should().ContainSingle();
        said[0].Should().NotContain("[").And.NotContain("example.invalid");
        said[0].Should().Contain("the control panel");
    }

    [Fact]
    public void ATablesPipesAreNotSpoken()
    {
        Heard("| factorio | running |\n").Should().NotContain("|");
    }

    [Fact]
    public void PunctuationThatShapesSpeechIsKept()
    {
        // Commas and full stops are what a synthesiser paces on; stripping them with the markup would
        // produce one flat run-on.
        Heard("Yes, it's running. It has been up for three hours!")
            .Should().Be("Yes, it's running. It has been up for three hours!");
    }

    // --- nothing to say -------------------------------------------------------------------------

    [Fact]
    public void NothingSayableProducesNothing() => Say("   ", "\n", "").Should().BeEmpty();

    [Fact]
    public void AnEmptyReplySaysNothing()
    {
        var sentences = new SpokenSentences();

        sentences.Take(null).Should().BeEmpty();
        sentences.Take(string.Empty).Should().BeEmpty();
        sentences.Flush().Should().BeNull();
    }

    [Fact]
    public void TheFlushEmptiesTheBuffer()
    {
        // A second call must not repeat what has already been spoken.
        var sentences = new SpokenSentences();

        sentences.Take("A short answer");
        sentences.Flush().Should().Be("A short answer");
        sentences.Flush().Should().BeNull();
    }

    // --- nothing is added, reworded or cut short -----------------------------------------------

    [Fact]
    public void TheWholeReplyIsSpokenAndNothingIsAddedOrDropped()
    {
        const string reply =
            "Factorio is running and has been up for eleven days without a restart. "
            + "Terraria is stopped, and it was stopped deliberately rather than by a crash. "
            + "Nothing else on this host needs attention right now.";

        string[] slices = Chunks(reply, 7);

        Say(slices).Should().HaveCountGreaterThan(1);
        Heard(slices).Should().Be(reply);
    }

    [Fact]
    public void ALongAnswerIsSpokenInFull()
    {
        // Nothing here decides that somebody has heard enough. Cutting a reply short leaves the
        // answer in the room disagreeing with the answer on the screen, and nothing anywhere to say
        // which was which. How long a reply runs is the assistant's to control.
        string reply = string.Join(" ", Enumerable.Repeat("Minecraft is running.", 60));

        Heard(reply).Should().Be(reply);
    }

    [Fact]
    public void WhatIsSpokenIsAlwaysWhatWasWritten()
    {
        // The property that matters: this strips markup and must never reword anything. A surface that
        // paraphrases on the way to being read out says things the assistant did not.
        const string reply = "Minecraft is running. Factorio is stopped. Ketchup is running.";

        Heard(reply).Should().Be(reply);
    }

    // --- a reply that is already whole ----------------------------------------------------------

    [Fact]
    public void AShortDirectAnswerIsSpokenAsItIs() =>
        SpokenSentences.Whole("Yes, it is.").Should().Be("Yes, it is.");

    [Fact]
    public void WholeStripsEmphasisAndCodeMarkers() =>
        SpokenSentences.Whole("**minecraft** is running on `port 25565`")
            .Should().Be("minecraft is running on port 25565");

    [Fact]
    public void WholeDropsAFencedBlock()
    {
        // Flattening it would read a stack trace aloud. It is on the screen, which is where somebody
        // would go to read it.
        const string reply = "It crashed. Here's the tail:\n```\nSegfault at 0x0\nat main()\n```\nI'd restart it.";

        SpokenSentences.Whole(reply).Should().Be("It crashed. Here's the tail: I'd restart it.");
    }

    [Fact]
    public void WholeTurnsListsAndHeadingsIntoSentences() =>
        SpokenSentences.Whole("## Servers\n- minecraft is up\n- factorio is down")
            .Should().Be("Servers minecraft is up factorio is down");

    [Fact]
    public void WholeKeepsThePunctuationSpeechIsPacedOn() =>
        SpokenSentences.Whole("Yes, it's running. It has been up for three hours!")
            .Should().Be("Yes, it's running. It has been up for three hours!");

    [Fact]
    public void WholeSaysALongAnswerInFull()
    {
        string reply = string.Join(" ", Enumerable.Repeat("Minecraft is running.", 60));

        SpokenSentences.Whole(reply).Should().Be(reply);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("```\njust a code block\n```")]
    public void WholeAnswersNothingWorthSayingWithNothing(string? reply) =>
        SpokenSentences.Whole(reply).Should().BeEmpty();
}
