using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hearth.Core.Models;
using Hearth.Core.Services;

namespace Hearth.Core.Tools;

/// <summary>Offers run_tools_script when code runs in the sandbox.</summary>
public sealed class ScriptToolProvider(CodeSandbox sandbox, IBotComputer computer) : IToolProvider
{
    public Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ITool>>(settings.Tools.PythonEnabled && sandbox.Active(settings)
            ? [new ScriptTool(sandbox, computer)] : []);
}

/// <summary>
/// "Programmatic tool calling": the model writes one Python script that calls its tools in loops (search several
/// queries, open each result, filter, compute) and prints just the outcome — one step instead of dozens of model
/// round trips. The script runs in the code sandbox; tool calls come back to Hearth over its input/output, and
/// only tools that wouldn't need approval are callable from it.
/// </summary>
public sealed class ScriptTool(CodeSandbox sandbox, IBotComputer computer) : ITool
{
    public const string Marker = "\u001eHEARTH_CALL ";
    public const int MaxCalls = 60;

    /// <summary>Tools a script must never call (they talk to people, start work, or recurse).</summary>
    private static readonly HashSet<string> NotFromScripts =
    [
        "run_tools_script", "message_bot", "delegate_task", "start_task", "propose_new_bot", "rename_chat",
        "follow_up", "cancel_follow_up", "use_skill", "save_skill", "improve_skill",
    ];

    public string Name => "run_tools_script";
    public string DisplayName => "Run a tools script";
    public string Icon => "🧩";
    public string Description =>
        "Write a Python 3 script that calls your other tools in loops and prints only the final result — use it when a " +
        "job needs many tool calls (several searches, opening each result, filtering or comparing), instead of calling " +
        "the tools one by one. In the script, call any tool as tools.<name>(arg=value) (e.g. " +
        "tools.fetch_url(url=u)); it returns the tool's text result or raises RuntimeError. tools.available() lists " +
        "what you can call. Tools that change things or need the user's OK can't be called from scripts. Up to " +
        $"{MaxCalls} calls, 5 minutes. Runs in the sandbox (files at /workspace).";
    public JsonObject Parameters => Schema.Object(("code", "string", "Complete Python 3 script; print the final answer", true));
    public bool RequiresApproval => false;

    public static IEnumerable<ITool> Callable(ToolContext ctx) =>
        ctx.Tools.Values.Where(t => !NotFromScripts.Contains(t.Name)
            && !PermissionCatalog.NeedsApproval(t, ctx.Settings, ctx)
            && !ContentTrust.ShouldHold(t, ctx));

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var code = args.Str("code");
        if (string.IsNullOrWhiteSpace(code)) return ToolResult.Error("code is required");
        if (!sandbox.Active(ctx.Settings)) return ToolResult.Error("Scripts need the code sandbox (Docker).");

        var dir = Path.Combine(computer.WorkspaceDirectory, ".hearth-scripts");
        Directory.CreateDirectory(dir);
        var file = $"s{Guid.NewGuid():N}"[..13] + ".py";
        await File.WriteAllTextAsync(Path.Combine(dir, file), Prelude + code, ct);

        var calls = new List<string>();
        try
        {
            var r = await sandbox.RunInteractiveAsync(ctx.Settings.Tools.Sandbox, computer.WorkspaceDirectory,
                $"PYTHONIOENCODING=utf-8 python3 -u .hearth-scripts/{file}", Marker,
                request => HandleAsync(request, ctx, calls, ct), TimeSpan.FromMinutes(5), ct);
            var summary = calls.Count == 0 ? "" :
                $"\n[script called: {string.Join(", ", calls.GroupBy(c => c).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key))}]";
            return SandboxOutput.ToResult(r, 300) is var res ? res with { Text = res.Text + summary } : res;
        }
        catch (InvalidOperationException ex) { return ToolResult.Error(ex.Message); }
        finally { try { File.Delete(Path.Combine(dir, file)); } catch { } }
    }

    /// <summary>Answers one tool request from a script (public for tests).</summary>
    public static async Task<string> HandleAsync(string request, ToolContext ctx, List<string> calls, CancellationToken ct)
    {
        try
        {
            var req = JsonNode.Parse(request) as JsonObject ?? throw new FormatException();
            var name = req["name"]?.ToString() ?? "";
            if (name == "__available__")
                return Answer(result: string.Join("\n", Callable(ctx).Select(t => $"{t.Name}: {t.Description}")));
            if (calls.Count >= MaxCalls) return Answer(error: $"Too many tool calls (limit {MaxCalls}).");
            var tool = Callable(ctx).FirstOrDefault(t => t.Name == name);
            if (tool is null)
                return Answer(error: ctx.Tools.ContainsKey(name) ? $"{name} can't be called from a script (it needs the user's OK or changes things)." : $"No tool called {name}.");
            calls.Add(name);
            var result = await tool.ExecuteAsync(req["args"] as JsonObject ?? [], ctx, ct);
            // Reading the web, email and the like from a script counts like reading it directly.
            if (ContentTrust.UntrustedSource(tool) is { } source) ctx.TaintedBy ??= source;
            return result.IsError ? Answer(error: result.Text) : Answer(result: result.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Answer(error: ex.Message); }
    }

    private static string Answer(string? result = null, string? error = null) =>
        JsonSerializer.Serialize(new Dictionary<string, string?> { ["result"] = result, ["error"] = error });

    /// <summary>Lets the script call tools: a request line on stdout, the answer as one line on stdin.</summary>
    public const string Prelude = """
import json as _json, sys as _sys

class _Tools:
    def _call(self, name, args):
        _sys.__stdout__.write("\x1eHEARTH_CALL " + _json.dumps({"name": name, "args": args}) + "\n")
        _sys.__stdout__.flush()
        reply = _json.loads(_sys.stdin.readline() or '{"error": "no answer from Hearth"}')
        if reply.get("error"):
            raise RuntimeError(reply["error"])
        return reply.get("result")

    def available(self):
        return self._call("__available__", {})

    def __getattr__(self, name):
        if name.startswith("_"):
            raise AttributeError(name)
        return lambda **args: self._call(name, args)

tools = _Tools()
# ---- your script ----

""";
}
