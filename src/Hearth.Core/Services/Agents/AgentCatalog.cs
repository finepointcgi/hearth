using System.Diagnostics;

namespace Hearth.Core.Services.Agents;

/// <summary>How Hearth talks to an agent.</summary>
public enum AgentDriver
{
    /// <summary>Agent Client Protocol over stdio (what editors like Zed use): streaming steps and permission requests.</summary>
    Acp,
    /// <summary>Claude Code's own headless mode (claude -p, stream-json).</summary>
    ClaudeCode,
}

/// <summary>How Hearth's skills are shared with the agent.</summary>
public enum SkillLink
{
    None,
    /// <summary>One link, skills/hearth → Hearth's skills folder (agents that look in subfolders).</summary>
    Folder,
    /// <summary>A link per skill, skills/hearth-&lt;name&gt; (agents that only look one level down).</summary>
    PerSkill,
}

/// <summary>An AI agent Hearth can run and look after.</summary>
public sealed record AgentDef(
    string Id,
    string Name,
    string Icon,
    string About,
    string Command,
    AgentDriver Driver,
    string[] RunArgs,
    /// <summary>Its folder under the home folder (~/.hermes).</summary>
    string Home,
    /// <summary>Files (under <see cref="Home"/>) with what it remembers; the first is where "what Hearth knows" goes.</summary>
    string[] MemoryFiles,
    string? SkillsDir = null,
    SkillLink Skills = SkillLink.None,
    string[]? UpdateArgs = null,
    string[]? StatusArgs = null,
    string? SetupHint = null,
    string? InstallUrl = null);

/// <summary>An agent as found on this computer.</summary>
public sealed record AgentStatus(AgentDef Def, string? Path, string? Version)
{
    public bool Installed => Path is not null;
}

/// <summary>The agents Hearth knows how to drive, and where to find them.</summary>
public static class AgentCatalog
{
    public static readonly IReadOnlyList<AgentDef> All =
    [
        new("claude-code", "Claude Code", "✳️", "Anthropic's coding agent. Great at changes across a codebase.",
            "claude", AgentDriver.ClaudeCode, [], ".claude", ["CLAUDE.md"], "skills", SkillLink.PerSkill,
            UpdateArgs: ["update"], SetupHint: "Run `claude` once in a terminal to sign in.", InstallUrl: "https://claude.com/code"),
        new("hermes", "Hermes", "🪽", "Nous Research's agent with its own memory, skills, gateway and schedules.",
            "hermes", AgentDriver.Acp, ["acp"], ".hermes", ["memories/USER.md", "memories/MEMORY.md", "SOUL.md"], "skills", SkillLink.Folder,
            UpdateArgs: ["update"], StatusArgs: ["status"], SetupHint: "Run `hermes setup` once to pick its model.",
            InstallUrl: "https://github.com/NousResearch/hermes-agent"),
        new("openclaw", "OpenClaw", "🦞", "Personal agent that runs through its own gateway.",
            "openclaw", AgentDriver.Acp, ["acp"], ".openclaw", ["workspace/USER.md", "workspace/MEMORY.md", "workspace/SOUL.md"],
            "workspace/skills", SkillLink.PerSkill, UpdateArgs: ["update"], StatusArgs: ["status"],
            SetupHint: "Its gateway has to be running (`openclaw gateway`).", InstallUrl: "https://docs.openclaw.ai"),
        new("gemini", "Gemini CLI", "♊", "Google's command-line agent.",
            "gemini", AgentDriver.Acp, ["--acp"], ".gemini", ["GEMINI.md"],
            SetupHint: "Run `gemini` once in a terminal to sign in with Google.", InstallUrl: "https://github.com/google-gemini/gemini-cli"),
        new("codex", "Codex", "🧩", "OpenAI's coding agent, through the codex-acp adapter.",
            "codex-acp", AgentDriver.Acp, [], ".codex", ["AGENTS.md"], "skills", SkillLink.PerSkill,
            SetupHint: "Install Codex and the codex-acp adapter, and sign in with `codex login`.", InstallUrl: "https://github.com/openai/codex"),
    ];

    public static AgentDef? Find(string id) => All.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                                                                   || a.Name.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static string HomeFolder => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Where agents are usually installed; background services often start with a bare PATH.</summary>
    public static IEnumerable<string> SearchDirs()
    {
        var home = HomeFolder;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat([System.IO.Path.Combine(home, ".local", "bin"), System.IO.Path.Combine(home, ".claude", "local"), "/opt/homebrew/bin",
                     "/usr/local/bin", System.IO.Path.Combine(home, ".npm-global", "bin"), System.IO.Path.Combine(home, ".cargo", "bin"),
                     System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")])
            .Distinct();
    }

    /// <summary>PATH for an agent's process: the usual one plus the install folders (node, python and friends live there).</summary>
    public static string ProcessPath() => string.Join(System.IO.Path.PathSeparator, SearchDirs().Concat(["/usr/bin", "/bin", "/usr/sbin", "/sbin"]).Distinct());

    public static string? Locate(string command)
    {
        var names = OperatingSystem.IsWindows() ? new[] { command + ".exe", command + ".cmd" } : [command];
        foreach (var d in SearchDirs())
            foreach (var n in names)
            {
                var p = System.IO.Path.Combine(d, n);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    /// <summary>Runs an agent's command once and returns what it printed (for status, update and version).</summary>
    public static async Task<(int exit, string output)> RunAsync(string cli, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(cli)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = HomeFolder,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = ProcessPath();
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["TERM"] = "dumb";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {cli}.");
        p.StandardInput.Close(); // never wait on a prompt
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = p.StandardError.ReadToEndAsync(cts.Token);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return (-1, "Stopped: it took longer than " + (timeout.TotalMinutes >= 1 ? $"{timeout.TotalMinutes:0} minutes." : $"{timeout.TotalSeconds:0} seconds."));
        }
        var text = (await stdout + "\n" + await stderr).Trim();
        return (p.ExitCode, StripAnsi(text));
    }

    public static string StripAnsi(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07]*\x07", "");
}
