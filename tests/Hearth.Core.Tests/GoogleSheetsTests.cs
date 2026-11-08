using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Core.Services.Google;
using Hearth.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Hearth.Core.Tests;

public class SheetsHelperTests
{
    [Theory]
    [InlineData(0, "A")] [InlineData(25, "Z")] [InlineData(26, "AA")] [InlineData(701, "ZZ")] [InlineData(702, "AAA")]
    public void ColumnNames(int i, string name) => Assert.Equal(name, SheetsClient.ColumnName(i));

    [Theory]
    [InlineData("Sales!B3:F20", 1, 3)] [InlineData("'My Tab'!$C$7", 2, 7)] [InlineData("A:F", 0, 1)] [InlineData("Sheet1", 0, 1)]
    public void StartOfRange(string range, int col, int row) => Assert.Equal((col, row), SheetsClient.StartOf(range));

    [Fact]
    public void RendersTableWithRealRowNumbers()
    {
        var t = SheetsClient.ToTable(new SheetValues("Sales!B3:C4", [["Name", "Total"], ["Ann", "4|2"]]));
        Assert.Contains("| row | B | C |", t);
        Assert.Contains("| 4 | Ann | 4\\|2 |", t);
    }

    [Fact]
    public void ParsesRowsEvenWhenStringified()
    {
        Assert.Equal([["a", "1"], ["b", "2"]], SheetsToolProvider.Rows(new JsonObject { ["rows"] = """[["a","1"],["b",2]]""" }).Select(r => r.Select(c => c.Trim('"')).ToList()));
        Assert.Equal("'My Tab'", SheetsToolProvider.Quote("My Tab"));
        Assert.Equal("Sales", SheetsToolProvider.Quote("Sales"));
    }
}

/// <summary>End-to-end against a fake Google API: service-account JWT, OAuth loopback, and the Sheets calls.</summary>
public sealed class GoogleSheetsEndToEndTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-google-").FullName;
    }

    private readonly FakeGoogle _google = new();
    private readonly TempPaths _paths = new();
    private readonly ServiceProvider _sp;

    public GoogleSheetsEndToEndTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: false));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        s.Google.ApiBaseOverride = _google.BaseUrl;
        store.SaveAsync(s).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task ServiceAccountReadsWritesAppendsAndCreates()
    {
        var auth = _sp.GetRequiredService<GoogleAuth>();
        using var rsa = RSA.Create(2048);
        var key = new JsonObject
        {
            ["type"] = "service_account",
            ["client_email"] = "hearth-bot@test.iam.gserviceaccount.com",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["token_uri"] = "https://oauth2.googleapis.com/token",
        }.ToJsonString();
        _google.PublicKey = rsa.ExportSubjectPublicKeyInfo();

        Assert.Equal("hearth-bot@test.iam.gserviceaccount.com", await auth.ConnectServiceAccountAsync(key));
        Assert.DoesNotContain("PRIVATE KEY", await File.ReadAllTextAsync(Path.Combine(_paths.DataDirectory, "google.json")));

        var tools = (await _sp.GetServices<IToolProvider>().OfType<SheetsToolProvider>().Single().GetToolsAsync(await _sp.GetRequiredService<SettingsStore>().GetAsync(), default))
            .ToDictionary(t => t.Name);
        Assert.True(tools["sheets_write"].RequiresApproval);
        Assert.False(tools["sheets_read"].RequiresApproval);

        async Task<string> Run(string tool, JsonObject args) => (await tools[tool].ExecuteAsync(args, null!, default)).Text;

        Assert.Contains("Budget 2026", await Run("sheets_find", new() { ["name"] = "Budget" }));
        var read = await Run("sheets_read", new() { ["spreadsheet"] = "Budget 2026" });
        Assert.Contains("| 2 | Rent | 1200 |", read);

        await Run("sheets_write", new() { ["spreadsheet"] = $"https://docs.google.com/spreadsheets/d/{FakeGoogle.SheetId}/edit", ["range"] = "Sheet1!B2", ["rows"] = new JsonArray(new JsonArray("1300")) });
        await Run("sheets_append", new() { ["spreadsheet"] = FakeGoogle.SheetId, ["rows"] = new JsonArray(new JsonArray("Food", "450")) });
        read = await Run("sheets_read", new() { ["spreadsheet"] = FakeGoogle.SheetId, ["range"] = "Sheet1" });
        Assert.Contains("| 2 | Rent | 1300 |", read);
        Assert.Contains("| 3 | Food | 450 |", read);

        Assert.Contains("Created \"Leads\"", await Run("sheets_create", new() { ["title"] = "Leads", ["headers"] = new JsonArray("Name", "Email") }));
        Assert.Contains("No spreadsheet named", await Run("sheets_read", new() { ["spreadsheet"] = "Nope" }));
    }

    [Fact]
    public async Task OAuthLoopbackSignInStoresEncryptedRefreshToken()
    {
        var auth = _sp.GetRequiredService<GoogleAuth>();
        var (url, completed) = await auth.StartSignInAsync("client-123.apps.googleusercontent.com", "shh-secret");
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Equal("S256", q["code_challenge_method"]);

        // Simulate Google redirecting the browser back to Hearth.
        using var http = new HttpClient();
        var page = await http.GetStringAsync($"{q["redirect_uri"]}?code=auth-code&state={q["state"]}");
        Assert.Contains("connected", page);
        Assert.Equal("me@example.com", await completed);

        var saved = await File.ReadAllTextAsync(Path.Combine(_paths.DataDirectory, "google.json"));
        Assert.DoesNotContain("refresh-xyz", saved);
        Assert.DoesNotContain("shh-secret", saved);
        Assert.Equal("access-refreshed", await auth.AccessTokenAsync());
    }

    public void Dispose()
    {
        _google.Dispose();
        _sp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Directory.Delete(_paths.DataDirectory, true);
    }

    /// <summary>Just enough of Google's token, Drive and Sheets endpoints.</summary>
    private sealed class FakeGoogle : IDisposable
    {
        public const string SheetId = "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";
        private readonly HttpListener _l;
        private readonly Dictionary<string, (string title, List<List<string>> rows)> _sheets = new()
        {
            [SheetId] = ("Budget 2026", [["Item", "Amount"], ["Rent", "1200"]]),
        };
        public byte[]? PublicKey { get; set; }
        public string BaseUrl { get; }

        public FakeGoogle()
        {
            (_l, var url) = LoopbackHttp.Listen();
            BaseUrl = url;
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_l.IsListening)
            {
                HttpListenerContext c;
                try { c = await _l.GetContextAsync(); } catch { return; }
                try { await Handle(c); } catch (Exception ex) { await Send(c, 500, new JsonObject { ["error"] = new JsonObject { ["message"] = ex.Message } }); }
            }
        }

        private async Task Handle(HttpListenerContext c)
        {
            var path = Uri.UnescapeDataString(c.Request.Url!.AbsolutePath);
            var body = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
            if (path == "/token")
            {
                var f = System.Web.HttpUtility.ParseQueryString(body);
                switch (f["grant_type"])
                {
                    case "urn:ietf:params:oauth:grant-type:jwt-bearer":
                        var parts = f["assertion"]!.Split('.');
                        using (var rsa = RSA.Create())
                        {
                            rsa.ImportSubjectPublicKeyInfo(PublicKey, out _);
                            var sig = Convert.FromBase64String(Pad(parts[2].Replace('-', '+').Replace('_', '/')));
                            if (!rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                            { await Send(c, 400, new JsonObject { ["error"] = "invalid_grant" }); return; }
                        }
                        await Send(c, 200, new JsonObject { ["access_token"] = "access-sa", ["expires_in"] = 3600 });
                        return;
                    case "authorization_code":
                        if (f["code"] != "auth-code" || f["code_verifier"] is null || f["client_secret"] != "shh-secret") { await Send(c, 400, new JsonObject { ["error"] = "invalid_grant" }); return; }
                        await Send(c, 200, new JsonObject { ["access_token"] = "access-1", ["refresh_token"] = "refresh-xyz", ["expires_in"] = 3600 });
                        return;
                    case "refresh_token":
                        await Send(c, f["refresh_token"] == "refresh-xyz" ? 200 : 400, new JsonObject { ["access_token"] = "access-refreshed", ["expires_in"] = 3600 });
                        return;
                }
            }
            if (c.Request.Headers["Authorization"] is not { } authz || !authz.StartsWith("Bearer ")) { await Send(c, 401, new JsonObject { ["error"] = new JsonObject { ["message"] = "no auth" } }); return; }

            if (path == "/drive/v3/about") { await Send(c, 200, new JsonObject { ["user"] = new JsonObject { ["emailAddress"] = "me@example.com" } }); return; }
            if (path == "/drive/v3/files")
            {
                var q = c.Request.QueryString["q"] ?? "";
                var name = q.Contains("name contains '") ? q.Split("name contains '")[1].TrimEnd('\'') : "";
                var files = _sheets.Where(s => s.Value.title.Contains(name, StringComparison.OrdinalIgnoreCase))
                    .Select(s => (JsonNode)new JsonObject { ["id"] = s.Key, ["name"] = s.Value.title, ["webViewLink"] = $"https://docs.google.com/spreadsheets/d/{s.Key}" });
                await Send(c, 200, new JsonObject { ["files"] = new JsonArray(files.ToArray()) });
                return;
            }
            if (path == "/v4/spreadsheets" && c.Request.HttpMethod == "POST")
            {
                var title = JsonNode.Parse(body)!["properties"]!["title"]!.ToString();
                var id = "NEW" + Guid.NewGuid().ToString("N");
                _sheets[id] = (title, []);
                await Send(c, 200, new JsonObject { ["spreadsheetId"] = id, ["spreadsheetUrl"] = $"https://docs.google.com/spreadsheets/d/{id}" });
                return;
            }
            var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // v4, spreadsheets, {id}, values, {range}
            var sid = seg[2].Split(':')[0];
            if (!_sheets.TryGetValue(sid, out var sheet)) { await Send(c, 404, new JsonObject { ["error"] = new JsonObject { ["message"] = "Requested entity was not found." } }); return; }
            if (seg.Length == 3)
            {
                await Send(c, 200, new JsonObject
                {
                    ["properties"] = new JsonObject { ["title"] = sheet.title },
                    ["sheets"] = new JsonArray(new JsonObject { ["properties"] = new JsonObject { ["title"] = "Sheet1", ["index"] = 0, ["gridProperties"] = new JsonObject { ["rowCount"] = 1000, ["columnCount"] = 26 } } }),
                });
                return;
            }
            var range = seg[4];
            if (range.EndsWith(":append"))
            {
                foreach (var r in JsonNode.Parse(body)!["values"]!.AsArray()) sheet.rows.Add(r!.AsArray().Select(x => x!.ToString()).ToList());
                await Send(c, 200, new JsonObject { ["updates"] = new JsonObject { ["updatedRange"] = $"Sheet1!A{sheet.rows.Count}" } });
                return;
            }
            if (c.Request.HttpMethod == "PUT")
            {
                var (col, row) = Hearth.Core.Services.Google.SheetsClient.StartOf(range);
                var vals = JsonNode.Parse(body)!["values"]!.AsArray();
                for (var i = 0; i < vals.Count; i++)
                {
                    while (sheet.rows.Count < row + i) sheet.rows.Add([]);
                    var target = sheet.rows[row + i - 1];
                    var cells = vals[i]!.AsArray();
                    for (var j = 0; j < cells.Count; j++)
                    {
                        while (target.Count <= col + j) target.Add("");
                        target[col + j] = cells[j]!.ToString();
                    }
                }
                await Send(c, 200, new JsonObject { ["updatedCells"] = vals.Sum(v => v!.AsArray().Count), ["updatedRange"] = range });
                return;
            }
            await Send(c, 200, new JsonObject
            {
                ["range"] = "Sheet1!A1:Z" + sheet.rows.Count,
                ["values"] = new JsonArray(sheet.rows.Select(r => (JsonNode)new JsonArray(r.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray())).ToArray()),
            });
        }

        private static string Pad(string s) => s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');

        private static async Task Send(HttpListenerContext c, int status, JsonObject json)
        {
            c.Response.StatusCode = status;
            var b = Encoding.UTF8.GetBytes(json.ToJsonString());
            c.Response.ContentType = "application/json";
            await c.Response.OutputStream.WriteAsync(b);
            c.Response.Close();
        }

        public void Dispose() => LoopbackHttp.Close(_l);
    }
}
