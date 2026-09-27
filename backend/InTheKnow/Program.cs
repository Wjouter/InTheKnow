using System.Threading.RateLimiting;
using InTheKnow;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;

// The server command of 004 FR-033b (research R13): make-admin <address> [--language en|nl]. Same configuration, no instance
// lock, no migration.
if (args is ["make-admin", ..])
{
    var language = args is [_, _, "--language", var l] ? l : args.Length == 2 ? "en" : null;
    if (args.Length < 2 || Accounts.NormalizeEmail(args[1]) is not { } address || language is not ("en" or "nl"))
    {
        Console.Error.WriteLine("usage: make-admin <address> [--language en|nl]");
        return 1;
    }
    var settings = Settings.Load(WebApplication.CreateBuilder([]).Configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    await using var db = Db.Create(settings.ConnectionString);
    if (!await Db.SchemaCurrentAsync(db))
    {
        Console.Error.WriteLine("the database schema is behind this build; start the server once first");
        return 1;
    }
    Console.WriteLine(await Accounts.MakeAdminAsync(db, address, language, TimeProvider.System.GetUtcNow()));
    return 0;
}

var builder = WebApplication.CreateBuilder(args);

// DI factories, so WebApplicationFactory configuration overrides (applied at Build()) are visible to them.
builder.Services.AddSingleton(sp => Settings.Load(
    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("Settings")));
builder.Services.AddSingleton(sp => Db.Create(sp.GetRequiredService<Settings>().ConnectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new Fetcher());
builder.Services.AddSingleton<HostBudget>();
// checker (T044)
builder.Services.AddSingleton<Checker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Checker>());
// subscriptions (T049)
builder.Services.AddSingleton<Subscriptions>();
// filters (002 T015)
builder.Services.AddSingleton<Presets>();
// notifications (003 T025)
builder.Services.AddSingleton<IChannelType, EmailChannel>();
builder.Services.AddSingleton<Channels>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Notifier>());
// api (T058)
builder.Services.AddProblemDetails();
// accounts (004 T020)
builder.Services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();
builder.Services.Configure<PasswordHasherOptions>(o =>
{
    o.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
    o.IterationCount = 210_000;
});
builder.Services.AddSingleton<Throttle>();
builder.Services.AddSingleton<SignIn>();
builder.Services.AddSingleton<Accounts>();
builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(SignIn.Policies);
builder.Services.AddSingleton<IAuthorizationHandler, ReasonHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ReasonResultHandler>();
// FR-037 to FR-040: the operator's bot check provider, or none.
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Settings>().Options.Accounts.BotCheck;
    return new BotCheck(o.Provider == "" ? null : new BotCheckProvider(o.Provider, BotCheck.Providers[o.Provider]), o.SiteKey, o.SecretKey,
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(BotCheck)), sp.GetRequiredService<ILogger<BotCheck>>(),
        sp.GetRequiredService<TimeProvider>());
});
// FR-012a, research R8: requests per source on the public routes that send email or accept a token.
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("public", http => RateLimitPartition.GetSlidingWindowLimiter(SignIn.Source(http), _ => new SlidingWindowRateLimiterOptions
    {
        PermitLimit = http.RequestServices.GetRequiredService<Settings>().Options.Accounts.SourceRequestsPerHour,
        Window = TimeSpan.FromHours(1),
        SegmentsPerWindow = 6,
        QueueLimit = 0,
    }));
    o.OnRejected = (context, _) =>
    {
        var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromSeconds(600);
        context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString();
        return new ValueTask(Api.Problem(429, "too_many_requests").ExecuteAsync(context.HttpContext));
    };
});

var app = builder.Build();

var appSettings = app.Services.GetRequiredService<Settings>(); // invalid configuration fails at startup
var dataSource = app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>();
await using var instanceLock = await Db.TakeInstanceLockAsync(dataSource);
await Db.MigrateAsync(dataSource);
// regroup (T070)
await app.Services.GetRequiredService<Checker>().RegroupAsync(app.Services.GetRequiredService<TimeProvider>().GetUtcNow());

// X-Forwarded-For only from the configured proxies (research R3). With no known proxy the middleware would believe every
// client, so it is left out and the direct peer is the client.
if (appSettings.TrustedProxyAddresses.Count + appSettings.TrustedProxyNetworks.Count > 0)
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = null };
    forwarded.KnownProxies.Clear();
    forwarded.KnownIPNetworks.Clear();
    foreach (var a in appSettings.TrustedProxyAddresses) forwarded.KnownProxies.Add(a);
    foreach (var n in appSettings.TrustedProxyNetworks) forwarded.KnownIPNetworks.Add(n);
    app.UseForwardedHeaders(forwarded);
}
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapApi();
app.MapFilterApi();
app.MapNotificationApi();
app.MapAccountApi();

app.Run();
return 0;

public partial class Program;
