using System.Diagnostics;
using Locus.Core.Models;

namespace Locus.Core.Services;

/// <summary>
/// Text from screenshots, photos of documents and scanned PDFs, read on this computer: Apple's Vision text recognition
/// on a Mac (a small helper compiled on first use), or tesseract when installed. Nothing leaves the machine.
/// </summary>
public sealed class Ocr(IAppPaths paths, PlatformInfo platform)
{
    private readonly SemaphoreSlim _build = new(1, 1);
    private string HelperPath => Path.Combine(paths.DataDirectory, "tools", "locus-ocr");

    /// <summary>Swift helper: prints the text of an image, or of each page of a PDF ("--- page N ---" between pages).</summary>
    private const string Source = """
        import Foundation
        import Vision
        import PDFKit
        import AppKit

        func recognize(_ image: CGImage) -> String {
            let request = VNRecognizeTextRequest()
            request.recognitionLevel = .accurate
            request.usesLanguageCorrection = true
            try? VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
            return (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }.joined(separator: "\n")
        }

        let path = CommandLine.arguments[1]
        let url = URL(fileURLWithPath: path)
        if path.lowercased().hasSuffix(".pdf"), let pdf = PDFDocument(url: url) {
            for i in 0..<min(pdf.pageCount, 50) {
                guard let page = pdf.page(at: i) else { continue }
                let bounds = page.bounds(for: .mediaBox)
                let scale: CGFloat = 2
                let size = NSSize(width: bounds.width * scale, height: bounds.height * scale)
                let image = page.thumbnail(of: size, for: .mediaBox)
                if let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) {
                    print("--- page \(i + 1) ---")
                    print(recognize(cg))
                }
            }
        } else if let image = NSImage(contentsOf: url), let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) {
            print(recognize(cg))
        } else {
            FileHandle.standardError.write("can't open \(path)\n".data(using: .utf8)!)
            exit(1)
        }
        """;

    private static string? Find(string exe) =>
        new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" }.Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);

    public bool Available => platform.CanRunProcesses && (OperatingSystem.IsMacOS() && (File.Exists(HelperPath) || Find("swiftc") is not null) || Find("tesseract") is not null);

    /// <summary>The text in an image or a scanned PDF (empty when there isn't any). Throws when OCR isn't available.</summary>
    public async Task<string> ReadAsync(byte[] data, string fileName, CancellationToken ct = default)
    {
        if (!Available) throw new InvalidOperationException("Reading text from images isn't available here.");
        var dir = Directory.CreateTempSubdirectory("locus-ocr-").FullName;
        try
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            var file = Path.Combine(dir, "in" + (ext.Length > 1 ? ext : ".png"));
            await File.WriteAllBytesAsync(file, data, ct);
            if (OperatingSystem.IsMacOS() && await HelperAsync(ct) is { } helper) return (await RunAsync(helper, [file], ct)).Trim();
            if (ext == ".pdf") throw new InvalidOperationException("Reading scanned PDFs needs a Mac (or a PDF with a text layer).");
            return (await RunAsync(Find("tesseract")!, [file, "stdout"], ct)).Trim();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>Text for a PDF whose text layer is (nearly) empty: a scan. Null when the PDF already has text.</summary>
    public async Task<string?> ScannedPdfTextAsync(byte[] pdf, string extractedText, CancellationToken ct = default)
    {
        var pages = Math.Max(1, extractedText.Split("[page ").Length - 1);
        var real = extractedText.Replace("[page", "").Count(char.IsLetter);
        if (real > 40 * pages || !Available) return null;
        return await ReadAsync(pdf, "scan.pdf", ct);
    }

    private async Task<string?> HelperAsync(CancellationToken ct)
    {
        if (File.Exists(HelperPath)) return HelperPath;
        var swiftc = Find("swiftc");
        if (swiftc is null) return null;
        await _build.WaitAsync(ct);
        try
        {
            if (File.Exists(HelperPath)) return HelperPath;
            Directory.CreateDirectory(Path.GetDirectoryName(HelperPath)!);
            var src = Path.Combine(Path.GetDirectoryName(HelperPath)!, "locus-ocr.swift");
            await File.WriteAllTextAsync(src, Source, ct);
            await RunAsync(swiftc, ["-O", "-o", HelperPath, src], ct);
            return HelperPath;
        }
        finally { _build.Release(); }
    }

    private static async Task<string> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {Path.GetFileName(exe)}.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed: {(await stderr).Trim().Split('\n').LastOrDefault()}");
        return await stdout;
    }
}
