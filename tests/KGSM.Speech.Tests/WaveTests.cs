using System.Text;

using FluentAssertions;

using TheKrystalShip.KGSM.Speech;

using Xunit;

namespace KGSM.Speech.Tests;

/// <summary>
/// The header is what makes a browser able to play what this daemon produces, and every field in it
/// is a number a decoder trusts without checking. A wrong byte rate is not a decode error — it is
/// audio that plays at the wrong speed, which sounds like a bad model rather than a bad header.
/// </summary>
public class WaveTests
{
    private static readonly byte[] Samples = Enumerable.Range(0, 4800).Select(i => (byte)(i % 251)).ToArray();

    [Fact]
    public void TheSamplesAreCarriedUntouchedAfterTheHeader()
    {
        byte[] wav = Wave.Mono16(Samples);

        wav.Length.Should().Be(Wave.HeaderBytes + Samples.Length);
        wav[Wave.HeaderBytes..].Should().Equal(Samples);
    }

    [Fact]
    public void ItIsARiffWaveFile()
    {
        byte[] wav = Wave.Mono16(Samples);

        Encoding.ASCII.GetString(wav, 0, 4).Should().Be("RIFF");
        Encoding.ASCII.GetString(wav, 8, 4).Should().Be("WAVE");
        Encoding.ASCII.GetString(wav, 12, 4).Should().Be("fmt ");
        Encoding.ASCII.GetString(wav, 36, 4).Should().Be("data");
    }

    [Fact]
    public void TheTwoLengthsDescribeTheSameFile()
    {
        // A RIFF size that disagrees with the data size is the classic truncated-playback bug: some
        // decoders honour one and some the other.
        byte[] wav = Wave.Mono16(Samples);

        BitConverter.ToInt32(wav, 4).Should().Be(36 + Samples.Length);
        BitConverter.ToInt32(wav, 40).Should().Be(Samples.Length);
    }

    [Fact]
    public void ItDeclaresUncompressedMonoSixteenBit()
    {
        byte[] wav = Wave.Mono16(Samples);

        BitConverter.ToInt16(wav, 20).Should().Be(1, "uncompressed PCM");
        BitConverter.ToInt16(wav, 22).Should().Be(1, "mono");
        BitConverter.ToInt16(wav, 34).Should().Be(16, "16-bit samples");
    }

    [Fact]
    public void TheRateFieldsAgreeWithEachOther()
    {
        // Kokoro's rate. Byte rate and block align are derivable, which is exactly why they are worth
        // asserting: a decoder uses them rather than recomputing, so a wrong one plays at the wrong speed.
        byte[] wav = Wave.Mono16(Samples);

        BitConverter.ToInt32(wav, 24).Should().Be(24000, "sample rate");
        BitConverter.ToInt32(wav, 28).Should().Be(24000 * 2, "byte rate = rate x channels x bytes");
        BitConverter.ToInt16(wav, 32).Should().Be(2, "block align = channels x bytes");
    }

    [Fact]
    public void ARateThatIsNotTheDefaultIsWrittenThrough()
    {
        byte[] wav = Wave.Mono16(Samples, sampleRate: 16000);

        BitConverter.ToInt32(wav, 24).Should().Be(16000);
        BitConverter.ToInt32(wav, 28).Should().Be(16000 * 2);
    }

    [Fact]
    public void NoSamplesIsStillAValidFile()
    {
        // What an unavailable synthesiser returns. A header claiming a length it does not have is
        // worse than an empty file, which a decoder simply plays as nothing.
        byte[] wav = Wave.Mono16([]);

        wav.Length.Should().Be(Wave.HeaderBytes);
        BitConverter.ToInt32(wav, 40).Should().Be(0);
    }
}
