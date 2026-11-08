using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hearth.Core.Models;

namespace Hearth.Core.Services.Agents;

/// <summary>
/// Runs outside agents (Hermes, Claude Code, Gemini CLI, OpenClaw, Codex) on jobs in a folder: from the Agents page,
/// or when a bot hands work over. Every step streams into an <see cref="AgentRun"/>; when an agent wants to do
/// something its mode doesn't cover, the request becomes a normal Hearth approval (app, dashboard, Telegram).
/// Agents that speak ACP keep their session open for follow-ups; Claude Code resumes its session.
/// </summary>
public sealed class AgentHub : IAsyncDisposable
{
    private readonly AgentRunStore _store;
    private readonly SettingsStore _settings;
    private readonly ApprovalCenter _approvals;
    private readonly ConversationStore _conversations;
    private readonly BotStore _bots;
    private readonly PlatformInfo _platform;
    private readonly ActivityLog _activity;
    private readonly ConcurrentDictionary<string, AgentRun> _live = new();
    private readonly ConcurrentDictionary<string, LiveSession> _sessions = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancel = new();
    private readonly ConcurrentDictionary<string, Task> _work = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastSave = new();
    /// <summary>Runs the user stopped (as opposed to ones that ran out of time).</summary>
    private readonly ConcurrentDictionary<string, bool> _stopped = new();
    private readonly Timer _sweep;
    private IReadOnlyList<AgentStatus>? _status;
    private DateTime _statusAt;

    /// <summary>How long a finished ACP session stays open for a follow-up.</summary>
    public static readonly TimeSpan KeepOpen = TimeSpan.FromMinutes(20);

    public AgentHub(AgentRunStore store, SettingsStore settings, ApprovalCenter approvals, ConversationStore conversations, BotStore bots,
        PlatformInfo platform, ActivityLog activity)
    {
        _store = store; _settings = settings; _approvals = approvals; _conversations = conversations; _bots = bots;
        _platform = platform; _activity = activity;
        _sweep = new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>A run changed (a step, its status). Fired often while it works.</summary>
    public event Action<AgentRun>? Changed;

    /// <summary>Agents run as local programs: desktop, or the web host with local access.</summary>
    public bool Available => _platform.CanRunProcesses;

    /// <summary>For tests: the program to run instead of looking the agent up.</summary>
    public Func<AgentDef, string?>? Locator { get; set; }

    // ------------------------------------------------------------------ what's installed

    public async Task<IReadOnlyList<AgentStatus>> StatusAsync(bool refresh = false)
    {
        if (!refresh && _status is not null && DateTime.UtcNow - _statusAt < TimeSpan.FromMinutes(10)) return _status;
        var found = await Task.WhenAll(AgentCatalog.All.Select(async def =>
        {
            var path = Available ? Find(def) : null;
            string? version = null;
            if (path is not null)
                try
                {
                    var (exit, output) = await AgentCatalog.RunAsync(path, ["--version"], TimeSpan.FromSeconds(20));
                    if (exit == 0) version = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                }
                catch { /* still installed, just quiet */ }
            return new AgentStatus(def, path, version is { Length: > 80 } ? version[..80] : version);
        }));
        _statusAt = DateTime.UtcNow;
        return _status = found;
    }

    private string? Find(AgentDef def) => Locator is { } l ? l(def) : AgentCatalog.Locate(def.Command);

    // ------------------------------------------------------------------ other computers (SSH)

    private readonly ConcurrentDictionary<string, (DateTime at, IReadOnlyList<AgentStatus> agents)> _nodes = new();

    /// <summary>On the remote shell: the usual install folders, since a non-interactive SSH session has a bare PATH.</summary>
    public const string RemotePath =
        "export PATH=\"$HOME/.local/bin:$HOME/.claude/local:/opt/homebrew/bin:/usr/local/bin:$HOME/.npm-global/bin:$HOME/.cargo/bin:$PATH\"; ";

    /// <summary>A computer from Remote computers, by name or id (null or "this computer": this one).</summary>
    public async Task<Tools.RemoteHost?> HostAsync(string? computer)
    {
        if (string.IsNullOrWhiteSpace(computer) || computer.Equals("this computer", StringComparison.OrdinalIgnoreCase)
            || computer.Equals("local", StringComparison.OrdinalIgnoreCase)) return null;
        var hosts = (await _settings.GetAsync()).RemoteHosts;
        return hosts.FirstOrDefault(h => h.Id == computer) ?? hosts.FirstOrDefault(h => h.Name.Equals(computer, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No computer called \"{computer}\". Add it under Agents → Computers (it uses SSH).");
    }

    /// <summary>Which agents a remote computer has (asked over SSH; remembered for 10 minutes).</summary>
    public async Task<IReadOnlyList<AgentStatus>> NodeStatusAsync(Tools.RemoteHost host, bool refresh = false)
    {
        if (!refresh && _nodes.TryGetValue(host.Id, out var known) && DateTime.UtcNow - known.at < TimeSpan.FromMinutes(10)) return known.agents;
        var script = RemotePath + "for c in " + string.Join(" ", AgentCatalog.All.Select(a => a.Command)) +
                     "; do p=$(command -v \"$c\" 2>/dev/null) && echo \"@@$c|$p|$(\"$c\" --version </dev/null 2>/dev/null | head -n 1)\"; done; echo @@done";
        var (code, output) = await NodeRunner(host, script, TimeSpan.FromSeconds(60));
        if (!output.Contains("@@done"))
            throw new InvalidOperationException($"Couldn't reach {host.Name}: {Clip(output.Trim().Length > 0 ? output.Trim() : $"ssh exit code {code}", 300)}");
        var found = output.Split('\n').Where(l => l.StartsWith("@@") && l.Contains('|'))
            .Select(l => l[2..].Split('|', 3)).ToDictionary(p => p[0], p => (path: p[1].Trim(), version: p.Length > 2 ? p[2].Trim() : ""));
        IReadOnlyList<AgentStatus> agents = AgentCatalog.All.Select(def => found.TryGetValue(def.Command, out var f)
            ? new AgentStatus(def, f.path, f.version.Length == 0 ? null : f.version.Length > 80 ? f.version[..80] : f.version)
            : new AgentStatus(def, null, null)).ToList();
        _nodes[host.Id] = (DateTime.UtcNow, agents);
        return agents;
    }

    /// <summary>What's known about a computer's agents without asking it (null: not checked yet).</summary>
    public IReadOnlyList<AgentStatus>? KnownNodeStatus(string hostId) => _nodes.TryGetValue(hostId, out var k) ? k.agents : null;

    /// <summary>For tests: runs a command on a remote computer (default: over SSH).</summary>
    public Func<Tools.RemoteHost, string, TimeSpan, Task<(int, string)>> NodeRunner { get; set; } =
        (h, command, timeout) => Tools.Ssh.RunAsync(h, command, timeout, CancellationToken.None);

    /// <summary>For tests: the process that runs a command line on a remote computer (default: ssh).</summary>
    public Func<Tools.RemoteHost, string, ProcessStartInfo> NodeStarter { get; set; } = Tools.Ssh.Start;

    /// <summary>Runs an agent's own command (status, update) on a remote computer.</summary>
    public async Task<(int exit, string output)> NodeCommandAsync(Tools.RemoteHost host, AgentStatus agent, string[] args, TimeSpan timeout)
    {
        if (agent.Path is null) return (1, $"{agent.Def.Name} isn't installed on {host.Name}.");
        var (code, output) = await NodeRunner(host, RemotePath + string.Join(" ", new[] { agent.Path }.Concat(args).Select(Tools.Ssh.Quote)) + " </dev/null", timeout);
        return (code, AgentCatalog.StripAnsi(output).Trim());
    }

    /// <summary>The process for an agent: here in the run's folder, or on its computer over SSH.</summary>
    private async Task<ProcessStartInfo> LaunchAsync(AgentRun run, string cli, IEnumerable<string> args)
    {
        if (run.NodeId is null)
        {
            var psi = new ProcessStartInfo(cli)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = run.Folder,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["PATH"] = AgentCatalog.ProcessPath();
            psi.Environment["NO_COLOR"] = "1";
            psi.Environment.Remove("CLAUDECODE"); // not a nested session of whoever started Hearth
            return psi;
        }
        var host = await HostAsync(run.NodeId) ?? throw new InvalidOperationException("That computer was removed.");
        return NodeStarter(host, RemoteCommand(run, cli, args));
    }

    /// <summary>The shell line run on the remote computer: into the folder, then the agent itself (stdin/stdout pass through SSH).</summary>
    public static string RemoteCommand(AgentRun run, string cli, IEnumerable<string> args) =>
        RemotePath + "export NO_COLOR=1; unset CLAUDECODE; cd " + Tools.Ssh.QuotePath(run.Folder) +
        " || { echo " + Tools.Ssh.Quote($"There's no folder {run.Folder} on {run.NodeName}.") + " >&2; exit 3; }; exec " +
        string.Join(" ", new[] { cli }.Concat(args).Select(Tools.Ssh.Quote));

    // ------------------------------------------------------------------ runs

    public async Task<AgentRun?> GetAsync(string id) => _live.TryGetValue(id, out var r) ? r : await _store.GetAsync(id);

    public async Task<List<AgentRun>> RecentAsync(int max = 30)
    {
        var saved = await _store.ListAsync(max);
        return saved.Select(r => _live.TryGetValue(r.Id, out var live) ? live : r)
            .Concat(_live.Values.Where(l => saved.All(s => s.Id != l.Id)))
            .OrderByDescending(r => r.Started).Take(max).ToList();
    }

    /// <summary>Starts a job. Returns at once; the work goes on in the background (see <see cref="WaitAsync"/>).</summary>
    public async Task<AgentRun> StartAsync(string agentId, string task, string folder, string? conversationId = null, string? botId = null,
        bool unattended = false, string? computer = null)
    {
        if (!Available) throw new InvalidOperationException("Agents run as programs on this computer, so they work in the desktop app or the web host with local access.");
        var def = AgentCatalog.Find(agentId) ?? throw new InvalidOperationException($"No agent called \"{agentId}\". Hearth knows: {string.Join(", ", AgentCatalog.All.Select(a => a.Name))}.");
        task = task.Trim();
        if (task.Length == 0) throw new InvalidOperationException("Say what the agent should do.");
        var app = await _settings.GetAsync();
        var host = await HostAsync(computer);
        if (host is null)
        {
            if (Find(def) is null) throw new InvalidOperationException($"{def.Name} isn't installed on this computer. {def.SetupHint}");
            folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder.Trim().Replace("~", AgentCatalog.HomeFolder)));
            if (!Directory.Exists(folder)) throw new InvalidOperationException($"There's no folder at {folder}.");
        }
        else
        {
            var there = (await NodeStatusAsync(host)).FirstOrDefault(a => a.Def.Id == def.Id);
            if (there?.Installed != true) throw new InvalidOperationException($"{def.Name} isn't installed on {host.Name} (or Hearth couldn't find it there). {def.SetupHint}");
            folder = folder.Trim();
            if (folder.Length == 0) folder = "~";
            if (!folder.StartsWith('/') && !folder.StartsWith('~')) throw new InvalidOperationException($"Give the folder's full path on {host.Name} (/… or ~/…).");
            var (_, where) = await NodeRunner(host, "cd " + Tools.Ssh.QuotePath(folder) + " 2>/dev/null && echo \"@@$(pwd)\" || echo @@missing", TimeSpan.FromSeconds(30));
            var line = where.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("@@"));
            if (line is null) throw new InvalidOperationException($"Couldn't reach {host.Name}: {Clip(where.Trim(), 300)}");
            if (line == "@@missing") throw new InvalidOperationException($"There's no folder {folder} on {host.Name}.");
            folder = line[2..]; // the full path there, which agents need
        }
        var settings = app.Agents.For(def.Id);
        var run = new AgentRun
        {
            AgentId = def.Id, AgentName = def.Name, Task = task, Folder = folder, Mode = settings.Mode,
            ConversationId = conversationId, BotId = botId, Unattended = unattended, NodeId = host?.Id, NodeName = host?.Name,
        };
        run.Steps.Add(new AgentStep { Kind = AgentStep.You, Content = task });
        _live[run.Id] = run;
        await _store.SaveAsync(run);
        Changed?.Invoke(run);
        _work[run.Id] = Task.Run(() => PromptAsync(run, def, task, settings.MaxMinutes));
        return run;
    }

    /// <summary>Sends a follow-up to a finished run (same session where the agent supports it).</summary>
    public async Task SendAsync(string runId, string message)
    {
        var run = await GetAsync(runId) ?? throw new InvalidOperationException("That run is gone.");
        if (run.Active) throw new InvalidOperationException("It's still working. Wait for it to finish, or stop it first.");
        var def = AgentCatalog.Find(run.AgentId) ?? throw new InvalidOperationException("That agent isn't known any more.");
        message = message.Trim();
        if (message.Length == 0) return;
        lock (run.Steps) run.Steps.Add(new AgentStep { Kind = AgentStep.You, Content = message });
        run.Status = AgentRunStatus.Running;
        run.Ended = null;
        _live[run.Id] = run;
        Changed?.Invoke(run);
        var max = (await _settings.GetAsync()).Agents.For(def.Id).MaxMinutes;
        _work[run.Id] = Task.Run(() => PromptAsync(run, def, message, max));
    }

    /// <summary>Waits for the current prompt to finish (or <paramref name="timeout"/>). Returns the run as it is then.</summary>
    public async Task<AgentRun> WaitAsync(string runId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_work.TryGetValue(runId, out var work))
            try { await work.WaitAsync(timeout, ct); } catch (TimeoutException) { }
        return await GetAsync(runId) ?? throw new InvalidOperationException("That run is gone.");
    }

    public async Task CancelAsync(string runId)
    {
        if (_cancel.TryGetValue(runId, out var cts)) { _stopped[runId] = true; await cts.CancelAsync(); }
        foreach (var p in _approvals.Pending.Where(p => p.ConversationId == "agent-run:" + runId)) _approvals.Cancel(p.Id);
    }

    private async Task PromptAsync(AgentRun run, AgentDef def, string text, int maxMinutes)
    {
        var limit = TimeSpan.FromMinutes(Math.Clamp(maxMinutes, 1, 24 * 60));
        using var cts = new CancellationTokenSource(limit);
        _cancel[run.Id] = cts;
        _stopped.TryRemove(run.Id, out _);
        run.Status = AgentRunStatus.Running;
        run.Error = null;
        Touch(run, force: true);
        try
        {
            var cli = run.NodeId is null ? Find(def) ?? throw new InvalidOperationException($"{def.Name} isn't installed any more.")
                : (await NodeStatusAsync(await HostAsync(run.NodeId) ?? throw new InvalidOperationException($"The computer {run.NodeName} was removed.")))
                  .FirstOrDefault(a => a.Def.Id == def.Id)?.Path ?? throw new InvalidOperationException($"{def.Name} isn't installed on {run.NodeName} any more.");
            if (def.Driver == AgentDriver.ClaudeCode) await RunClaudeAsync(run, cli, text, cts.Token);
            else await RunAcpAsync(run, def, cli, text, cts.Token);
            run.Result = run.LatestAnswer() is { Length: > 0 } answer ? answer : run.Result;
            run.Status = AgentRunStatus.Done;
        }
        catch (Exception ex) when (cts.IsCancellationRequested && ex is OperationCanceledException or AcpException or TaskCanceledException)
        {
            if (_stopped.TryRemove(run.Id, out _)) { run.Status = AgentRunStatus.Cancelled; run.Note("Stopped."); }
            else { run.Status = AgentRunStatus.Failed; run.Error = $"Stopped after {limit.TotalMinutes:0} minutes (the limit on the Agents page)."; }
            await CloseSessionAsync(run.Id);
        }
        catch (Exception ex)
        {
            run.Status = AgentRunStatus.Failed;
            run.Error = ex.Message;
            await CloseSessionAsync(run.Id);
        }
        finally
        {
            run.Ended = DateTimeOffset.Now;
            _cancel.TryRemove(run.Id, out _);
            Changed?.Invoke(run);
            await SaveAsync(run);
            try
            {
                await _activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Task, $"{def.Name}: {Clip(run.Task, 80)}",
                    run.Error ?? Clip(run.Result ?? "Done.", 280), run.BotId, run.ConversationId, run.Status == AgentRunStatus.Done));
            }
            catch { /* the log is a nicety */ }
        }
    }

    /// <summary>Tells watchers and saves (at most every 2 seconds while it streams).</summary>
    private void Touch(AgentRun run, bool force = false)
    {
        Changed?.Invoke(run);
        var now = DateTime.UtcNow;
        if (!force && _lastSave.TryGetValue(run.Id, out var last) && now - last < TimeSpan.FromSeconds(2)) return;
        _lastSave[run.Id] = now;
        _ = SaveAsync(run);
    }

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _saving = new();

    /// <summary>Saves one at a time per run, each taking the run as it is then, so an older copy never lands last.</summary>
    private async Task SaveAsync(AgentRun run)
    {
        var gate = _saving.GetOrAdd(run.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { await _store.SaveAsync(run); }
        catch { /* the next save catches up */ }
        finally { gate.Release(); }
    }

    // ------------------------------------------------------------------ asking the user

    private static readonly HashSet<string> Looking = ["read", "search", "think", "fetch"];
    private static readonly HashSet<string> Editing = ["edit", "delete", "move"];

    /// <summary>Whether the run's mode allows a kind of step without asking (null: ask).</summary>
    public static bool? Allowed(AgentMode mode, string? kind)
    {
        var k = (kind ?? "other").ToLowerInvariant();
        return mode switch
        {
            AgentMode.FullAuto => true,
            AgentMode.ReadOnly => Looking.Contains(k),
            AgentMode.AcceptEdits => Looking.Contains(k) || Editing.Contains(k) ? true : null,
            _ => Looking.Contains(k) ? true : null,
        };
    }

    private async Task<bool> DecideAsync(AgentRun run, string title, string? kind, string details, CancellationToken ct, bool alwaysAsk = false)
    {
        if (!alwaysAsk && Allowed(run.Mode, kind) is { } auto)
        {
            if (!auto) run.Note($"Refused (read-only): {title}");
            return auto;
        }
        if (run.Unattended)
        {
            run.Note($"Refused, no one to ask: {title}");
            return false;
        }
        var conv = new Conversation { Id = run.ApprovalKey, Title = $"{run.AgentName}: {Clip(run.Task, 60)}" };
        var bot = run.BotId is null ? null : await _bots.GetAsync(run.BotId);
        var call = new ToolCall
        {
            Name = $"{run.AgentName}: {Clip(title, 120)}",
            ArgumentsJson = Clip(details, 1500),
            ApprovalReason = $"{run.AgentName} is working in {run.Folder}{(run.NodeName is { } node ? $" on {node}" : "")}.",
        };
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Status = AgentRunStatus.WaitingForYou;
        run.WaitingOn = call.Id;
        Touch(run, force: true);
        _approvals.Register(conv, bot, call, decision);
        bool ok;
        using (ct.Register(() => _approvals.Cancel(call.Id)))
            try { ok = await decision.Task; }
            catch (TaskCanceledException) { ok = false; }
        ct.ThrowIfCancellationRequested();
        run.WaitingOn = null;
        run.Status = AgentRunStatus.Running;
        run.Note(ok ? $"You allowed: {title}" : $"You said no: {title}");
        Touch(run, force: true);
        return ok;
    }

    // ------------------------------------------------------------------ ACP agents

    private sealed class LiveSession(Process process, AcpClient client, string sessionId, StringBuilder errors) : IAsyncDisposable
    {
        public Process Process { get; } = process;
        public AcpClient Client { get; } = client;
        public string SessionId { get; } = sessionId;
        public StringBuilder Errors { get; } = errors;
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        public bool Alive => !Client.IsClosed && !Process.HasExited;

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            try { if (!Process.HasExited) Process.Kill(true); } catch { }
            Process.Dispose();
        }
    }

    private async Task RunAcpAsync(AgentRun run, AgentDef def, string cli, string text, CancellationToken ct)
    {
        var session = _sessions.TryGetValue(run.Id, out var open) && open.Alive ? open : await OpenAsync(run, def, cli, ct);
        session.LastUsed = DateTime.UtcNow;
        var prompt = new JsonObject
        {
            ["sessionId"] = session.SessionId,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        };
        JsonNode? result;
        try
        {
            result = await session.Client.RequestAsync("session/prompt", prompt, ct);
        }
        catch (OperationCanceledException)
        {
            try { await session.Client.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = session.SessionId }); } catch { }
            throw;
        }
        catch (AcpException ex) when (!session.Alive)
        {
            throw new InvalidOperationException($"{def.Name} stopped: {LastLines(session.Errors) ?? ex.Message}");
        }
        session.LastUsed = DateTime.UtcNow;
        var stop = result?["stopReason"]?.GetValue<string>();
        if (stop is "max_tokens" or "max_turn_requests") run.Note("It stopped early (it hit its own limit). Send a follow-up to continue.");
        else if (stop == "refusal") run.Note($"{def.Name} declined to do this.");
    }

    private async Task<LiveSession> OpenAsync(AgentRun run, AgentDef def, string cli, CancellationToken ct)
    {
        await CloseSessionAsync(run.Id);
        var psi = await LaunchAsync(run, cli, def.RunArgs);
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {def.Name}.");
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (errors) { errors.AppendLine(AgentCatalog.StripAnsi(e.Data)); if (errors.Length > 8000) errors.Remove(0, errors.Length - 8000); }
        };
        process.BeginErrorReadLine();
        var client = new AcpClient(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
        var loading = false;
        client.OnNotification = (method, p) =>
        {
            if (method == "session/update" && !loading) OnUpdate(run, p?["update"]);
        };
        client.OnRequest = async (method, p) => method switch
        {
            "session/request_permission" => await PermissionAsync(run, p, ct),
            _ => throw new AcpException($"Hearth doesn't support {method}."),
        };
        client.Start();
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(TimeSpan.FromMinutes(2));
            var init = await client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new JsonObject
                {
                    ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                    ["terminal"] = false,
                },
                ["clientInfo"] = new JsonObject { ["name"] = "hearth", ["title"] = "Hearth", ["version"] = "1" },
            }, startup.Token);
            string sessionId;
            var canLoad = init?["agentCapabilities"]?["loadSession"]?.GetValue<bool>() == true;
            if (canLoad && run.SessionId is { } earlier)
            {
                // A follow-up after the agent was closed: pick the same conversation back up (its replay isn't shown again).
                loading = true;
                try
                {
                    await client.RequestAsync("session/load", new JsonObject { ["sessionId"] = earlier, ["cwd"] = run.Folder, ["mcpServers"] = new JsonArray() }, startup.Token);
                    sessionId = earlier;
                }
                catch (AcpException) { sessionId = await NewSessionAsync(client, run, startup.Token); }
                finally { loading = false; }
            }
            else sessionId = await NewSessionAsync(client, run, startup.Token);
            run.SessionId = sessionId;
            var live = new LiveSession(process, client, sessionId, errors);
            _sessions[run.Id] = live;
            return live;
        }
        catch (Exception ex) when (ex is AcpException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            await client.DisposeAsync();
            try { if (!process.HasExited) process.Kill(true); } catch { }
            var why = LastLines(errors);
            var message = ex is OperationCanceledException ? $"{def.Name} didn't answer within 2 minutes." : ex.Message;
            if (message.Contains("auth", StringComparison.OrdinalIgnoreCase) || message.Contains("key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("login", StringComparison.OrdinalIgnoreCase))
                message += " " + def.SetupHint;
            throw new InvalidOperationException(why is null ? message : $"{message} ({why})");
        }
    }

    private static async Task<string> NewSessionAsync(AcpClient client, AgentRun run, CancellationToken ct)
    {
        var created = await client.RequestAsync("session/new", new JsonObject { ["cwd"] = run.Folder, ["mcpServers"] = new JsonArray() }, ct);
        return created?["sessionId"]?.GetValue<string>() ?? throw new AcpException("The agent didn't start a session.");
    }

    private static string? LastLines(StringBuilder errors)
    {
        string text;
        lock (errors) text = errors.ToString();
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3).ToList();
        return lines.Count == 0 ? null : Clip(string.Join(" ", lines), 400);
    }

    /// <summary>One session/update: text, thoughts, tool calls and the plan.</summary>
    public void OnUpdate(AgentRun run, JsonNode? u)
    {
        if (u is not JsonObject update) return;
        switch (update["sessionUpdate"]?.GetValue<string>())
        {
            case "agent_message_chunk":
                if (Str(update["content"], "text") is { } said) run.AddText(AgentStep.Text, said);
                break;
            case "agent_thought_chunk":
                if (Str(update["content"], "text") is { } thought) run.AddText(AgentStep.Thought, thought);
                break;
            case "tool_call":
            case "tool_call_update":
                run.Tool(Str(update, "toolCallId"), Str(update, "title") ?? "", Str(update, "kind"), Str(update, "status"));
                break;
            case "plan":
                if (update["entries"] is JsonArray entries)
                {
                    var lines = entries.OfType<JsonObject>().Select(e => (Str(e, "status") switch { "completed" => "☑", "in_progress" => "◐", _ => "☐" }) + " " + Str(e, "content"));
                    lock (run.Steps)
                    {
                        var plan = run.Steps.LastOrDefault(s => s.Kind == AgentStep.Plan);
                        if (plan is null || run.Steps.IndexOf(plan) < run.Steps.FindLastIndex(s => s.Kind == AgentStep.You))
                            run.Steps.Add(plan = new AgentStep { Kind = AgentStep.Plan });
                        plan.Content = string.Join("\n", lines);
                    }
                }
                break;
            default:
                return;
        }
        Touch(run);
    }

    private async Task<JsonNode?> PermissionAsync(AgentRun run, JsonNode? p, CancellationToken ct)
    {
        var call = p?["toolCall"] as JsonObject;
        var title = Str(call, "title") ?? "do something";
        var kind = Str(call, "kind");
        var details = call?["rawInput"]?.ToJsonString() ?? call?.ToJsonString() ?? "{}";
        if (call is not null) run.Tool(Str(call, "toolCallId"), title, kind, "pending");
        var options = (p?["options"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var ok = await DecideAsync(run, title, kind, details, ct);
        string? Pick(params string[] kinds) => kinds.Select(k => options.FirstOrDefault(o => Str(o, "kind") == k)).OfType<JsonObject>().Select(o => Str(o, "optionId")).FirstOrDefault();
        var option = ok ? Pick("allow_once", "allow_always") : Pick("reject_once", "reject_always");
        return option is null
            ? new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } }
            : new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = option } };
    }

    private async Task CloseSessionAsync(string runId)
    {
        if (_sessions.TryRemove(runId, out var s)) await s.DisposeAsync();
    }

    private async Task SweepAsync()
    {
        foreach (var (id, s) in _sessions)
            if ((!s.Alive || DateTime.UtcNow - s.LastUsed > KeepOpen) && !(_live.TryGetValue(id, out var r) && r.Active))
                await CloseSessionAsync(id);
        foreach (var (id, r) in _live)
            if (!r.Active && r.Ended is { } ended && DateTimeOffset.Now - ended > KeepOpen && !_sessions.ContainsKey(id))
                _live.TryRemove(id, out _);
    }

    // ------------------------------------------------------------------ Claude Code

    private async Task RunClaudeAsync(AgentRun run, string cli, string text, CancellationToken ct)
    {
        var allowed = new List<string>();
        for (var round = 0; round < 6; round++)
        {
            var outcome = await RunClaudeOnceAsync(run, cli, text, allowed, ct);
            if (outcome.Denials.Count == 0 || run.Mode is AgentMode.ReadOnly or AgentMode.FullAuto) return;
            var asked = outcome.Denials.Select(d => Describe(run, d.tool, d.input)).Distinct().ToList();
            var ok = await DecideAsync(run, string.Join("; ", asked), "other",
                new JsonArray(outcome.Denials.Select(d => (JsonNode)new JsonObject { ["tool"] = d.tool, ["input"] = d.input?.DeepClone() }).ToArray()).ToJsonString(),
                ct, alwaysAsk: true);
            if (!ok) return;
            allowed.AddRange(outcome.Denials.Select(d => Rule(d.tool, d.input)).Where(r => !allowed.Contains(r)));
            text = $"You now have permission for what was refused ({string.Join("; ", asked)}). Carry on with the task.";
        }
    }

    private sealed record ClaudeOutcome(List<(string tool, JsonNode? input)> Denials);

    private async Task<ClaudeOutcome> RunClaudeOnceAsync(AgentRun run, string cli, string text, List<string> allowed, CancellationToken ct)
    {
        var psi = await LaunchAsync(run, cli, ClaudeArgs(run, text, allowed));
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start Claude Code.");
        process.StandardInput.Close();
        var errors = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } l) lock (errors) { errors.AppendLine(l); if (errors.Length > 8000) errors.Remove(0, errors.Length - 8000); }
        });
        var denials = new List<(string, JsonNode?)>();
        var gotResult = false;
        using var kill = ct.Register(() => { try { process.Kill(true); } catch { } });
        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            JsonObject? o;
            try { o = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { continue; }
            if (o is null) continue;
            if (OnClaudeEvent(run, o, denials)) gotResult = true;
            Touch(run);
        }
        await process.WaitForExitAsync(ct);
        await errTask;
        if (!gotResult)
        {
            string err;
            lock (errors) err = errors.ToString().Trim();
            throw new InvalidOperationException(err.Length > 0 ? "Claude Code: " + Clip(AgentCatalog.StripAnsi(err), 500) : $"Claude Code stopped (exit code {process.ExitCode}).");
        }
        return new ClaudeOutcome(denials);
    }

    public static List<string> ClaudeArgs(AgentRun run, string text, IReadOnlyList<string> allowed)
    {
        var args = new List<string> { "-p", text, "--output-format", "stream-json", "--verbose", "--permission-mode",
            run.Mode switch { AgentMode.ReadOnly => "plan", AgentMode.AcceptEdits => "acceptEdits", AgentMode.FullAuto => "bypassPermissions", _ => "default" } };
        if (run.SessionId is { } sid) { args.Add("--resume"); args.Add(sid); }
        if (allowed.Count > 0) { args.Add("--allowedTools"); args.AddRange(allowed); }
        return args;
    }

    /// <summary>One line of Claude Code's stream-json output. Returns true for the final result.</summary>
    public static bool OnClaudeEvent(AgentRun run, JsonObject o, List<(string tool, JsonNode? input)> denials)
    {
        switch (Str(o, "type"))
        {
            case "system" when Str(o, "subtype") == "init":
                run.SessionId = Str(o, "session_id") ?? run.SessionId;
                return false;
            case "assistant":
                foreach (var c in (o["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    switch (Str(c, "type"))
                    {
                        case "text" when Str(c, "text") is { Length: > 0 } t:
                            lock (run.Steps)
                            {
                                if (run.Steps.Count > 0 && run.Steps[^1].Kind == AgentStep.Text) run.Steps[^1].Content += "\n\n" + t;
                                else run.Steps.Add(new AgentStep { Kind = AgentStep.Text, Content = t });
                            }
                            break;
                        case "tool_use":
                            var name = Str(c, "name") ?? "tool";
                            run.Tool(Str(c, "id"), Describe(run, name, c["input"]), KindOf(name), "in_progress");
                            break;
                    }
                return false;
            case "user":
                foreach (var c in (o["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    if (Str(c, "type") == "tool_result")
                        run.Tool(Str(c, "tool_use_id"), "", null, c["is_error"]?.GetValue<bool>() == true ? "failed" : "completed");
                return false;
            case "result":
                run.SessionId = Str(o, "session_id") ?? run.SessionId;
                if (Str(o, "result") is { Length: > 0 } r) run.Result = r;
                foreach (var d in (o["permission_denials"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    denials.Add((Str(d, "tool_name") ?? "tool", d["tool_input"]?.DeepClone()));
                if (o["is_error"]?.GetValue<bool>() == true && denials.Count == 0)
                    throw new InvalidOperationException("Claude Code: " + Clip(Str(o, "result") ?? Str(o, "subtype") ?? "it reported an error", 400));
                return true;
        }
        return false;
    }

    public static string KindOf(string tool) => tool switch
    {
        "Read" or "NotebookRead" or "LS" => "read",
        "Write" or "Edit" or "MultiEdit" or "NotebookEdit" => "edit",
        "Bash" or "BashOutput" or "KillShell" => "execute",
        "Glob" or "Grep" or "WebSearch" => "search",
        "WebFetch" => "fetch",
        "TodoWrite" or "Task" => "think",
        _ => "other",
    };

    /// <summary>A Claude Code tool call in a few words ("Edit src/app.cs", "Run `npm test`").</summary>
    public static string Describe(AgentRun run, string tool, JsonNode? input)
    {
        string? Arg(string name) => input is JsonObject o ? Str(o, name) : null;
        string Rel(string? p) => p is null ? "" : p.StartsWith(run.Folder.TrimEnd('/') + "/", StringComparison.Ordinal) ? p[(run.Folder.TrimEnd('/').Length + 1)..] : p;
        return tool switch
        {
            "Bash" => $"Run `{Clip(Arg("command") ?? "", 160)}`",
            "Read" => $"Read {Rel(Arg("file_path"))}",
            "Write" => $"Write {Rel(Arg("file_path"))}",
            "Edit" or "MultiEdit" => $"Edit {Rel(Arg("file_path"))}",
            "NotebookEdit" => $"Edit {Rel(Arg("notebook_path"))}",
            "Glob" => $"Find files {Arg("pattern")}",
            "Grep" => $"Search for {Clip(Arg("pattern") ?? "", 80)}",
            "WebFetch" => $"Open {Arg("url")}",
            "WebSearch" => $"Search the web: {Arg("query")}",
            "TodoWrite" => "Update its to-do list",
            "Task" => $"Start a helper: {Arg("description")}",
            _ => tool,
        };
    }

    /// <summary>The --allowedTools rule that lets exactly this step through on the next try.</summary>
    public static string Rule(string tool, JsonNode? input) =>
        tool == "Bash" && input is JsonObject o && Str(o, "command") is { Length: > 0 } cmd && !cmd.Contains(')') ? $"Bash({cmd})" : tool;

    // ------------------------------------------------------------------ helpers

    private static string? Str(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";

    public async ValueTask DisposeAsync()
    {
        await _sweep.DisposeAsync();
        foreach (var id in _sessions.Keys.ToList()) await CloseSessionAsync(id);
        foreach (var c in _cancel.Values) try { c.Cancel(); } catch { }
    }
}
