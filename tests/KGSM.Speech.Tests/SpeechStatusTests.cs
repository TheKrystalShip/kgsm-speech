using FluentAssertions;

using TheKrystalShip.KGSM.Speech;

using Xunit;

namespace KGSM.Speech.Tests;

/// <summary>
/// The status report crosses the same two-process gap as everything else on this wire, and it is the
/// one message whose whole job is to be believed: a panel renders it as fact, so a field that comes
/// back as a plausible zero instead of "not measured" is a fabricated measurement with a page built
/// on top of it.
/// </summary>
public class SpeechStatusTests
{
    [Fact]
    public void EverythingMeasuredSurvivesTheRoundTrip()
    {
        var started = DateTimeOffset.Parse("2026-08-15T09:00:00Z");

        var status = new SpeechStatus
        {
            StartedAt = started,
            Loaded = true,
            LoadedAt = started.AddSeconds(12),
            LoadMilliseconds = 3184,
            IdleMinutes = 5,
            LastAskedAt = started.AddMinutes(3),
            UnloadsAt = started.AddMinutes(8),
            Surfaces = ["kgsm-bot", "kgsm-assistant"],
            SpeakingVoice = "bm_george",
            ConfiguredVoice = "af_heart",
            InstalledVoices = 28,
            Hearing = new SpeechLane
            {
                Available = true,
                Detail = "ggml-small.en.bin on the GPU",
                Model = "ggml-small.en.bin",
                ModelBytes = 487_601_984,
                Runtime = "gpu",
                Busy = true,
                Waiting = 2,
                Done = 41,
                Rejected = 3,
                Failed = 1,
                AudioSeconds = 128.5,
                LastMilliseconds = 240,
                MeanMilliseconds = 310,
                P95Milliseconds = 900,
                LastAt = started.AddMinutes(3),
                LastOutcome = "done",
            },
            Speaking = new SpeechLane
            {
                Available = false,
                Detail = "no model at '/var/lib/kgsm-speech/models/kokoro.onnx'",
                Model = "kokoro.onnx",
                Runtime = "cpu",
                Characters = 4096,
            },
        };

        SpeechStatus read = SpeechStatus.Parse(status.ToText());

        read.Should().BeEquivalentTo(status);
    }

    [Fact]
    public void AVoiceChangedOnTheRunningDaemonReadsAsAnOverride()
    {
        // The one divergence nothing else on this host surfaces: SpeakAs changes the voice for every
        // surface until the daemon restarts and is never written back, so the configured value is what
        // the next process will speak in and the panel has to be able to say both.
        var overridden = new SpeechStatus { SpeakingVoice = "bm_george", ConfiguredVoice = "af_heart" };
        var agreeing = new SpeechStatus { SpeakingVoice = "af_heart", ConfiguredVoice = "af_heart" };
        var untouched = new SpeechStatus();

        overridden.VoiceOverridden.Should().BeTrue();
        agreeing.VoiceOverridden.Should().BeFalse();

        // Neither known is a daemon that has not been asked to say anything, which is not a mismatch.
        untouched.VoiceOverridden.Should().BeFalse();
    }

    [Fact]
    public void AKeyFromANewerDaemonIsSkippedRatherThanRefused()
    {
        // A surface is deployed on its own schedule and will meet a daemon newer than itself. It loses
        // the field it never knew about and keeps every one it did.
        SpeechStatus read = SpeechStatus.Parse(
            "loaded=true\nvoice.speaking=af_heart\nsomethingNobodyHasWrittenYet=42\n"
            + "hear.available=true\nhear.futureThing=x\n");

        read.Loaded.Should().BeTrue();
        read.SpeakingVoice.Should().Be("af_heart");
        read.Hearing.Available.Should().BeTrue();
    }

    [Fact]
    public void NothingMeasuredComesBackAsNothingRatherThanAsZero()
    {
        // A daemon that has not loaded its models has no durations to report. Null is what makes a
        // surface say "not measured" instead of drawing a 0ms pass that never happened.
        SpeechStatus read = SpeechStatus.Parse(new SpeechStatus().ToText());

        read.LoadMilliseconds.Should().BeNull();
        read.LoadedAt.Should().BeNull();
        read.UnloadsAt.Should().BeNull();
        read.Hearing.LastMilliseconds.Should().BeNull();
        read.Hearing.P95Milliseconds.Should().BeNull();
        read.Hearing.LastAt.Should().BeNull();
        read.Hearing.RealtimeFactor.Should().BeNull();
        read.Surfaces.Should().BeEmpty();
    }

    [Fact]
    public void AudioPerSecondOfWorkIsTheMeanOverThePassesThatRan()
    {
        // 20 seconds of audio across 4 passes averaging 250ms is 5s of audio per pass in a quarter of a
        // second — twenty times faster than the person talking, which is what a card looks like.
        var lane = new SpeechLane { Done = 4, AudioSeconds = 20, MeanMilliseconds = 250 };

        lane.RealtimeFactor.Should().BeApproximately(20, 0.001);
    }
}
