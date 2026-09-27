using System.Net;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace InTheKnow.Tests;

// specs/004 research R8, FR-012, SC-005.
public class ThrottleTests
{
    readonly FakeTimeProvider time = new(Harness.T0);

    Throttle New(int sourceFailures = 3000) =>
        new(time, TestDb.Offline(("InTheKnow:Accounts:SourceFailuresPerHour", sourceFailures.ToString())));

    static TimeSpan Min(double m) => TimeSpan.FromMinutes(m);

    [Fact]
    public void PerKeyAndSourceSchedule()
    {
        var t = New();
        for (var i = 0; i < 4; i++)
        {
            t.Fail("a@x.nl", "s1");
            Assert.Null(t.Wait("a@x.nl", "s1"));
        }
        t.Fail("a@x.nl", "s1");
        Assert.Equal(TimeSpan.FromSeconds(30), t.Wait("a@x.nl", "s1"));
        Assert.Null(t.Wait("a@x.nl", "s2")); // another source is unaffected
        foreach (var expected in new[] { Min(1), Min(2), Min(4), Min(8), Min(15), Min(15) })
        {
            time.Advance(t.Wait("a@x.nl", "s1")!.Value); // a refused attempt is not counted
            Assert.Null(t.Wait("a@x.nl", "s1"));
            t.Fail("a@x.nl", "s1");
            Assert.Equal(expected, t.Wait("a@x.nl", "s1"));
        }
    }

    [Fact]
    public void PerKeyAcrossSources()
    {
        var t = New();
        for (var i = 0; i < 20; i++) t.Fail("a@x.nl", $"s{i % 5}");
        foreach (var s in new[] { "s0", "s4", "fresh" }) Assert.Equal(Min(15), t.Wait("a@x.nl", s));
        time.Advance(Min(15));
        Assert.Null(t.Wait("a@x.nl", "fresh")); // one attempt when the wait ends
        t.Fail("a@x.nl", "fresh");
        Assert.Equal(Min(15), t.Wait("a@x.nl", "other"));
        Assert.Null(t.Wait("b@x.nl", "other"));
    }

    // SC-005: from fresh sources, at most 23 failures are evaluated in any rolling hour (20, then one per 15-minute wait).
    [Fact]
    public void WorstCaseIs23PerRollingHour()
    {
        var t = New();
        var failures = new List<DateTimeOffset>();
        var n = 0;
        for (var step = 0; step < 4 * 3600; step++, time.Advance(TimeSpan.FromSeconds(1)))
            while (t.Wait("a@x.nl", $"src{n}") is null)
            {
                t.Fail("a@x.nl", $"src{n++}");
                failures.Add(time.GetUtcNow());
            }
        var max = failures.Max(start => failures.Count(f => f >= start && f < start + TimeSpan.FromHours(1)));
        Assert.Equal(23, max);
    }

    [Fact]
    public void PerSourceAcrossKeys()
    {
        var t = New(sourceFailures: 20);
        for (var i = 0; i < 20; i++)
        {
            t.Fail($"k{i}@x.nl", "campus");
            time.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Equal(TimeSpan.FromHours(1) - Min(20), t.Wait("k20@x.nl", "campus"));
        Assert.Null(t.Wait("k20@x.nl", "elsewhere"));
        time.Advance(TimeSpan.FromHours(1) - Min(20));
        Assert.Null(t.Wait("k20@x.nl", "campus"));
    }

    [Fact]
    public void SucceedAndReset()
    {
        var t = New();
        for (var i = 0; i < 20; i++) t.Fail("a@x.nl", i < 5 ? "s1" : $"s{i}");
        Assert.NotNull(t.Wait("a@x.nl", "s1"));
        t.Succeed("a@x.nl", "s1");
        Assert.Null(t.Wait("a@x.nl", "s1"));
        Assert.Null(t.Wait("a@x.nl", "s9"));

        for (var i = 0; i < 5; i++) { t.Fail("b@x.nl", "s1"); t.Fail("b@x.nl", "s2"); }
        t.Reset("b@x.nl");
        Assert.Null(t.Wait("b@x.nl", "s1"));
        Assert.Null(t.Wait("b@x.nl", "s2"));
        t.Fail("b@x.nl", "s1");
        Assert.Null(t.Wait("b@x.nl", "s1")); // counting starts again
    }

    [Fact]
    public void ForgetDropsOldEntries()
    {
        var t = New();
        for (var i = 0; i < 5; i++) t.Fail("a@x.nl", "s1");
        time.Advance(Min(59));
        t.Forget(time.GetUtcNow());
        t.Fail("a@x.nl", "s1");
        Assert.Equal(Min(1), t.Wait("a@x.nl", "s1")); // still the 6th failure
        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        t.Forget(time.GetUtcNow());
        t.Fail("a@x.nl", "s1");
        Assert.Null(t.Wait("a@x.nl", "s1")); // forgotten: the 1st again
    }

    // Keys are plain strings: unknown addresses behave like known ones, and an address key never makes account:{id} wait.
    [Fact]
    public void KeysAreIndependent()
    {
        var t = New();
        for (var i = 0; i < 20; i++) t.Fail("known@x.nl", $"s{i}");
        for (var i = 0; i < 20; i++) t.Fail("unknown@x.nl", $"s{i}");
        Assert.Equal(t.Wait("known@x.nl", "s0"), t.Wait("unknown@x.nl", "s0"));
        Assert.Null(t.Wait("account:1", "s0"));
    }

    [Fact]
    public void SourceKeys()
    {
        static string K(string a) => Throttle.SourceKey(IPAddress.Parse(a));
        Assert.Equal(K("2001:db8:1:2:3:4:5:6"), K("2001:db8:1:2:ffff::1"));
        Assert.NotEqual(K("2001:db8:1:2:3:4:5:6"), K("2001:db8:1:3::1"));
        Assert.Equal("2001:db8:1:2::/64", K("2001:db8:1:2:3:4:5:6"));
        Assert.Equal("192.0.2.1", K("192.0.2.1"));
        Assert.Equal("192.0.2.1", K("::ffff:192.0.2.1"));
        Assert.Equal("unknown", Throttle.SourceKey(null));
    }
}
