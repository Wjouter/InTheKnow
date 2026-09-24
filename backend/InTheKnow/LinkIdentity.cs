using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace InTheKnow;

// Calendar identity per link (research R12, investigation.md §1). Keys can contain the link token,
// so only their hash is stored and they are never logged.
static partial class LinkIdentity
{
    [GeneratedRegex("^[0-9a-fA-F]+$")] private static partial Regex HexMarker();

    public static string Normalise(Uri u) => Build(u, QueryParts(u));

    public static (string Key, bool MyTimetablesDerived) KeyFor(Uri u, Settings s)
    {
        var host = u.IdnHost.ToLowerInvariant();
        var f = s.FeaturesFor(host);
        var parts = QueryParts(u);
        string? Param(string name) => parts.FirstOrDefault(p => p.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

        var timetable = Param("timetable");
        if (f is null || !u.AbsolutePath.Equals("/ical", StringComparison.OrdinalIgnoreCase)
            || timetable is null || Param("eu") is null || Param("h") is null
            || Decode(timetable) is not { } decoded)
            return (Normalise(u), false);

        if (f.ClassGroupSharing && decoded.Split('!') is [_, "xedule_studentgroup", _])
            return ($"mt|{host}|{decoded}", true);
        if (f.DuplicateLinkMerging)
            return (Build(u, [.. parts.Where(p => !HexMarker().IsMatch(p))]), true);
        return (Normalise(u), false);
    }

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    // Lower-case scheme and host, no default port, path unchanged, query parameters sorted by name then value.
    static string Build(Uri u, List<string> parts)
    {
        var sb = new StringBuilder().Append(u.Scheme.ToLowerInvariant()).Append("://").Append(u.IdnHost.ToLowerInvariant());
        if (!u.IsDefaultPort) sb.Append(':').Append(u.Port);
        sb.Append(u.AbsolutePath);
        if (parts.Count > 0) sb.Append('?').AppendJoin('&', parts);
        return sb.ToString();
    }

    static List<string> QueryParts(Uri u) =>
    [
        .. u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(p => p.Split('=', 2)[0], StringComparer.Ordinal)
            .ThenBy(p => p, StringComparer.Ordinal),
    ];

    // "!" + URL-decoded base64 (live links carry the leading "!"), tolerant of missing padding and of '-'/'_';
    // decodes to "Source!kind!id".
    static string? Decode(string timetable)
    {
        var b64 = Uri.UnescapeDataString(timetable).TrimStart('!').Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            return text.Split('!').Length == 3 ? text : null;
        }
        catch (FormatException) { return null; }
    }
}
