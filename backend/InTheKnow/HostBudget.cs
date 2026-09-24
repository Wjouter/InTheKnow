namespace InTheKnow;

// Even request spacing per source host: one request every 1 min / budget (FR-012a, FR-012b, research R4).
sealed class HostBudget(Settings settings)
{
    readonly Dictionary<string, DateTimeOffset> nextFree = [];

    public string[] BusyHosts(DateTimeOffset now)
    {
        lock (nextFree) return [.. nextFree.Where(kv => kv.Value > now).Select(kv => kv.Key)];
    }

    public DateTimeOffset? EarliestFree(DateTimeOffset now)
    {
        lock (nextFree) return nextFree.Values.Where(v => v > now).Select(v => (DateTimeOffset?)v).Min();
    }

    // The slot to send at, or null (and no change) when it is more than maxWait away.
    public DateTimeOffset? TryReserve(string host, DateTimeOffset now, TimeSpan maxWait)
    {
        host = host.ToLowerInvariant();
        lock (nextFree)
        {
            var slot = nextFree.TryGetValue(host, out var free) && free > now ? free : now;
            if (slot - now > maxWait) return null;
            nextFree[host] = slot + TimeSpan.FromMinutes(1) / settings.BudgetFor(host);
            return slot;
        }
    }
}
