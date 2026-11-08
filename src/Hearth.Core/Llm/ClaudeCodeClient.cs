using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hearth.Core.Models;
using Hearth.Core.Services;

namespace Hearth.Core.Llm;

/// <summary>
/// Claude through the local Claude Code CLI ("claude -p"), using whatever login Claude Code has (Claude
/// subscription or API key). Claude Code's own tools are switched off; Hearth's tools are offered through a
/// small text protocol (&lt;tool_calls&gt; JSON) so the normal Hearth tool loop, approvals and cards still apply.
/// Desktop and web-with-local-access only.
/// </summary>
public sealed partial class ClaudeCodeClient(ProviderProfile profile, PlatformInfo platform, IAppPaths paths) : ILlmClient
{
    public static readonly string[] Models =
        ["opus", "sonnet", "haiku", "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-1"];

    private string WorkDir => Path.Combine(paths.DataDirectory, "claude-code");

    /// <summary>The configured path, or claude on PATH / in the usual install locations.</summary>
    public static string? FindCli(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && !configured.Contains("://"))
            return File.Exists(configured) ? configured : null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : ["claude"];
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat([Path.Combine(home, ".local", "bin"), Path.Combine(home, ".claude", "local"), "/opt/homebrew/bin", "/usr/local/bin",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")]);
        foreach (var d in dirs)
            foreach (var n in names)
                if (File.Exists(Path.Combine(d, n))) return Path.Combine(d, n);
        return null;
    }

    private string Cli()
    {
        if (!platform.CanRunProcesses)
            throw new LlmException("Claude Code runs as a local program, so it's available in the desktop app " +
                                   "(or the web host with Hearth:AllowLocalProcesses). On phones, use the Anthropic API provider.");
        // A program path in the address would run any program: only for accounts allowed to use this computer.
        return FindCli(platform.CanUseComputer ? profile.BaseUrl : null)
            ?? throw new LlmException("Claude Code isn't installed. Install it from claude.com/code, run `claude` once to sign in, " +
                                      "or put the full path to the claude program in this provider's address field.");
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var cli = Cli();
        var psi = new ProcessStartInfo(cli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--version");
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new LlmException($"`claude --version` failed: {await p.StandardError.ReadToEndAsync(ct)}");
        return Models.Select(m => new ModelInfo(m, "claude-code", null, true)).ToList();
    }

    public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) =>
        Task.FromResult<ModelCapabilities?>(new ModelCapabilities(Tools: true, Vision: true, Thinking: true, Embedding: false));

    public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) =>
        throw new LlmException("Claude Code doesn't provide embeddings. In Documents, pick a local embedding provider (e.g. Ollama with nomic-embed-text).");

    public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest req, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var cli = Cli();
        Directory.CreateDirectory(WorkDir);
        var system = BuildSystemPrompt(req);
        var userBlocks = BuildInput(req);

        var psi = new ProcessStartInfo(cli)
        {
            WorkingDirectory = WorkDir, // empty folder: no CLAUDE.md or project settings get picked up
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[]
                 {
                     "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                     "--include-partial-messages", "--no-session-persistence", "--strict-mcp-config",
                     "--setting-sources", "", "--tools", "", "--model", req.Model, "--system-prompt", system,
                 })
            psi.ArgumentList.Add(a);
        if (req.Think) { psi.ArgumentList.Add("--effort"); psi.ArgumentList.Add("high"); }

        using var proc = Process.Start(psi) ?? throw new LlmException("Couldn't start Claude Code.");
        using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        var input = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = userBlocks },
        };
        await proc.StandardInput.WriteLineAsync(input.ToJsonString());
        proc.StandardInput.Close();

        var filter = new ToolCallFilter();
        var sawDeltas = false;
        string? finalText = null;
        string? error = null;
        LlmStats? stats = null;

        var startedAt = DateTime.UtcNow;
        while (!filter.Closed && await proc.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); } catch (JsonException) { continue; }
            switch (node?["type"]?.ToString())
            {
                case "stream_event":
                    var ev = node["event"];
                    if (ev?["type"]?.ToString() != "content_block_delta") break;
                    var delta = ev["delta"];
                    switch (delta?["type"]?.ToString())
                    {
                        case "text_delta":
                            sawDeltas = true;
                            var visible = filter.Push(delta["text"]?.ToString() ?? "");
                            if (visible.Length > 0) yield return new LlmChunk(Content: visible);
                            break;
                        case "thinking_delta":
                            yield return new LlmChunk(Thinking: delta["thinking"]?.ToString());
                            break;
                    }
                    break;
                case "result":
                    finalText = node["result"]?.ToString();
                    if (node["is_error"]?.GetValue<bool>() == true) error = finalText ?? node["subtype"]?.ToString() ?? "unknown error";
                    var usage = node["usage"];
                    var outTok = usage?["output_tokens"]?.GetValue<int>();
                    var secs = (DateTime.UtcNow - startedAt).TotalSeconds;
                    stats = new LlmStats(usage?["input_tokens"]?.GetValue<int>(), outTok, outTok > 0 && secs > 0 ? outTok / secs : null);
                    break;
            }
        }
        if (filter.Closed)
        {
            // The model asked for tools. The CLI can't stop at a sequence, so end it here before it invents results.
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
        await proc.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        if (error is not null)
        {
            if (error.Contains("auth", StringComparison.OrdinalIgnoreCase) || error.Contains("login", StringComparison.OrdinalIgnoreCase)
                || error.Contains("OAuth", StringComparison.OrdinalIgnoreCase))
                error += " — open a terminal, run `claude`, and sign in (/login). Hearth uses the same login.";
            else if (IsServiceProblem(error))
                // Worded so the fallback servers (Settings) take over during outages and usage limits.
                throw new LlmException($"Could not reach Claude through Claude Code: {error}");
            throw new LlmException($"Claude Code: {error}");
        }
        if (finalText is null && !sawDeltas && !filter.Closed)
        {
            var err = (await stderr).Trim();
            throw new LlmException($"Claude Code exited (code {proc.ExitCode}) without a reply. {err}".Trim());
        }

        // Older CLIs without partial messages: use the final text.
        if (!sawDeltas && finalText is not null)
        {
            var visible = filter.Push(finalText);
            if (visible.Length > 0) yield return new LlmChunk(Content: visible);
        }
        var tail = filter.Flush();
        var calls = ParseToolCalls(filter.ToolBlock, req.Tools);
        yield return new LlmChunk(Content: tail, Stats: stats, ToolCalls: calls.Count > 0 ? calls : null);
    }

    /// <summary>Anthropic is down, overloaded, rate-limited, or the network failed — not a problem with the request.</summary>
    public static bool IsServiceProblem(string error) =>
        new[] { "overloaded", "529", "500", "502", "503", "504", "connection error", "econnrefused", "econnreset", "etimedout",
                "fetch failed", "network", "timed out", "rate limit", "usage limit", "limit reached", "hit your limit", "429" }
            .Any(k => error.Contains(k, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ prompt building

    private static string BuildSystemPrompt(LlmRequest req)
    {
        var sb = new StringBuilder(string.Join("\n\n", req.Messages.Where(m => m.Role == "system").Select(m => m.Content)));
        if (req.Tools is { Count: > 0 })
        {
            sb.Append("\n\n# Tools\nYou can use the tools below. To call tools, end your reply with exactly one block:\n")
              .Append("<tool_calls>\n[{\"name\": \"tool_name\", \"arguments\": { ... }}]\n</tool_calls>\n")
              .Append("The arguments must be valid JSON matching the tool's parameters. You may write one short sentence before ")
              .Append("the block. Stop immediately after </tool_calls>: never write <tool_result> yourself — the app runs the ")
              .Append("tools and sends the real results back in the next message, then you continue. ")
              .Append("Only call tools listed here, and never invent tool results.\n\nAvailable tools:\n");
            foreach (var t in req.Tools)
                sb.Append($"\n## {t.Name}\n{t.Description}\nParameters (JSON Schema): {t.Parameters.ToJsonString()}\n");
        }
        return sb.ToString();
    }

    /// <summary>The conversation as one user turn: a transcript plus any images from the latest message.</summary>
    private static JsonArray BuildInput(LlmRequest req)
    {
        var turns = req.Messages.Where(m => m.Role != "system").ToList();
        var blocks = new JsonArray();
        var lastUser = turns.LastOrDefault(m => m.Role == "user");
        foreach (var img in lastUser?.Images ?? [])
            blocks.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = img.MimeType, ["data"] = img.Base64 },
            });

        string text;
        if (turns.Count == 1 && turns[0].Role == "user")
        {
            text = turns[0].Content;
        }
        else
        {
            var sb = new StringBuilder("<conversation>\n");
            foreach (var m in turns)
            {
                switch (m.Role)
                {
                    case "user": sb.Append("<user>\n").Append(m.Content).Append("\n</user>\n"); break;
                    case "assistant":
                        if (m.Content.Length > 0) sb.Append("<assistant>\n").Append(m.Content).Append("\n</assistant>\n");
                        if (m.ToolCalls.Count > 0)
                            sb.Append("<assistant>\n<tool_calls>\n")
                              .Append(new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
                              {
                                  ["name"] = c.Name, ["arguments"] = LlmJson.ParseArgs(c.ArgumentsJson),
                              }).ToArray()).ToJsonString())
                              .Append("\n</tool_calls>\n</assistant>\n");
                        break;
                    case "tool":
                        sb.Append($"<tool_result name=\"{m.ToolName}\">\n").Append(m.Content).Append("\n</tool_result>\n");
                        break;
                }
            }
            sb.Append("</conversation>\n\nWrite the assistant's next turn: reply to the latest user message, " +
                      "or continue now that the tool results above are in. Don't repeat the tags.");
            text = sb.ToString();
        }
        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text.Length > 0 ? text : "(empty message)" });
        return blocks;
    }

    // ------------------------------------------------------------------ tool-call parsing

    public static List<LlmToolCall> ParseToolCalls(string? block, IReadOnlyList<ToolDefinition>? tools)
    {
        if (string.IsNullOrWhiteSpace(block) || tools is not { Count: > 0 }) return [];
        var json = block.Trim();
        var fence = Fence().Match(json);
        if (fence.Success) json = fence.Groups[1].Value;
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException)
        {
            // Hand the problem back to the model as a failed call so it can retry.
            return [new LlmToolCall(NewId(), "invalid_tool_call",
                new JsonObject { ["error"] = "The <tool_calls> block wasn't valid JSON", ["received"] = json.Length > 300 ? json[..300] : json }.ToJsonString())];
        }
        var items = node is JsonArray a ? a.ToList() : [node];
        return items.OfType<JsonObject>()
            .Select(o => new LlmToolCall(NewId(), o["name"]?.ToString() ?? "",
                (o["arguments"] ?? o["input"] ?? o["parameters"])?.ToJsonString() ?? "{}"))
            .Where(c => c.Name.Length > 0)
            .ToList();
    }

    private static string NewId() => "call_" + Guid.NewGuid().ToString("N")[..12];

    [GeneratedRegex(@"^```(?:json)?\s*([\s\S]*?)\s*```$")] private static partial Regex Fence();

    /// <summary>Streams text through, but holds back everything from "&lt;tool_calls&gt;" on (tags may span chunks).</summary>
    public sealed class ToolCallFilter
    {
        private const string Open = "<tool_calls>";
        private const string Close = "</tool_calls>";
        private readonly StringBuilder _pending = new();
        private readonly StringBuilder _block = new();
        private bool _inBlock;

        public string? ToolBlock => _block.Length > 0 ? _block.ToString() : null;
        /// <summary>The closing tag arrived; anything after it is the model running on and is discarded.</summary>
        public bool Closed { get; private set; }

        public string Push(string chunk)
        {
            if (Closed) return "";
            if (_inBlock) { AppendBlock(chunk); return ""; }
            _pending.Append(chunk);
            var text = _pending.ToString();
            var idx = text.IndexOf(Open, StringComparison.Ordinal);
            if (idx >= 0)
            {
                _inBlock = true;
                _pending.Clear();
                AppendBlock(text[(idx + Open.Length)..]);
                return text[..idx];
            }
            var keep = 0;
            for (var len = Math.Min(Open.Length - 1, text.Length); len > 0; len--)
                if (text.EndsWith(Open[..len], StringComparison.Ordinal)) { keep = len; break; }
            _pending.Clear().Append(text[(text.Length - keep)..]);
            return text[..(text.Length - keep)];
        }

        private void AppendBlock(string s)
        {
            _block.Append(s);
            var t = _block.ToString();
            var end = t.IndexOf(Close, StringComparison.Ordinal);
            if (end < 0) return;
            _block.Clear().Append(t[..end]);
            Closed = true;
        }

        public string Flush()
        {
            var rest = _inBlock ? "" : _pending.ToString();
            _pending.Clear();
            return rest;
        }
    }
}
