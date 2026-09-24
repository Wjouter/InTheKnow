using Xunit;

namespace InTheKnow.Tests;

public class HostBudgetTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    const string Mt = "mytimetables.inholland.nl";

    static HostBudget Budget() => new(TestDb.Offline(
        ("InTheKnow:MaxRequestsPerHostPerMinute", "10"),
        ("InTheKnow:MyTimetables:Domains:0:MaxRequestsPerMinute", "60")));

    [Fact]
    public void SlotsAreEvenlySpaced()
    {
        var b = Budget();
        Assert.Equal(T0, b.TryReserve("a.example", T0, TimeSpan.MaxValue));
        Assert.Equal(T0.AddSeconds(6), b.TryReserve("a.example", T0, TimeSpan.MaxValue));
        Assert.Equal(T0.AddSeconds(12), b.TryReserve("A.Example", T0, TimeSpan.MaxValue));
    }

    [Fact]
    public void DomainOverride()
    {
        var b = Budget();
        b.TryReserve(Mt, T0, TimeSpan.MaxValue);
        Assert.Equal(T0.AddSeconds(1), b.TryReserve(Mt, T0, TimeSpan.MaxValue));
        b.TryReserve("other.example", T0, TimeSpan.MaxValue);
        Assert.Equal(T0.AddSeconds(6), b.TryReserve("other.example", T0, TimeSpan.MaxValue));
    }

    [Fact]
    public void BusyUntilNextFree()
    {
        var b = Budget();
        b.TryReserve("a.example", T0, TimeSpan.MaxValue);
        Assert.Equal(["a.example"], b.BusyHosts(T0));
        Assert.Equal(T0.AddSeconds(6), b.EarliestFree(T0));
        Assert.Equal(["a.example"], b.BusyHosts(T0.AddSeconds(5.9)));
        Assert.Empty(b.BusyHosts(T0.AddSeconds(6)));
        Assert.Null(b.EarliestFree(T0.AddSeconds(6)));
    }

    [Fact]
    public void BoundedWaitLeavesStateAlone()
    {
        var b = Budget();
        b.TryReserve("a.example", T0, TimeSpan.MaxValue);  // next free at +6 s
        Assert.Null(b.TryReserve("a.example", T0, TimeSpan.FromSeconds(5)));
        Assert.Equal(T0.AddSeconds(6), b.EarliestFree(T0));
        Assert.Equal(T0.AddSeconds(6), b.TryReserve("a.example", T0.AddSeconds(1), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ReservationPushesNextSlotBack()
    {
        var b = Budget();
        b.TryReserve("a.example", T0, TimeSpan.MaxValue);
        b.TryReserve("a.example", T0.AddSeconds(2), TimeSpan.FromSeconds(5)); // a validation takes +6 s
        Assert.Equal(T0.AddSeconds(12), b.TryReserve("a.example", T0.AddSeconds(2), TimeSpan.MaxValue));
    }
}
