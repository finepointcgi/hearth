using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hearth.Core.Models;

namespace Hearth.Core.Tools;

/// <summary>A computer bots may run commands on over SSH (uses your SSH keys; no passwords are stored).</summary>
public sealed class RemoteHost
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    /// <summary>user@host</summary>
    public string Target { get; set; } = "";
    public int Port { get; set; } = 22;
    /// <summary>Optional private key file; otherwise your SSH agent / ~/.ssh config is used.</summary>
    public string? KeyPath { get; set; }
    public string? Notes { get; set; }
}

public static partial class Ssh
{
    [GeneratedRegex(@"^[A-Za-z0-9._-]+@[A-Za-z0-9._:\[\]-]+$|^[A-Za-z0-9._-]+$")] private static partial Regex TargetPattern();

    public static string? Validate(RemoteHost h) =>
        string.IsNullOrWhiteSpace(h.Name) ? "Give it a name."
        : !TargetPattern().IsMatch(h.Target.Trim()) || h.Target.StartsWith('-') ? "Use user@host (or a Host alias from ~/.ssh/config)."
        : h.Port is < 1 or > 65535 ? "Port must be 1–65535." : null;

    /// <summary>
    /// The ssh process for one command on the host: key auth only (BatchMode, never a password prompt), no terminal,
    /// so the command's stdin and stdout pass straight through (agents speak JSON over them).
    /// </summary>
    public static ProcessStartInfo Start(RemoteHost h, string command)
    {
        if (Validate(h) is { } problem) throw new InvalidOperationException(problem);
        var psi = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=30", "-o", "StrictHostKeyChecking=accept-new",
                     "-o", "LogLevel=ERROR", "-p", h.Port.ToString() })
            psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(h.KeyPath)) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(Environment.ExpandEnvironmentVariables(h.KeyPath.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))); }
        psi.ArgumentList.Add(h.Target.Trim());
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(command);
        return psi;
    }

    /// <summary>A word for the remote shell, quoted so nothing in it is interpreted.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>A folder path for the remote shell; ~ stays the remote user's home.</summary>
    public static string QuotePath(string path) =>
        path == "~" ? "\"$HOME\"" : path.StartsWith("~/") ? "\"$HOME\"/" + Quote(path[2..]) : Quote(path);

    /// <summary>Runs one command on the host. BatchMode: key auth only, never a password prompt.</summary>
    public static async Task<(int code, string output)> RunAsync(RemoteHost h, string command, TimeSpan timeout, CancellationToken ct)
    {
        var psi = Start(h, command);
        psi.RedirectStandardInput = false;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start ssh. Is OpenSSH installed?");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var o = p.StandardOutput.ReadToEndAsync(cts.Token);
        var e = p.StandardError.ReadToEndAsync(cts.Token);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            return (-1, $"Timed out after {timeout.TotalSeconds:0}s.");
        }
        var output = (await o.ConfigureAwait(false)) + (await e.ConfigureAwait(false));
        return (p.ExitCode, output);
    }
}

public sealed class RunRemoteTool(IReadOnlyList<RemoteHost> hosts) : ITool, IPermissionInfo
{
    public string Name => "run_remote";
    public string DisplayName => "Run on remote computer";
    public string Icon => "🛰️";
    public string Description =>
        "Run a shell command on one of the user's other computers over SSH and get its output. Computers: " +
        string.Join("; ", hosts.Select(h => $"\"{h.Name}\"{(string.IsNullOrWhiteSpace(h.Notes) ? "" : $" ({h.Notes})")}")) +
        ". Each command runs on its own (no shell state carries over), so chain steps with && or cd first.";
    public JsonObject Parameters => Schema.Object(
        ("computer", "string", "Which computer (its name)", true),
        ("command", "string", "Shell command to run there", true),
        ("timeout_seconds", "integer", "Default 120, max 600", false));
    public bool RequiresApproval => true;
    public string Area => "remote";
    public string AreaTitle => "Remote computers";
    public bool IsWrite => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var name = args.Str("computer") ?? "";
        var host = hosts.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? (hosts.Count == 1 ? hosts[0] : null);
        if (host is null) return ToolResult.Error($"No computer named '{name}'. Computers: {string.Join(", ", hosts.Select(h => h.Name))}");
        var command = args.Str("command");
        if (string.IsNullOrWhiteSpace(command)) return ToolResult.Error("command is required");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(args.Num("timeout_seconds") ?? 120, 5, 600));
        try
        {
            var (code, output) = await Ssh.RunAsync(host, command, timeout, ct);
            if (output.Length > 15000) output = output[..7000] + "\n…(trimmed)…\n" + output[^7000..];
            if (code == 255 && output.Contains("Permission denied"))
                return ToolResult.Error($"SSH to {host.Name} was refused: Hearth signs in with SSH keys only. Add this computer's key to {host.Target} (ssh-copy-id).");
            return new ToolResult($"[{host.Name}] exit {code}\n{output}", IsError: code != 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Error(ex.Message);
        }
    }
}
