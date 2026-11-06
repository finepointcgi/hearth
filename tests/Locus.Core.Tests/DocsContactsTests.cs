using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Services.Google;
using Locus.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Locus.Core.Tests;

public sealed class DocsContactsTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-docs-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly HttpListener _l = new();
    private readonly ServiceProvider _sp;
    private readonly List<(string path, string query, string body, string? contentType)> _requests = [];
    private string Url { get; }

    public DocsContactsTests()
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
        s.Google.DocsEnabled = s.Google.ContactsEnabled = true;
        store.SaveAsync(s).GetAwaiter().GetResult();
    }

    private async Task<Dictionary<string, ITool>> ConnectAsync()
    {
        var auth = _sp.GetRequiredService<GoogleAuth>();
        var (url, completed) = await auth.StartSignInAsync("cid", "secret");
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Contains("drive.readonly", q["scope"]);
        Assert.Contains("contacts.readonly", q["scope"]);
        using (var http = new HttpClient()) await http.GetStringAsync($"{q["redirect_uri"]}?code=c&state={q["state"]}");
        await completed;
        var settings = await _sp.GetRequiredService<SettingsStore>().GetAsync();
        var tools = new List<ITool>();
        foreach (var p in _sp.GetServices<IToolProvider>().Where(p => p is GoogleDocsToolProvider or ContactsToolProvider))
            tools.AddRange(await p.GetToolsAsync(settings, default));
        return tools.ToDictionary(t => t.Name);
    }

    [Fact]
    public async Task FindsReadsAndWritesGoogleDocs()
    {
        var tools = await ConnectAsync();
        async Task<string> Run(string t, JsonObject a) => (await tools[t].ExecuteAsync(a, null!, default)).Text;

        var found = await Run("drive_search", new() { ["query"] = "resume" });
        Assert.Contains("📄 Resume 2026 | modified", found);
        Assert.Contains("(id d1)", found);
        var search = _requests.Last(r => r.path == "/drive/v3/files");
        Assert.Contains("name contains 'resume' or fullText contains 'resume'", Uri.UnescapeDataString(search.query));

        var read = await Run("drive_read", new() { ["id"] = "d1" });
        Assert.Contains("# Resume 2026", read);
        Assert.Contains("Director of Advanced Software", read);
        Assert.Contains(_requests, r => r.path == "/drive/v3/files/d1/export" && r.query.Contains("text%2Fplain"));
        Assert.Contains("sheets_read", await Run("drive_read", new() { ["id"] = "s1" })); // Sheets go to the Sheets tools

        Assert.True(tools["docs_create"].RequiresApproval);
        var made = await Run("docs_create", new() { ["title"] = "Cover letter", ["content"] = "# Hello\n- **15 years** in software\n1. Apply" });
        Assert.Contains("Created Google Doc \"Cover letter\": https://docs.google.com/d/new", made);
        var upload = _requests.Last(r => r.path == "/upload/drive/v3/files");
        Assert.StartsWith("multipart/related", upload.contentType);
        Assert.Contains("\"mimeType\":\"application/vnd.google-apps.document\"", upload.body);
        Assert.Contains("<h1>Hello</h1><ul><li><b>15 years</b> in software</li></ul><ol><li>Apply</li></ol>", upload.body);
    }

    [Fact]
    public async Task FindsContacts()
    {
        var tools = await ConnectAsync();
        var result = (await tools["find_contact"].ExecuteAsync(new JsonObject { ["name"] = "dentist" }, null!, default)).Text;
        Assert.Equal("- Dr. Kim (Smile Dental): email kim@smile.test; phone (313) 555-0170 [Google]", result);
        Assert.Contains(_requests, r => r.path == "/people/v1/people:searchContacts" && r.query.Contains("query=&")); // warm-up
    }

    [Fact]
    public void MarkdownBecomesSimpleHtml()
    {
        Assert.Equal("<html><body><h2>Plan &amp; goals</h2><p>See <a href=\"https://x.test\">this</a> and <i>that</i></p></body></html>",
            DriveClient.ToHtml("## Plan & goals\nSee [this](https://x.test) and *that*"));
    }

    private async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); } catch { return; }
            var path = c.Request.Url!.AbsolutePath;
            var body = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
            lock (_requests) _requests.Add((path, c.Request.Url.Query, body, c.Request.ContentType));
            string text = path switch
            {
                "/token" => new JsonObject { ["access_token"] = "a", ["refresh_token"] = "r", ["expires_in"] = 3600,
                    ["scope"] = $"{GoogleAuth.DriveReadScope} {GoogleAuth.DriveFileScope} {GoogleAuth.ContactsScope}" }.ToJsonString(),
                "/drive/v3/about" => new JsonObject { ["user"] = new JsonObject { ["emailAddress"] = "me@x.test" } }.ToJsonString(),
                "/drive/v3/files" => """{"files":[{"id":"d1","name":"Resume 2026","mimeType":"application/vnd.google-apps.document","modifiedTime":"2026-11-01T10:00:00Z"}]}""",
                "/drive/v3/files/d1" => """{"name":"Resume 2026","mimeType":"application/vnd.google-apps.document","webViewLink":"https://docs.google.com/d/d1"}""",
                "/drive/v3/files/s1" => """{"name":"Job tracker","mimeType":"application/vnd.google-apps.spreadsheet"}""",
                "/drive/v3/files/d1/export" => "Mitchell McCollum\nDirector of Advanced Software",
                "/upload/drive/v3/files" => """{"id":"new","webViewLink":"https://docs.google.com/d/new"}""",
                "/people/v1/people:searchContacts" => c.Request.QueryString["query"] == "dentist"
                    ? """{"results":[{"person":{"names":[{"displayName":"Dr. Kim"}],"emailAddresses":[{"value":"kim@smile.test"}],"phoneNumbers":[{"value":"(313) 555-0170"}],"organizations":[{"name":"Smile Dental"}]}}]}"""
                    : "{}",
                _ => "{}",
            };
            var b = Encoding.UTF8.GetBytes(text);
            c.Response.ContentType = text.StartsWith('{') ? "application/json" : "text/plain";
            await c.Response.OutputStream.WriteAsync(b);
            c.Response.Close();
        }
    }

    public void Dispose()
    {
        try { _l.Stop(); _l.Close(); } catch { }
        _sp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch (IOException) { }
    }
}
