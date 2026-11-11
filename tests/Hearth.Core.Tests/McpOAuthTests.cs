using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Core.Tools;
using ModelContextProtocol.Client;

namespace Hearth.Core.Tests;

/// <summary>
/// MCP authorization end to end against fakes: a protected MCP server that answers 401 with resource metadata, and an
/// identity provider. The real MCP client discovers the provider, Hearth catches the loopback redirect, and the tool call
/// then carries the token.
/// </summary>
public sealed class McpOAuthTests
{
    private const string Token = "access-token-for-ann";

    [Fact]
    public async Task Signs_in_through_the_servers_identity_provider_and_calls_tools()
    {
        var (listener, baseUrl) = LoopbackHttp.Listen();
        var codes = 0;
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(ctx, baseUrl, () => Interlocked.Increment(ref codes)));
            }
        });
        try
        {
            var server = new McpServerConfig { Name = "keel", Transport = McpTransport.Http, Url = baseUrl + "/mcp", OAuthClientId = "keel-mcp" };
            using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            var opened = new List<string>();
            var oauth = McpOAuth.OptionsFor(server, new PlatformInfo("test", false, CanRunProcesses: true), url =>
            {
                opened.Add(url);
                _ = browser.GetAsync(url);   // the "browser" follows the IdP's redirect back to Hearth's loopback listener
            })!;
            Assert.Equal("keel-mcp", oauth.ClientId);
            Assert.StartsWith("http://127.0.0.1:", oauth.RedirectUri!.ToString());

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(server.Url), Name = "keel", TransportMode = HttpTransportMode.StreamableHttp, OAuth = oauth,
            });
            await using var client = await McpClient.CreateAsync(transport);
            var tools = await client.ListToolsAsync();
            Assert.Equal("search_parts", Assert.Single(tools).Name);
            var result = await client.CallToolAsync("search_parts", new Dictionary<string, object?>());
            Assert.Contains("P-000123", ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text);
            Assert.Single(opened);
            Assert.Contains("code_challenge=", opened[0]);
            Assert.Equal(1, codes);
        }
        finally
        {
            LoopbackHttp.Close(listener);
            McpOAuth.Forget("keel");
        }
    }

    [Fact]
    public void Servers_without_a_client_id_or_without_a_desktop_dont_use_oauth()
    {
        Assert.Null(McpOAuth.OptionsFor(new McpServerConfig { Transport = McpTransport.Http, Url = "http://x/mcp" }, new PlatformInfo("test", false, CanRunProcesses: true)));
        Assert.Throws<PlatformNotSupportedException>(() => McpOAuth.OptionsFor(
            new McpServerConfig { Transport = McpTransport.Http, Url = "http://x/mcp", OAuthClientId = "keel-mcp" }, new PlatformInfo("test", false, CanRunProcesses: false)));
    }

    [Fact]
    public void Header_values_are_masked_and_kept_when_unchanged()
    {
        var stored = new Dictionary<string, string> { ["Authorization"] = "Bearer wbk_secretvalue1234", ["X-Team"] = "ab" };
        var shown = McpHeaderMask.Show(stored);
        Assert.DoesNotContain("secretvalue", shown);
        Assert.Contains("Authorization: Bearer ••••••1234", shown);
        Assert.Equal(stored, McpHeaderMask.Parse(shown, stored));
        var edited = McpHeaderMask.Parse(shown.Replace("Bearer ••••••1234", "Bearer wbk_new"), stored);
        Assert.Equal("Bearer wbk_new", edited["Authorization"]);
        Assert.Equal("ab", edited["X-Team"]);
    }

    private static async Task Handle(HttpListenerContext ctx, string baseUrl, Action issued)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        var res = ctx.Response;
        try
        {
            switch (path)
            {
                case "/.well-known/oauth-protected-resource/mcp" or "/.well-known/oauth-protected-resource":
                    await Json(res, new JsonObject { ["resource"] = baseUrl + "/mcp", ["authorization_servers"] = new JsonArray(baseUrl + "/idp") });
                    return;
                case "/.well-known/oauth-authorization-server/idp" or "/idp/.well-known/openid-configuration" or "/.well-known/openid-configuration/idp":
                    await Json(res, new JsonObject
                    {
                        ["issuer"] = baseUrl + "/idp",
                        ["authorization_endpoint"] = baseUrl + "/idp/authorize",
                        ["token_endpoint"] = baseUrl + "/idp/token",
                        ["response_types_supported"] = new JsonArray("code"),
                        ["code_challenge_methods_supported"] = new JsonArray("S256"),
                    });
                    return;
                case "/idp/authorize":
                    var q = ctx.Request.QueryString;
                    Assert.Equal("keel-mcp", q["client_id"]);
                    Assert.Equal("S256", q["code_challenge_method"]);
                    issued();
                    res.Redirect($"{q["redirect_uri"]}?code=the-code&state={Uri.EscapeDataString(q["state"] ?? "")}");
                    res.Close();
                    return;
                case "/idp/token":
                    var form = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                    Assert.Contains("code=the-code", form);
                    Assert.Contains("code_verifier=", form);
                    await Json(res, new JsonObject { ["access_token"] = Token, ["token_type"] = "Bearer", ["expires_in"] = 300 });
                    return;
                case "/mcp":
                    if (ctx.Request.Headers["Authorization"] != "Bearer " + Token)
                    {
                        res.StatusCode = 401;
                        res.Headers["WWW-Authenticate"] = $"Bearer resource_metadata=\"{baseUrl}/.well-known/oauth-protected-resource/mcp\"";
                        res.Close();
                        return;
                    }
                    var body = JsonNode.Parse(await new StreamReader(ctx.Request.InputStream).ReadToEndAsync())!.AsObject();
                    if (body["id"] is null) { res.StatusCode = 202; res.Close(); return; }
                    var method = body["method"]!.ToString();
                    if (method is not ("initialize" or "tools/list" or "tools/call"))
                    {
                        await Json(res, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" } });
                        return;
                    }
                    JsonObject result = method switch
                    {
                        "initialize" => new() { ["protocolVersion"] = body["params"]!["protocolVersion"]!.ToString(), ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() }, ["serverInfo"] = new JsonObject { ["name"] = "keel", ["version"] = "1" } },
                        "tools/list" => new() { ["tools"] = new JsonArray(new JsonObject { ["name"] = "search_parts", ["inputSchema"] = new JsonObject { ["type"] = "object" } }) },
                        "tools/call" => new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "MARKINGS: none\n[{\"number\":\"P-000123\"}]" }), ["isError"] = false },
                        _ => new(),
                    };
                    await Json(res, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone(), ["result"] = result });
                    return;
                default:
                    res.StatusCode = 404;
                    res.Close();
                    return;
            }
        }
        catch (Exception)
        {
            try { res.StatusCode = 500; res.Close(); } catch { }
        }
    }

    private static async Task Json(HttpListenerResponse res, JsonNode node)
    {
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }
}
