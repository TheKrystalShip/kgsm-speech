

namespace TheKrystalShip.KGSM.Speech.Daemon;

/// <summary>
/// The Kokoro voices this host has, read off the disk.
/// </summary>
/// <remarks>
/// <para>
/// <b>A directory listing, so asking costs nothing and loads nothing.</b> That is what lets a surface
/// offer the picker, validate a name and report what it speaks in without a synthesiser anywhere in
/// the process — the voices sit beside the binary and the names are the filenames.
/// </para>
/// <para>
/// ⚠ <b>Top level only.</b> Kokoro's other languages are in a subdirectory, and walking into it is how
/// a listing of 28 becomes one of 157.
/// </para>
/// </remarks>
internal static class InstalledVoices
{
    /// <summary>Where the <c>.npy</c> voice files live — beside the binary, as they are published.</summary>
    internal static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "voices");

    /// <summary>Every voice on this host, unordered.</summary>
    internal static IEnumerable<string> All()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];

        return System.IO.Directory
            .EnumerateFiles(Directory, "*.npy", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!);
    }

    /// <summary>
    /// The English ones, in the order they are worth choosing from.
    /// </summary>
    /// <remarks>
    /// Kokoro's other languages expect text in those languages — offered here they would be twenty-odd
    /// ways to read an English answer badly. <see cref="Find"/> still accepts them, because refusing a
    /// voice the host has would be this deciding something it has no business deciding.
    /// </remarks>
    internal static IReadOnlyList<string> Offered()
    {
        var order = SpeechVoices.Preferred
            .Select((name, at) => (name, at))
            .ToDictionary(x => x.name, x => x.at, StringComparer.OrdinalIgnoreCase);

        return All()
            .Where(n => n.Length > 1 && n[0] is 'a' or 'b')
            // A voice the preference list has never heard of sorts last rather than vanishing: this
            // decides what is suggested first, never what may be used.
            .OrderBy(n => order.TryGetValue(n, out int at) ? at : int.MaxValue)
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The file for <paramref name="name"/>, or null when this host does not have that voice.
    /// </summary>
    /// <remarks>
    /// Matched against the listing rather than composed into a path: a voice name is configuration,
    /// and configuration that reaches the filesystem unchecked is how <c>..</c> gets read as a voice.
    /// </remarks>
    internal static string? Find(string? name)
    {
        string wanted = (name ?? string.Empty).Trim();
        if (wanted.Length == 0) return null;

        string? file = All().FirstOrDefault(n => n.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        return file is null ? null : Path.Combine(Directory, file + ".npy");
    }
}
