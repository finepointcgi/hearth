using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Locus.Core.Models;
using Locus.Core.Services;

namespace Locus.Core.Tools;

/// <summary>
/// Tools for code sessions (a chat bound to a folder, like Claude Code): find files, search, read with line numbers,
/// make exact edits, run shell commands and keep a task list. Every path stays inside the session's folder. File
/// edits ask first unless the session accepts edits; commands ask unless the code sandbox is on; plan mode only reads.
/// </summary>
public sealed class CodeTools(ProjectHistory history, CodeSandbox sandbox, PlatformInfo platform)
{
    /// <summary>Folders that are never listed or searched (dependencies, build output, VCS data).</summary>
    public static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".vs", ".idea", ".venv", "venv", "__pycache__",
        "dist", "build", ".next", ".nuxt", "target", ".gradle", ".dart_tool", "Pods", "DerivedData",
    };

    /// <summary>Code sessions need a local file system (desktop, or the web host with local access).</summary>
    public bool Available => platform.CanRunProcesses;

    /// <summary>The folder a run codes in: the chat's code session, else the bot's own project folder (Code ability).</summary>
    public static CodeSession? SessionFor(Conversation conv, Bot? bot) =>
        conv.Code ?? (bot is { Abilities: { Code: true, ProjectFolder: { Length: > 0 } folder } }
            ? new CodeSession { Directory = folder, Mode = bot.Abilities.CodeMode } : null);

    public IReadOnlyList<ITool> For(CodeSession? code)
    {
        if (code is null || !Available || !Directory.Exists(code.Directory)) return [];
        List<ITool> tools = [new CodeGlobTool(), new CodeGrepTool(), new CodeReadTool(), new TodoWriteTool()];
        if (code.Mode == CodePermissionMode.Plan) return tools;
        tools.Add(new CodeEditTool(history));
        tools.Add(new CodeWriteTool(history));
        tools.Add(new CodeBashTool(sandbox));
        tools.Add(new CodeUndoTool(history));
        return tools;
    }

    /// <summary>Resolves a path the model gave (relative to the session folder, or absolute inside it).
    /// Returns an error for anything outside the folder, including through symbolic links.</summary>
    public static (string full, string? error) Resolve(CodeSession? code, string? path)
    {
        var root = Root(code);
        if (root is null) return ("", "There's no project folder for this chat.");
        var p = (path ?? "").Trim();
        var full = Path.GetFullPath(p.Length == 0 ? root : Path.IsPathRooted(p) ? p : Path.Combine(root, p));
        if (!Inside(root, full)) return (full, $"{p} is outside the session folder ({root}).");
        try
        {
            // A link inside the folder must not lead outside it.
            for (var dir = full; dir.Length > root.Length; dir = Path.GetDirectoryName(dir)!)
            {
                FileSystemInfo info = Directory.Exists(dir) ? new DirectoryInfo(dir) : new FileInfo(dir);
                if (info.Exists && info.LinkTarget is not null
                    && info.ResolveLinkTarget(returnFinalTarget: true) is { } target && !Inside(root, target.FullName))
                    return (full, $"{p} links outside the session folder.");
            }
        }
        catch (IOException) { }
        return (full, null);
    }

    public static string? Root(CodeSession? code) =>
        code?.Directory is { Length: > 0 } d ? Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : null;

    public static string Relative(CodeSession? code, string full) => Path.GetRelativePath(Root(code)!, full).Replace('\\', '/');

    private static bool Inside(string root, string full)
    {
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(root, cmp) || full.StartsWith(root + Path.DirectorySeparatorChar, cmp);
    }

    /// <summary>Files under a folder, skipping dependency/build folders and hidden folders.</summary>
    public static IEnumerable<string> Files(string dir)
    {
        var stack = new Stack<string>([dir]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            IEnumerable<string> files, dirs;
            try { files = Directory.EnumerateFiles(d); dirs = Directory.EnumerateDirectories(d); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files) yield return f;
            foreach (var sub in dirs)
            {
                var name = Path.GetFileName(sub);
                if (SkippedDirs.Contains(name) || (name.StartsWith('.') && name is not (".github" or ".claude"))) continue;
                if (new DirectoryInfo(sub).LinkTarget is not null) continue;
                stack.Push(sub);
            }
        }
    }

    /// <summary>A glob ("**/*.cs", "src/*.{ts,tsx}") as a regex over '/'-separated relative paths.</summary>
    public static Regex Glob(string pattern)
    {
        var sb = new StringBuilder("^");
        var p = pattern.Replace('\\', '/').TrimStart('/');
        if (!p.Contains('/')) sb.Append("(?:.*/)?"); // "*.cs" matches at any depth, like most tools
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                var slash = i + 2 < p.Length && p[i + 2] == '/';
                sb.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else if (c == '{') sb.Append("(?:");
            else if (c == '}') sb.Append(')');
            else if (c == ',' && p.LastIndexOf('{', i) > p.LastIndexOf('}', i)) sb.Append('|');
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    public static bool LooksBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> buf = stackalloc byte[1024];
            var n = fs.Read(buf);
            return buf[..n].Contains((byte)0);
        }
        catch { return true; }
    }

    internal static async Task WriteAtomicAsync(string path, string text, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".locus-tmp";
        await File.WriteAllTextAsync(tmp, text, new UTF8Encoding(false), ct);
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class CodeGlobTool : ITool
{
    public const int Max = 200;
    public string Name => "glob";
    public string DisplayName => "Find files";
    public string Icon => "🗂️";
    public string Description =>
        "Find files in the session folder by name pattern, e.g. \"**/*.cs\", \"src/**/*.{ts,tsx}\", \"*Test*\". " +
        "Newest first. Dependency and build folders are skipped.";
    public JsonObject Parameters => Schema.Object(
        ("pattern", "string", "Glob pattern", true),
        ("path", "string", "Sub-folder to search in (default: the session folder)", false));
    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var (dir, error) = CodeTools.Resolve(ctx.Code, args.Str("path"));
        if (error is not null) return Task.FromResult(ToolResult.Error(error));
        if (!Directory.Exists(dir)) return Task.FromResult(ToolResult.Error($"No folder at {dir}."));
        var regex = CodeTools.Glob(args.Str("pattern") is { Length: > 0 } p ? p : "**/*");
        var hits = CodeTools.Files(dir)
            .Where(f => regex.IsMatch(Path.GetRelativePath(dir, f).Replace('\\', '/')))
            .Select(f => (f, t: File.GetLastWriteTimeUtc(f)))
            .OrderByDescending(x => x.t).Select(x => CodeTools.Relative(ctx.Code, x.f)).ToList();
        if (hits.Count == 0) return Task.FromResult(new ToolResult("No files match."));
        var text = string.Join("\n", hits.Take(Max));
        if (hits.Count > Max) text += $"\n… {hits.Count - Max} more (narrow the pattern)";
        return Task.FromResult(new ToolResult(text));
    }
}

public sealed class CodeGrepTool : ITool
{
    public const int MaxMatches = 200;
    public string Name => "grep";
    public string DisplayName => "Search code";
    public string Icon => "🔎";
    public string Description =>
        "Search file contents in the session folder with a regular expression (.NET syntax). Returns path:line: text. " +
        "Use include to limit files (e.g. \"*.cs\"); files_only to list just the matching files.";
    public JsonObject Parameters => Schema.Object(
        ("pattern", "string", "Regular expression", true),
        ("path", "string", "File or sub-folder to search (default: the session folder)", false),
        ("include", "string", "Glob of files to search, e.g. \"*.{ts,tsx}\"", false),
        ("ignore_case", "boolean", "Case-insensitive (default false)", false),
        ("files_only", "boolean", "Only list matching files (default false)", false));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var (target, error) = CodeTools.Resolve(ctx.Code, args.Str("path"));
        if (error is not null) return ToolResult.Error(error);
        Regex regex;
        try
        {
            regex = new Regex(args.Str("pattern") ?? "", (args.Bool("ignore_case") ? RegexOptions.IgnoreCase : 0) | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException ex) { return ToolResult.Error("Invalid regex: " + ex.Message); }
        var include = args.Str("include") is { Length: > 0 } inc ? CodeTools.Glob(inc) : null;
        var files = File.Exists(target) ? [target] : Directory.Exists(target) ? CodeTools.Files(target) : null;
        if (files is null) return ToolResult.Error($"Nothing at {target}.");
        var filesOnly = args.Bool("files_only");

        var sb = new StringBuilder();
        var matches = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (include is not null && !include.IsMatch(Path.GetFileName(f)) && !include.IsMatch(CodeTools.Relative(ctx.Code, f))) continue;
            var info = new FileInfo(f);
            if (info.Length > 1_000_000 || CodeTools.LooksBinary(f)) continue;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(f, ct); } catch (IOException) { continue; }
            var rel = CodeTools.Relative(ctx.Code, f);
            for (var i = 0; i < lines.Length; i++)
            {
                bool hit;
                try { hit = regex.IsMatch(lines[i]); } catch (RegexMatchTimeoutException) { hit = false; }
                if (!hit) continue;
                matches++;
                if (filesOnly) { sb.AppendLine(rel); break; }
                var line = lines[i].Trim();
                sb.Append(rel).Append(':').Append(i + 1).Append(": ").AppendLine(line.Length > 300 ? line[..300] + "…" : line);
                if (matches >= MaxMatches) break;
            }
            if (matches >= MaxMatches)
            {
                sb.AppendLine($"… stopped at {MaxMatches} matches (narrow the search)");
                break;
            }
        }
        return new ToolResult(matches == 0 ? "No matches." : sb.ToString().TrimEnd());
    }
}

public sealed class CodeReadTool : ITool
{
    public const int DefaultLines = 2000;
    public string Name => "read";
    public string DisplayName => "Read file";
    public string Icon => "📄";
    public string Description =>
        "Read a file in the session folder, with line numbers (\"  12\\tcode\"). For long files pass offset (first line, " +
        "1-based) and limit. Always read a file before editing it. A folder path lists its entries.";
    public JsonObject Parameters => Schema.Object(
        ("path", "string", "File path (relative to the session folder, or absolute inside it)", true),
        ("offset", "integer", "First line to read (default 1)", false),
        ("limit", "integer", $"How many lines (default {DefaultLines})", false));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var (full, error) = CodeTools.Resolve(ctx.Code, args.Str("path"));
        if (error is not null) return ToolResult.Error(error);
        if (Directory.Exists(full))
        {
            var entries = Directory.EnumerateFileSystemEntries(full).OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .Select(e => Path.GetFileName(e) + (Directory.Exists(e) ? "/" : "")).Take(500);
            return new ToolResult(string.Join("\n", entries) is { Length: > 0 } list ? list : "(empty folder)");
        }
        if (!File.Exists(full)) return ToolResult.Error($"No file at {CodeTools.Relative(ctx.Code, full)}.");
        if (new FileInfo(full).Length > 5_000_000) return ToolResult.Error("That file is too large (over 5 MB).");
        if (CodeTools.LooksBinary(full)) return ToolResult.Error("That looks like a binary file.");
        var lines = await File.ReadAllLinesAsync(full, ct);
        var offset = Math.Max(1, (int)(args.Num("offset") ?? 1));
        var limit = Math.Clamp((int)(args.Num("limit") ?? DefaultLines), 1, DefaultLines);
        if (lines.Length == 0) return new ToolResult("(empty file)");
        if (offset > lines.Length) return ToolResult.Error($"The file has only {lines.Length} lines.");
        var sb = new StringBuilder();
        var end = Math.Min(lines.Length, offset - 1 + limit);
        for (var i = offset - 1; i < end; i++)
        {
            var l = lines[i];
            sb.Append((i + 1).ToString().PadLeft(6)).Append('\t').AppendLine(l.Length > 2000 ? l[..2000] + "…" : l);
        }
        if (end < lines.Length) sb.AppendLine($"… {lines.Length - end} more lines (read on with offset={end + 1})");
        return new ToolResult(sb.ToString().TrimEnd('\n', '\r'));
    }
}

/// <summary>File edits: ask in "Ask" mode, run straight away when the session accepts edits.</summary>
public abstract class CodeWriteBase(ProjectHistory history) : IPermissionInfo, IContextualApproval
{
    public bool RequiresApproval => true;
    public bool RequiresApprovalFor(ToolContext ctx) => ctx.Code?.Mode != CodePermissionMode.AcceptEdits;
    public string Area => "projects";
    public string AreaTitle => "Project folders";
    public bool IsWrite => true;
    protected ProjectHistory History => history;

    protected Task<ProjectChange> RecordAsync(ToolContext ctx, string full, string summary) =>
        history.RecordAsync("code", full, summary, ctx.Conversation.Id, ctx.Bot?.Id);
}

public sealed class CodeEditTool(ProjectHistory history) : CodeWriteBase(history), ITool
{
    public string Name => "edit";
    public string DisplayName => "Edit file";
    public string Icon => "✏️";
    public string Description =>
        "Replace exact text in a file in the session folder. old_text must match the file exactly (copy it from read " +
        "without the line-number prefix) and be unique — include more surrounding lines if needed — or set replace_all.";
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "File path" },
            ["old_text"] = new JsonObject { ["type"] = "string", ["description"] = "Exact text to replace" },
            ["new_text"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement text" },
            ["replace_all"] = new JsonObject { ["type"] = "boolean", ["description"] = "Replace every occurrence (default: must be unique)" },
        },
        ["required"] = new JsonArray("path", "old_text", "new_text"),
    };

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var (full, error) = CodeTools.Resolve(ctx.Code, args.Str("path"));
        if (error is not null) return ToolResult.Error(error);
        var rel = CodeTools.Relative(ctx.Code, full);
        if (!File.Exists(full)) return ToolResult.Error($"No file at {rel}. To create one, use write.");
        if (new FileInfo(full).Length > ProjectFiles.MaxBytes) return ToolResult.Error("That file is too large to edit here.");
        var oldText = args.Str("old_text") ?? "";
        var newText = args.Str("new_text") ?? "";
        if (oldText.Length == 0) return ToolResult.Error("old_text can't be empty.");
        if (oldText == newText) return ToolResult.Error("old_text and new_text are the same.");
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
        var change = await RecordAsync(ctx, full, $"Edited {rel}");
        await CodeTools.WriteAtomicAsync(full, text.Replace(oldText, newText, StringComparison.Ordinal), ct);
        return new ToolResult($"Edited {rel} ({count} place{(count == 1 ? "" : "s")}). Change {change.Id}.");
    }

    internal static int Count(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}

public sealed class CodeWriteTool(ProjectHistory history) : CodeWriteBase(history), ITool
{
    public string Name => "write";
    public string DisplayName => "Write file";
    public string Icon => "💾";
    public string Description =>
        "Create a file (or replace a whole file) in the session folder. To change part of an existing file use edit; " +
        "read an existing file before replacing it.";
    public JsonObject Parameters => Schema.Object(("path", "string", "File path", true), ("content", "string", "The whole file", true));

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var (full, error) = CodeTools.Resolve(ctx.Code, args.Str("path"));
        if (error is not null) return ToolResult.Error(error);
        var content = args.Str("content") ?? "";
        if (Encoding.UTF8.GetByteCount(content) > ProjectFiles.MaxBytes) return ToolResult.Error("That's too large to write here.");
        if (Directory.Exists(full)) return ToolResult.Error("That's a folder.");
        var rel = CodeTools.Relative(ctx.Code, full);
        var existed = File.Exists(full);
        var change = await RecordAsync(ctx, full, $"{(existed ? "Rewrote" : "Created")} {rel}");
        await CodeTools.WriteAtomicAsync(full, content, ct);
        return new ToolResult($"{(existed ? "Replaced" : "Created")} {rel}. Change {change.Id}.");
    }
}

public sealed class CodeUndoTool(ProjectHistory history) : CodeWriteBase(history), ITool
{
    public string Name => "undo_edit";
    public string DisplayName => "Undo change";
    public string Icon => "↩️";
    public string Description => "Put a file back the way it was before one of your edits in this session (the change id from edit/write). Newest change to a file first.";
    public JsonObject Parameters => Schema.Object(("id", "string", "Change id", true));

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var change = (await History.ListAsync()).FirstOrDefault(c => c.Id == args.Str("id"));
        if (change is null || change.ConversationId != ctx.Conversation.Id) return ToolResult.Error("No change with that id in this session.");
        if (CodeTools.Resolve(ctx.Code, change.Path).error is { } error) return ToolResult.Error(error);
        return await History.UndoAsync(change.Id) is { } err ? ToolResult.Error(err) : new ToolResult($"Undid: {change.Summary}.");
    }
}

public sealed class CodeBashTool(CodeSandbox sandbox) : ITool, IPermissionInfo, IContextualApproval
{
    public const int MaxSeconds = 600;
    public string Name => "bash";
    public string DisplayName => "Run command";
    public string Icon => "⌨️";
    public string Description =>
        "Run a shell command in the session folder (build, tests, git, linters, scripts) and get its output. Runs in " +
        "the code sandbox (Linux container, folder mounted at /project) when it's on; otherwise on the user's computer " +
        "(bash, or cmd on Windows). Don't use it to read or search files — use read, grep and glob. timeout_seconds up to 600.";
    public JsonObject Parameters => Schema.Object(
        ("command", "string", "The command line", true),
        ("cwd", "string", "Sub-folder to run in (default: the session folder)", false),
        ("timeout_seconds", "integer", "Up to 600 (default 120)", false));
    public bool RequiresApproval => true;
    public bool RequiresApprovalFor(ToolContext ctx) => !sandbox.Active(ctx.Settings);
    public string Area => "code";
    public string AreaTitle => "Code";
    public bool IsWrite => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var command = args.Str("command");
        if (string.IsNullOrWhiteSpace(command)) return ToolResult.Error("command is required");
        var (dir, error) = CodeTools.Resolve(ctx.Code, args.Str("cwd"));
        if (error is not null) return ToolResult.Error(error);
        if (!Directory.Exists(dir)) return ToolResult.Error($"No folder at {dir}.");
        var secs = (int)Math.Clamp(args.Num("timeout_seconds") ?? 120, 5, MaxSeconds);
        return await ProjectCommand.RunAsync(ctx.Settings, sandbox, CodeTools.Root(ctx.Code)!, dir, command, null, secs, ct);
    }
}

public sealed class TodoWriteTool : ITool
{
    public string Name => "todo_write";
    public string DisplayName => "Task list";
    public string Icon => "📋";
    public string Description =>
        "Keep your task list for this job (the user sees it). Use it for work with 3+ steps: write the whole list, " +
        "mark one item in_progress while you work on it and completed as soon as it's done. Each call replaces the list.";
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["todos"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "The whole list, in order",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["content"] = new JsonObject { ["type"] = "string", ["description"] = "Short imperative step" },
                        ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("pending", "in_progress", "completed") },
                    },
                    ["required"] = new JsonArray("content", "status"),
                },
            },
        },
        ["required"] = new JsonArray("todos"),
    };
    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Code is not { } code) return Task.FromResult(ToolResult.Error("There's no project folder for this chat."));
        var items = args["todos"] switch
        {
            JsonArray a => a,
            JsonValue v when v.TryGetValue<string>(out var s) => JsonNode.Parse(s) as JsonArray, // some models send it as a string
            _ => null,
        };
        if (items is null) return Task.FromResult(ToolResult.Error("todos must be an array of {content, status}."));
        var list = new List<CodeTodo>();
        foreach (var item in items.OfType<JsonObject>())
        {
            var content = item.Str("content")?.Trim();
            if (string.IsNullOrEmpty(content)) continue;
            var status = item.Str("status") switch
            {
                "in_progress" => CodeTodoStatus.InProgress,
                "completed" or "done" => CodeTodoStatus.Completed,
                _ => CodeTodoStatus.Pending,
            };
            list.Add(new CodeTodo(content, status));
        }
        code.Todos = list.Take(50).ToList();
        var done = list.Count(t => t.Status == CodeTodoStatus.Completed);
        return Task.FromResult(new ToolResult($"Task list updated ({done}/{list.Count} done)."));
    }
}
