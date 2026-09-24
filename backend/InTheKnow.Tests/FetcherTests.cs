using System.Net;
using System.Net.Sockets;
using Xunit;

namespace InTheKnow.Tests;

public class FetcherTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12::1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("ff02::1", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void IsBlocked(string address, bool blocked) => Assert.Equal(blocked, Fetcher.IsBlocked(IPAddress.Parse(address)));

    [Theory]
    [InlineData("file:///etc/passwd", "unsupported_scheme")]
    [InlineData("ftp://x", "unsupported_scheme")]
    [InlineData("http://example.com/cal.ics", "unsupported_scheme")]
    [InlineData("not a url", "invalid_url")]
    public async Task CheckUrlRejects(string url, string reason)
    {
        var (uri, why) = await new Fetcher(new FakeHttp(), FakeHttp.Resolve).CheckUrlAsync(url, Ct);
        Assert.Null(uri);
        Assert.Equal(reason, why);
    }

    [Fact]
    public async Task CheckUrlRejectsLongUrl()
    {
        var url = "https://example.com/" + new string('a', 2049 - "https://example.com/".Length);
        Assert.Equal(2049, url.Length);
        Assert.Equal("url_too_long", (await new Fetcher(new FakeHttp(), FakeHttp.Resolve).CheckUrlAsync(url, Ct)).Reason);
    }

    [Fact]
    public async Task CheckUrlAcceptsPublicHttps()
    {
        var (uri, why) = await new Fetcher(new FakeHttp(), FakeHttp.Resolve).CheckUrlAsync("HTTPS://Example.com/cal.ics", Ct);
        Assert.Null(why);
        Assert.Equal("https://example.com/cal.ics", uri!.AbsoluteUri);
    }

    [Fact]
    public async Task RealHandlerRefusesLocalhost()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var result = await new Fetcher().FetchAsync(new Uri($"https://localhost:{port}/"), Ct);
        Assert.Equal("blocked_address", result.Error);
    }

    [Fact]
    public async Task SizeBound()
    {
        var http = new FakeHttp();
        http.Map["https://example.com/big"] = Reply.Bytes(10 * 1024 * 1024 + 1);
        http.Map["https://example.com/max"] = Reply.Bytes(10 * 1024 * 1024);
        var fetcher = new Fetcher(http, FakeHttp.Resolve);

        Assert.Equal("too_large", (await fetcher.FetchAsync(new Uri("https://example.com/big"), Ct)).Error);
        var ok = await fetcher.FetchAsync(new Uri("https://example.com/max"), Ct);
        Assert.Null(ok.Error);
        Assert.Equal(10 * 1024 * 1024, ok.Body!.Length);
    }

    [Fact]
    public async Task ErrorCodes()
    {
        var http = new FakeHttp();
        http.Map["https://example.com/http"] = Reply.Throw(new HttpRequestException("secret https://example.com/http"));
        http.Map["https://example.com/io"] = Reply.Throw(new IOException("boom"));
        http.Map["https://example.com/500"] = HttpStatusCode.InternalServerError;
        http.Map["https://example.com/slow"] = Reply.Timeout;
        var fetcher = new Fetcher(http, FakeHttp.Resolve);

        Assert.Equal("network", (await fetcher.FetchAsync(new Uri("https://example.com/http"), Ct)).Error);
        Assert.Equal("network", (await fetcher.FetchAsync(new Uri("https://example.com/io"), Ct)).Error);
        Assert.Equal("http_500", (await fetcher.FetchAsync(new Uri("https://example.com/500"), Ct)).Error);
        Assert.Equal("timeout", (await fetcher.FetchAsync(new Uri("https://example.com/slow"), Ct)).Error);
    }
}
