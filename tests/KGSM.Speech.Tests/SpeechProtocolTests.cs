using FluentAssertions;

using TheKrystalShip.KGSM.Speech;

using Xunit;

namespace KGSM.Speech.Tests;

/// <summary>
/// A surface and this daemon are two processes that only ever hear each other through this, so a
/// field written on one side and read at the wrong offset on the other is not a compile error — it is
/// audio that arrives as gibberish, or a length prefix that makes the reader allocate whatever the
/// noise happened to say.
/// </summary>
public class SpeechProtocolTests
{
    [Fact]
    public async Task AFrameComesBackWithItsKindAndItsBytes()
    {
        var pipe = new MemoryStream();
        await SpeechProtocol.WriteAsync(pipe, SpeechProtocol.Kind.Ready, [1, 2, 3]);
        pipe.Position = 0;

        (SpeechProtocol.Kind Kind, byte[] Payload)? frame = await SpeechProtocol.ReadAsync(pipe);

        frame.Should().NotBeNull();
        frame!.Value.Kind.Should().Be(SpeechProtocol.Kind.Ready);
        frame.Value.Payload.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task FramesQueuedTogetherAreReadOneAtATime()
    {
        // The real stream carries several: an utterance finishing while an answer is being synthesised
        // puts two messages in flight, and a reader that took the whole buffer as one would lose both.
        var pipe = new MemoryStream();
        await SpeechProtocol.WriteAsync(pipe, SpeechProtocol.Kind.Transcribe, [7]);
        await SpeechProtocol.WriteAsync(pipe, SpeechProtocol.Kind.Synthesize, [8, 9]);
        pipe.Position = 0;

        (await SpeechProtocol.ReadAsync(pipe))!.Value.Payload.Should().Equal(7);
        (await SpeechProtocol.ReadAsync(pipe))!.Value.Payload.Should().Equal(8, 9);
    }

    [Fact]
    public async Task AClosedConnectionIsNoMessageRatherThanAFailure() =>
        // How both ends find out the other has gone: a surface stopping, or this daemon idling out.
        // Neither is an error worth throwing.
        (await SpeechProtocol.ReadAsync(new MemoryStream())).Should().BeNull();

    [Fact]
    public async Task AHalfWrittenFrameIsNotReadAsAWholeOne()
    {
        var pipe = new MemoryStream();
        await SpeechProtocol.WriteAsync(pipe, SpeechProtocol.Kind.Synthesized, [1, 2, 3, 4, 5, 6]);

        // Cut short, as a connection dropping mid-message leaves it.
        var truncated = new MemoryStream(pipe.ToArray()[..8]);

        (await SpeechProtocol.ReadAsync(truncated)).Should().BeNull();
    }

    [Fact]
    public async Task AFrameClaimingAnAbsurdLengthIsRefused()
    {
        // A desynchronised stream reads noise as a length. Refusing beats allocating what it said.
        var pipe = new MemoryStream();
        pipe.Write(BitConverter.GetBytes(int.MaxValue));
        pipe.WriteByte((byte)SpeechProtocol.Kind.Transcribe);
        pipe.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(async () => await SpeechProtocol.ReadAsync(pipe));
    }

    [Fact]
    public void EveryAnswerCarriesItsRequestsIdInTheSamePlace()
    {
        // This is what lets a client route an answer without knowing what it is an answer to, and what
        // makes replies safe to return out of order — which they are, because recognition and
        // synthesis run at the same time.
        SpeechProtocol.IdOf(SpeechProtocol.Ready(11, true, true, "x")).Should().Be(11u);
        SpeechProtocol.IdOf(SpeechProtocol.Transcribed(12, SpeechProtocol.Outcome.Done, "x")).Should().Be(12u);
        SpeechProtocol.IdOf(SpeechProtocol.Synthesized(13, SpeechProtocol.Outcome.Done, [1])).Should().Be(13u);
        SpeechProtocol.IdOf(SpeechProtocol.VoiceList(14, SpeechProtocol.Outcome.Done, "af_heart", ["af_heart"]))
            .Should().Be(14u);
    }

    [Fact]
    public void ReadyCarriesWhatTheDaemonTurnedOutToBeAbleToDo()
    {
        (uint id, bool canHear, bool canSpeak, string detail) = SpeechProtocol.ReadReady(
            SpeechProtocol.Ready(5, canHear: true, canSpeak: false, "ggml-small.en.bin on the GPU"));

        id.Should().Be(5);
        canHear.Should().BeTrue();
        canSpeak.Should().BeFalse();
        detail.Should().Be("ggml-small.en.bin on the GPU");
    }

    [Fact]
    public void AnUtteranceSurvivesTheRoundTrip()
    {
        byte[] audio = Enumerable.Range(0, 32000).Select(i => (byte)(i % 251)).ToArray();

        (uint id, bool ifIdle, string vocabulary, byte[] back) = SpeechProtocol.ReadTranscribe(
            SpeechProtocol.Transcribe(42, ifIdle: true, "factorio, terraria", audio));

        id.Should().Be(42);
        ifIdle.Should().BeTrue();
        vocabulary.Should().Be("factorio, terraria");
        back.Should().Equal(audio);
    }

    [Fact]
    public void AnUtteranceWithNoNamesToExpectIsStillReadCorrectly()
    {
        // Priming is a caller's choice, so the empty string is the ordinary case on plenty of hosts —
        // and a zero-length field is exactly where an offset bug hides.
        (_, bool ifIdle, string vocabulary, byte[] audio) =
            SpeechProtocol.ReadTranscribe(SpeechProtocol.Transcribe(1, ifIdle: false, string.Empty, [9, 8, 7]));

        ifIdle.Should().BeFalse();
        vocabulary.Should().BeEmpty();
        audio.Should().Equal(9, 8, 7);
    }

    [Theory]
    // The outcome travels as its number, so the cases are written as numbers: this is the one place
    // where reading the byte as the wrong member would be silent, and "busy" arriving as "done" is an
    // utterance nobody was ever told was skipped.
    [InlineData(0, "restart factorio")]
    [InlineData(1, "")]
    [InlineData(2, "")]
    [InlineData(3, "")]
    public void ATranscriptComesBackWithHowItWent(byte outcome, string text)
    {
        (uint id, SpeechProtocol.Outcome came, string said) = SpeechProtocol.ReadTranscribed(
            SpeechProtocol.Transcribed(7, (SpeechProtocol.Outcome)outcome, text));

        id.Should().Be(7);
        came.Should().Be((SpeechProtocol.Outcome)outcome);
        said.Should().Be(text);
    }

    [Fact]
    public void WhatWasSaidSurvivesBeingSaidInAnotherAlphabet()
    {
        // The transcript is whatever whisper produced and the request is whatever somebody typed;
        // neither is ASCII by contract, and a length counted in characters would truncate both.
        const string said = "перезапусти factorio — сейчас";

        SpeechProtocol.ReadTranscribed(SpeechProtocol.Transcribed(1, SpeechProtocol.Outcome.Done, said))
            .Text.Should().Be(said);

        (_, string voice, string text) =
            SpeechProtocol.ReadSynthesize(SpeechProtocol.Synthesize(1, "bf_emma", said));

        voice.Should().Be("bf_emma");
        text.Should().Be(said);
    }

    [Fact]
    public void AnAnswerToSayCarriesTheVoiceToSayItIn()
    {
        (uint id, string voice, string text) =
            SpeechProtocol.ReadSynthesize(SpeechProtocol.Synthesize(3, "af_heart", "One moment."));

        id.Should().Be(3);
        voice.Should().Be("af_heart");
        text.Should().Be("One moment.");
    }

    [Fact]
    public void NoVoiceNamedMeansThisHostsVoice()
    {
        // What every surface should send: the point of the host owning the voice is that surfaces
        // agree on it without coordinating, and an empty name is how they ask for it.
        (_, string voice, string text) =
            SpeechProtocol.ReadSynthesize(SpeechProtocol.Synthesize(1, string.Empty, "One moment."));

        voice.Should().BeEmpty();
        text.Should().Be("One moment.");
    }

    [Fact]
    public void SynthesisedAudioComesBackWhole()
    {
        // Ten seconds of 24kHz mono, which is a long answer and the size this actually carries.
        byte[] audio = new byte[24000 * 2 * 10];
        Random.Shared.NextBytes(audio);

        (uint id, SpeechProtocol.Outcome outcome, byte[] back) = SpeechProtocol.ReadSynthesized(
            SpeechProtocol.Synthesized(11, SpeechProtocol.Outcome.Done, audio));

        id.Should().Be(11);
        outcome.Should().Be(SpeechProtocol.Outcome.Done);
        back.Should().Equal(audio);
    }

    [Fact]
    public void NothingSynthesisedIsAnEmptyPayloadRatherThanAMissingOne()
    {
        (_, SpeechProtocol.Outcome outcome, byte[] audio) = SpeechProtocol.ReadSynthesized(
            SpeechProtocol.Synthesized(2, SpeechProtocol.Outcome.Unavailable, []));

        outcome.Should().Be(SpeechProtocol.Outcome.Unavailable);
        audio.Should().BeEmpty();
    }

    [Fact]
    public void TheVoiceListCarriesWhatThereIsAndWhichOneIsSpeaking()
    {
        (uint id, SpeechProtocol.Outcome outcome, string speaking, IReadOnlyList<string> voices) =
            SpeechProtocol.ReadVoiceList(SpeechProtocol.VoiceList(
                9, SpeechProtocol.Outcome.Done, "af_heart", ["af_heart", "bf_emma", "am_fenrir"]));

        id.Should().Be(9);
        outcome.Should().Be(SpeechProtocol.Outcome.Done);
        speaking.Should().Be("af_heart");
        voices.Should().Equal("af_heart", "bf_emma", "am_fenrir");
    }

    [Fact]
    public void AHostWithNoVoicesAnswersWithNoneRatherThanWithOneBlankName()
    {
        // Splitting an empty string yields one empty entry, which would render as a nameless voice in
        // every picker on the host.
        (_, _, string speaking, IReadOnlyList<string> voices) =
            SpeechProtocol.ReadVoiceList(SpeechProtocol.VoiceList(
                1, SpeechProtocol.Outcome.Unavailable, string.Empty, []));

        speaking.Should().BeEmpty();
        voices.Should().BeEmpty();
    }

    [Fact]
    public void AskingToSpeakAsAVoiceCarriesItsName()
    {
        (uint id, string voice) = SpeechProtocol.ReadSpeakAs(SpeechProtocol.SpeakAs(4, "bf_emma"));

        id.Should().Be(4);
        voice.Should().Be("bf_emma");
    }

    [Fact]
    public void WakingCarriesNothingButItsId() =>
        SpeechProtocol.IdOf(SpeechProtocol.Wake(77)).Should().Be(77u);
}
