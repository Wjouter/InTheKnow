using System.Net;
using System.Net.Mail;

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
    public NotificationsOptions Notifications { get; set; } = new();
    public AccountsOptions Accounts { get; set; } = new();
}

// specs/004 contracts/configuration.md.
sealed class AccountsOptions
{
    public TimeSpan SessionIdleLifetime { get; set; } = TimeSpan.FromDays(30);
    public int SourceFailuresPerHour { get; set; } = 3000;
    public int SourceRequestsPerHour { get; set; } = 1000;
    public List<string> TrustedProxies { get; set; } = []; // IP addresses or CIDR networks
    public BotCheckOptions BotCheck { get; set; } = new();
}

sealed class BotCheckOptions
{
    public string Provider { get; set; } = "";
    public string SiteKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
}

sealed class NotificationsOptions
{
    public TimeSpan CollectionWindow { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan RetryWindow { get; set; } = TimeSpan.FromDays(1);
    public string DefaultTimeZone { get; set; } = "Europe/Amsterdam";
    public string PublicBaseUrl { get; set; } = "";
    public string FrontendBaseUrl { get; set; } = "";
    public EmailOptions Email { get; set; } = new();
}

sealed class EmailOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool StartTls { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "InTheKnow";
    public int MaxMessagesPerMinute { get; set; } = 60;
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
    public required TimeZoneInfo DefaultTimeZone { get; init; }
    public IReadOnlyList<IPAddress> TrustedProxyAddresses { get; init; } = [];
    public IReadOnlyList<IPNetwork> TrustedProxyNetworks { get; init; } = [];
    Dictionary<string, DomainOptions> domains = [];

    public static Settings Load(IConfiguration c, ILogger log)
    {
        var o = c.GetSection("InTheKnow").Get<InTheKnowOptions>() ?? new();
        var cs = c.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("ConnectionStrings:Default is required");
        if (o.ApiKey != "") log.LogWarning("InTheKnow:ApiKey is retired and ignored");

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

        // contracts/configuration.md (003).
        const string n = "InTheKnow:Notifications:";
        var no = o.Notifications;
        Require(no.CollectionWindow >= TimeSpan.FromMinutes(1) && no.CollectionWindow <= TimeSpan.FromMinutes(60),
            n + "CollectionWindow must be between 1 and 60 minutes");
        Require(no.RetryWindow >= no.CollectionWindow + TimeSpan.FromHours(1), n + "RetryWindow must be at least CollectionWindow + 1 hour");
        var zone = Texts.FindTimeZone(no.DefaultTimeZone);
        Require(zone is not null, n + "DefaultTimeZone must be an IANA time zone id");
        var e = no.Email;
        if (e.Host != "")
        {
            Require(BaseUrl(no.PublicBaseUrl), n + "PublicBaseUrl must be an origin only (https://host[:port], no path, trailing slash, query or fragment; http only on localhost) when email is configured");
            Require(BaseUrl(no.FrontendBaseUrl), n + "FrontendBaseUrl must be an origin only (https://host[:port], no path, trailing slash, query or fragment; http only on localhost) when email is configured");
            Require(MailAddress.TryCreate(e.FromAddress, out _), n + "Email:FromAddress must be an email address when email is configured");
        }
        Require(e.Port is >= 1 and <= 65535, n + "Email:Port must be between 1 and 65535");
        Require(e.StartTls || e.Host is "" or "localhost" or "127.0.0.1" or "::1", n + "Email:StartTls may be false only for localhost");
        Require((e.Username == "") == (e.Password == ""), n + "Email:Username and Email:Password must be set together");
        Require(e.FromName is { Length: >= 1 and <= 100 } && !e.FromName.Any(char.IsControl),
            n + "Email:FromName must be 1 to 100 characters without control characters");
        Require(e.MaxMessagesPerMinute >= 1, n + "Email:MaxMessagesPerMinute must be at least 1");
        if (e.Host == "") log.LogWarning("Email is not configured: account confirmation, reset and invite emails wait until it is");

        // specs/004 contracts/configuration.md.
        const string a = "InTheKnow:Accounts:";
        var ao = o.Accounts;
        Require(ao.SessionIdleLifetime >= TimeSpan.FromHours(1) && ao.SessionIdleLifetime <= TimeSpan.FromDays(365),
            a + "SessionIdleLifetime must be between 1 hour and 365 days");
        Require(ao.SourceFailuresPerHour >= 20, a + "SourceFailuresPerHour must be at least 20");
        Require(ao.SourceRequestsPerHour >= 10, a + "SourceRequestsPerHour must be at least 10");
        var (proxies, networks) = (new List<IPAddress>(), new List<IPNetwork>());
        for (var i = 0; i < ao.TrustedProxies.Count; i++)
        {
            if (IPAddress.TryParse(ao.TrustedProxies[i], out var ip)) proxies.Add(ip);
            else if (IPNetwork.TryParse(ao.TrustedProxies[i], out var net)) networks.Add(net);
            else Require(false, $"{a}TrustedProxies:{i} must be an IP address or a CIDR network");
        }
        if (proxies.Count + networks.Count == 0)
            log.LogWarning("InTheKnow:Accounts:TrustedProxies is empty: the direct peer is the client, so all users behind a proxy share one throttling source");
        var bot = ao.BotCheck;
        Require(bot.Provider is "" or "turnstile" or "recaptcha", a + "BotCheck:Provider must be empty, turnstile or recaptcha");
        Require(bot.Provider == "" || bot.SiteKey != "" && bot.SecretKey != "", a + "BotCheck:SiteKey and BotCheck:SecretKey are required when a provider is set");

        return new Settings
        {
            Options = o,
            ConnectionString = cs,
            DefaultTimeZone = zone!,
            TrustedProxyAddresses = proxies,
            TrustedProxyNetworks = networks,
            domains = o.MyTimetables.Domains.ToDictionary(d => d.Host, StringComparer.OrdinalIgnoreCase),
        };
    }

    // Origin only (006 R5): links are joined as {base}/path. Uri normalizes a trailing slash away, so check the string.
    static bool BaseUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http" && u.Host == "localhost")
        && !url.EndsWith('/') && u.AbsolutePath == "/" && u.Query == "" && u.Fragment == "" && u.UserInfo == "";

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
