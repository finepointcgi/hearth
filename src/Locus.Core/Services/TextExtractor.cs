using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using UglyToad.PdfPig;

namespace Locus.Core.Services;

/// <summary>Turns PDFs, HTML and text-like files into plain text for the model.</summary>
public static partial class TextExtractor
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".log",
        ".cs", ".razor", ".cshtml", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".kt", ".swift", ".go", ".rs",
        ".c", ".h", ".cpp", ".hpp", ".rb", ".php", ".sh", ".ps1", ".sql", ".css", ".scss", ".lua", ".gd", ".r", ".tex",
    };

    public static bool IsSupported(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || ext is ".html" or ".htm" || TextExtensions.Contains(ext);
    }

    public static string Extract(string fileName, byte[] data)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => FromPdf(data),
            ".html" or ".htm" => FromHtml(Encoding.UTF8.GetString(data)).text,
            _ => Encoding.UTF8.GetString(data),
        };
    }

    public static string FromPdf(byte[] data)
    {
        using var doc = PdfDocument.Open(data);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            sb.AppendLine($"[page {page.Number}]");
            sb.AppendLine(page.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static (string title, string text) FromHtml(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        foreach (var el in doc.QuerySelectorAll("script,style,noscript,svg,nav,footer,header,aside,form,iframe,template,button").ToList())
            el.Remove();
        var root = (IElement?)doc.QuerySelector("article") ?? doc.QuerySelector("main") ?? doc.Body ?? doc.DocumentElement;
        var sb = new StringBuilder();
        Walk(root, sb);
        var text = MultiNewline().Replace(SpaceRun().Replace(sb.ToString(), " "), "\n\n").Trim();
        return (doc.Title?.Trim() ?? "", text);
    }

    private static readonly HashSet<string> Blocks =
    [
        "P", "DIV", "LI", "H1", "H2", "H3", "H4", "H5", "H6", "TR", "BR", "SECTION", "ARTICLE", "PRE", "BLOCKQUOTE",
        "UL", "OL", "TABLE", "DT", "DD", "FIGCAPTION",
    ];

    private static void Walk(INode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText t) { sb.Append(t.Data); continue; }
            if (child is not IElement e) continue;
            var block = Blocks.Contains(e.TagName);
            if (block) sb.Append('\n');
            if (e.TagName is "H1" or "H2" or "H3") sb.Append("## ");
            if (e.TagName == "LI") sb.Append("- ");
            if (e.TagName is "TD" or "TH") sb.Append(" | ");
            Walk(e, sb);
            if (block) sb.Append('\n');
        }
    }

    [GeneratedRegex(@"[ \t ]+")] private static partial Regex SpaceRun();
    [GeneratedRegex(@"\s*\n\s*\n\s*")] private static partial Regex MultiNewline();
}
