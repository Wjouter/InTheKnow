using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Calendar = Ical.Net.Calendar;

namespace InTheKnow;

sealed record Occurrence(string Key, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Status,
                         DateTimeOffset? OccurrenceStart, string? Location, string? Description);

sealed record ParseResult(IReadOnlyList<Occurrence>? Events, string? Error);

// Ical.Net → normalised occurrences with stable keys (research R2, R6, R7, R8, R8a, R10).
static class FeedParser
{
    const int MaxOccurrences = 50_000;
    const int MaxKeyLength = 1000;
    const string NoUidMarker = "X-INTHEKNOW-NO-UID";

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Sha256Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    public static ParseResult Parse(string ics, DateTimeOffset horizon, ILogger log, long calendarId, CancellationToken ct)
    {
        Calendar? calendar;
        try { calendar = Calendar.Load(MarkMissingUids(ics)); }
        catch (Exception) { return new(null, "unparseable"); }
        if (calendar is null) return new(null, "unparseable");
        ct.ThrowIfCancellationRequested();

        try
        {
            var zone = calendar.Properties["X-WR-TIMEZONE"]?.Value as string
                ?? (calendar.TimeZones.Count == 1 ? calendar.TimeZones[0]?.TzId : null)
                ?? "UTC";

            var position = 0;
            foreach (var e in calendar.Events.ToList())
            {
                position++;
                if (e.DtStart is null)
                {
                    log.LogWarning("Calendar {CalendarId}: event {Position} has no start and is skipped", calendarId, position);
                    calendar.Events.Remove(e);
                }
                // One such event otherwise makes the whole enumeration throw "Duration must be >= 0".
                else if (e.DtEnd is not null && Utc(e.DtEnd, zone) < Utc(e.DtStart, zone)) e.DtEnd = e.DtStart;
            }
            ct.ThrowIfCancellationRequested();

            var events = calendar.Events.ToList();
            bool Marked(CalendarEvent e) => e.Properties.ContainsKey(NoUidMarker);
            var duplicateUids = events.Where(e => e.RecurrenceIdentifier is null && !Marked(e))
                .GroupBy(e => e.Uid).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            bool IsSingle(CalendarEvent e) =>
                e.RecurrenceRule is null && !e.RecurrenceDates.GetAllDates().Any() && e.RecurrenceIdentifier is null;

            // TakeWhileBefore also drops single events past the horizon; the spec only bounds recurring expansion.
            var horizonCal = new CalDateTime(horizon.UtcDateTime, "UTC");
            var occurrences = calendar.GetOccurrences<CalendarEvent>().TakeWhileBefore(horizonCal)
                .Concat(events.Where(e => IsSingle(e) && Utc(e.DtStart!, zone) >= horizon).SelectMany(e => e.GetOccurrences().Take(1)));

            var result = new List<Occurrence>();
            var keys = new HashSet<string>();
            foreach (var o in occurrences)
            {
                ct.ThrowIfCancellationRequested();
                if (result.Count >= MaxOccurrences) return new(null, "too_many_occurrences");
                var e = (CalendarEvent)o.Source;
                var start = Utc(o.Period.StartTime, zone);
                var end = Utc(o.Period.EffectiveEndTime ?? o.Period.StartTime, zone);
                if (end < start) end = start;
                var name = e.Summary ?? "";

                DateTimeOffset? original = null;
                string key;
                if (Marked(e)) key = "content:" + Sha256Hex($"{name}|{Iso(start)}|{Iso(end)}|{e.Location}");
                else if (!IsSingle(e))
                {
                    original = e.RecurrenceIdentifier is { } rid ? Utc(rid.StartTime, zone) : start;
                    key = $"{e.Uid}|{Iso(original.Value)}";
                }
                else key = duplicateUids.Contains(e.Uid) ? $"{e.Uid}|{Iso(start)}" : e.Uid!;
                if (key.Length > MaxKeyLength) key = "sha256:" + Sha256Hex(key);

                if (!keys.Add(key))
                {
                    log.LogWarning("Calendar {CalendarId}: duplicate event key skipped", calendarId);
                    continue;
                }
                result.Add(new(key, name, start, end, e.Status?.ToLowerInvariant(), original, e.Location, e.Description));
            }
            return new(result, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(null, "unparseable"); }
    }

    // Floating and date-only values are local time in the calendar's zone.
    static DateTimeOffset Utc(CalDateTime t, string zone)
    {
        var utc = !t.HasTime ? new CalDateTime(t.Date, new TimeOnly(0, 0), zone).AsUtc
            : t.IsFloating ? new CalDateTime(t.Date, t.Time, zone).AsUtc
            : t.AsUtc;
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    // Ical.Net gives UID-less events a random GUID, so mark them before loading.
    static string MarkMissingUids(string ics)
    {
        var lines = ics.Split('\n');
        var output = new List<string>(lines.Length + 4);
        bool inEvent = false, hasUid = false, changed = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) (inEvent, hasUid) = (true, false);
            else if (inEvent && (line.StartsWith("UID:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("UID;", StringComparison.OrdinalIgnoreCase))) hasUid = true;
            else if (inEvent && line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (!hasUid)
                {
                    output.Add(NoUidMarker + ":1");
                    changed = true;
                }
                inEvent = false;
            }
            output.Add(raw);
        }
        return changed ? string.Join('\n', output) : ics;
    }
}
