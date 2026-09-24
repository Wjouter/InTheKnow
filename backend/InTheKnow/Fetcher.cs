using System.Net;
using System.Net.Sockets;
using System.Text;

namespace InTheKnow;

sealed record FetchResult(string? Body, string? Error);

// One shared HttpClient with an SSRF guard at connect time (research R5). Never logs or returns the URL
// or exception messages, because links are secrets (FR-028).
sealed class Fetcher
{
    // ponytail: worst case 10 MB body (~20 MB string) + Ical.Net graph per check × MaxConcurrentChecks; lower the bound or stream-parse if strangers can add links
    const int MaxBytes = 10 * 1024 * 1024;
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    readonly HttpClient http;
    readonly Func<string, CancellationToken, Task<IPAddress[]>> resolve;

    public Fetcher(HttpMessageHandler? handler = null, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        this.resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        handler ??= new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectAsync,
        };
        http = new HttpClient(handler) { Timeout = Timeout, MaxResponseContentBufferSize = MaxBytes };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("InTheKnow/1.0 (timetable change detection)");
    }

    public static bool IsBlocked(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.IPv6Any)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return a.IsIPv6UniqueLocal || a.IsIPv6LinkLocal || a.IsIPv6Multicast;
        var b = a.GetAddressBytes();
        return b[0] == 0                                  // 0.0.0.0/8, unspecified
            || b[0] == 10                                 // 10.0.0.0/8
            || (b[0] == 172 && (b[1] & 0xF0) == 16)       // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168)               // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254)               // 169.254.0.0/16
            || (b[0] == 100 && (b[1] & 0xC0) == 64)       // 100.64.0.0/10
            || b[0] >= 224;                               // multicast, reserved, broadcast
    }

    public async Task<(Uri? Uri, string? Reason)> CheckUrlAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return (null, "invalid_url");
        if (uri.Scheme != Uri.UriSchemeHttps) return (null, "unsupported_scheme");
        if (uri.AbsoluteUri.Length > 2048) return (null, "url_too_long");
        IPAddress[] addresses;
        try { addresses = await resolve(uri.IdnHost, ct); }
        catch (SocketException) { addresses = []; }
        return addresses.Length == 0 || addresses.Any(IsBlocked) ? (null, "blocked_address") : (uri, null);
    }

    public async Task<FetchResult> FetchAsync(Uri url, CancellationToken ct)
    {
        // HttpClient.Timeout stops at the headers with ResponseHeadersRead, so the body read gets its own limit.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (!response.IsSuccessStatusCode) return new(null, $"http_{(int)response.StatusCode}");

            await using var body = await response.Content.ReadAsStreamAsync(limit.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(chunk, limit.Token)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > MaxBytes) return new(null, "too_large");
            }
            return new(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('﻿'), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (IsBlockedChain(e)) { return new(null, "blocked_address"); }
        catch (OperationCanceledException) { return new(null, "timeout"); }
        catch (HttpRequestException) { return new(null, "network"); }
        catch (IOException) { return new(null, "network"); }
    }

    static bool IsBlockedChain(Exception? e)
    {
        for (; e is not null; e = e.InnerException) if (e is BlockedAddressException) return true;
        return false;
    }

    async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await resolve(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(IsBlocked)) throw new BlockedAddressException();
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses[0], context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    sealed class BlockedAddressException : Exception;
}
