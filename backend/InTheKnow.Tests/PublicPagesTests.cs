using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using static InTheKnow.Tests.LinkIdentityTests;

namespace InTheKnow.Tests;

// The /n/ pages (research R8): no operator key, GET changes nothing, POST acts on its own channel only.
public class PublicPagesTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    const string A = "a@example.nl", B = "b@example.nl";

    static async Task<(HttpStatusCode Status, string Html, string? ContentType)> GetAsync(DeliveryHost d, string url)
    {
        using var bare = d.H.Factory.CreateClient();
        var r = await bare.GetAsync(new Uri(url).PathAndQuery, Ct);
        return (r.StatusCode, await r.Content.ReadAsStringAsync(Ct), r.Content.Headers.ContentType?.ToString());
    }

    static async Task<(HttpStatusCode Status, string Html)> PostAsync(DeliveryHost d, string url, HttpContent? body = null)
    {
        var r = await d.PostPageAsync(url, body);
        return (r.StatusCode, await r.Content.ReadAsStringAsync(Ct));
    }

    static async Task<Message> ConfirmationAsync(DeliveryHost d, SignedIn who, string target)
    {
        Assert.Equal(HttpStatusCode.Created, (await d.PostAsync(who, "/api/me/channels", new { type = d.C.Type, target })).StatusCode);
        await d.NotifyAsync(3);
        return d.To(target).Last(m => m.Kind == MessageKind.Confirmation);
    }

    static Task<string> StatusAsync(DeliveryHost d, string target) => d.TextAsync("SELECT status FROM channel WHERE target = $1", target);

    static string T(string language, string key) => System.Net.WebUtility.HtmlEncode(Texts.Get(language, key)).Replace("&#39;", "&#x27;");

    [Fact]
    public async Task ConfirmPage()
    {
        await using var d = await DeliveryHost.CreateAsync("test");
        var m = await ConfirmationAsync(d, d.S1, A);

        var get = await GetAsync(d, m.ConfirmUrl!);
        Assert.Equal((HttpStatusCode.OK, "text/html; charset=utf-8"), (get.Status, get.ContentType));
        Assert.Single(Regex.Matches(get.Html, "<form method=\"post\">"));
        Assert.Contains("<html lang=\"en\">", get.Html);
        Assert.Equal("awaitingConfirmation", await StatusAsync(d, A));

        var post = await PostAsync(d, m.ConfirmUrl!);
        Assert.Equal(HttpStatusCode.OK, post.Status);
        Assert.Contains(T("en", "page.confirm.done"), post.Html);
        Assert.Equal("active", await StatusAsync(d, A));
        Assert.Contains(T("en", "page.alreadyDone"), (await PostAsync(d, m.ConfirmUrl!)).Html);
        Assert.DoesNotContain(A, get.Html + post.Html);
    }

    // 004 FR-030: every /n/ page, GET and POST, works with no Authorization header (the helpers use a bare client).
    [Fact]
    public async Task PagesNeedNoSession()
    {
        await using var d = await DeliveryHost.CreateAsync("test");
        var m = await ConfirmationAsync(d, d.S1, A);
        var unsubscribe = $"{EmailChannelTests.Public}/n/unsubscribe/{await d.TextAsync("SELECT unsubscribe_token FROM channel WHERE target = $1", A)}";
        using var bare = d.H.Factory.CreateClient();
        Assert.Null(bare.DefaultRequestHeaders.Authorization);
        foreach (var url in new[] { m.ConfirmUrl!, m.DeclineUrl!, unsubscribe })
            Assert.Equal(HttpStatusCode.OK, (await bare.GetAsync(new Uri(url).PathAndQuery, Ct)).StatusCode);
        foreach (var url in new[] { m.ConfirmUrl!, unsubscribe, m.DeclineUrl! })
            Assert.Equal(HttpStatusCode.OK, (await bare.PostAsync(new Uri(url).PathAndQuery, null, Ct)).StatusCode);
    }

    [Fact]
    public async Task ExpiredAndUnavailable()
    {
        await using var d = await DeliveryHost.CreateAsync("test");
        var expired = await ConfirmationAsync(d, d.S1, A);
        d.H.Time.Advance(TimeSpan.FromHours(48) + TimeSpan.FromSeconds(1));
        var post = await PostAsync(d, expired.ConfirmUrl!);
        Assert.Equal(HttpStatusCode.OK, post.Status);
        Assert.Contains(T("en", "page.expired"), post.Html);
        Assert.Equal("awaitingConfirmation", await StatusAsync(d, A));

        var unavailable = await ConfirmationAsync(d, d.S1, B);
        ((TestTypeUnderTest)d.C).Channel.Configured = false;
        Assert.Contains(T("en", "page.unavailable"), (await PostAsync(d, unavailable.ConfirmUrl!)).Html);
        Assert.Equal("awaitingConfirmation", await StatusAsync(d, B));
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("decline")]
    [InlineData("unsubscribe")]
    public async Task UnknownTokens(string action)
    {
        await using var d = await DeliveryHost.CreateAsync("test");
        foreach (var token in new[] { Channels.NewToken(), "short", "%3Cscript%3E" })
        {
            var url = $"{EmailChannelTests.Public}/n/{action}/{token}";
            foreach (var (status, html) in new[] { await PostAsync(d, url), ((await GetAsync(d, url)).Status, (await GetAsync(d, url)).Html) })
            {
                Assert.Equal(HttpStatusCode.NotFound, status);
                Assert.Contains(T("en", "page.notFound.text"), html);
                Assert.Contains(T("nl", "page.notFound.text"), html);
                Assert.DoesNotContain("<script", html);
            }
        }
    }

    // US2-4, FR-006, FR-010, SC-003 (008), FR-003 (008): links in emails, the footer unsubscribe link too, change nothing until the button is used.
    [Fact]
    public async Task DeclineAndUnsubscribePages()
    {
        await using var d = await DeliveryHost.CreateAsync("test");
        var m = await ConfirmationAsync(d, d.S1, A);
        var other = await d.AddConfirmedChannelAsync(d.S1, B);
        var decline = m.DeclineUrl!;
        var unsubscribe = $"{EmailChannelTests.Public}/n/unsubscribe/{await d.TextAsync("SELECT unsubscribe_token FROM channel WHERE id = $1", other)}";

        foreach (var url in new[] { decline, unsubscribe })
        {
            var get = await GetAsync(d, url);
            Assert.Equal(HttpStatusCode.OK, get.Status);
            Assert.Single(Regex.Matches(get.Html, "<form method=\"post\">"));
        }
        Assert.Equal(("awaitingConfirmation", "active"), (await StatusAsync(d, A), await StatusAsync(d, B)));

        Assert.Contains(T("en", "page.decline.done"), (await PostAsync(d, decline)).Html);
        Assert.Equal(("unsubscribed", "active"), (await StatusAsync(d, A), await StatusAsync(d, B)));
        Assert.Contains(T("en", "page.declined"), (await PostAsync(d, m.ConfirmUrl!)).Html);
        Assert.Equal("unsubscribed", await StatusAsync(d, A));

        // RFC 8058 one-click, and a plain POST with no body.
        var oneClick = await PostAsync(d, unsubscribe, new FormUrlEncodedContent([new("List-Unsubscribe", "One-Click")]));
        Assert.Equal(HttpStatusCode.OK, oneClick.Status);
        Assert.Contains(T("en", "page.unsubscribe.done"), oneClick.Html);
        Assert.Equal("unsubscribed", await StatusAsync(d, B));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(d, unsubscribe)).Status);
    }

    // FR-031, SC-010: no address, token, /n/ path or calendar link in any log line.
    [Fact]
    public async Task NothingSensitiveIsLogged()
    {
        await using var d = await DeliveryHost.CreateAsync("email");
        var url = MtLink(user: "1001");
        await d.SubscribeAsync(d.S1, url);
        var m = await ConfirmationAsync(d, d.S1, A);
        await GetAsync(d, m.ConfirmUrl!);
        await PostAsync(d, m.ConfirmUrl!);
        var declined = await ConfirmationAsync(d, d.S1, B);
        await PostAsync(d, declined.DeclineUrl!);
        await d.RecordChangesAsync(url, "mt-nl-edited.ics");
        await d.AdvanceAndNotifyAsync(d.Window);
        var digest = Assert.Single(d.To(A), x => x.Kind == MessageKind.Changes);
        await PostAsync(d, digest.UnsubscribeUrl!, new FormUrlEncodedContent([new("List-Unsubscribe", "One-Click")]));

        // A failed send, and a rejected one.
        var c = await d.AddActiveChannelAsync(d.S1, "c@example.nl");
        d.C.RejectPermanently("c@example.nl");
        Assert.Equal(HttpStatusCode.Accepted, (await d.PostAsync(d.S1, $"/api/me/channels/{c}/test")).StatusCode);
        await d.NotifyAsync(3);
        Assert.Equal("failing", await StatusAsync(d, "c@example.nl"));

        var secrets = new[] { A, B, "c@example.nl", "/n/", "/ical?", m.ConfirmUrl![^43..], digest.UnsubscribeUrl![^43..] };
        Assert.NotEmpty(d.H.Logs.Lines);
        foreach (var line in d.H.Logs.Lines)
            foreach (var secret in secrets)
                Assert.DoesNotContain(secret, line);
        Assert.Contains(d.H.Logs.Lines, l => l.Contains("send failed with smtp_550"));
    }

    // US3-4: confirmation pages follow the email's language; unsubscribe pages the current one.
    [Fact]
    public async Task PageLanguages()
    {
        await using var d = await DeliveryHost.CreateAsync("test");
        Assert.Equal(HttpStatusCode.OK, (await d.PutAsync(d.S1, "/api/me/notification-settings", """{"language":"nl"}""")).StatusCode);
        var m = await ConfirmationAsync(d, d.S1, A);
        Assert.Equal(HttpStatusCode.OK, (await d.PutAsync(d.S1, "/api/me/notification-settings", """{"language":"en"}""")).StatusCode);
        foreach (var url in new[] { m.ConfirmUrl!, m.DeclineUrl! })
        {
            var get = await GetAsync(d, url);
            Assert.Contains("<html lang=\"nl\">", get.Html);
            Assert.Contains(T("nl", url == m.ConfirmUrl ? "page.confirm.button" : "page.decline.button"), get.Html);
        }
        var confirmed = await PostAsync(d, m.ConfirmUrl!);
        Assert.Contains("<html lang=\"nl\">", confirmed.Html);
        Assert.Contains(T("nl", "page.confirm.done"), confirmed.Html);

        var unsubscribe = $"{EmailChannelTests.Public}/n/unsubscribe/{await d.TextAsync("SELECT unsubscribe_token FROM channel WHERE target = $1", A)}";
        Assert.Contains("<html lang=\"en\">", (await GetAsync(d, unsubscribe)).Html);
        Assert.Equal(HttpStatusCode.OK, (await d.PutAsync(d.S1, "/api/me/notification-settings", """{"language":"nl"}""")).StatusCode);
        var post = await PostAsync(d, unsubscribe);
        Assert.Contains("<html lang=\"nl\">", post.Html);
        Assert.Contains(T("nl", "page.unsubscribe.done"), post.Html);
    }
}
