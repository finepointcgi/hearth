using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

public sealed record SandboxResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>
/// Runs bots' code in Docker containers instead of directly on this computer, so it doesn't need your approval:
/// no extra privileges (all capabilities dropped, no-new-privileges), limited memory, CPU and processes, and it only
/// sees Hearth's workspace folder (or, for project commands, that one project folder). Containers are kept for
/// quick reuse and removed after 30 minutes idle.
/// </summary>
public sealed class CodeSandbox : IDisposable
{
    public const string WorkspaceMount = "/workspace";
    public const string ProjectMount = "/project";
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(30);

    private readonly PlatformInfo _platform;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUse = new();
    private readonly SemaphoreSlim _start = new(1, 1);
    private readonly Timer _idle;
    private string? _docker;
    private bool _lookedForDocker;

    public CodeSandbox(PlatformInfo platform) : this(platform, null, false) { }

    /// <summary>For tests: a fixed docker path (or none).</summary>
    public CodeSandbox(PlatformInfo platform, string? dockerPath, bool lookedForDocker = true)
    {
        _platform = platform;
        _docker = dockerPath;
        _lookedForDocker = lookedForDocker;
        _idle = new Timer(_ => _ = RemoveIdleAsync(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>The docker command, if installed (a stable check: code never silently moves out of the sandbox).</summary>
    public string? DockerPath
    {
        get
        {
            if (_lookedForDocker) return _docker;
            _lookedForDocker = true;
            if (!_platform.CanRunProcesses) return null;
            string[] dirs = [.. (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator),
                "/usr/local/bin", "/opt/homebrew/bin", "/usr/bin", "/Applications/Docker.app/Contents/Resources/bin",
                @"C:\Program Files\Docker\Docker\resources\bin"];
            var exe = OperatingSystem.IsWindows() ? "docker.exe" : "docker";
            return _docker = dirs.Where(d => d.Length > 0).Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
        }
    }

    /// <summary>Code runs in the sandbox (and so needs no approval) when it's on and Docker is installed.</summary>
    public bool Active(AppSettings s) => s.Tools.Sandbox.Enabled && DockerPath is not null;

    /// <summary>Is Docker running? (For the settings page.)</summary>
    public async Task<string?> ProblemAsync(CancellationToken ct = default)
    {
        if (DockerPath is null) return "Docker isn't installed.";
        var r = await DockerAsync(["info", "--format", "{{.ServerVersion}}"], null, TimeSpan.FromSeconds(10), ct);
        return r.ExitCode == 0 ? null : "Docker isn't running. Start Docker Desktop.";
    }

    /// <summary>
    /// Runs a shell command in the sandbox. <paramref name="projectPath"/> mounts that folder at /project and
    /// starts there; otherwise it starts in /workspace (Hearth's workspace folder).
    /// </summary>
    public async Task<SandboxResult> RunAsync(SandboxSettings s, string workspace, string command, string? stdin = null,
        string? projectPath = null, string? image = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (DockerPath is null) throw new InvalidOperationException("Docker isn't installed, so the sandbox isn't available.");
        image = string.IsNullOrWhiteSpace(image) ? s.Image : image.Trim();
        var limit = timeout ?? TimeSpan.FromSeconds(120);
        var name = ContainerName(workspace, projectPath, image, s.Network);
        await EnsureRunningAsync(name, s, workspace, projectPath, image, ct);
        _lastUse[name] = DateTimeOffset.Now;
        try
        {
            // `timeout` inside the container stops the program itself; the host-side limit is a backstop.
            var secs = (int)Math.Ceiling(limit.TotalSeconds);
            var r = await DockerAsync(["exec", "-i", "-w", projectPath is null ? WorkspaceMount : ProjectMount, name,
                "timeout", "-k", "5", secs.ToString(), "sh", "-c", command], stdin, limit + TimeSpan.FromSeconds(15), ct);
            return r with { TimedOut = r.TimedOut || r.ExitCode is 124 or 137 };
        }
        finally { _lastUse[name] = DateTimeOffset.Now; }
    }

    /// <summary>
    /// Like <see cref="RunAsync"/>, but the program can talk back while it runs: every output line starting with
    /// <paramref name="marker"/> goes to <paramref name="onRequest"/>, whose answer is written to the program's input
    /// as one line. Other output is collected as usual.
    /// </summary>
    public async Task<SandboxResult> RunInteractiveAsync(SandboxSettings s, string workspace, string command, string marker,
        Func<string, Task<string>> onRequest, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (DockerPath is null) throw new InvalidOperationException("Docker isn't installed, so the sandbox isn't available.");
        var limit = timeout ?? TimeSpan.FromSeconds(300);
        var name = ContainerName(workspace, null, s.Image, s.Network);
        await EnsureRunningAsync(name, s, workspace, null, s.Image, ct);
        _lastUse[name] = DateTimeOffset.Now;
        var psi = new ProcessStartInfo(DockerPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "exec", "-i", "-w", WorkspaceMount, name, "timeout", "-k", "5", ((int)Math.Ceiling(limit.TotalSeconds)).ToString(), "sh", "-c", command })
            psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = $"{Path.GetDirectoryName(DockerPath)}{Path.PathSeparator}/usr/local/bin{Path.PathSeparator}/opt/homebrew/bin{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}";
        using var proc = Process.Start(psi)!;
        using var limitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limitCts.CancelAfter(limit + TimeSpan.FromSeconds(15));
        var stderr = proc.StandardError.ReadToEndAsync(limitCts.Token);
        var output = new StringBuilder();
        try
        {
            while (await proc.StandardOutput.ReadLineAsync(limitCts.Token) is { } line)
            {
                if (line.StartsWith(marker, StringComparison.Ordinal))
                {
                    var answer = await onRequest(line[marker.Length..]);
                    await proc.StandardInput.WriteLineAsync(answer.Replace("\n", " ").Replace("\r", " "));
                    await proc.StandardInput.FlushAsync(limitCts.Token);
                }
                else if (output.Length < 200_000) output.AppendLine(line);
            }
            await proc.WaitForExitAsync(limitCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return new SandboxResult(-1, output.ToString(), "Timed out.", true);
        }
        finally { _lastUse[name] = DateTimeOffset.Now; }
        return new SandboxResult(proc.ExitCode, output.ToString(), await stderr, proc.ExitCode is 124 or 137);
    }

    private static string ContainerName(string workspace, string? project, string image, bool network)
    {
        var key = $"{workspace}|{project}|{image}|{network}";
        return "hearth-code-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
    }

    private async Task EnsureRunningAsync(string name, SandboxSettings s, string workspace, string? project, string image, CancellationToken ct)
    {
        await _start.WaitAsync(ct);
        try
        {
            var state = await DockerAsync(["inspect", "-f", "{{.State.Running}}", name], null, TimeSpan.FromSeconds(15), ct);
            if (state.ExitCode == 0 && state.Stdout.Trim() == "true") return;
            if (state.ExitCode == 0) await DockerAsync(["rm", "-f", name], null, TimeSpan.FromSeconds(30), ct);

            if ((await DockerAsync(["image", "inspect", image], null, TimeSpan.FromSeconds(15), ct)).ExitCode != 0)
            {
                var pull = await DockerAsync(["pull", image], null, TimeSpan.FromMinutes(10), ct);
                if (pull.ExitCode != 0) throw new InvalidOperationException($"Couldn't download the sandbox image {image}: {Last(pull.Stderr)}");
            }
            Directory.CreateDirectory(workspace);
            List<string> args =
            [
                "run", "-d", "--name", name, "--label", "hearth=code", "--init",
                "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                "--pids-limit", "512", "--memory", $"{Math.Max(256, s.MemoryMb)}m", "--cpus", "2",
                "-v", $"{workspace}:{WorkspaceMount}",
                "-w", project is null ? WorkspaceMount : ProjectMount,
            ];
            if (project is not null) args.AddRange(["-v", $"{project}:{ProjectMount}"]);
            if (!s.Network) args.AddRange(["--network", "none"]);
            args.AddRange([image, "sleep", "infinity"]);
            var run = await DockerAsync(args, null, TimeSpan.FromMinutes(1), ct);
            if (run.ExitCode != 0)
                throw new InvalidOperationException(run.Stderr.Contains("daemon", StringComparison.OrdinalIgnoreCase)
                    ? "Docker isn't running. Start Docker Desktop." : $"Couldn't start the sandbox: {Last(run.Stderr)}");
        }
        finally { _start.Release(); }
    }

    private async Task RemoveIdleAsync()
    {
        foreach (var (name, last) in _lastUse.ToArray())
            if (DateTimeOffset.Now - last > IdleAfter && _lastUse.TryRemove(name, out _))
                try { await DockerAsync(["rm", "-f", name], null, TimeSpan.FromSeconds(30), default); } catch { }
    }

    private static string Last(string s) => s.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";

    private async Task<SandboxResult> DockerAsync(IEnumerable<string> args, string? stdin, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(DockerPath!)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Docker Desktop's helpers live next to docker; a background service may have a minimal PATH.
        psi.Environment["PATH"] = $"{Path.GetDirectoryName(DockerPath)}{Path.PathSeparator}/usr/local/bin{Path.PathSeparator}/opt/homebrew/bin{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}";
        using var proc = Process.Start(psi)!;
        if (stdin is not null) await proc.StandardInput.WriteAsync(stdin);
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try { await proc.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return new SandboxResult(-1, "", "Timed out.", true);
        }
        return new SandboxResult(proc.ExitCode, await stdout, await stderr, false);
    }

    public void Dispose() => _idle.Dispose();
}
