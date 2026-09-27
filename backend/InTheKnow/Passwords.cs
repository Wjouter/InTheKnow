using System.Collections.Frozen;

namespace InTheKnow;

// The password rules of FR-007 (specs/004 research R4). Length is counted in UTF-16 code units, as entered.
static class Passwords
{
    // The embedded SecLists-derived list: entries of 12 to 128 characters, lowercased. Lines starting with # are comments.
    static readonly FrozenSet<string> Common = Load();

    public static string? Check(string? password, string email) =>
        password is not { Length: >= 12 and <= 128 } ? "password_length"
        : Common.Contains(password.ToLowerInvariant()) ? "password_common"
        : string.Equals(password, email.Trim(), StringComparison.OrdinalIgnoreCase) ? "password_is_email"
        : null;

    static FrozenSet<string> Load()
    {
        var asm = typeof(Passwords).Assembly;
        using var reader = new StreamReader(asm.GetManifestResourceStream(asm.GetManifestResourceNames().Single(n => n.EndsWith("common-passwords.txt")))!);
        var words = new List<string>();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && !line.StartsWith('#')) words.Add(line);
        return words.ToFrozenSet(StringComparer.Ordinal);
    }
}
