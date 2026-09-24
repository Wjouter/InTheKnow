using System.Collections.Concurrent;
using System.Net;

namespace InTheKnow.Tests;

// A response for one URL: a fixture file, a status code, or a special behaviour.
abstract record Reply
{
    public static implicit operator Reply(string fixture) => new FixtureReply(fixture);
    public static implicit operator Reply(HttpStatusCode status) => new StatusReply(status);
    public static readonly Reply Hang = new HangReply();
    public static readonly Reply Timeout = new TimeoutReply();
    public static Reply Bytes(int n) => new BytesReply(n);
    public static Reply Throw(Exception e) => new ThrowReply(e);
}
sealed record FixtureReply(string File) : Reply;
sealed record StatusReply(HttpStatusCode Status) : Reply;
sealed record HangReply : Reply;
sealed record TimeoutReply : Reply;
sealed record BytesReply(int Count) : Reply;
sealed record ThrowReply(Exception Exception) : Reply;

// Serves fixtures by absolute URL and records what was asked, when, and how many at once.
sealed class FakeHttp(TimeProvider? time = null) : HttpMessageHandler
{
    readonly TimeProvider time = time ?? TimeProvider.System;
    public readonly ConcurrentDictionary<string, Reply> Map = new();
    public readonly ConcurrentDictionary<string, int> PerUrl = new();
    public readonly ConcurrentDictionary<string, int> PerHost = new();
    public readonly ConcurrentQueue<(string Host, DateTimeOffset At)> Requests = new();
    public TaskCompletionSource? Gate { get; set; }
    int inFlight, maxInFlight;
    public int InFlight => Volatile.Read(ref inFlight);
    public int MaxInFlight => Volatile.Read(ref maxInFlight);

    public static Task<IPAddress[]> Resolve(string host, CancellationToken ct) => Task.FromResult(new[] { IPAddress.Parse("93.184.215.14") });

    public static string FixturePath(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", file);

    public int Count(string url) => PerUrl.GetValueOrDefault(url);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.AbsoluteUri;
        PerUrl.AddOrUpdate(url, 1, (_, n) => n + 1);
        PerHost.AddOrUpdate(request.RequestUri.Host, 1, (_, n) => n + 1);
        Requests.Enqueue((request.RequestUri.Host, time.GetUtcNow()));
        var now = Interlocked.Increment(ref inFlight);
        for (int max; now > (max = maxInFlight) && Interlocked.CompareExchange(ref maxInFlight, now, max) != max;) { }
        try
        {
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            return Map.TryGetValue(url, out var reply) ? await AnswerAsync(reply, ct) : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        finally { Interlocked.Decrement(ref inFlight); }
    }

    static async Task<HttpResponseMessage> AnswerAsync(Reply reply, CancellationToken ct)
    {
        switch (reply)
        {
            case FixtureReply f:
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(FixturePath(f.File), ct)) };
            case StatusReply s:
                return new(s.Status);
            case HangReply:
                await Task.Delay(System.Threading.Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            case TimeoutReply:
                throw new TaskCanceledException("fake HttpClient.Timeout");
            case BytesReply b:
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Enumerable.Repeat((byte)'A', b.Count).ToArray()) };
            case ThrowReply t:
                throw t.Exception;
            default:
                throw new ArgumentOutOfRangeException(nameof(reply));
        }
    }
}
