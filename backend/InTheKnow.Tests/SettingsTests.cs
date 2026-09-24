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
}
