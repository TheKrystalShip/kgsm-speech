using System.Text;

namespace TheKrystalShip.KGSM.Speech;

/// <summary>
/// The words this host is likely to hear that a general recogniser has no reason to produce.
/// </summary>
/// <remarks>
/// <para>
/// <b>A recogniser knows English, not this host.</b> Whisper was measured here turning the server
/// <c>Ketchup</c> into "catch-up" — not a mistake about the sound, which is right, but about which of
/// the plausible spellings was meant. It cannot prefer the right one without being told the name
/// exists, and no amount of audio quality fixes that.
/// </para>
/// <para>
/// <b>Composed here, for everybody, and for the same reason <see cref="SpokenSentences"/> is.</b> A
/// person asks the same host about the same servers from a voice channel and from a browser, so the
/// names to expect are the host's rather than any one surface's — and two surfaces composing them
/// separately mishear different things. What a surface still owns is <em>when</em> to refresh the
/// list and where the names come from.
/// </para>
/// <para>
/// <b>The fix is prior context, and it belongs at the recogniser.</b> Whisper accepts text to
/// condition on, as though it were the transcript of what came immediately before. Naming the servers
/// there shifts the odds toward spelling them the way this host does. That is the whole mechanism:
/// nothing downstream rewrites anybody's words, so a request still says what the person said — the
/// alternative, correcting a misheard name after the fact, means guessing which server somebody meant
/// and acting on the guess.
/// </para>
/// <para>
/// <b>Bounded, because the window is.</b> Whisper reserves about half its text context for this and
/// truncates from the front, so an unbounded list silently drops the names at the start — and a long
/// one crowds out what was actually said. Instances come before blueprints: an instance is what people
/// name in a request, where a blueprint is named once when something is installed.
/// </para>
/// </remarks>
public static class SpokenVocabulary
{
    /// <summary>
    /// How much prior context to spend, in characters.
    /// </summary>
    /// <remarks>
    /// Whisper's prompt window is counted in tokens — around 224 of them — and English averages
    /// something near four characters to a token. This is deliberately well under the ceiling: the
    /// budget is a bound on crowding as much as on truncation, and the names worth having are the
    /// first few dozen.
    /// </remarks>
    public const int DefaultBudget = 600;

    /// <summary>
    /// The shortest run of words that can be dismissed as the recogniser echoing its own context.
    /// </summary>
    /// <remarks>
    /// A single name is a real thing to say — "minecraft" is the whole answer to which server somebody
    /// meant — so matching the context is not on its own evidence of an echo. Three words in the order
    /// they were given is.
    /// </remarks>
    private const int EchoWordFloor = 3;

    /// <summary>
    /// Composes the prior context, or an empty string when there is nothing worth conditioning on.
    /// </summary>
    /// <remarks>
    /// Written as sentences rather than as a bare list because that is what it is pretending to be —
    /// the transcript of a moment ago. The trigger phrase leads: it is the one thing said in every
    /// request, and a request whose trigger was misheard is not a request at all.
    /// </remarks>
    public static string Compose(
        IEnumerable<string> triggers,
        IEnumerable<string> instances,
        IEnumerable<string> blueprints,
        int budget = DefaultBudget)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();

        foreach (string trigger in Clean(triggers, seen))
        {
            if (builder.Length + trigger.Length + 2 > budget) break;
            builder.Append(trigger).Append(". ");
        }

        Append(builder, "The servers are", Clean(instances, seen), budget);
        Append(builder, "The games are", Clean(blueprints, seen), budget);

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Whether a transcript is the recogniser handing back the context it was given.
    /// </summary>
    /// <remarks>
    /// Conditioning on text has a documented failure of its own: given audio with nothing recognisable
    /// in it, whisper sometimes continues the context instead of returning nothing. That arrives here
    /// looking exactly like speech, and a run of server names is a plausible enough request to be
    /// worth answering — so it is caught by what it is, a span of the context reproduced in order.
    /// </remarks>
    public static bool IsEchoOf(string? transcript, string? context)
    {
        if (string.IsNullOrWhiteSpace(transcript) || string.IsNullOrWhiteSpace(context)) return false;

        string said = Normalise(transcript);
        if (said.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < EchoWordFloor) return false;

        return Normalise(context).Contains(said, StringComparison.Ordinal);
    }

    private static void Append(StringBuilder builder, string lead, List<string> names, int budget)
    {
        if (names.Count == 0) return;

        int opened = builder.Length;
        builder.Append(lead).Append(' ');

        var written = 0;
        foreach (string name in names)
        {
            // +2 covers the separator and the sentence's full stop, so the budget is never overshot
            // by the punctuation that has to follow the last name written.
            if (builder.Length + name.Length + 2 > budget) break;
            if (written > 0) builder.Append(", ");
            builder.Append(name);
            written++;
        }

        // A lead-in with nothing after it is a sentence about no servers, which is worse than silence
        // — it is prior context suggesting the host is empty.
        if (written == 0) builder.Length = opened;
        else builder.Append(". ");
    }

    /// <summary>Names worth conditioning on, in order, without repeating one already taken.</summary>
    private static List<string> Clean(IEnumerable<string> names, HashSet<string> seen)
    {
        var kept = new List<string>();
        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string trimmed = name.Trim();
            if (seen.Add(trimmed)) kept.Add(trimmed);
        }

        return kept;
    }

    /// <summary>Reduces text to the words in it, so punctuation and casing cannot hide a match.</summary>
    private static string Normalise(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = true;

        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (!space)
            {
                builder.Append(' ');
                space = true;
            }
        }

        return builder.ToString().Trim();
    }
}
