using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Services.Google;
using Locus.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Locus.Core.Tests;

public sealed class GmailCalendarTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-gw-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly HttpListener _l = new();
    private readonly ServiceProvider _sp;
    private readonly List<JsonNode> _sent = [];
    private readonly List<JsonNode> _events = [];
    private string Url { get; }

    public GmailCalendarTests()
    {
        Url = $"http://127.0.0.1:{new Random().Next(20000, 40000)}";
        _l.Prefixes.Add(Url + "/");
        _l.Start();
        _ = Task.Run(Loop);
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddLocusCore(new PlatformInfo("test", false, CanRunProcesses: false));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        s.Google.ApiBaseOverride = Url;
        s.Google.GmailEnabled = s.Google.CalendarEnabled = true;
        store.SaveAsync(s).GetAwaiter().GetResult();
    }

    private static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task GmailAndCalendarToolsWork()
    {
        var auth = _sp.GetRequiredService<GoogleAuth>();
        var (url, completed) = await auth.StartSignInAsync("cid", "secret");
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Contains("gmail.modify", q["scope"]);
        Assert.Contains("calendar.events", q["scope"]);
        using (var http = new HttpClient()) await http.GetStringAsync($"{q["redirect_uri"]}?code=c&state={q["state"]}");
        await completed;

        var settings = await _sp.GetRequiredService<SettingsStore>().GetAsync();
        var tools = (await _sp.GetServices<IToolProvider>().OfType<GoogleWorkspaceToolProvider>().Single().GetToolsAsync(settings, default))
            .ToDictionary(t => t.Name);
        Assert.True(tools["gmail_send"].RequiresApproval);
        async Task<string> Run(string t, JsonObject a) => (await tools[t].ExecuteAsync(a, null!, default)).Text;

        var found = await Run("gmail_search", new());
        Assert.Contains("Anna <anna@x.test>", found);
        Assert.Contains("Lunch Friday?", found);
        Assert.Contains("Are you free", await Run("gmail_read", new() { ["id"] = "m1" }));

        Assert.Contains("Draft saved for Anna <anna@x.test>", await Run("gmail_draft", new() { ["body"] = "Yes — noon works! 🍝", ["reply_to_id"] = "m1" }));
        var draft = _sent.Single()["message"]!;
        Assert.Equal("t1", draft["threadId"]!.ToString());
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(draft["raw"]!.ToString().Replace('-', '+').Replace('_', '/'))));
        Assert.Contains("In-Reply-To: <abc@x.test>", raw);
        Assert.Contains("Subject: =?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Re: Lunch Friday?")), raw);

        Assert.Contains("Created", await Run("calendar_create", new() { ["title"] = "Lunch with Anna", ["start"] = "2026-11-06T12:00", ["attendees"] = "anna@x.test" }));
        Assert.Contains("Lunch with Anna", await Run("calendar_events", new() { ["from"] = "2026-11-06", ["to"] = "2026-11-07" }));
    }

    private static string Pad(string s) => s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');

    private async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); } catch { return; }
            var path = c.Request.Url!.AbsolutePath;
            var body = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
            JsonNode res = path switch
            {
                "/token" => new JsonObject { ["access_token"] = "a", ["refresh_token"] = "r", ["expires_in"] = 3600,
                    ["scope"] = $"{GoogleAuth.SheetsScope} {GoogleAuth.GmailScope} {GoogleAuth.CalendarScope}" },
                "/drive/v3/about" => new JsonObject { ["user"] = new JsonObject { ["emailAddress"] = "me@x.test" } },
                "/gmail/v1/users/me/messages" => new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["id"] = "m1" }) },
                "/gmail/v1/users/me/messages/m1" => new JsonObject
                {
                    ["id"] = "m1", ["threadId"] = "t1", ["snippet"] = "Are you free", ["labelIds"] = new JsonArray("INBOX", "UNREAD"),
                    ["payload"] = new JsonObject
                    {
                        ["headers"] = new JsonArray(
                            new JsonObject { ["name"] = "From", ["value"] = "Anna <anna@x.test>" },
                            new JsonObject { ["name"] = "Subject", ["value"] = "Lunch Friday?" },
                            new JsonObject { ["name"] = "Date", ["value"] = "Thu, 5 Nov 2026 09:00:00 +0000" },
                            new JsonObject { ["name"] = "Message-ID", ["value"] = "<abc@x.test>" }),
                        ["mimeType"] = "multipart/alternative",
                        ["parts"] = new JsonArray(new JsonObject { ["mimeType"] = "text/plain", ["body"] = new JsonObject { ["data"] = B64Url("Are you free for lunch on Friday?") } }),
                    },
                },
                "/gmail/v1/users/me/drafts" => Record(_sent, body, new JsonObject { ["id"] = "d1" }),
                "/calendar/v3/calendars/primary/events" when c.Request.HttpMethod == "POST" => Record(_events, body, null),
                "/calendar/v3/calendars/primary/events" => new JsonObject { ["items"] = new JsonArray(_events.Select(e => e.DeepClone()).ToArray()) },
                _ => new JsonObject(),
            };
            var b = Encoding.UTF8.GetBytes(res.ToJsonString());
            c.Response.ContentType = "application/json";
            await c.Response.OutputStream.WriteAsync(b);
            c.Response.Close();
        }
    }

    private static JsonNode Record(List<JsonNode> list, string body, JsonNode? reply)
    {
        var n = JsonNode.Parse(body)!;
        if (reply is null) { n["id"] = "e" + list.Count; reply = n.DeepClone(); }
        list.Add(n);
        return reply;
    }

    public void Dispose()
    {
        try { _l.Stop(); _l.Close(); } catch { }
        _sp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch (IOException) { }
    }
}
