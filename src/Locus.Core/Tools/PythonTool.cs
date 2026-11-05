using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Locus.Core.Services;

namespace Locus.Core.Tools;

/// <summary>
/// Runs Python in the code sandbox (a Docker container, no approval needed) or, with the sandbox off or Docker
/// missing, on the user's machine — which is NOT a security sandbox, so it always asks first.
/// </summary>
public sealed class PythonTool(CodeSandbox? sandbox = null, IBotComputer? computer = null) : ITool, IContextualApproval
{
    public string Name => "run_python";
    public string DisplayName => "Run Python";
    public string Icon => "🐍";
    public string Description =>
        "Execute a Python 3 script and return stdout/stderr. Use for data analysis, precise computation, parsing or " +
        "simulations. Print results explicitly. No input() is available. Time limit 60s. In the sandbox, files in the " +
        "workspace are at /workspace and you can pip install packages.";
    public JsonObject Parameters => Schema.Object(("code", "string", "Complete Python 3 source code", true));
    public bool RequiresApproval => true;
    public bool RequiresApprovalFor(ToolContext ctx) => !(sandbox?.Active(ctx.Settings) ?? false);

    public static string? FindPython(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var names = OperatingSystem.IsWindows() ? new[] { "python.exe", "py.exe", "python3.exe" } : ["python3", "python"];
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]);
        foreach (var dir in dirs)
            foreach (var n in names)
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var code = args.Str("code");
        if (string.IsNullOrWhiteSpace(code)) return ToolResult.Error("code is required");
        if (sandbox is not null && computer is not null && sandbox.Active(ctx.Settings))
        {
            try
            {
                var r = await sandbox.RunAsync(ctx.Settings.Tools.Sandbox, computer.WorkspaceDirectory,
                    "PYTHONIOENCODING=utf-8 python3 -", code, timeout: TimeSpan.FromSeconds(60), ct: ct);
                return SandboxOutput.ToResult(r, 60);
            }
            catch (InvalidOperationException ex) { return ToolResult.Error(ex.Message); }
        }
        var python = FindPython(ctx.Settings.Tools.PythonPath);
        if (python is null) return ToolResult.Error("Python was not found on this computer.");

        var dir = Directory.CreateTempSubdirectory("locus-py-");
        try
        {
            var file = Path.Combine(dir.FullName, "main.py");
            await File.WriteAllTextAsync(file, code, ct);
            var psi = new ProcessStartInfo(python)
            {
                WorkingDirectory = dir.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-I");
            psi.ArgumentList.Add(file);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            using var proc = Process.Start(psi)!;
            proc.StandardInput.Close();
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                proc.Kill(entireProcessTree: true);
                return ToolResult.Error(ct.IsCancellationRequested ? "Cancelled." : "Timed out after 60 seconds.");
            }

            var sb = new StringBuilder();
            var o = await stdout;
            var e = await stderr;
            if (o.Length > 0) sb.AppendLine(Schema.Truncate(o, 10000));
            if (e.Length > 0) sb.AppendLine("stderr:\n" + Schema.Truncate(e, 4000));
            sb.Append($"(exit code {proc.ExitCode})");
            return new ToolResult(sb.ToString(), IsError: proc.ExitCode != 0);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>Formats sandbox output like the local tools do.</summary>
public static class SandboxOutput
{
    public static ToolResult ToResult(SandboxResult r, int seconds)
    {
        if (r.TimedOut) return ToolResult.Error($"Timed out after {seconds} seconds.\n{Schema.Truncate(r.Stdout, 4000)}");
        var sb = new StringBuilder();
        if (r.Stdout.Length > 0) sb.AppendLine(Schema.Truncate(r.Stdout, 12000));
        if (r.Stderr.Length > 0) sb.AppendLine("stderr:\n" + Schema.Truncate(r.Stderr, 4000));
        sb.Append($"(exit code {r.ExitCode}, in the sandbox)");
        return new ToolResult(sb.ToString(), IsError: r.ExitCode != 0);
    }
}
