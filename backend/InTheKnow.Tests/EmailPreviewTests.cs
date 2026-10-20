using Xunit;

namespace InTheKnow.Tests;

// Opt-in: sends every email shape in both languages to a local SMTP catcher (specs/007 quickstart.md).
public class EmailPreviewTests
{
    [Fact]
    public async Task SendEveryFixture()
    {
        var port = Environment.GetEnvironmentVariable("INTHEKNOW_PREVIEW_SMTP_PORT");
        Assert.SkipWhen(port is null, "set INTHEKNOW_PREVIEW_SMTP_PORT (Mailpit: 1025) to send every email shape");
        var email = new EmailChannel(TestDb.Offline(EmailChannelTests.EmailSettings(int.Parse(port!))));
        foreach (var language in new[] { "en", "nl" })
            foreach (var (name, message) in EmailChannelTests.EmailFixtures(language))
            {
                var result = await email.SendAsync($"{name}.{language}@example.test", message, TestContext.Current.CancellationToken);
                Assert.Equal(SendStatus.Sent, result.Status);
            }
    }
}
