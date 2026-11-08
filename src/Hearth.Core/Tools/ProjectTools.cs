using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Hearth.Core.Models;
using Hearth.Core.Services;

namespace Hearth.Core.Tools;

/// <summary>
/// Coding in folders you made editable (Documents → Linked folders → "Bots can edit"): change files (you approve each
/// change and see the diff; every change can be undone) and run commands there — in the code sandbox, which only sees
/// that folder, without asking; on this computer, asking first.
/// </summary>
public sealed class ProjectToolProvider(LinkedFolders folders, ProjectHistory history, CodeSandbox sandbox, PlatformInfo platform) : IToolProvider
{
    public async Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct)
    {
        if (!(await folders.ListAsync()).Any(f => f.Enabled && f.Editable)) return [];
        List<ITool> tools =
        [
            new EditProjectFileTool(folders, history),
            new WriteProjectFileTool(folders, history),
            new ListProjectChangesTool(history),
            new UndoProjectChangeTool(folders, history),
        ];
        if (platform.CanRunProcesses) tools.Add(new RunInProjectTool(folders, sandbox));
        return tools;
    }
}

/// <summary>Shared checks for writing into a project folder.</summary>
internal static class ProjectFiles
{
    public const long MaxBytes = 2 * 1024 * 1024;

    public static async Task<(LinkedFolder? folder, string full, string? error)> ResolveAsync(LinkedFolders folders, string? path)
    {
        var full = LinkedFolders.Normalize(path ?? "");
        if (full.Length == 0) return (null, full, "Give the file's full path.");
        var any = await folders.FolderForAsync(full);
        if (any is null) return (null, full, "That path isn't in a linked folder (hidden files and links can't be changed).");
        if (!any.Editable) return (null, full, $"{any.Path} is read-only. The user can allow edits on the Documents page.");
        return (any, full, null);
    }

    public static async Task WriteAtomicAsync(string path, string text, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".hearth-tmp";
        await File.WriteAllTextAsync(tmp, text, new UTF8Encoding(false), ct);
        File.Move(tmp, path, overwrite: true);
    }

    public static string Who(ToolContext ctx) => ctx.Bot?.Name ?? "Hearth";
}

public sealed class EditProjectFileTool(LinkedFolders folders, ProjectHistory history) : ITool, IPermissionInfo
{
    public string Name => "edit_project_file";
    public string DisplayName => "Edit file";
    public string Icon => "✏️";
    public string Description =>
        "Change part of a file in an editable project folder: old_text (copied exactly from the file, with enough " +
        "surrounding lines to be unique) is replaced by new_text. Read the file first with read_linked_file. The user " +
        "sees the change and approves it; it can be undone.";
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Full path of the file" },
            ["old_text"] = new JsonObject { ["type"] = "string", ["description"] = "Exact text to replace" },
            ["new_text"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement text" },
            ["replace_all"] = new JsonObject { ["type"] = "boolean", ["description"] = "Replace every occurrence (default: must be unique)" },
        },
        ["required"] = new JsonArray("path", "old_text", "new_text"),
    };
    public bool RequiresApproval => true;
    public string Area => "projects";
    public string AreaTitle => "Project folders";
    public bool IsWrite => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var (folder, full, error) = await ProjectFiles.ResolveAsync(folders, args.Str("path"));
        if (error is not null) return ToolResult.Error(error);
        if (!File.Exists(full)) return ToolResult.Error($"No file at {full}. To create one, use write_project_file.");
        if (new FileInfo(full).Length > ProjectFiles.MaxBytes) return ToolResult.Error("That file is too large to edit here.");
        var oldText = args.Str("old_text") ?? "";
        var newText = args.Str("new_text") ?? "";
        if (oldText.Length == 0) return ToolResult.Error("old_text can't be empty.");
        var text = await File.ReadAllTextAsync(full, ct);
        if (!text.Contains(oldText, StringComparison.Ordinal) && text.Contains("\r\n") && !oldText.Contains('\r'))
        {
            oldText = oldText.Replace("\n", "\r\n");
            newText = newText.Replace("\n", "\r\n");
        }
        var count = Count(text, oldText);
        if (count == 0) return ToolResult.Error("old_text wasn't found in the file. Read it again and copy the text exactly.");
        if (count > 1 && !args.Bool("replace_all"))
            return ToolResult.Error($"old_text appears {count} times. Include more surrounding lines to make it unique, or set replace_all.");
        var change = await history.RecordAsync(folder!.Id, full, $"Edited {Path.GetFileName(full)}", ctx.Conversation.Id, ctx.Bot?.Id);
        await ProjectFiles.WriteAtomicAsync(full, text.Replace(oldText, newText, StringComparison.Ordinal), ct);
        return new ToolResult($"Edited {full} ({count} place{(count == 1 ? "" : "s")}). Change {change.Id} (undo_project_change can revert it).");
    }

    private static int Count(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}

public sealed class WriteProjectFileTool(LinkedFolders folders, ProjectHistory history) : ITool, IPermissionInfo
{
    public string Name => "write_project_file";
    public string DisplayName => "Write file";
    public string Icon => "💾";
    public string Description =>
        "Create a file (or replace a whole file) in an editable project folder. For changing part of an existing file, " +
        "use edit_project_file instead. The user approves it; it can be undone.";
    public JsonObject Parameters => Schema.Object(("path", "string", "Full path of the file", true), ("content", "string", "The whole file", true));
    public bool RequiresApproval => true;
    public string Area => "projects";
    public string AreaTitle => "Project folders";
    public bool IsWrite => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var (folder, full, error) = await ProjectFiles.ResolveAsync(folders, args.Str("path"));
        if (error is not null) return ToolResult.Error(error);
        var content = args.Str("content") ?? "";
        if (Encoding.UTF8.GetByteCount(content) > ProjectFiles.MaxBytes) return ToolResult.Error("That's too large to write here.");
        if (Directory.Exists(full)) return ToolResult.Error("That's a folder.");
        var existed = File.Exists(full);
        var change = await history.RecordAsync(folder!.Id, full, $"{(existed ? "Rewrote" : "Created")} {Path.GetFileName(full)}", ctx.Conversation.Id, ctx.Bot?.Id);
        await ProjectFiles.WriteAtomicAsync(full, content, ct);
        return new ToolResult($"{(existed ? "Replaced" : "Created")} {full}. Change {change.Id} (undo_project_change can revert it).");
    }
}

public sealed class ListProjectChangesTool(ProjectHistory history) : ITool
{
    public string Name => "list_project_changes";
    public string DisplayName => "Recent changes";
    public string Icon => "🕘";
    public string Description => "List recent changes bots made to project files (newest first), with ids for undo_project_change.";
    public JsonObject Parameters => Schema.Object(("this_chat_only", "boolean", "Only changes made in this chat (default true)", false));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var onlyHere = args["this_chat_only"] is null || args.Bool("this_chat_only");
        var list = (await history.ListAsync()).Where(c => !onlyHere || c.ConversationId == ctx.Conversation.Id)
            .OrderByDescending(c => c.At).Take(30).ToList();
        return new ToolResult(list.Count == 0 ? "No changes." :
            string.Join("\n", list.Select(c => $"{c.Id}: {c.At:MMM d h:mm tt} — {c.Summary} ({c.Path})")));
    }
}

public sealed class UndoProjectChangeTool(LinkedFolders folders, ProjectHistory history) : ITool, IPermissionInfo
{
    public string Name => "undo_project_change";
    public string DisplayName => "Undo change";
    public string Icon => "↩️";
    public string Description => "Put a file back the way it was before one of your changes (id from list_project_changes). Newest change to a file first.";
    public JsonObject Parameters => Schema.Object(("id", "string", "Change id", true));
    public bool RequiresApproval => true;
    public string Area => "projects";
    public string AreaTitle => "Project folders";
    public bool IsWrite => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var change = (await history.ListAsync()).FirstOrDefault(c => c.Id == args.Str("id"));
        if (change is null) return ToolResult.Error("No change with that id.");
        if (await folders.EditableFolderForAsync(change.Path) is null) return ToolResult.Error("That folder isn't editable any more.");
        return await history.UndoAsync(change.Id) is { } error ? ToolResult.Error(error) : new ToolResult($"Undid: {change.Summary}.");
    }
}

public sealed class RunInProjectTool(LinkedFolders folders, CodeSandbox sandbox) : ITool, IPermissionInfo, IContextualApproval
{
    public const int MaxSeconds = 600;

    public string Name => "run_in_project";
    public string DisplayName => "Run in project";
    public string Icon => "⌨️";
    public string Description =>
        "Run a shell command in an editable project folder (build, tests, linters, scripts) and return its output. " +
        "In the code sandbox (a Linux container that only sees this folder, mounted at /project) when it's on; " +
        "otherwise on the user's computer. timeout_seconds up to 600 (default 180).";
    public JsonObject Parameters => Schema.Object(
        ("folder", "string", "The project folder (or a folder inside it) to run in", true),
        ("command", "string", "The command line, e.g. dotnet test or npm run build", true),
        ("timeout_seconds", "integer", "Up to 600", false));
    public bool RequiresApproval => true;
    public bool RequiresApprovalFor(ToolContext ctx) => !sandbox.Active(ctx.Settings);
    public string Area => "code";
    public string AreaTitle => "Code";
    public bool IsWrite => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var full = LinkedFolders.Normalize(args.Str("folder") ?? "");
        var folder = full.Length == 0 ? null : await folders.EditableFolderForAsync(full);
        if (folder is null || !Directory.Exists(full)) return ToolResult.Error("That isn't a folder inside an editable project folder.");
        var command = args.Str("command");
        if (string.IsNullOrWhiteSpace(command)) return ToolResult.Error("command is required");
        var secs = (int)Math.Clamp(args.Num("timeout_seconds") ?? 180, 5, MaxSeconds);

        return await ProjectCommand.RunAsync(ctx.Settings, sandbox, folder.Path, full, command, folder.SandboxImage, secs, ct);
    }
}

/// <summary>Runs a shell command for a project: in the code sandbox (only the project mounted) when it's on, otherwise
/// on this computer in <c>workDir</c>.</summary>
internal static class ProjectCommand
{
    public static async Task<ToolResult> RunAsync(AppSettings settings, CodeSandbox sandbox, string projectRoot, string workDir,
        string command, string? image, int secs, CancellationToken ct)
    {
        if (sandbox.Active(settings))
        {
            // The whole project is mounted (so builds see everything); start in the requested sub-folder.
            var sub = Path.GetRelativePath(projectRoot, workDir).Replace('\\', '/');
            var cd = sub == "." ? "" : $"cd '{sub.Replace("'", "'\\''")}' && ";
            try
            {
                var r = await sandbox.RunAsync(settings.Tools.Sandbox, LinkedFolders.Normalize(Path.Combine(Path.GetTempPath(), "hearth-sandbox-empty")),
                    cd + command, projectPath: projectRoot, image: image, timeout: TimeSpan.FromSeconds(secs), ct: ct);
                return SandboxOutput.ToResult(r, secs);
            }
            catch (InvalidOperationException ex) { return ToolResult.Error(ex.Message); }
        }

        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", command } }
            : new ProcessStartInfo("/bin/bash") { ArgumentList = { "-lc", command } };
        psi.WorkingDirectory = workDir;
        psi.RedirectStandardOutput = psi.RedirectStandardError = psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(secs));
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            return ToolResult.Error(ct.IsCancellationRequested ? "Cancelled." : $"Timed out after {secs} seconds.");
        }
        var res = SandboxOutput.ToResult(new SandboxResult(proc.ExitCode, await stdout, await stderr, false), secs);
        return res with { Text = res.Text.Replace(", in the sandbox)", ", on this computer)") };
    }
}
