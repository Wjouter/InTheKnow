using System.Net;

namespace InTheKnow;

// Failed sign-in and code attempts (specs/004 research R8). The account key is the lowercased address, or
// "account:{id}" for the code step and step-up, so unknown addresses count and wait exactly like known ones (SC-003).
// ponytail: in-memory counters; the instance lock guarantees one process, so move them to PostgreSQL only if the app ever runs as several instances (research R8)
sealed class Throttle(TimeProvider time, Settings settings)
{
    const int Free = 5, KeyLimit = 20;
    static readonly TimeSpan Hour = TimeSpan.FromHours(1), MaxWait = TimeSpan.FromMinutes(15);
    static readonly TimeSpan[] Schedule =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8)];

    sealed class Pair { public int Count; public DateTimeOffset Last, Until; }
    sealed class Key { public readonly Queue<DateTimeOffset> Failures = new(); public DateTimeOffset Until; }

    readonly Lock gate = new();
    readonly Dictionary<(string Key, string Source), Pair> pairs = [];
    readonly Dictionary<string, Key> keys = [];
    readonly Dictionary<string, Queue<DateTimeOffset>> sources = [];

    // Null: this attempt may be evaluated. Otherwise how long the caller must wait.
    public TimeSpan? Wait(string accountKey, string source)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            var until = DateTimeOffset.MinValue;
            if (pairs.TryGetValue((accountKey, source), out var p) && p.Last > now - Hour) until = p.Until;
            if (keys.TryGetValue(accountKey, out var k) && k.Until > until) until = k.Until;
            if (sources.TryGetValue(source, out var q) && Prune(q, now).Count >= settings.Options.Accounts.SourceFailuresPerHour
                && q.Peek() + Hour > until)
                until = q.Peek() + Hour;
            return until > now ? until - now : null;
        }
    }

    public void Fail(string accountKey, string source)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            if (!pairs.TryGetValue((accountKey, source), out var p) || p.Last <= now - Hour) pairs[(accountKey, source)] = p = new();
            p.Count++;
            p.Last = now;
            if (p.Count >= Free) p.Until = now + (p.Count - Free < Schedule.Length ? Schedule[p.Count - Free] : MaxWait);

            if (!keys.TryGetValue(accountKey, out var k)) keys[accountKey] = k = new();
            Prune(k.Failures, now).Enqueue(now);
            // Every failure at or above 20 in the hour starts a new wait: 20, then one attempt per 15 minutes (SC-005).
            if (k.Failures.Count >= KeyLimit) k.Until = now + MaxWait;

            if (!sources.TryGetValue(source, out var q)) sources[source] = q = new();
            Prune(q, now).Enqueue(now);
        }
    }

    // Clears (key, source) and the key's all-source wait.
    public void Succeed(string accountKey, string source)
    {
        lock (gate)
        {
            pairs.Remove((accountKey, source));
            if (keys.TryGetValue(accountKey, out var k)) k.Until = DateTimeOffset.MinValue;
        }
    }

    // A completed password reset: every source of this key.
    public void Reset(string accountKey)
    {
        lock (gate)
        {
            foreach (var pair in pairs.Keys.Where(p => p.Key == accountKey).ToList()) pairs.Remove(pair);
            keys.Remove(accountKey);
        }
    }

    // Drops entries whose last failure is over an hour old.
    public void Forget(DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var (key, _) in pairs.Where(p => p.Value.Last <= now - Hour).ToList()) pairs.Remove(key);
            foreach (var (key, _) in keys.Where(k => Prune(k.Value.Failures, now).Count == 0 && k.Value.Until <= now).ToList()) keys.Remove(key);
            foreach (var (key, _) in sources.Where(s => Prune(s.Value, now).Count == 0).ToList()) sources.Remove(key);
        }
    }

    // IPv4 as is, IPv6 as its /64 (research R3), "unknown" without an address.
    public static string SourceKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    static Queue<DateTimeOffset> Prune(Queue<DateTimeOffset> q, DateTimeOffset now)
    {
        while (q.Count > 0 && q.Peek() <= now - Hour) q.Dequeue();
        return q;
    }
}
