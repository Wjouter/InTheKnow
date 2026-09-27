using System.Security.Claims;
using System.Text.Json.Nodes;
using static InTheKnow.Api;

namespace InTheKnow;

// Filter and preset routes (specs/002 contracts/api.md), under /api/me and /api/admin (004).
// Bodies bind as JsonNode, so a wrong shape gets a 400 with a reason instead of a binding failure.
static class FilterApi
{
    public static void MapFilterApi(this WebApplication app)
    {
        var me = Me(app);
        const string filter = "/calendars/{calendarId:long}/filter";

        me.MapGet(filter, async (ClaimsPrincipal user, long calendarId, Presets presets, CancellationToken ct) =>
            FilterResult(await presets.GetFilterAsync(user.AccountId(), calendarId, ct)));

        me.MapPut(filter, async (ClaimsPrincipal user, long calendarId, JsonNode? body, Presets presets, CancellationToken ct) =>
        {
            var (rules, error) = FilterRules.Parse(Field(body, "rules"));
            if (error is not null) return Problem(400, error);
            return await presets.ReplaceFilterAsync(user.AccountId(), calendarId, rules!, ct)
                ? FilterResult(await presets.GetFilterAsync(user.AccountId(), calendarId, ct))
                : Results.NotFound();
        });

        me.MapPut(filter + "/preset", async (ClaimsPrincipal user, long calendarId, JsonNode? body, Presets presets, CancellationToken ct) =>
            Field(body, "presetId") is JsonValue v && v.TryGetValue<long>(out var presetId)
            && await presets.ApplyAsync(user.AccountId(), calendarId, presetId, ct)
                ? FilterResult(await presets.GetFilterAsync(user.AccountId(), calendarId, ct))
                : Results.NotFound());

        // Personal presets: the account's own, plus the global ones to list and apply (FR-015, FR-016).
        me.MapGet("/presets", async (ClaimsPrincipal user, Presets presets, CancellationToken ct) => Items(await presets.ListAsync(user.AccountId(), ct)));
        me.MapPost("/presets", (ClaimsPrincipal user, JsonNode? body, Presets presets, CancellationToken ct) =>
            CreateAsync(presets, user.AccountId(), body, ct));
        me.MapPut("/presets/{id:long}", (ClaimsPrincipal user, long id, JsonNode? body, Presets presets, CancellationToken ct) =>
            UpdateAsync(presets, user.AccountId(), id, body, ct));
        me.MapDelete("/presets/{id:long}", async (ClaimsPrincipal user, long id, Presets presets, CancellationToken ct) =>
            Deleted(await presets.DeleteAsync(user.AccountId(), id, ct)));

        // Global presets, admins only (004).
        var admin = Admin(app);
        admin.MapGet("/presets", async (Presets presets, CancellationToken ct) => Items(await presets.ListAsync(null, ct)));
        admin.MapPost("/presets", (JsonNode? body, Presets presets, CancellationToken ct) => CreateAsync(presets, null, body, ct));
        admin.MapPut("/presets/{id:long}", (long id, JsonNode? body, Presets presets, CancellationToken ct) => UpdateAsync(presets, null, id, body, ct));
        admin.MapDelete("/presets/{id:long}", async (long id, Presets presets, CancellationToken ct) => Deleted(await presets.DeleteAsync(null, id, ct)));
    }

    static async Task<IResult> CreateAsync(Presets presets, long? owner, JsonNode? body, CancellationToken ct)
    {
        var (rules, error) = FilterRules.Parse(Field(body, "rules"));
        if (error is not null) return Problem(400, error);
        var r = await presets.CreateAsync(owner, FilterRules.Text(Field(body, "name")), rules!, ct);
        return r.Error is null ? Results.Json(PresetJson(r.Preset!), statusCode: 201) : Error(r.Error);
    }

    static async Task<IResult> UpdateAsync(Presets presets, long? owner, long id, JsonNode? body, CancellationToken ct)
    {
        var (nameNode, rulesNode) = (Field(body, "name"), Field(body, "rules"));
        if (nameNode is null && rulesNode is null) return Problem(400, "invalid_rules");
        IReadOnlyList<Rule>? rules = null;
        if (rulesNode is not null && FilterRules.Parse(rulesNode) is var (parsed, error))
        {
            if (error is not null) return Problem(400, error);
            rules = parsed;
        }
        var name = FilterRules.Text(nameNode);
        if (nameNode is not null && name is null) return Problem(400, "invalid_name");
        var r = await presets.UpdateAsync(owner, id, name, rules, ct);
        return r.Error is null ? Results.Json(PresetJson(r.Preset!)) : Error(r.Error);
    }

    static IResult Deleted(string? error) => error is null ? Results.NoContent() : Error(error);

    static IResult Error(string reason) => reason switch
    {
        "not_found" => Results.NotFound(),
        "preset_is_global" or "preset_is_default" => Problem(403, reason),
        "name_taken" or "preset_limit" => Problem(409, reason),
        _ => Problem(400, reason),
    };

    static JsonNode? Field(JsonNode? body, string key) => (body as JsonObject)?[key];

    static IResult Items(List<PresetRow> presets) => Results.Json(new { items = presets.Select(PresetJson) });

    static string Scope(PresetRow p) => p.Owner is null ? "global" : "personal";

    static JsonObject PresetJson(PresetRow p)
    {
        var json = new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["scope"] = Scope(p), ["rules"] = FilterRules.ToJson(p.Rules) };
        if (p.IsDefault) json["isDefault"] = true;
        return json;
    }

    static IResult FilterResult(FilterView? v) => v is null ? Results.NotFound() : Results.Json(new JsonObject
    {
        ["source"] = v.Filter.Preset is { } p
            ? new JsonObject { ["type"] = "preset", ["presetId"] = p.Id, ["name"] = p.Name, ["scope"] = Scope(p) }
            : new JsonObject { ["type"] = "custom" },
        ["rules"] = FilterRules.ToJson(v.Filter.Rules),
        ["inactiveRules"] = new JsonArray([.. v.InactiveRules.Select(i => (JsonNode)i)]),
        ["activeConditions"] = new JsonArray([.. v.Active.Keys.Select(k => (JsonNode)k)]),
        ["values"] = new JsonObject(v.Values.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonArray([.. kv.Value.Select(x => (JsonNode)x)])))),
    });
}
