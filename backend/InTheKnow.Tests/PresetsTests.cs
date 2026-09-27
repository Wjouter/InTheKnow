using Xunit;

namespace InTheKnow.Tests;

// Presets and subscription filters against the database (data-model.md § Lifecycle).
public class PresetsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal const string HideZelf = """[{"outcome":"hide","when":{"nameContains":"Zelfstudie"}}]""";
    static IReadOnlyList<Rule> R(string json) => FilterEngineTests.R(json);

    static async Task<long> SeedAsync(TestDb db, long subscriber, string host = Harness.Mt) =>
        (await db.SeedCalendarAsync(host, [$"https://{host}/ical?{Guid.NewGuid():N}"], subscriber)).CalendarId;

    // Filter rows as (calendar, subscriber, preset id, rule count when customised).
    internal static async Task<List<(long, long, long?, long?)>> FiltersAsync(TestDb db) =>
        [.. (await db.RowsAsync("SELECT calendar_id, subscriber, preset_id, jsonb_array_length(rules)::bigint FROM subscription_filter ORDER BY 1, 2"))
            .Select(r => ((long)r[0]!, (long)r[1]!, (long?)r[2], (long?)r[3]))];

    [Fact]
    public async Task ReplaceCustomises()
    {
        await using var db = await TestDb.CreateAsync();
        var (s1, s2, s3) = await TestHost.AccountsAsync(db.DataSource);
        var presets = new Presets(db.DataSource, db.Settings());
        var calendar = await SeedAsync(db, s1);

        var initial = (await presets.GetFilterAsync(s1, calendar, Ct))!;
        Assert.Equal(("preset", "Default", true, 4), (initial.Filter.SourceType, initial.Filter.Preset!.Name, initial.Filter.Preset.IsDefault, initial.Filter.Rules.Count));
        Assert.Empty(await FiltersAsync(db));

        // From "no row", the row holds the submitted rules, not Default's.
        Assert.True(await presets.ReplaceFilterAsync(s1, calendar, R(HideZelf), Ct));
        Assert.Equal([(calendar, s1, null, 1L)], await FiltersAsync(db));
        var custom = (await presets.GetFilterAsync(s1, calendar, Ct))!;
        Assert.Equal(("custom", null), (custom.Filter.SourceType, custom.Filter.Preset));
        Assert.Equal(HideZelf, FilterRules.ToJson(custom.Filter.Rules).ToJsonString());

        Assert.True(await presets.ReplaceFilterAsync(s1, calendar, R("[]"), Ct));
        Assert.Equal([(calendar, s1, null, 0L)], await FiltersAsync(db));

        // No subscription: nothing to show or replace.
        Assert.Null(await presets.GetFilterAsync(s2, calendar, Ct));
        Assert.Null(await presets.GetFilterAsync(s1, calendar + 1, Ct));
        Assert.False(await presets.ReplaceFilterAsync(s2, calendar, R(HideZelf), Ct));
        Assert.Single(await FiltersAsync(db));
    }

    // US3, FR-013, SC-006: picker values come from the calendar's current events, for active conditions only.
    [Fact]
    public async Task PickerValues()
    {
        await using var db = await TestDb.CreateAsync();
        var (s1, s2, s3) = await TestHost.AccountsAsync(db.DataSource);
        var mt = await SeedAsync(db, s1);
        var generic = await SeedAsync(db, s1, host: "example.com");
        foreach (var (calendar, key, fields) in new[]
        {
            (mt, "a", """{"type":"lesson","groups":["ES2","ES1"],"rooms":["R1"],"teachers":["Docent1, Anna"]}"""),
            (mt, "b", """{"type":"test","groups":["ES1"],"rooms":["R2","R1"]}"""),
            (mt, "c", """{"type":"lesson"}"""),
            (generic, "d", """{"location":"Somewhere"}"""),
        })
            await db.ExecAsync("INSERT INTO event_snapshot (calendar_id, event_key, name, starts_at, ends_at, fields) VALUES ($1, $2, 'n', now(), now(), $3::jsonb)",
                calendar, key, fields);

        async Task<Dictionary<string, List<string>>> ValuesAsync(Settings settings, long calendar) =>
            (await new Presets(db.DataSource, settings).GetFilterAsync(s1, calendar, Ct))!.Values;

        var values = await ValuesAsync(db.Settings(), mt);
        Assert.Equal(["activityType", "group", "room", "teacher"], values.Keys.Order());
        Assert.Equal(["lesson", "test"], values["activityType"]);
        Assert.Equal(["ES1", "ES2"], values["group"]);
        Assert.Equal(["R1", "R2"], values["room"]);
        Assert.Equal(["Docent1, Anna"], values["teacher"]);
        Assert.DoesNotContain("teacher", (await ValuesAsync(db.Settings(("InTheKnow:MyTimetables:Domains:0:TeacherDetection", "false")), mt)).Keys);
        Assert.Empty(await ValuesAsync(db.Settings(), generic));
    }

    // US4 scenario 5: turning event details off makes MyTimetables rules inactive at the next start, with no edit.
    [Fact]
    public async Task EventDetailsSettingTogglesRules()
    {
        await using var db = await TestDb.CreateAsync();
        var (s1, s2, s3) = await TestHost.AccountsAsync(db.DataSource);
        var calendar = await SeedAsync(db, s1);
        var on = new Presets(db.DataSource, db.Settings());
        Assert.True(await on.ReplaceFilterAsync(s1, calendar, R("""[{"outcome":"showOnly","when":{"group":["ES1"]}},{"outcome":"hide","when":{"eventEnded":true}}]"""), Ct));
        Assert.Empty((await on.GetFilterAsync(s1, calendar, Ct))!.InactiveRules);
        var off = new Presets(db.DataSource, db.Settings(("InTheKnow:MyTimetables:Domains:0:EventDetails", "false")));
        var inactive = (await off.GetFilterAsync(s1, calendar, Ct))!.InactiveRules;
        Assert.Equal([0], inactive);
        Assert.Equal(new ActiveConditions(false, false, false, false), (await off.LoadFiltersAsync(s1, Ct))[calendar].Active);
    }

    static string Json(IReadOnlyList<Rule> rules) => FilterRules.ToJson(rules).ToJsonString();

    // US5: personal presets, linked until customised, private to their owner.
    [Fact]
    public async Task PersonalPresetLifecycle()
    {
        await using var db = await TestDb.CreateAsync();
        var (s1, s2, s3) = await TestHost.AccountsAsync(db.DataSource);
        var p = new Presets(db.DataSource, db.Settings());
        var (cal1, cal2) = (await SeedAsync(db, s1), await SeedAsync(db, s1));
        async Task<string> EffectiveAsync(long calendar) => Json((await p.LoadFiltersAsync(s1, Ct))[calendar].Filter.Rules);

        var minor = (await p.CreateAsync(s1, "Minor", R(HideZelf), Ct)).Preset!;
        Assert.Equal((s1, "Minor", HideZelf, false), (minor.Owner, minor.Name, Json(minor.Rules), minor.IsDefault));
        Assert.Equal("name_taken", (await p.CreateAsync(s1, "Minor", R("[]"), Ct)).Error);
        var foreign = (await p.CreateAsync(s2, "Minor", R("[]"), Ct)).Preset!; // the same name for another subscriber
        Assert.Equal("invalid_name", (await p.CreateAsync(s1, "", R("[]"), Ct)).Error);
        Assert.Equal("invalid_name", (await p.CreateAsync(s1, new string('x', 101), R("[]"), Ct)).Error);
        Assert.Equal(["Minor", "Default"], (await p.ListAsync(s1, Ct)).Select(x => x.Name));

        var renamed = (await p.UpdateAsync(s1, minor.Id, "Renamed", null, Ct)).Preset!;
        Assert.Equal(("Renamed", HideZelf), (renamed.Name, Json(renamed.Rules)));
        Assert.Equal("[]", Json((await p.UpdateAsync(s1, minor.Id, null, R("[]"), Ct)).Preset!.Rules));
        Assert.Equal("name_taken", (await p.CreateAsync(s1, "Renamed", R("[]"), Ct)).Error);
        Assert.Equal("invalid_name", (await p.UpdateAsync(s1, minor.Id, "", null, Ct)).Error);

        // Linked subscriptions follow edits (SC-003); a customised one does not; applying again re-links.
        Assert.True(await p.ApplyAsync(s1, cal1, minor.Id, Ct));
        Assert.True(await p.ApplyAsync(s1, cal2, minor.Id, Ct));
        Assert.Equal([(cal1, s1, minor.Id, null), (cal2, s1, minor.Id, null)], await FiltersAsync(db));
        await p.UpdateAsync(s1, minor.Id, null, R(HideZelf), Ct);
        Assert.Equal((HideZelf, HideZelf), (await EffectiveAsync(cal1), await EffectiveAsync(cal2)));
        Assert.True(await p.ReplaceFilterAsync(s1, cal2, R("[]"), Ct));
        await p.UpdateAsync(s1, minor.Id, null, R("""[{"outcome":"lowPriority","when":{"eventEnded":true}}]"""), Ct);
        Assert.Equal("[]", await EffectiveAsync(cal2));
        Assert.True(await p.ApplyAsync(s1, cal2, minor.Id, Ct));
        Assert.Equal(await EffectiveAsync(cal1), await EffectiveAsync(cal2));

        // Deleting converts linked subscriptions to customised with the preset's last rules (FR-018).
        var last = await EffectiveAsync(cal1);
        Assert.Null(await p.DeleteAsync(s1, minor.Id, Ct));
        Assert.Equal([(cal1, s1, null, 1L), (cal2, s1, null, 1L)], await FiltersAsync(db));
        Assert.Equal((last, last), (await EffectiveAsync(cal1), await EffectiveAsync(cal2)));
        Assert.Equal("not_found", await p.DeleteAsync(s1, minor.Id, Ct));

        // Another subscriber's preset does not exist for s1 (FR-016).
        Assert.DoesNotContain(await p.ListAsync(s1, Ct), x => x.Id == foreign.Id);
        Assert.Equal("not_found", (await p.UpdateAsync(s1, foreign.Id, "x", null, Ct)).Error);
        Assert.Equal("not_found", await p.DeleteAsync(s1, foreign.Id, Ct));
        Assert.False(await p.ApplyAsync(s1, cal1, foreign.Id, Ct));
        Assert.False(await p.ApplyAsync(s1, cal1, 999_999, Ct));
        Assert.False(await p.ApplyAsync(s3, cal1, 1, Ct)); // no subscription

        // Presets outlive the subscriber's last subscription (FR-019).
        await db.ExecAsync("DELETE FROM calendar_link WHERE subscriber = $1", s2);
        Assert.Equal(["Minor", "Default"], (await p.ListAsync(s2, Ct)).Select(x => x.Name));
    }

    [Fact]
    public async Task PersonalPresetLimit()
    {
        await using var db = await TestDb.CreateAsync();
        var (s1, s2, s3) = await TestHost.AccountsAsync(db.DataSource);
        var p = new Presets(db.DataSource, db.Settings());
        await db.ExecAsync("INSERT INTO preset (owner, name, rules) SELECT $1, 'p' || g, '[]' FROM generate_series(1, 49) g", s1);
        Assert.Null((await p.CreateAsync(s1, "fiftieth", R("[]"), Ct)).Error);
        Assert.Equal("preset_limit", (await p.CreateAsync(s1, "one more", R("[]"), Ct)).Error);
        Assert.Null((await p.CreateAsync(s2, "one more", R("[]"), Ct)).Error);
    }

    // US6: global presets are the operator's (owner null); Default can be edited, never deleted.
    [Fact]
    public async Task GlobalPresetLifecycle()
    {
        await using var db = await TestDb.CreateAsync();
        var (s1, s2, s3) = await TestHost.AccountsAsync(db.DataSource);
        var p = new Presets(db.DataSource, db.Settings());
        var (cal1, cal2) = (await SeedAsync(db, s1), await SeedAsync(db, s2));
        Assert.True(await p.ReplaceFilterAsync(s2, cal2, R("[]"), Ct));
        var before = await FiltersAsync(db);

        var minor = (await p.CreateAsync(null, "Minor", R(HideZelf), Ct)).Preset!;
        Assert.Equal((null, "Minor"), (minor.Owner, minor.Name));
        Assert.Equal("name_taken", (await p.CreateAsync(null, "Default", R("[]"), Ct)).Error);
        Assert.Null((await p.CreateAsync(s1, "Minor", R("[]"), Ct)).Error); // personal names are separate
        Assert.Equal(["Default", "Minor"], (await p.ListAsync(null, Ct)).Select(x => x.Name));
        Assert.Equal("Other", (await p.UpdateAsync(null, minor.Id, "Other", null, Ct)).Preset!.Name);

        // A global preset changes no existing choice (FR-023); a new subscription starts on Default.
        Assert.Equal(before, await FiltersAsync(db));
        Assert.True((await p.GetFilterAsync(s1, cal1, Ct))!.Filter.Preset!.IsDefault);

        // Subscribers apply or copy a global preset, but cannot edit or delete it (FR-022).
        Assert.Equal("preset_is_global", (await p.UpdateAsync(s1, minor.Id, "Mine", null, Ct)).Error);
        Assert.Equal("preset_is_global", await p.DeleteAsync(s1, minor.Id, Ct));
        var copy = (await p.CreateAsync(s1, "Copy", (await p.ListAsync(s1, Ct)).Single(x => x.Id == minor.Id).Rules, Ct)).Preset!;
        Assert.Equal((s1, HideZelf), (copy.Owner, Json(copy.Rules)));
        Assert.Equal("not_found", (await p.UpdateAsync(null, copy.Id, "x", null, Ct)).Error); // the operator routes see global presets only

        // Deleting a global preset converts its linked subscriptions (FR-018).
        Assert.True(await p.ApplyAsync(s1, cal1, minor.Id, Ct));
        Assert.Null(await p.DeleteAsync(null, minor.Id, Ct));
        Assert.Contains((cal1, s1, null, 1L), await FiltersAsync(db));

        // Default: name and rules editable, never deletable (FR-012).
        var edited = (await p.UpdateAsync(null, 1, "Standard", R("[]"), Ct)).Preset!;
        Assert.Equal(("Standard", "[]", true), (edited.Name, Json(edited.Rules), edited.IsDefault));
        Assert.Equal("preset_is_default", await p.DeleteAsync(null, 1, Ct));
        Assert.Equal("preset_is_global", await p.DeleteAsync(s1, 1, Ct));

        await db.ExecAsync("INSERT INTO preset (name, rules) SELECT 'g' || g, '[]' FROM generate_series(1, 98) g");
        Assert.Null((await p.CreateAsync(null, "hundredth", R("[]"), Ct)).Error);
        Assert.Equal("preset_limit", (await p.CreateAsync(null, "one more", R("[]"), Ct)).Error);
    }
}
