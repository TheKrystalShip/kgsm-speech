using System.Text;

namespace TheKrystalShip.KGSM.Speech;

/// <summary>
/// What a surface and the speech daemon say to each other.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four questions and their answers.</b> Load the models, read this audio, say these words, and
/// what voices are there. The daemon holds no per-client state beyond the models themselves — the
/// names to expect travel with each utterance and the voice to speak in is the host's, so a client
/// that reconnects after the daemon idled out is answered exactly as one that never left.
/// </para>
/// <para>
/// <b>Every message carries an id and replies may arrive out of order.</b> Recognition and synthesis
/// run at the same time in there, and more than one surface may be asking at once — a protocol that
/// made the connection a queue would serialise work that is not.
/// </para>
/// <para>
/// <b>Audio is 16-bit signed little-endian PCM in both directions</b>, mono: 16kHz going in, as
/// whisper wants it, and 24kHz coming back, as Kokoro produces it. Converting to whatever a surface
/// plays is the surface's own job, and it keeps four times the bytes off this wire.
/// </para>
/// </remarks>
public static class SpeechProtocol
{
    /// <summary>
    /// The largest frame either end will read.
    /// </summary>
    /// <remarks>
    /// Audio is the only thing here with any size to it: twenty seconds of 16kHz mono is 640KB, and a
    /// spoken answer a few times that. This is not a tuning knob — it is the point past which a length
    /// prefix is more likely to be a desynchronised stream than a real message, and honouring it would
    /// mean allocating whatever the noise said.
    /// </remarks>
    internal const int LargestFrame = 32 * 1024 * 1024;

    /// <summary>The kind of message a frame carries.</summary>
    public enum Kind : byte
    {
        /// <summary>Load the models now — speech is about to be wanted. Answered with <see cref="Ready"/>.</summary>
        Wake = 1,

        /// <summary>What the daemon turned out to be able to do, once loaded.</summary>
        Ready = 2,

        /// <summary>Read this audio.</summary>
        Transcribe = 3,

        /// <summary>What was heard in it.</summary>
        Transcribed = 4,

        /// <summary>Say these words, as raw PCM.</summary>
        /// <remarks>
        /// The shorthand, and the right message for a caller that plays audio itself: a Discord
        /// connection takes PCM and would only have to unwrap a container to get back to it.
        /// </remarks>
        Synthesize = 5,

        /// <summary>The audio of them.</summary>
        Synthesized = 6,

        /// <summary>What voices this host has, and which one it speaks in.</summary>
        Voices = 7,

        /// <summary>The answer to either <see cref="Voices"/> or <see cref="SpeakAs"/>.</summary>
        VoiceList = 8,

        /// <summary>Speak in this voice from now on — this host's voice, on every surface.</summary>
        SpeakAs = 9,

        /// <summary>
        /// Say these words in a named <see cref="Format"/> — for a caller that needs a container
        /// rather than raw samples.
        /// </summary>
        /// <remarks>
        /// Its own message rather than a field on <see cref="Synthesize"/>, because that one is
        /// already spoken by deployed clients: a shape that changed under them would be misread
        /// rather than refused. A caller that wants PCM keeps sending the shorthand forever.
        /// </remarks>
        SynthesizeAs = 10,

        /// <summary>
        /// What this daemon is doing right now — for a surface reporting on it rather than using it.
        /// </summary>
        /// <remarks>
        /// ⚠ <b>The one message that does not count as being asked.</b> Everything else here pushes the
        /// idle deadline out, because everything else is somebody using this. A panel polling for a
        /// status is not, and treating it as such would hold 1.6GB of models resident for as long as
        /// anybody had the page open.
        /// </remarks>
        Status = 11,

        /// <summary>The answer to <see cref="Status"/>.</summary>
        Reported = 12,
    }

    /// <summary>
    /// What a caller wants the audio wrapped in.
    /// </summary>
    /// <remarks>
    /// Negotiated per request rather than configured, because two surfaces on one host want different
    /// things at the same moment: a voice connection plays samples, and a browser needs something
    /// <c>decodeAudioData</c> understands. The daemon synthesises once either way — a format is a
    /// wrapper applied on the way out, not a second synthesis.
    /// </remarks>
    public enum Format : byte
    {
        /// <summary>24kHz mono signed 16-bit little-endian samples, exactly as Kokoro produces them.</summary>
        Pcm = 0,

        /// <summary>The same samples behind a 44-byte RIFF header — what a browser can decode as-is.</summary>
        Wav = 1,
    }

    /// <summary>Why a request came back without what was asked for.</summary>
    public enum Outcome : byte
    {
        /// <summary>It worked. For recognition, empty text means nothing was said, which is normal.</summary>
        Done = 0,

        /// <summary>
        /// The recogniser was already running and the caller said not to wait. Distinct from
        /// <see cref="Done"/> with no text, because one means "not heard" and this means "not tried".
        /// </summary>
        Busy = 1,

        /// <summary>There is no model loaded to ask — a misconfigured host, not a failed request.</summary>
        Unavailable = 2,

        /// <summary>It was attempted and it threw. The daemon logs the detail; the caller carries on.</summary>
        Failed = 3,
    }

    /// <summary>Reads one frame, or null when the other end has gone away.</summary>
    /// <remarks>
    /// A closed connection is the ordinary end of both processes' interest in each other — a surface
    /// stopping, or the daemon idling out — so it is reported as an absence of a message rather than
    /// as an error either end has to catch.
    /// </remarks>
    public static async Task<(Kind Kind, byte[] Payload)?> ReadAsync(
        Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[5];
        if (!await FillAsync(stream, header, ct).ConfigureAwait(false)) return null;

        int length = BitConverter.ToInt32(header, 0);
        if (length < 0 || length > LargestFrame)
            throw new InvalidDataException($"A speech frame claimed to be {length} bytes.");

        byte[] payload = new byte[length];
        if (length > 0 && !await FillAsync(stream, payload, ct).ConfigureAwait(false)) return null;

        return ((Kind)header[4], payload);
    }

    /// <summary>Writes one frame whole, so two writers cannot interleave halves of two messages.</summary>
    public static async Task WriteAsync(
        Stream stream, Kind kind, byte[] payload, CancellationToken ct = default)
    {
        byte[] frame = new byte[5 + payload.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), payload.Length);
        frame[4] = (byte)kind;
        payload.CopyTo(frame, 5);

        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int at = 0;
        while (at < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(at), ct).ConfigureAwait(false);
            if (read == 0) return false;
            at += read;
        }

        return true;
    }

    // The payload encodings. Deliberately hand-written rather than serialised: there are nine of them,
    // audio as a length-prefixed byte array costs one copy where a serialiser would cost several, and
    // a contract this small is easier to read as offsets than as attributes.

    /// <summary>The id every request carries and its answer echoes back.</summary>
    public static uint IdOf(byte[] payload) => BitConverter.ToUInt32(payload, 0);

    public static byte[] Wake(uint id) => BitConverter.GetBytes(id);

    public static byte[] Ready(uint id, bool canHear, bool canSpeak, string detail)
    {
        byte[] text = Encoding.UTF8.GetBytes(detail);
        byte[] payload = new byte[6 + text.Length];

        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), id);
        payload[4] = canHear ? (byte)1 : (byte)0;
        payload[5] = canSpeak ? (byte)1 : (byte)0;
        text.CopyTo(payload, 6);

        return payload;
    }

    public static (uint Id, bool CanHear, bool CanSpeak, string Detail) ReadReady(byte[] payload) =>
        (IdOf(payload), payload[4] == 1, payload[5] == 1,
         Encoding.UTF8.GetString(payload, 6, payload.Length - 6));

    public static byte[] Transcribe(uint id, bool ifIdle, string vocabulary, byte[] audio)
    {
        byte[] words = Encoding.UTF8.GetBytes(vocabulary);
        byte[] payload = new byte[4 + 1 + 4 + words.Length + audio.Length];

        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), id);
        payload[4] = ifIdle ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(payload.AsSpan(5, 4), words.Length);
        words.CopyTo(payload, 9);
        audio.CopyTo(payload, 9 + words.Length);

        return payload;
    }

    public static (uint Id, bool IfIdle, string Vocabulary, byte[] Audio) ReadTranscribe(byte[] payload)
    {
        int words = BitConverter.ToInt32(payload, 5);

        return (IdOf(payload), payload[4] == 1,
            Encoding.UTF8.GetString(payload, 9, words),
            payload[(9 + words)..]);
    }

    public static byte[] Transcribed(uint id, Outcome outcome, string text)
    {
        byte[] said = Encoding.UTF8.GetBytes(text);
        byte[] payload = new byte[5 + said.Length];

        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), id);
        payload[4] = (byte)outcome;
        said.CopyTo(payload, 5);

        return payload;
    }

    public static (uint Id, Outcome Outcome, string Text) ReadTranscribed(byte[] payload) =>
        (IdOf(payload), (Outcome)payload[4], Encoding.UTF8.GetString(payload, 5, payload.Length - 5));

    /// <summary>
    /// Words to say, in <paramref name="voice"/> — or in this host's own voice when that is empty,
    /// which is what every surface should send unless it has a reason not to.
    /// </summary>
    public static byte[] Synthesize(uint id, string voice, string text)
    {
        byte[] named = Encoding.UTF8.GetBytes(voice);
        byte[] said = Encoding.UTF8.GetBytes(text);
        byte[] payload = new byte[4 + 4 + named.Length + said.Length];

        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), id);
        BitConverter.TryWriteBytes(payload.AsSpan(4, 4), named.Length);
        named.CopyTo(payload, 8);
        said.CopyTo(payload, 8 + named.Length);

        return payload;
    }

    public static (uint Id, string Voice, string Text) ReadSynthesize(byte[] payload)
    {
        int named = BitConverter.ToInt32(payload, 4);

        return (IdOf(payload),
            Encoding.UTF8.GetString(payload, 8, named),
            Encoding.UTF8.GetString(payload, 8 + named, payload.Length - 8 - named));
    }

    /// <summary>Words to say, wrapped in <paramref name="format"/> on the way back.</summary>
    public static byte[] SynthesizeAs(uint id, Format format, string voice, string text)
    {
        byte[] named = Encoding.UTF8.GetBytes(voice);
        byte[] said = Encoding.UTF8.GetBytes(text);
        byte[] payload = new byte[4 + 1 + 4 + named.Length + said.Length];

        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), id);
        payload[4] = (byte)format;
        BitConverter.TryWriteBytes(payload.AsSpan(5, 4), named.Length);
        named.CopyTo(payload, 9);
        said.CopyTo(payload, 9 + named.Length);

        return payload;
    }

    public static (uint Id, Format Format, string Voice, string Text) ReadSynthesizeAs(byte[] payload)
    {
        int named = BitConverter.ToInt32(payload, 5);

        return (IdOf(payload), (Format)payload[4],
            Encoding.UTF8.GetString(payload, 9, named),
            Encoding.UTF8.GetString(payload, 9 + named, payload.Length - 9 - named));
    }

    public static byte[] Synthesized(uint id, Outcome outcome, byte[] audio)
    {
        byte[] payload = new byte[5 + audio.Length];

        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), id);
        payload[4] = (byte)outcome;
        audio.CopyTo(payload, 5);

        return payload;
    }

    public static (uint Id, Outcome Outcome, byte[] Audio) ReadSynthesized(byte[] payload) =>
        (IdOf(payload), (Outcome)payload[4], payload[5..]);

    public static byte[] Voices(uint id) => BitConverter.GetBytes(id);

    public static byte[] Status(uint id) => BitConverter.GetBytes(id);

    /// <summary>
    /// The daemon's own account of itself, as <see cref="SpeechStatus"/>'s <c>key=value</c> text.
    /// </summary>
    /// <remarks>
    /// Text rather than offsets, unlike everything else here: this one carries thirty-odd fields that
    /// grow, and a reader that skips a key it does not know keeps working against a daemon newer than
    /// itself — which a fixed layout cannot do.
    /// </remarks>
    public static byte[] Reported(uint id, string report)
    {
        byte[] text = Encoding.UTF8.GetBytes(report);
        byte[] payload = new byte[4 + text.Length];

        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), id);
        text.CopyTo(payload, 4);

        return payload;
    }

    public static (uint Id, string Report) ReadReported(byte[] payload) =>
        (IdOf(payload), Encoding.UTF8.GetString(payload, 4, payload.Length - 4));

    public static byte[] SpeakAs(uint id, string voice)
    {
        byte[] named = Encoding.UTF8.GetBytes(voice);
        byte[] payload = new byte[4 + named.Length];

        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), id);
        named.CopyTo(payload, 4);

        return payload;
    }

    public static (uint Id, string Voice) ReadSpeakAs(byte[] payload) =>
        (IdOf(payload), Encoding.UTF8.GetString(payload, 4, payload.Length - 4));

    /// <summary>
    /// The voices this host has and the one it is speaking in, as a newline-separated list.
    /// </summary>
    /// <remarks>
    /// A voice name is a filename stem — letters, digits and underscores — so a separator that cannot
    /// occur in one needs no escaping and no length table.
    /// </remarks>
    public static byte[] VoiceList(uint id, Outcome outcome, string speaking, IEnumerable<string> voices)
    {
        byte[] text = Encoding.UTF8.GetBytes(string.Join('\n', voices));
        byte[] current = Encoding.UTF8.GetBytes(speaking);
        byte[] payload = new byte[4 + 1 + 4 + current.Length + text.Length];

        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), id);
        payload[4] = (byte)outcome;
        BitConverter.TryWriteBytes(payload.AsSpan(5, 4), current.Length);
        current.CopyTo(payload, 9);
        text.CopyTo(payload, 9 + current.Length);

        return payload;
    }

    public static (uint Id, Outcome Outcome, string Speaking, IReadOnlyList<string> Voices) ReadVoiceList(
        byte[] payload)
    {
        int current = BitConverter.ToInt32(payload, 5);
        string speaking = Encoding.UTF8.GetString(payload, 9, current);
        string list = Encoding.UTF8.GetString(payload, 9 + current, payload.Length - 9 - current);

        return (IdOf(payload), (Outcome)payload[4], speaking,
            list.Length == 0 ? [] : list.Split('\n'));
    }
}
