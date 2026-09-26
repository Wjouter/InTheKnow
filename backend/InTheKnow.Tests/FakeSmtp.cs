using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace InTheKnow.Tests;

sealed record CapturedMail(string From, IReadOnlyList<string> Recipients, string Data);

// A scriptable SMTP server for tests (research R11): no TLS, no AUTH, one connection at a time.
// A mail is captured only when its DATA got 250.
sealed class FakeSmtp : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<string, int?> recipients = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, int?> dataReplies = new(StringComparer.OrdinalIgnoreCase);
    readonly List<CapturedMail> mails = [];
    Task loop = Task.CompletedTask;

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public IReadOnlyList<CapturedMail> Mails { get { lock (mails) return [.. mails]; } }
    public bool Refuse { get; set; }
    public int? DataReply { get; set; }

    // The RCPT reply for this address: 450, 550…; null = 250.
    public void Recipient(string address, int? code) => recipients[address] = code;

    // The DATA-end reply when this address is the recipient; takes precedence over DataReply.
    public void DataReplyFor(string address, int? code) => dataReplies[address] = code;

    public static Task<FakeSmtp> StartAsync()
    {
        var smtp = new FakeSmtp();
        smtp.listener.Start();
        smtp.loop = Task.Run(smtp.AcceptAsync);
        return Task.FromResult(smtp);
    }

    async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (Exception) when (stop.IsCancellationRequested) { return; }
            using (client)
            {
                if (Refuse) continue; // close without a greeting
                try { await SessionAsync(client.GetStream()); }
                catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
            }
        }
    }

    async Task SessionAsync(NetworkStream stream)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 localhost fake SMTP");
        string from = "";
        var to = new List<string>();
        while (await reader.ReadLineAsync(stop.Token) is { } line)
        {
            var verb = line.Split(' ', 2)[0].ToUpperInvariant();
            switch (verb)
            {
                case "EHLO" or "HELO":
                    await writer.WriteLineAsync("250 localhost");
                    break;
                case "MAIL":
                    (from, to) = (Address(line), []);
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "RCPT":
                    var address = Address(line);
                    if (recipients.TryGetValue(address, out var code) && code is { } c)
                        await writer.WriteLineAsync($"{c} scripted reply");
                    else
                    {
                        to.Add(address);
                        await writer.WriteLineAsync("250 OK");
                    }
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 go ahead");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync(stop.Token) is { } d && d != ".")
                        data.Append(d.StartsWith("..", StringComparison.Ordinal) ? d[1..] : d).Append("\r\n");
                    var reply = to.Select(r => dataReplies.TryGetValue(r, out var x) ? x : null).FirstOrDefault(x => x is not null) ?? DataReply;
                    if (reply is { } fail) await writer.WriteLineAsync($"{fail} scripted reply");
                    else
                    {
                        lock (mails) mails.Add(new(from, [.. to], data.ToString()));
                        await writer.WriteLineAsync("250 OK");
                    }
                    break;
                case "RSET" or "NOOP":
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 bye");
                    return;
                default:
                    await writer.WriteLineAsync("500 unknown command");
                    break;
            }
        }
    }

    static string Address(string line) => line[(line.IndexOf('<') + 1)..line.IndexOf('>')];

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        await loop;
        listener.Stop();
        stop.Dispose();
    }
}
