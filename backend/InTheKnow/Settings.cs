namespace InTheKnow;

sealed class InTheKnowOptions
{
    public string ApiKey { get; set; } = "";
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(10);
    public int MaxRequestsPerHostPerMinute { get; set; } = 120;
    public int MaxConcurrentChecks { get; set; } = 16;
    public TimeSpan FailingCheckInterval { get; set; } = TimeSpan.FromHours(24);
    public int ExpansionHorizonMonths { get; set; } = 12;
    public TimeSpan ChangeRetention { get; set; } = TimeSpan.FromDays(365);
    // No default domain here: the binder appends to an initialised list, so appsettings.json holds the default.
    public MyTimetablesOptions MyTimetables { get; set; } = new();
}

sealed class MyTimetablesOptions
{
    public List<DomainOptions> Domains { get; set; } = [];
}

sealed class DomainOptions
{
    public string Host { get; set; } = "";
    public bool DuplicateLinkMerging { get; set; } = true;
    public bool ClassGroupSharing { get; set; } = true;
    public bool EmptyFeedProtection { get; set; } = true;
    public bool TeacherDetection { get; set; } = true;
    public bool EventDetails { get; set; } = true;
    public bool DraftMarkerDetection { get; set; } = true;
    public int? MaxRequestsPerMinute { get; set; }
}

sealed record Features(bool DuplicateLinkMerging, bool ClassGroupSharing, bool EmptyFeedProtection,
                       bool TeacherDetection, bool EventDetails, bool DraftMarkerDetection)
{
    // Fixed order dlm,cgs,efp,td,ed,dmd; stored as calendar.baseline_features.
    public string Canonical => "mt:" + string.Concat(
        new[] { DuplicateLinkMerging, ClassGroupSharing, EmptyFeedProtection, TeacherDetection, EventDetails, DraftMarkerDetection }
            .Select(b => b ? '1' : '0'));
}

sealed class Settings
{
    public required InTheKnowOptions Options { get; init; }
    public required string ConnectionString { get; init; }
    Dictionary<string, DomainOptions> domains = [];

    public static Settings Load(IConfiguration c, ILogger log)
    {
        var o = c.GetSection("InTheKnow").Get<InTheKnowOptions>() ?? new();
        var cs = c.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("ConnectionStrings:Default is required");
        if (o.ApiKey.Length < 32) throw new InvalidOperationException("InTheKnow:ApiKey must be at least 32 characters");

        var minimum = TimeSpan.FromMinutes(5);
        if (o.CheckInterval < minimum)
        {
            log.LogWarning("InTheKnow:CheckInterval {Value} is below the minimum and is clamped to {Minimum}", o.CheckInterval, minimum);
            o.CheckInterval = minimum;
        }
        if (o.FailingCheckInterval < o.CheckInterval)
        {
            log.LogWarning("InTheKnow:FailingCheckInterval {Value} is below CheckInterval and is clamped to {Minimum}", o.FailingCheckInterval, o.CheckInterval);
            o.FailingCheckInterval = o.CheckInterval;
        }
        Require(o.MaxRequestsPerHostPerMinute >= 1, "InTheKnow:MaxRequestsPerHostPerMinute must be at least 1");
        Require(o.MaxConcurrentChecks is >= 1 and <= 64, "InTheKnow:MaxConcurrentChecks must be between 1 and 64");
        Require(o.ExpansionHorizonMonths >= 1, "InTheKnow:ExpansionHorizonMonths must be at least 1");
        Require(o.ChangeRetention > TimeSpan.Zero, "InTheKnow:ChangeRetention must be positive");
        var domains = o.MyTimetables.Domains;
        for (var i = 0; i < domains.Count; i++)
        {
            Require(!string.IsNullOrWhiteSpace(domains[i].Host), $"InTheKnow:MyTimetables:Domains:{i}:Host is required");
            Require(domains[i].MaxRequestsPerMinute is null or >= 1, $"InTheKnow:MyTimetables:Domains:{i}:MaxRequestsPerMinute must be at least 1");
            Require(domains.Take(i).All(d => !d.Host.Equals(domains[i].Host, StringComparison.OrdinalIgnoreCase)),
                $"InTheKnow:MyTimetables:Domains:{i}:Host duplicates another domain");
        }

        return new Settings
        {
            Options = o,
            ConnectionString = cs,
            domains = o.MyTimetables.Domains.ToDictionary(d => d.Host, StringComparer.OrdinalIgnoreCase),
        };
    }

    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    public Features? FeaturesFor(string host) =>
        domains.TryGetValue(host, out var d)
            ? new Features(d.DuplicateLinkMerging, d.ClassGroupSharing, d.EmptyFeedProtection,
                           d.TeacherDetection && d.EventDetails, d.EventDetails, d.DraftMarkerDetection)
            : null;

    public string CanonicalFeaturesFor(string host) => FeaturesFor(host)?.Canonical ?? "generic";

    public int BudgetFor(string host) =>
        domains.TryGetValue(host, out var d) && d.MaxRequestsPerMinute is { } m ? m : Options.MaxRequestsPerHostPerMinute;
}
