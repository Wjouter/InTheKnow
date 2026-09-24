using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace InTheKnow.Tests;

public class LinkIdentityTests
{
    const string Mt = "mytimetables.inholland.nl";

    // A synthetic MyTimetables link, shaped like the live ones (investigation.md §1): timetable is "!" + base64.
    internal static string MtLink(string id = "ALTI26VT4ES1", string kind = "xedule_studentgroup", string user = "1001",
                                  string? hex = "6ab568c1", string host = Mt, string? token = null)
    {
        var timetable = Convert.ToBase64String(Encoding.UTF8.GetBytes($"Xedule!{kind}!{Base32(id)}"));
        var eu = Convert.ToBase64String(Encoding.UTF8.GetBytes(user));
        token ??= Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(kind + id + user))).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var marker = hex is null ? "" : hex + "&";
        return $"https://{host}/ical?{marker}timetable=!{Uri.EscapeDataString(timetable)}&eu={Uri.EscapeDataString(eu)}&h={token}";
    }

    static string Base32(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(Encoding.ASCII.GetBytes(s).Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
        return string.Concat(bits.Chunk(5).Select(c => alphabet[Convert.ToInt32(new string(c).PadRight(5, '0'), 2)]));
    }

    static Settings S(params (string, string?)[] o) => TestDb.Offline(o);
    static string Key(string url, Settings? s = null) => LinkIdentity.KeyFor(new Uri(url), s ?? S()).Key;

    [Fact]
    public void Normalise()
    {
        Assert.Equal("https://example.com/a.ics?a=1&a=2&b&c=3",
            LinkIdentity.Normalise(new Uri("HTTPS://Example.COM:443/a.ics?c=3&b&a=2&a=1")));
        Assert.Equal("https://example.com:8443/A.ics", LinkIdentity.Normalise(new Uri("https://example.com:8443/A.ics")));
    }

    [Fact]
    public void HexMarkerIsIgnored()
    {
        var s = S(("InTheKnow:MyTimetables:Domains:0:ClassGroupSharing", "false"));
        Assert.Equal(Key(MtLink(hex: "6ab568c1"), s), Key(MtLink(hex: "6ab56946"), s));
        Assert.Equal(Key(MtLink(hex: "6ab568c1"), s), Key(MtLink(hex: null), s));
        Assert.True(LinkIdentity.KeyFor(new Uri(MtLink()), s).MyTimetablesDerived);
    }

    [Fact]
    public void ClassmatesShareTheGroup()
    {
        var a = LinkIdentity.KeyFor(new Uri(MtLink(user: "1001")), S());
        var b = LinkIdentity.KeyFor(new Uri(MtLink(user: "2002")), S());
        Assert.Equal(a, b);
        Assert.Equal($"mt|{Mt}|Xedule!xedule_studentgroup!{Base32("ALTI26VT4ES1")}", a.Key);
        Assert.True(a.MyTimetablesDerived);
        Assert.NotEqual(a.Key, Key(MtLink(id: "ALET26VT4A")));
    }

    [Fact]
    public void PersonalTimetablesStaySeparate()
    {
        Assert.NotEqual(Key(MtLink(kind: "xedule_personal", user: "1001")), Key(MtLink(kind: "xedule_personal", user: "2002")));
        Assert.Equal(Key(MtLink(kind: "xedule_personal", hex: "01")), Key(MtLink(kind: "xedule_personal", hex: "02")));
    }

    [Fact]
    public void UnlistedHostUsesTheNormalisedUrl()
    {
        var url = MtLink(host: "rooster.example.nl", user: "1001");
        var (key, derived) = LinkIdentity.KeyFor(new Uri(url), S());
        Assert.Equal(LinkIdentity.Normalise(new Uri(url)), key);
        Assert.False(derived);
        Assert.NotEqual(key, Key(MtLink(host: "rooster.example.nl", user: "2002")));
    }

    [Theory]
    [InlineData("/calendar", "timetable")]
    [InlineData("/ical", "eu")]
    [InlineData("/ical", "h")]
    [InlineData("/ical", "timetable")]
    public void MissingStructureFallsBack(string path, string drop)
    {
        var url = MtLink().Replace("/ical?", path + "?");
        var parts = new Uri(url).Query.TrimStart('?').Split('&').Where(p => path != "/ical" || !p.StartsWith(drop + "="));
        url = url[..url.IndexOf('?')] + "?" + string.Join('&', parts);
        var (key, derived) = LinkIdentity.KeyFor(new Uri(url), S());
        Assert.Equal(LinkIdentity.Normalise(new Uri(url)), key);
        Assert.False(derived);
    }

    [Fact]
    public void FeaturesOff()
    {
        var noSharing = S(("InTheKnow:MyTimetables:Domains:0:ClassGroupSharing", "false"));
        Assert.NotEqual(Key(MtLink(user: "1001"), noSharing), Key(MtLink(user: "2002"), noSharing));
        var none = S(("InTheKnow:MyTimetables:Domains:0:ClassGroupSharing", "false"), ("InTheKnow:MyTimetables:Domains:0:DuplicateLinkMerging", "false"));
        Assert.NotEqual(Key(MtLink(hex: "01"), none), Key(MtLink(hex: "02"), none));
    }

    [Fact]
    public void TimetableWithoutTheLeadingBangAlsoDecodes()
    {
        var bare = MtLink().Replace("timetable=!", "timetable=");
        Assert.Equal(Key(MtLink()), Key(bare));
    }

    [Fact]
    public void HashIs32Bytes() => Assert.Equal(32, LinkIdentity.Hash("x").Length);
}
