using InTheKnow;

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
// api (T058)
builder.Services.AddProblemDetails();

var app = builder.Build();

app.Services.GetRequiredService<Settings>(); // invalid configuration fails at startup
var dataSource = app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>();
await using var instanceLock = await Db.TakeInstanceLockAsync(dataSource);
await Db.MigrateAsync(dataSource);
// regroup (T070)

app.MapApi();

app.Run();

public partial class Program;
