using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace InTheKnow;

sealed record PresetRow(long Id, long? Owner, string Name, IReadOnlyList<Rule> Rules, bool IsDefault);

sealed record EffectiveFilter(string SourceType, PresetRow? Preset, IReadOnlyList<Rule> Rules);

// A subscription's filter as the API shows it (contracts/api.md § Subscription filter).
sealed record FilterView(EffectiveFilter Filter, ActiveConditions Active, Dictionary<string, List<string>> Values)
{
    public int[] InactiveRules => [.. Filter.Rules.Select((r, i) => (r, i)).Where(x => !Active.IsActive(x.r)).Select(x => x.i)];
}

sealed record PresetResult(PresetRow? Preset, string? Error);

// Presets and subscription filters (data-model.md, research R1/R7). A preset without an owner is global; every
// preset method takes the owner, so passing null is the operator's global variant.
sealed class Presets(NpgsqlDataSource db, Settings settings)
{
    const int PersonalLimit = 50, GlobalLimit = 100;
    const string Columns = "p.id, p.owner, p.name, p.rules::text, p.is_default";

    // Calendar id → effective filter and active conditions for each of the subscriber's subscriptions.
    // No subscription_filter row means the Default preset (research R1).
    public async Task<Dictionary<long, (EffectiveFilter Filter, ActiveConditions Active)>> LoadFiltersAsync(
        long subscriber, CancellationToken ct, long? calendarId = null)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT s.calendar_id, c.host, f.rules::text, {Columns}
            FROM subscription s
            JOIN calendar c ON c.id = s.calendar_id
            LEFT JOIN subscription_filter f ON f.calendar_id = s.calendar_id AND f.subscriber = s.subscriber
            LEFT JOIN preset p ON p.id = COALESCE(f.preset_id, CASE WHEN f.subscriber IS NULL THEN (SELECT id FROM preset WHERE is_default) END)
            WHERE s.subscriber = $1 AND ($2::bigint IS NULL OR s.calendar_id = $2)
            """);
        cmd.Parameters.Add(new() { Value = subscriber });
        cmd.Parameters.Add(new() { Value = (object?)calendarId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var filters = new Dictionary<long, (EffectiveFilter, ActiveConditions)>();
        while (await r.ReadAsync(ct))
        {
            var preset = r.IsDBNull(3) ? null : ReadPreset(r, 3);
            var filter = new EffectiveFilter(preset is null ? "custom" : "preset", preset, preset?.Rules ?? Parse(r.GetString(2)));
            filters[r.GetInt64(0)] = (filter, ActiveConditions.For(settings.FeaturesFor(r.GetString(1))));
        }
        return filters;
    }

    // Null when the subscriber has no subscription to the calendar.
    public async Task<FilterView?> GetFilterAsync(long subscriber, long calendarId, CancellationToken ct)
    {
        if (!(await LoadFiltersAsync(subscriber, ct, calendarId)).TryGetValue(calendarId, out var f)) return null;
        var values = new Dictionary<string, List<string>>();
        if (!f.Active.ActivityType) return new(f.Filter, f.Active, values); // no MyTimetables conditions active

        // Picker values from the calendar's current events (research R8), for the active conditions only.
        await using var cmd = db.CreateCommand("""
            SELECT 'activityType', fields->>'type' FROM event_snapshot WHERE calendar_id = $1 AND fields ? 'type'
            UNION SELECT 'group', jsonb_array_elements_text(fields->'groups') FROM event_snapshot WHERE calendar_id = $1
            UNION SELECT 'room', jsonb_array_elements_text(fields->'rooms') FROM event_snapshot WHERE calendar_id = $1
            UNION SELECT 'teacher', jsonb_array_elements_text(fields->'teachers') FROM event_snapshot WHERE calendar_id = $1
            ORDER BY 1, 2
            """);
        cmd.Parameters.Add(new() { Value = calendarId });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var keys = f.Active.Keys;
        while (await r.ReadAsync(ct))
        {
            var key = r.GetString(0);
            if (!keys.Contains(key)) continue;
            if (!values.TryGetValue(key, out var list)) values[key] = list = [];
            list.Add(r.GetString(1));
        }
        return new(f.Filter, f.Active, values);
    }

    // Customises the subscription with these rules. False when there is no subscription.
    public async Task<bool> ReplaceFilterAsync(long subscriber, long calendarId, IReadOnlyList<Rule> rules, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO subscription_filter (calendar_id, subscriber, rules)
            SELECT $1, $2, $3 WHERE EXISTS (SELECT 1 FROM calendar_link WHERE calendar_id = $1 AND subscriber = $2)
            ON CONFLICT (calendar_id, subscriber) DO UPDATE SET preset_id = NULL, rules = excluded.rules
            """);
        cmd.Parameters.Add(new() { Value = calendarId });
        cmd.Parameters.Add(new() { Value = subscriber });
        cmd.Parameters.Add(Rules(rules));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // Links the subscription to a preset that is the subscriber's own or global. False when either is missing.
    public async Task<bool> ApplyAsync(long subscriber, long calendarId, long presetId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO subscription_filter (calendar_id, subscriber, preset_id)
            SELECT $1, $2, p.id FROM preset p
            WHERE p.id = $3 AND (p.owner IS NULL OR p.owner = $2)
              AND EXISTS (SELECT 1 FROM calendar_link WHERE calendar_id = $1 AND subscriber = $2)
            ON CONFLICT (calendar_id, subscriber) DO UPDATE SET preset_id = excluded.preset_id, rules = NULL
            """);
        cmd.Parameters.Add(new() { Value = calendarId });
        cmd.Parameters.Add(new() { Value = subscriber });
        cmd.Parameters.Add(new() { Value = presetId });
        try { return await cmd.ExecuteNonQueryAsync(ct) > 0; }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation) { return false; } // preset deleted meanwhile
    }

    // A subscriber sees their own presets by name, then the global ones; the operator (owner null) only the global ones.
    public async Task<List<PresetRow>> ListAsync(long? owner, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM preset p WHERE owner IS NULL OR owner = $1 ORDER BY owner IS NULL, name");
        cmd.Parameters.Add(Owner(owner));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var presets = new List<PresetRow>();
        while (await r.ReadAsync(ct)) presets.Add(ReadPreset(r, 0));
        return presets;
    }

    // Per personal preset: how many of the owner's calendars follow it (005 contracts/api.md § 4). EXISTS, not a join:
    // a filter row survives when its owner removed a calendar that other accounts still have, and one account can have
    // several links to one calendar.
    public async Task<Dictionary<long, int>> FollowersAsync(long owner, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT f.preset_id, count(*) FROM subscription_filter f
            WHERE f.subscriber = $1 AND f.preset_id IS NOT NULL
              AND EXISTS (SELECT 1 FROM calendar_link l WHERE l.calendar_id = f.calendar_id AND l.subscriber = f.subscriber)
            GROUP BY f.preset_id
            """);
        cmd.Parameters.Add(new() { Value = owner });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var followers = new Dictionary<long, int>();
        while (await r.ReadAsync(ct)) followers[r.GetInt64(0)] = (int)r.GetInt64(1);
        return followers;
    }

    public async Task<PresetResult> CreateAsync(long? owner, string? name, IReadOnlyList<Rule> rules, CancellationToken ct)
    {
        if (name is not { Length: >= 1 and <= 100 }) return Fail("invalid_name");
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // ponytail: two simultaneous creates can overshoot the cap by one (research R7); an advisory lock per owner if that matters
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM preset WHERE owner IS NOT DISTINCT FROM $1", conn, tx) { Parameters = { Owner(owner) } })
            if ((long)(await count.ExecuteScalarAsync(ct))! >= (owner is null ? GlobalLimit : PersonalLimit)) return Fail("preset_limit");
        await using var insert = new NpgsqlCommand($"INSERT INTO preset AS p (owner, name, rules) VALUES ($1, $2, $3) RETURNING {Columns}", conn, tx)
        { Parameters = { Owner(owner), new() { Value = name }, Rules(rules) } };
        var created = await SingleAsync(insert, ct);
        if (created.Error is null) await tx.CommitAsync(ct);
        return created;
    }

    // Linked subscriptions follow the new rules, since they read the preset row (FR-017).
    public async Task<PresetResult> UpdateAsync(long? owner, long id, string? name, IReadOnlyList<Rule>? rules, CancellationToken ct)
    {
        if (name is { Length: < 1 or > 100 }) return Fail("invalid_name");
        await using var cmd = db.CreateCommand($"""
            UPDATE preset p SET name = COALESCE($3, name), rules = COALESCE($4, rules)
            WHERE id = $1 AND owner IS NOT DISTINCT FROM $2 RETURNING {Columns}
            """);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(Owner(owner));
        cmd.Parameters.Add(new() { Value = (object?)name ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(rules is null ? new() { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb } : Rules(rules));
        var updated = await SingleAsync(cmd, ct);
        return updated.Error == "not_found" ? Fail(await MissingAsync(owner, id, ct)) : updated;
    }

    // Subscriptions linked to the preset become customised with its last rules, then it is deleted (FR-018, research R7).
    public async Task<string?> DeleteAsync(long? owner, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var find = new NpgsqlCommand("SELECT is_default FROM preset WHERE id = $1 AND owner IS NOT DISTINCT FROM $2 FOR UPDATE", conn, tx)
        { Parameters = { new() { Value = id }, Owner(owner) } })
            switch (await find.ExecuteScalarAsync(ct))
            {
                case null: return await MissingAsync(owner, id, ct);
                case true: return "preset_is_default";
            }
        foreach (var sql in new[]
        {
            "UPDATE subscription_filter SET rules = (SELECT rules FROM preset WHERE id = $1), preset_id = NULL WHERE preset_id = $1",
            "DELETE FROM preset WHERE id = $1",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx) { Parameters = { new() { Value = id } } };
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return null;
    }

    // Why a preset is not the caller's: a global preset on a subscriber route is visible but not theirs (FR-022).
    async Task<string> MissingAsync(long? owner, long id, CancellationToken ct)
    {
        if (owner is null) return "not_found";
        await using var cmd = db.CreateCommand("SELECT 1 FROM preset WHERE id = $1 AND owner IS NULL");
        cmd.Parameters.Add(new() { Value = id });
        return await cmd.ExecuteScalarAsync(ct) is null ? "not_found" : "preset_is_global";
    }

    static async Task<PresetResult> SingleAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? new(ReadPreset(r, 0), null) : Fail("not_found");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return Fail("name_taken"); }
    }

    static PresetRow ReadPreset(NpgsqlDataReader r, int i) =>
        new(r.GetInt64(i), r.IsDBNull(i + 1) ? null : r.GetInt64(i + 1), r.GetString(i + 2), Parse(r.GetString(i + 3)), r.GetBoolean(i + 4));

    // Stored rules were validated on write.
    static IReadOnlyList<Rule> Parse(string json) =>
        FilterRules.Parse(JsonNode.Parse(json)).Rules ?? throw new InvalidOperationException("stored filter rules are invalid");

    static NpgsqlParameter Owner(long? owner) => new() { Value = (object?)owner ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    static NpgsqlParameter Rules(IReadOnlyList<Rule> rules) => new() { Value = FilterRules.ToJson(rules).ToJsonString(), NpgsqlDbType = NpgsqlDbType.Jsonb };

    static PresetResult Fail(string reason) => new(null, reason);
}
