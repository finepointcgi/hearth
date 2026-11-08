using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Hearth.Core.Services.Microsoft;

/// <summary>Reads the text of Word documents and writes simple ones from Markdown (headings, bullets, bold), with no extra packages.</summary>
public static partial class WordDocs
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>The text of a .docx, one paragraph per line ("- " for list items).</summary>
    public static string ReadText(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("This isn't a Word document.");
        using var s = entry.Open();
        var doc = XDocument.Load(s);
        var sb = new StringBuilder();
        foreach (var p in doc.Descendants(W + "p"))
        {
            var text = string.Concat(p.Descendants().Select(e =>
                e.Name == W + "t" ? e.Value : e.Name == W + "tab" ? "\t" : e.Name == W + "br" ? "\n" : ""));
            var style = p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
            var listed = p.Element(W + "pPr")?.Element(W + "numPr") is not null;
            if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && text.Length > 0) text = "## " + text;
            else if (listed && text.Length > 0) text = "- " + text;
            sb.AppendLine(text);
        }
        return MultiBlank().Replace(sb.ToString(), "\n\n").Trim();
    }

    /// <summary>A .docx from Markdown: # headings, - / * / 1. lists, **bold**, paragraphs.</summary>
    public static byte[] FromMarkdown(string markdown, string? title = null)
    {
        var body = new StringBuilder();
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var heading = Heading().Match(line);
            if (heading.Success)
            {
                var level = Math.Min(3, heading.Groups[1].Value.Length);
                body.Append(Paragraph(heading.Groups[2].Value, size: level switch { 1 => 32, 2 => 28, _ => 24 }, bold: true, spaceBefore: 240));
                continue;
            }
            var bullet = Bullet().Match(line);
            if (bullet.Success) { body.Append(Paragraph("• " + bullet.Groups[1].Value, indent: 360)); continue; }
            var numbered = Numbered().Match(line);
            if (numbered.Success) { body.Append(Paragraph(numbered.Groups[1].Value + ". " + numbered.Groups[2].Value, indent: 360)); continue; }
            body.Append(Paragraph(line));
        }
        var document = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}<w:sectPr><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="720" w:footer="720" w:gutter="0"/></w:sectPr></w:body></w:document>""";
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            Add("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/></Types>""");
            Add("_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>""");
            Add("docProps/core.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>{Esc(title ?? "")}</dc:title><dc:creator>Hearth</dc:creator></cp:coreProperties>""");
            Add("word/document.xml", document);
        }
        return ms.ToArray();
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    private static string Paragraph(string text, int size = 22, bool bold = false, int indent = 0, int spaceBefore = 0)
    {
        var sb = new StringBuilder("<w:p><w:pPr>");
        if (indent > 0) sb.Append($"<w:ind w:left=\"{indent}\"/>");
        sb.Append($"<w:spacing w:before=\"{spaceBefore}\" w:after=\"120\"/></w:pPr>");
        // **bold** spans become bold runs.
        var parts = BoldSpan().Split(text);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            var b = bold || i % 2 == 1;
            sb.Append("<w:r><w:rPr>").Append(b ? "<w:b/>" : "").Append($"<w:sz w:val=\"{size}\"/></w:rPr>")
              .Append($"<w:t xml:space=\"preserve\">{Esc(parts[i])}</w:t></w:r>");
        }
        return sb.Append("</w:p>").ToString();
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex Heading();
    [GeneratedRegex(@"^\s*[-*•]\s+(.*)$")] private static partial Regex Bullet();
    [GeneratedRegex(@"^\s*(\d+)[.)]\s+(.*)$")] private static partial Regex Numbered();
    [GeneratedRegex(@"\*\*(.+?)\*\*")] private static partial Regex BoldSpan();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex MultiBlank();
}
