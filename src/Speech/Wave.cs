namespace TheKrystalShip.KGSM.Speech;

/// <summary>
/// Puts a RIFF header on raw samples.
/// </summary>
/// <remarks>
/// <para>
/// <b>44 bytes and one copy — no encoding happens here.</b> WAV is a container around exactly the
/// samples that went in, which is what makes it the cheap format to offer: a browser decodes it with
/// no library, and the daemon spends nothing producing it.
/// </para>
/// <para>
/// It is deliberately not the smallest thing that could go over a wire — a spoken sentence is around
/// 120KB where Opus would be 7KB. That trade is worth stating: this costs bandwidth to save a codec,
/// a container muxer and a dependency, and the format is negotiated per request so a smaller one can
/// be added on the daemon's side alone.
/// </para>
/// </remarks>
public static class Wave
{
    /// <summary>The header's fixed size — what a reader must skip to reach the samples.</summary>
    public const int HeaderBytes = 44;

    /// <summary>
    /// Wraps signed 16-bit little-endian mono samples at <paramref name="sampleRate"/>.
    /// </summary>
    /// <remarks>
    /// The defaults are what Kokoro produces, so a caller wrapping this daemon's output passes
    /// nothing. Nothing is resampled and nothing is converted: a rate that disagrees with the samples
    /// makes a file that plays at the wrong speed, which is the caller's to get right.
    /// </remarks>
    public static byte[] Mono16(byte[] samples, int sampleRate = 24000)
    {
        ArgumentNullException.ThrowIfNull(samples);

        const short Channels = 1;
        const short BitsPerSample = 16;

        byte[] wav = new byte[HeaderBytes + samples.Length];
        var at = new Writer(wav);

        at.Ascii("RIFF");
        at.Int32(36 + samples.Length);          // everything after this field
        at.Ascii("WAVE");
        at.Ascii("fmt ");
        at.Int32(16);                           // PCM header length
        at.Int16(1);                            // uncompressed
        at.Int16(Channels);
        at.Int32(sampleRate);
        at.Int32(sampleRate * Channels * BitsPerSample / 8);   // byte rate
        at.Int16(Channels * BitsPerSample / 8);                // block align
        at.Int16(BitsPerSample);
        at.Ascii("data");
        at.Int32(samples.Length);

        samples.CopyTo(wav, HeaderBytes);
        return wav;
    }

    /// <summary>A cursor over the header being written, so the field order reads as the format does.</summary>
    private struct Writer(byte[] buffer)
    {
        private int _at = 0;

        public void Ascii(string four)
        {
            for (int i = 0; i < four.Length; i++) buffer[_at++] = (byte)four[i];
        }

        public void Int32(int value)
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(_at, 4), value);
            _at += 4;
        }

        public void Int16(int value)
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(_at, 2), (short)value);
            _at += 2;
        }
    }
}
