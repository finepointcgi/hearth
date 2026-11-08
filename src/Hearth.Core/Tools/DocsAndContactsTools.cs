using Hearth.Core.Models;
using Hearth.Core.Services.Google;
using Hearth.Core.Services.Microsoft;

namespace Hearth.Core.Tools;

/// <summary>Google Docs and Drive (Connections → Google → Docs &amp; Drive).</summary>
public sealed class GoogleDocsToolProvider(GoogleAuth auth, DriveClient drive) : IToolProvider
{
    public async Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct)
    {
        if (!settings.Google.DocsEnabled || !await auth.HasScopeAsync(GoogleAuth.DriveReadScope)) return [];
        return
        [
            new GTool("drive_search", "Search Google Drive", "🔎",
                "Find files in the user's Google Drive by name or content (Docs, Sheets, Slides, PDFs, Word files). Returns name, date and id.",
                Schema.Object(("query", "string", "Words to search for", true)), false,
                a => drive.SearchAsync(a.Str("query") ?? "", default)),
            new GTool("drive_read", "Read Google Drive file", "📄",
                "Read a Drive file's text by id (from drive_search): Google Docs and Slides, Word, PDF, text. For Google Sheets use sheets_read.",
                Schema.Object(("id", "string", "File id", true)), false,
                a => drive.ReadAsync(a.Str("id") ?? "", default)),
            new GTool("docs_create", "Create Google Doc", "📝",
                "Write a new Google Doc in the user's Drive from Markdown (# headings, - bullets, 1. lists, **bold**, [links](url)). Returns its link.",
                Schema.Object(("title", "string", "Document title, e.g. Cover letter - Shield AI", true), ("content", "string", "The document in Markdown", true)),
                !settings.Google.AutoApproveEdits,
                a => drive.CreateDocAsync(a.Str("title") ?? "Untitled", a.Str("content") ?? "", default)),
        ];
    }
}

/// <summary>find_contact: the user's Google and Outlook contacts, whichever are connected.</summary>
public sealed class ContactsToolProvider(GoogleAuth google, DriveClient drive, MicrosoftAuth microsoft, GraphClient graph) : IToolProvider
{
    public async Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct)
    {
        var useGoogle = settings.Google.ContactsEnabled && await google.HasScopeAsync(GoogleAuth.ContactsScope);
        var useMicrosoft = await microsoft.IsConnectedAsync();
        if (!useGoogle && !useMicrosoft) return [];
        return
        [
            new GTool("find_contact", "Find contact", "👥",
                "Look up someone in the user's contacts (" + string.Join(" and ", new[] { useGoogle ? "Google" : null, useMicrosoft ? "Outlook" : null }.OfType<string>()) +
                ") by name, company or email: returns their emails and phone numbers. Use it before emailing or calling someone by name.",
                Schema.Object(("name", "string", "Name, company or email to look for", true)), false,
                async a =>
                {
                    var q = a.Str("name")?.Trim() ?? "";
                    if (q.Length < 2) return "Give a name to look for.";
                    var found = new List<Contact>();
                    var problems = new List<string>();
                    if (useGoogle)
                        try { found.AddRange(await drive.FindContactsAsync(q, default)); }
                        catch (InvalidOperationException ex) { problems.Add(ex.Message); }
                    if (useMicrosoft)
                        try { found.AddRange(await graph.FindContactsAsync(q, default)); }
                        catch (InvalidOperationException ex) { problems.Add(ex.Message); }
                    var lines = found.Where(c => c.Emails.Count + c.Phones.Count > 0).Select(c => c.ToString()).Distinct().ToList();
                    return (lines.Count == 0 ? $"No contact matching \"{q}\"." : string.Join("\n", lines)) +
                           (problems.Count > 0 ? "\n(" + string.Join("; ", problems) + ")" : "");
                }),
        ];
    }
}
