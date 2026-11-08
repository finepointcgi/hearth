using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hearth.Core.Models;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Hearth.Core.Tools;

public sealed record McpServerStatus(string Id, string Name, bool Connected, int ToolCount, string? Error, IReadOnlyList<string> ToolNames);

/// <summary>
/// Connects to Model Context Protocol servers (stdio on desktop, HTTP everywhere) and exposes their tools.
/// Connections are created lazily and recreated when a server's config changes.
/// </summary>
public sealed partial class McpManager(PlatformInfo platform, IHttpClientFactory http) : IToolProvider, IAsyncDisposable
{
    private sealed class Connection
    {
        public required string Fingerprint { get; init; }
        public McpClient? Client { get; set; }
        public IList<McpClientTool> Tools { get; set; } = [];
        public string? Error { get; set; }
        public DateTime ErrorAt { get; set; }
    }

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, Connection> _connections = [];

    public event Action? Changed;

    public IReadOnlyList<McpServerStatus> Status(AppSettings settings) => settings.McpServers.Select(s =>
    {
        _connections.TryGetValue(s.Id, out var c);
        return new McpServerStatus(s.Id, s.Name, c?.Client is not null, c?.Tools.Count ?? 0, c?.Error,
            c?.Tools.Select(t => t.Name).ToList() ?? []);
    }).ToList();

    public async Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct)
    {
        var tools = new List<ITool>();
        foreach (var server in settings.McpServers.Where(s => s.Enabled))
        {
            var conn = await EnsureAsync(server, force: false, ct);
            if (conn.Client is null) continue;
            tools.AddRange(conn.Tools.Select(t => new McpTool(server, t)));
        }
        return tools;
    }

    public async Task<McpServerStatus> ReconnectAsync(McpServerConfig server, CancellationToken ct)
    {
        var c = await EnsureAsync(server, force: true, ct);
        return new McpServerStatus(server.Id, server.Name, c.Client is not null, c.Tools.Count, c.Error, c.Tools.Select(t => t.Name).ToList());
    }

    private async Task<Connection> EnsureAsync(McpServerConfig server, bool force, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!force && _connections.TryGetValue(server.Id, out var existing) && existing.Fingerprint == server.Fingerprint)
            {
                // Retry failed servers at most every 30 seconds.
                if (existing.Client is not null || DateTime.UtcNow - existing.ErrorAt < TimeSpan.FromSeconds(30))
                    return existing;
            }
            if (_connections.Remove(server.Id, out var old) && old.Client is not null)
                await old.Client.DisposeAsync();

            var conn = new Connection { Fingerprint = server.Fingerprint };
            _connections[server.Id] = conn;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                conn.Client = await McpClient.CreateAsync(CreateTransport(server), cancellationToken: timeout.Token);
                conn.Tools = await conn.Client.ListToolsAsync(cancellationToken: timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                conn.Error = ex.Message;
                conn.ErrorAt = DateTime.UtcNow;
                if (conn.Client is not null) { await conn.Client.DisposeAsync(); conn.Client = null; }
            }
            Changed?.Invoke();
            return conn;
        }
        finally { _lock.Release(); }
    }

    private IClientTransport CreateTransport(McpServerConfig s)
    {
        if (s.Transport == McpTransport.Http)
        {
            var opts = new HttpClientTransportOptions { Endpoint = new Uri(s.Url), Name = s.Name };
            if (s.Headers.Count > 0) opts.AdditionalHeaders = new Dictionary<string, string>(s.Headers);
            return new HttpClientTransport(opts, http.CreateClient("mcp"), ownsHttpClient: false);
        }
        if (!platform.CanRunProcesses)
            throw new PlatformNotSupportedException(
                "This platform can't launch local MCP servers. Use an HTTP MCP server instead.");
        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = s.Name,
            Command = s.Command,
            Arguments = SplitArgs(s.Arguments),
            EnvironmentVariables = s.Environment.ToDictionary(k => k.Key, v => (string?)v.Value),
        });
    }

    public static List<string> SplitArgs(string args)
    {
        var list = new List<string>();
        foreach (Match m in ArgRegex().Matches(args ?? ""))
            list.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Value);
        return list;
    }

    /// <summary>Imports servers from a Claude Desktop / Cursor style { "mcpServers": { ... } } JSON.</summary>
    public static List<McpServerConfig> ParseConfig(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("Not a JSON object.");
        var servers = (root["mcpServers"] ?? root["servers"] ?? root)?.AsObject() ?? throw new FormatException("No servers found.");
        var result = new List<McpServerConfig>();
        foreach (var (name, node) in servers)
        {
            if (node is not JsonObject o) continue;
            var cfg = new McpServerConfig { Name = name };
            if (o["url"]?.ToString() is { Length: > 0 } url)
            {
                cfg.Transport = McpTransport.Http;
                cfg.Url = url;
                if (o["headers"] is JsonObject h) cfg.Headers = h.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? "");
            }
            else
            {
                cfg.Command = o["command"]?.ToString() ?? "";
                cfg.Arguments = string.Join(' ', (o["args"] as JsonArray ?? []).Select(a =>
                {
                    var s = a?.ToString() ?? "";
                    return s.Contains(' ') ? $"\"{s}\"" : s;
                }));
                if (o["env"] is JsonObject env) cfg.Environment = env.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? "");
            }
            result.Add(cfg);
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _connections.Values)
            if (c.Client is not null) await c.Client.DisposeAsync();
        _connections.Clear();
    }

    [GeneratedRegex("\"([^\"]*)\"|'([^']*)'|\\S+")] private static partial Regex ArgRegex();
    [GeneratedRegex("[^a-zA-Z0-9_-]")] internal static partial Regex InvalidName();
}

internal sealed class McpTool(McpServerConfig server, McpClientTool tool) : ITool, IPermissionInfo
{
    public string Name { get; } = Truncate(McpManager.InvalidName().Replace($"{server.Name}_{tool.Name}", "_"), 64);
    public string DisplayName => $"{server.Name}: {tool.ProtocolTool.Title ?? tool.Name}";
    public string Icon => "🔌";
    public string Description => string.IsNullOrWhiteSpace(tool.Description) ? tool.Name : tool.Description;

    public JsonObject Parameters
    {
        get
        {
            var schema = JsonNode.Parse(tool.JsonSchema.GetRawText()) as JsonObject ?? new JsonObject();
            schema["type"] ??= "object";
            schema["properties"] ??= new JsonObject();
            schema.Remove("$schema");
            return schema;
        }
    }

    public bool RequiresApproval => !server.AutoApprove && tool.ProtocolTool.Annotations?.ReadOnlyHint != true;
    public string Area => "mcp:" + server.Name;
    public string AreaTitle => "MCP · " + server.Name;
    public bool IsWrite => tool.ProtocolTool.Annotations?.ReadOnlyHint != true;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString())!
            .ToDictionary(k => k.Key, v => (object?)v.Value);
        var result = await tool.CallAsync(dict, cancellationToken: ct);

        var text = new StringBuilder();
        var images = new List<ImageAttachment>();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock t: text.AppendLine(t.Text); break;
                case ImageContentBlock i:
                    images.Add(new ImageAttachment("mcp-image", i.MimeType, Convert.ToBase64String(i.DecodedData.ToArray())));
                    text.AppendLine("[image shown to the user]");
                    break;
                default: text.AppendLine(JsonSerializer.Serialize<object>(block)); break;
            }
        }
        if (text.Length == 0 && result.StructuredContent is { } sc) text.Append(sc.ToString());
        return new ToolResult(Schema.Truncate(text.ToString().Trim(), 16000), images, IsError: result.IsError == true);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
