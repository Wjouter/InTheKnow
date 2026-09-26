using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InTheKnow.Tests;

public class SettingsTests
{
    [Fact]
    public void ShippedDefaults()
    {
        var s = TestDb.Offline();
        var o = s.Options;
        Assert.Equal(TimeSpan.FromMinutes(10), o.CheckInterval);
        Assert.Equal(120, o.MaxRequestsPerHostPerMinute);
        Assert.Equal(16, o.MaxConcurrentChecks);
        Assert.Equal(TimeSpan.FromHours(24), o.FailingCheckInterval);
        Assert.Equal(12, o.ExpansionHorizonMonths);
        Assert.Equal(TimeSpan.FromDays(365), o.ChangeRetention);
        var d = Assert.Single(o.MyTimetables.Domains);
        Assert.Equal("mytimetables.inholland.nl", d.Host);
        Assert.Equal(new Features(true, true, true, true, true, true), s.FeaturesFor("mytimetables.inholland.nl"));
        Assert.Equal("mt:111111", s.CanonicalFeaturesFor("mytimetables.inholland.nl"));
        Assert.Equal(120, s.BudgetFor("mytimetables.inholland.nl"));
    }

    [Fact]
    public void RequiredValues()
    {
        var empty = Assert.Throws<InvalidOperationException>(() => TestDb.LoadSettings(NullLogger.Instance,
            ("ConnectionStrings:Default", ""), ("InTheKnow:ApiKey", TestDb.ApiKey)));
        Assert.Contains("ConnectionStrings:Default", empty.Message);
        var shortKey = new string('k', 31);
        var key = Assert.Throws<InvalidOperationException>(() => TestDb.LoadSettings(NullLogger.Instance,
            ("ConnectionStrings:Default", "Host=x"), ("InTheKnow:ApiKey", shortKey)));
        Assert.Contains("InTheKnow:ApiKey", key.Message);
        Assert.DoesNotContain(shortKey, key.Message);
    }

    [Fact]
    public void HostLookup()
    {
        var s = TestDb.Offline();
        Assert.NotNull(s.FeaturesFor("MyTimetables.Inholland.NL"));
        Assert.Null(s.FeaturesFor("x.mytimetables.inholland.nl"));
        Assert.Equal("generic", s.CanonicalFeaturesFor("example.com"));
    }

    [Fact]
    public void TeacherDetectionNeedsEventDetails()
    {
        var s = TestDb.Offline(("InTheKnow:MyTimetables:Domains:0:EventDetails", "false"));
        var f = s.FeaturesFor("mytimetables.inholland.nl")!;
        Assert.False(f.TeacherDetection);
        Assert.Equal("mt:111001", f.Canonical);
    }

    static Settings Load(ILogger log, params (string, string?)[] o) =>
        TestDb.LoadSettings(log, [("ConnectionStrings:Default", "Host=x"), ("InTheKnow:ApiKey", TestDb.ApiKey), .. o]);

    [Fact]
    public void IntervalsAreClampedWithAWarning()
    {
        var logs = new CapturingLogs();
        var o = Load(logs.CreateLogger("Settings"), ("InTheKnow:CheckInterval", "00:01:00")).Options;
        Assert.Equal(TimeSpan.FromMinutes(5), o.CheckInterval);
        Assert.Contains(logs.Lines, l => l.Contains("CheckInterval"));

        logs = new CapturingLogs();
        o = Load(logs.CreateLogger("Settings"), ("InTheKnow:FailingCheckInterval", "00:07:00")).Options;
        Assert.Equal(TimeSpan.FromMinutes(10), o.FailingCheckInterval);
        Assert.Contains(logs.Lines, l => l.Contains("FailingCheckInterval"));
    }

    [Theory]
    [InlineData("InTheKnow:MaxConcurrentChecks", "0")]
    [InlineData("InTheKnow:MaxConcurrentChecks", "65")]
    [InlineData("InTheKnow:MaxRequestsPerHostPerMinute", "0")]
    [InlineData("InTheKnow:MyTimetables:Domains:0:MaxRequestsPerMinute", "0")]
    [InlineData("InTheKnow:ExpansionHorizonMonths", "0")]
    [InlineData("InTheKnow:ChangeRetention", "00:00:00")]
    [InlineData("InTheKnow:MyTimetables:Domains:1:Host", "MyTimetables.Inholland.nl")]
    public void InvalidValuesFailStartup(string key, string value)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Load(NullLogger.Instance, (key, value)));
        Assert.Contains(key.Replace(":Host", "").Replace(":MaxRequestsPerMinute", ""), e.Message);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("64")]
    public void ConcurrencyBounds(string value) =>
        Assert.Equal(int.Parse(value), Load(NullLogger.Instance, ("InTheKnow:MaxConcurrentChecks", value)).Options.MaxConcurrentChecks);

    // 003 contracts/configuration.md: each rule fails startup with a message naming the key.
    const string N = "InTheKnow:Notifications:";
    static readonly (string, string?)[] Email =
    [
        (N + "Email:Host", "smtp.example.nl"), (N + "Email:FromAddress", "intheknow@example.nl"),
        (N + "PublicBaseUrl", "https://intheknow.example.nl"), (N + "FrontendBaseUrl", "https://app.example.nl"),
    ];

    static (string, string?)[] WithEmail(params (string Key, string? Value)[] o) => [.. Email.Where(e => o.All(x => x.Key != e.Item1)), .. o];

    [Theory]
    [InlineData("CollectionWindow", "00:00:59", "CollectionWindow", false)]
    [InlineData("CollectionWindow", "01:00:01", "CollectionWindow", false)]
    [InlineData("RetryWindow", "01:00:00", "RetryWindow", false)]
    [InlineData("DefaultTimeZone", "Nowhere/City", "DefaultTimeZone", false)]
    [InlineData("DefaultTimeZone", "W. Europe Standard Time", "DefaultTimeZone", false)]
    [InlineData("PublicBaseUrl", "", "PublicBaseUrl", true)]
    [InlineData("FrontendBaseUrl", "", "FrontendBaseUrl", true)]
    [InlineData("Email:FromAddress", "", "Email:FromAddress", true)]
    [InlineData("PublicBaseUrl", "http://intheknow.example.nl", "PublicBaseUrl", true)]
    [InlineData("FrontendBaseUrl", "http://app.example.nl", "FrontendBaseUrl", true)]
    [InlineData("Email:Port", "0", "Email:Port", true)]
    [InlineData("Email:Port", "65536", "Email:Port", true)]
    [InlineData("Email:StartTls", "false", "Email:StartTls", true)]
    [InlineData("Email:Username", "user", "Email:Username", true)]
    [InlineData("Email:Password", "secret-password", "Email:Username", true)]
    [InlineData("Email:FromName", "", "Email:FromName", true)]
    [InlineData("Email:FromName", "Line\nbreak", "Email:FromName", true)]
    [InlineData("Email:MaxMessagesPerMinute", "0", "Email:MaxMessagesPerMinute", true)]
    public void InvalidNotificationSettingsFailStartup(string key, string value, string named, bool withEmail)
    {
        (string, string?)[] o = withEmail ? WithEmail((N + key, value)) : [(N + key, value)];
        var e = Assert.Throws<InvalidOperationException>(() => Load(NullLogger.Instance, o));
        Assert.Contains(N + named, e.Message);
        Assert.DoesNotContain("secret-password", e.Message);
    }

    [Fact]
    public void NotificationSettingsBoundaries()
    {
        var s = TestDb.Offline();
        Assert.Equal("", s.Options.Notifications.Email.Host); // email not configured
        Assert.Equal("Europe/Amsterdam", s.DefaultTimeZone.Id);
        Assert.Equal(TimeSpan.FromMinutes(65), Load(NullLogger.Instance, (N + "RetryWindow", "01:05:00")).Options.Notifications.RetryWindow);
        Load(NullLogger.Instance, (N + "Email:FromName", new string('x', 100)));
        Assert.Throws<InvalidOperationException>(() => Load(NullLogger.Instance, (N + "Email:FromName", new string('x', 101))));
        Load(NullLogger.Instance, WithEmail((N + "Email:Host", "localhost"), (N + "Email:StartTls", "false"), (N + "PublicBaseUrl", "http://localhost:5000")));
        Load(NullLogger.Instance, WithEmail((N + "Email:Username", "u"), (N + "Email:Password", "p")));
    }
}
