using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Locus.Core.Services;

namespace Locus.Core.Tools;

/// <summary>Resolves paths inside the bots' workspace and refuses anything that escapes it.</summary>
public static class Workspace
{
    public static string Resolve(string root, string? relative)
    {
        Directory.CreateDirectory(root);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootFull, (relative ?? "").TrimStart('/', '\\')));
        if (!(full + Path.DirectorySeparatorChar).StartsWith(rootFull, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Paths must stay inside the workspace.");
        return full;
    }

    public static string Relative(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');
}

public sealed class ListFilesTool(IBotComputer computer) : ITool
{
    public string Name => "list_files";
    public string DisplayName => "List files";
    public string Icon => "🗂️";
    public string Description => "List files in the shared workspace (where downloads and files you write are kept).";
    public JsonObject Parameters => Schema.Object(("path", "string", "Subfolder (default: workspace root)", false));
    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var root = computer.WorkspaceDirectory;
        var dir = Workspace.Resolve(root, args.Str("path"));
        if (!Directory.Exists(dir)) return Task.FromResult(ToolResult.Error("No such folder."));
        var sb = new StringBuilder();
        foreach (var d in Directory.EnumerateDirectories(dir).Take(200)) sb.AppendLine($"📁 {Workspace.Relative(root, d)}/");
        foreach (var f in Directory.EnumerateFiles(dir).Take(500))
        {
            var i = new FileInfo(f);
            sb.AppendLine($"📄 {Workspace.Relative(root, f)}  ({i.Length:N0} bytes, {i.LastWriteTime:yyyy-MM-dd HH:mm})");
        }
        return Task.FromResult(new ToolResult(sb.Length == 0 ? "(empty)" : sb.ToString()));
    }
}

public sealed class ReadFileTool(IBotComputer computer) : ITool
{
    public string Name => "read_file";
    public string DisplayName => "Read file";
    public string Icon => "📖";
    public string Description => "Read a file from the shared workspace (text, code, PDF, HTML).";
    public JsonObject Parameters => Schema.Object(("path", "string", "Path relative to the workspace", true));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var path = Workspace.Resolve(computer.WorkspaceDirectory, args.Str("path"));
        if (!File.Exists(path)) return ToolResult.Error("No such file.");
        if (new FileInfo(path).Length > 20 * 1024 * 1024) return ToolResult.Error("File is larger than 20 MB.");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var text = TextExtractor.IsSupported(path) || Path.GetExtension(path).Length == 0
            ? TextExtractor.Extract(path, bytes)
            : $"(binary file, {bytes.Length:N0} bytes)";
        return new ToolResult(Schema.Truncate(text, 30000));
    }
}

public sealed class WriteFileTool(IBotComputer computer) : ITool
{
    public string Name => "write_file";
    public string DisplayName => "Write file";
    public string Icon => "💾";
    public string Description => "Create or overwrite a text file in the shared workspace (notes, reports, CSV, code).";
    public JsonObject Parameters => Schema.Object(
        ("path", "string", "Path relative to the workspace, e.g. reports/weekly.md", true),
        ("content", "string", "Full file content", true),
        ("append", "boolean", "Append instead of overwrite", false));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var path = Workspace.Resolve(computer.WorkspaceDirectory, args.Str("path"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = args.Str("content") ?? "";
        if (args.Bool("append")) await File.AppendAllTextAsync(path, content, ct);
        else await File.WriteAllTextAsync(path, content, ct);
        return new ToolResult($"Saved {Workspace.Relative(computer.WorkspaceDirectory, path)} ({content.Length:N0} chars).");
    }
}

/// <summary>Shell commands in the workspace (desktop only, opt-in, always asks unless auto-approve is on).</summary>
public sealed class RunCommandTool(IBotComputer computer, CodeSandbox? sandbox = null) : ITool, IContextualApproval
{
    public string Name => "run_command";
    public string DisplayName => "Run command";
    public string Icon => "⌨️";
    public string Description =>
        "Run a shell command in the shared workspace folder and return its output (in the code sandbox — a Linux " +
        "container where the workspace is /workspace — when it's on; otherwise on the user's computer). " +
        "Time limit 120s. No interactive programs.";
    public JsonObject Parameters => Schema.Object(("command", "string", "The command line", true));
    public bool RequiresApproval => true;
    public bool RequiresApprovalFor(ToolContext ctx) => !(sandbox?.Active(ctx.Settings) ?? false);

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var command = args.Str("command");
        if (string.IsNullOrWhiteSpace(command)) return ToolResult.Error("command is required");
        if (sandbox is not null && sandbox.Active(ctx.Settings))
        {
            try { return SandboxOutput.ToResult(await sandbox.RunAsync(ctx.Settings.Tools.Sandbox, computer.WorkspaceDirectory, command, ct: ct), 120); }
            catch (InvalidOperationException ex) { return ToolResult.Error(ex.Message); }
        }
        var dir = Workspace.Resolve(computer.WorkspaceDirectory, null);
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", command } }
            : new ProcessStartInfo("/bin/bash") { ArgumentList = { "-lc", command } };
        psi.WorkingDirectory = dir;
        psi.RedirectStandardOutput = psi.RedirectStandardError = psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using var proc = Process.Start(psi)!;
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            return ToolResult.Error(ct.IsCancellationRequested ? "Cancelled." : "Timed out after 120 seconds.");
        }
        var o = await stdout;
        var e = await stderr;
        var sb = new StringBuilder();
        if (o.Length > 0) sb.AppendLine(Schema.Truncate(o, 12000));
        if (e.Length > 0) sb.AppendLine("stderr:\n" + Schema.Truncate(e, 4000));
        sb.Append($"(exit code {proc.ExitCode})");
        return new ToolResult(sb.ToString(), IsError: proc.ExitCode != 0);
    }
}
