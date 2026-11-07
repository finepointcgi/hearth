using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Locus.Core;
using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Services.Microsoft;
using Locus.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Locus.Core.Tests;

public sealed class MicrosoftTests : IAsyncDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-ms-").FullName;
    }

    /// <summary>Stands in for login.microsoftonline.com and graph.microsoft.com.</summary>
    private sealed class FakeMicrosoft : HttpMessageHandler
    {
        public readonly List<(HttpMethod method, string url, string body, string? auth)> Requests = [];
        public int Pending = 1;
        public int Issued;
        public Func<HttpMethod, string, HttpResponseMessage>? Graph;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content is null ? "" : req.Content.Headers.ContentType?.MediaType?.Contains("openxml") == true
                ? Convert.ToBase64String(await req.Content.ReadAsByteArrayAsync(ct)) : await req.Content.ReadAsStringAsync(ct);
            var url = req.RequestUri!.AbsoluteUri; // as sent (escaped)
            Requests.Add((req.Method, url, body, req.Headers.Authorization?.Parameter));
            if (url.EndsWith("/devicecode"))
                return Json("{\"device_code\":\"DC\",\"user_code\":\"ABCD-1234\",\"verification_uri\":\"https://microsoft.com/devicelogin\",\"interval\":1,\"expires_in\":60}");
            if (url.EndsWith("/token"))
            {
                if (body.Contains("device_code") && Pending-- > 0) return Json("{\"error\":\"authorization_pending\"}", HttpStatusCode.BadRequest);
                Issued++;
                return Json($"{{\"access_token\":\"AT{Issued}\",\"refresh_token\":\"RT{Issued}\",\"expires_in\":3600}}");
            }
            if (url.Contains("/me?$select")) return Json("{\"mail\":\"mitch@outlook.com\"}");
            return Graph?.Invoke(req.Method, url) ?? Json("{\"value\":[]}");
        }

        public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private readonly TempPaths _paths = new();
    private readonly FakeMicrosoft _ms = new();
    private readonly ServiceProvider _sp;

    public MicrosoftTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddLocusCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(new ScriptedTeam()));
        services.AddHttpClient(LlmClientFactory.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _ms);
        _sp = services.BuildServiceProvider();
        Auth.MinPoll = TimeSpan.FromMilliseconds(10);
    }

    private MicrosoftAuth Auth => _sp.GetRequiredService<MicrosoftAuth>();
    private const string ClientId = "1a2b3c4d-0000-0000-0000-000000000001";

    private async Task ConnectAsync()
    {
        var signIn = await Auth.StartSignInAsync(ClientId, "common");
        await signIn.Completed;
    }

    private async Task<ITool> ToolAsync(string name) =>
        (await _sp.GetServices<IToolProvider>().OfType<MicrosoftToolProvider>().Single().GetToolsAsync(new AppSettings(), default)).Single(t => t.Name == name);

    private static ToolContext Ctx() => new() { Conversation = new(), Reply = new(), Settings = new AppSettings(), Client = null!, Model = "m" };

    [Fact]
    public async Task SignsInWithACodeAndKeepsTheTokenSealed()
    {
        var signIn = await Auth.StartSignInAsync(ClientId, "common");
        Assert.Equal("ABCD-1234", signIn.UserCode);
        Assert.Equal("https://microsoft.com/devicelogin", signIn.VerificationUri);
        Assert.Equal("mitch@outlook.com", await signIn.Completed); // after one "authorization_pending"

        var conn = await Auth.GetAsync();
        Assert.True(conn.Connected);
        Assert.Equal("mitch@outlook.com", conn.Account);
        var file = await File.ReadAllTextAsync(Path.Combine(_paths.DataDirectory, "microsoft.json"));
        Assert.DoesNotContain("RT1", file); // the refresh token is encrypted on disk
        Assert.Contains(_ms.Requests, r => r.url.EndsWith("/common/oauth2/v2.0/devicecode") && r.body.Contains("Mail.Send") && r.body.Contains("offline_access"));

        Assert.ThrowsAsync<InvalidOperationException>(() => Auth.StartSignInAsync("not-a-guid", "common")).Wait();
    }

    [Fact]
    public async Task RefreshesExpiredTokensAndKeepsTheNewRefreshToken()
    {
        await ConnectAsync();
        // A new process (restart): no access token in memory, so it refreshes with the stored refresh token.
        var later = new MicrosoftAuth(_paths, _sp.GetRequiredService<IHttpClientFactory>());
        Assert.Equal("AT2", await later.AccessTokenAsync());
        Assert.Contains(_ms.Requests, r => r.body.Contains("grant_type=refresh_token") && r.body.Contains("refresh_token=RT1"));
        var again = new MicrosoftAuth(_paths, _sp.GetRequiredService<IHttpClientFactory>());
        Assert.Equal("AT3", await again.AccessTokenAsync());
        Assert.Contains(_ms.Requests, r => r.body.Contains("refresh_token=RT2")); // rotated token was saved
    }

    [Fact]
    public async Task ToolsOnlyAppearOnceConnectedAndUseGraph()
    {
        var provider = _sp.GetServices<IToolProvider>().OfType<MicrosoftToolProvider>().Single();
        Assert.Empty(await provider.GetToolsAsync(new AppSettings(), default));
        await ConnectAsync();
        var tools = await provider.GetToolsAsync(new AppSettings(), default);
        Assert.Equal(12, tools.Count);

        _ms.Graph = (_, url) => url.Contains("/me/messages?$search")
            ? FakeMicrosoft.Json("{\"value\":[{\"id\":\"M1\",\"subject\":\"Offer letter\",\"isRead\":false,\"receivedDateTime\":\"2026-11-05T14:00:00Z\",\"bodyPreview\":\"Congrats\",\"from\":{\"emailAddress\":{\"name\":\"Shield AI\",\"address\":\"hr@shield.ai\"}}}]}")
            : FakeMicrosoft.Json("{\"value\":[]}");
        var found = await (await ToolAsync("outlook_search")).ExecuteAsync(new JsonObject { ["query"] = "offer" }, Ctx(), default);
        Assert.Contains("● ", found.Text);
        Assert.Contains("Shield AI <hr@shield.ai> | Offer letter — Congrats (id M1)", found.Text);
        var search = _ms.Requests.Last();
        Assert.Contains("$search=%22offer%22", search.url);
        Assert.Equal("AT1", search.auth);
    }

    [Fact]
    public async Task WritesWordDocumentsAndExcelRanges()
    {
        await ConnectAsync();
        _ms.Graph = (method, url) => method == HttpMethod.Put
            ? FakeMicrosoft.Json("{\"id\":\"F1\",\"webUrl\":\"https://onedrive.live.com/F1\"}")
            : FakeMicrosoft.Json("{\"address\":\"Jobs!A1:B2\"}");

        var saved = await (await ToolAsync("onedrive_write")).ExecuteAsync(new JsonObject
        {
            ["path"] = "Documents/Cover letter.docx",
            ["content"] = "# Cover letter\nDear team,\n- **15 years** leading software\n- Defense and automotive",
        }, Ctx(), default);
        Assert.Contains("Saved Word document Documents/Cover letter.docx", saved.Text);
        var put = _ms.Requests.Last(r => r.method == HttpMethod.Put);
        Assert.EndsWith("/me/drive/root:/Documents/Cover%20letter.docx:/content", put.url);
        var text = WordDocs.ReadText(Convert.FromBase64String(put.body)); // a real .docx: read it back
        Assert.Contains("Cover letter", text);
        Assert.Contains("• 15 years leading software", text);

        var wrote = await (await ToolAsync("excel_write")).ExecuteAsync(new JsonObject
        {
            ["id"] = "W1", ["sheet"] = "Jobs", ["range"] = "A1:B2", ["values"] = "[[\"Company\",\"Role\"],[\"Shield AI\",\"Director\"]]",
        }, Ctx(), default);
        Assert.Equal("Wrote 2 row(s) to Jobs!A1:B2.", wrote.Text);
        var patch = _ms.Requests.Last(r => r.method == HttpMethod.Patch);
        Assert.Contains("/workbook/worksheets('Jobs')/range(address='A1%3AB2')", patch.url);
        Assert.Equal("{\"values\":[[\"Company\",\"Role\"],[\"Shield AI\",\"Director\"]]}", patch.body);
    }

    [Fact]
    public async Task ChangesCanBeUndone()
    {
        await ConnectAsync();
        _ms.Graph = (method, url) =>
            method == HttpMethod.Get && url.Contains("formulas") ? FakeMicrosoft.Json("{\"formulas\":[[\"Company\",\"=1+1\"],[\"\",\"\"]]}")
            : method == HttpMethod.Get && url.Contains(":/content") ? FakeMicrosoft.Json("{}", HttpStatusCode.NotFound) // a new file
            : method == HttpMethod.Put ? FakeMicrosoft.Json("{\"id\":\"F1\",\"webUrl\":\"https://onedrive.live.com/F1\"}")
            : method == HttpMethod.Post && url.EndsWith("/me/events") ? FakeMicrosoft.Json("{\"id\":\"E1\"}")
            : FakeMicrosoft.Json("{\"address\":\"Jobs!A1:B2\"}");
        var undo = _sp.GetRequiredService<UndoLog>();
        async Task<string> RunAsync(string tool, JsonObject args, string callId)
        {
            List<UndoStep> steps;
            using (UndoScope.Begin(out steps)) await (await ToolAsync(tool)).ExecuteAsync(args, Ctx(), default);
            await undo.AddAsync(callId, tool, null, "chat1", steps);
            return string.Join("; ", steps.Select(st => st.Description));
        }

        Assert.Equal("Put back Jobs!A1:B2", await RunAsync("excel_write", new JsonObject
            { ["id"] = "W1", ["sheet"] = "Jobs", ["range"] = "A1:B2", ["values"] = "[[\"a\",\"b\"],[\"c\",\"d\"]]" }, "c1"));
        Assert.Equal("Delete Notes.md", await RunAsync("onedrive_write", new JsonObject { ["path"] = "Documents/Notes.md", ["content"] = "hi" }, "c2"));
        Assert.Contains("Delete the event", await RunAsync("outlook_add_event", new JsonObject { ["subject"] = "Interview", ["start"] = "2026-11-09 14:00" }, "c3"));

        await undo.UndoAsync("c1");
        var restore = _ms.Requests.Last(r => r.method == HttpMethod.Patch);
        Assert.Equal("=1+1", JsonNode.Parse(restore.body)!["formulas"]![0]![1]!.ToString()); // formulas come back as formulas
        await undo.UndoAsync("c2");
        Assert.Contains(_ms.Requests, r => r.method == HttpMethod.Delete && r.url.EndsWith("/me/drive/items/F1"));
        Assert.Contains("Deleted the Outlook event", await undo.UndoAsync("c3"));
        Assert.Contains(_ms.Requests, r => r.method == HttpMethod.Delete && r.url.EndsWith("/me/events/E1"));
        Assert.Empty(await undo.RecentAsync()); // all undone
        Assert.Equal("Already undone.", (await undo.UndoAsync("c3"))[..15]);
    }

    [Fact]
    public async Task OneDriveFoldersStayIndexed()
    {
        await ConnectAsync();
        var docx = Convert.ToBase64String(WordDocs.FromMarkdown("# Cover letter\nShield AI director role, salary floor $230k"));
        _ms.Graph = (method, url) =>
            url.Contains("/me/drive/root:/Documents/Job%20Search?") ? FakeMicrosoft.Json("{\"id\":\"D1\",\"name\":\"Job Search\",\"webUrl\":\"https://od/D1\",\"folder\":{}}")
            : url.Contains("/items/D1/children") ? FakeMicrosoft.Json("{\"value\":[" +
                "{\"id\":\"F1\",\"name\":\"Cover letter.docx\",\"size\":100,\"lastModifiedDateTime\":\"2026-11-01T10:00:00Z\",\"file\":{}}," +
                "{\"id\":\"P1\",\"name\":\"photo.jpg\",\"size\":100,\"lastModifiedDateTime\":\"2026-11-01T10:00:00Z\",\"file\":{}}," +
                "{\"id\":\"D2\",\"name\":\"Offers\",\"folder\":{}}]}")
            : url.Contains("/items/D2/children") ? FakeMicrosoft.Json("{\"value\":[{\"id\":\"F2\",\"name\":\"anduril.txt\",\"size\":40,\"lastModifiedDateTime\":\"2026-11-02T10:00:00Z\",\"file\":{}}]}")
            : url.Contains("/items/F1/content") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String(docx)) }
            : url.Contains("/items/F2/content") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Anduril offer: $245k base") }
            : FakeMicrosoft.Json("{\"value\":[]}");
        var cloud = _sp.GetRequiredService<CloudFolders>();
        Assert.Null(await cloud.AddAsync("onedrive", "Documents/Job Search"));
        var folder = (await cloud.ListAsync()).Single();
        Assert.Equal("Job Search", folder.Name);
        await cloud.SyncAsync(folder.Id);
        folder = (await cloud.ListAsync()).Single();
        Assert.Equal(2, folder.Files); // the photo isn't text
        Assert.Null(folder.Error);
        var docs = _sp.GetRequiredService<DocumentStore>();
        var names = (await docs.ListAsync()).Select(d => d.Name).ToList();
        Assert.Contains("Cover letter.docx · OneDrive", names);
        Assert.Contains("Offers/anduril.txt · OneDrive", names);
        var hits = await docs.SearchAsync("Anduril offer", 3, default);
        Assert.Contains(hits, h => h.Text.Contains("$245k"));

        // Unchanged files aren't downloaded again; unlinking drops them from the library.
        var downloads = _ms.Requests.Count(r => r.url.Contains("/content"));
        await cloud.SyncAsync(folder.Id);
        Assert.Equal(downloads, _ms.Requests.Count(r => r.url.Contains("/content")));
        await cloud.RemoveAsync(folder.Id);
        Assert.Empty(await docs.ListAsync());
    }

    [Fact]
    public async Task SendingAsksAndReadingCountsAsUntrusted()
    {
        await ConnectAsync();
        var settings = new AppSettings();
        Assert.True(PermissionCatalog.NeedsApproval(await ToolAsync("outlook_send"), settings));
        Assert.True(PermissionCatalog.NeedsApproval(await ToolAsync("onedrive_write"), settings));
        Assert.False(PermissionCatalog.NeedsApproval(await ToolAsync("outlook_read"), settings));
        Assert.Equal(("microsoft.send", true), PermissionCatalog.Classify(await ToolAsync("outlook_send")));
        Assert.Equal("email", ContentTrust.UntrustedSource(await ToolAsync("outlook_read")));
        Assert.Equal("a file", ContentTrust.UntrustedSource(await ToolAsync("onedrive_read")));
        // Bots need the ability switched on (it's your email); chats without a bot always have it.
        Assert.False(new BotAbilities().Allows("outlook_search", new BotMemory()));
        Assert.True(new BotAbilities { Microsoft365 = true }.Allows("excel_write", new BotMemory()));
    }

    [Fact]
    public void WordRoundTrip()
    {
        var docx = WordDocs.FromMarkdown("## Plan\n1. Apply to Shield AI\nSalary floor **$230k** & up <ok>");
        var text = WordDocs.ReadText(docx);
        Assert.Contains("Plan", text);
        Assert.Contains("1. Apply to Shield AI", text);
        Assert.Contains("Salary floor $230k & up <ok>", text);
    }

    public async ValueTask DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }
}
