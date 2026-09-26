namespace InTheKnow.Tests;

// The test-only channel type (contracts/channel-type.md § Implementations): records what it is sent and can be
// scripted to fail, so the delivery suite runs over it and over email (SC-008).
sealed class RecordingChannel : IChannelType
{
    readonly Dictionary<string, (SendStatus Status, int Times, string Reason)> scripts = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(string Target, Message Message)> sent = [];

    public string Name => "test";
    public bool Configured { get; set; } = true;
    public int MaxMessagesPerMinute { get; set; } = 60;
    public bool Outage { get; set; }

    // Only calls that returned Sent.
    public List<(string Target, Message Message)> Sent { get { lock (sent) return [.. sent]; } }

    public void Script(string target, SendStatus status, int times, string reason = "scripted")
    {
        lock (scripts) scripts[target] = (status, times, reason);
    }

    public string? Normalize(string target)
    {
        var t = target.Trim();
        return t.Length is >= 1 and <= 254 ? t : null;
    }

    public Task<SendResult> SendAsync(string target, Message message, CancellationToken ct)
    {
        if (Outage) return Task.FromResult(new SendResult(SendStatus.TypeUnavailable, "unreachable"));
        lock (scripts)
            if (scripts.TryGetValue(target, out var s) && s.Times > 0)
            {
                scripts[target] = s with { Times = s.Times == int.MaxValue ? s.Times : s.Times - 1 };
                return Task.FromResult(new SendResult(s.Status, s.Reason));
            }
        lock (sent) sent.Add((target, message));
        return Task.FromResult(new SendResult(SendStatus.Sent));
    }
}
