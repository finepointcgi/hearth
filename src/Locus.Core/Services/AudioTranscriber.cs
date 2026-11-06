using System.Diagnostics;
using Locus.Core.Models;

namespace Locus.Core.Services;

/// <summary>
/// Voice memos and recordings to text: a speech-to-text server when one is set (Settings → Voice), otherwise
/// whisper.cpp on this computer (private, offline) when it's installed with a model.
/// </summary>
public sealed class AudioTranscriber(SpeechService speech, SettingsStore settingsStore, IAppPaths paths, PlatformInfo platform)
{
    private static readonly string[] AudioExtensions = [".mp3", ".m4a", ".wav", ".ogg", ".oga", ".opus", ".webm", ".flac", ".aac", ".amr", ".caf"];

    public static bool IsAudio(string fileName, string? mime = null) =>
        mime?.StartsWith("audio/") == true || AudioExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>Where downloaded whisper.cpp models live.</summary>
    public string ModelFolder => Path.Combine(paths.DataDirectory, "models", "whisper");

    /// <summary>The recommended local model: English, small and fast (about 148 MB).</summary>
    public const string DefaultModel = "ggml-base.en.bin";
    public const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + DefaultModel;

    private static string? Find(string exe) =>
        new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            .Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);

    private string? LocalModel() => Directory.Exists(ModelFolder) ? Directory.EnumerateFiles(ModelFolder, "ggml-*.bin").OrderBy(f => f).FirstOrDefault() : null;

    /// <summary>How audio would be transcribed here, or why it can't be.</summary>
    public async Task<(bool ready, string how)> StatusAsync()
    {
        var s = await settingsStore.GetAsync();
        if (!string.IsNullOrWhiteSpace(s.Voice.SttUrl)) return (true, $"speech-to-text server {s.Voice.SttUrl}");
        if (!platform.CanRunProcesses) return (false, "set a speech-to-text server in Settings → Voice");
        var cli = Find("whisper-cli");
        var model = LocalModel();
        if (cli is not null && model is not null && Find("ffmpeg") is not null) return (true, $"whisper.cpp on this computer ({Path.GetFileName(model)})");
        return (false, cli is null ? "install whisper.cpp (Settings → Voice → Set up local transcription)" : "download a whisper model (Settings → Voice)");
    }

    public async Task<string> TranscribeAsync(byte[] audio, string fileName, string mime, CancellationToken ct)
    {
        var s = await settingsStore.GetAsync();
        if (!string.IsNullOrWhiteSpace(s.Voice.SttUrl)) return await speech.TranscribeAsync(s.Voice, audio, mime, ct);
        var (ready, how) = await StatusAsync();
        if (!ready) throw new InvalidOperationException($"I can't transcribe audio yet: {how}.");

        var dir = Directory.CreateTempSubdirectory("locus-audio-").FullName;
        try
        {
            var input = Path.Combine(dir, "in" + (Path.GetExtension(fileName) is { Length: > 1 } e ? e : ".audio"));
            var wav = Path.Combine(dir, "in.wav");
            await File.WriteAllBytesAsync(input, audio, ct);
            // whisper.cpp wants 16 kHz mono WAV.
            await RunAsync(Find("ffmpeg")!, ["-hide_banner", "-loglevel", "error", "-y", "-i", input, "-ar", "16000", "-ac", "1", wav], ct);
            var lang = s.Voice.Language.Split('-')[0];
            var model = LocalModel()!;
            var text = await RunAsync(Find("whisper-cli")!,
                ["-m", model, "-f", wav, "-nt", "-np", "-l", Path.GetFileName(model).Contains(".en.") ? "en" : lang.Length == 2 ? lang : "auto"], ct);
            return string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static async Task<string> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {Path.GetFileName(exe)}.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed: {(await stderr).Trim().Split('\n').LastOrDefault()}");
        return await stdout;
    }

    /// <summary>
    /// Sets up local transcription: installs whisper.cpp with Homebrew (if missing) and downloads the default model.
    /// Run only when the user asks (Settings → Voice).
    /// </summary>
    public async Task<string> SetUpLocalAsync(IHttpClientFactory http, IProgress<string> progress, CancellationToken ct)
    {
        if (!platform.CanRunProcesses) throw new InvalidOperationException("This Locus can't install programs; set a speech-to-text server instead.");
        if (Find("ffmpeg") is null) throw new InvalidOperationException("ffmpeg is needed: install it with Homebrew (brew install ffmpeg).");
        if (Find("whisper-cli") is null)
        {
            var brew = Find("brew") ?? throw new InvalidOperationException("Install Homebrew first (brew.sh), or install whisper.cpp yourself.");
            progress.Report("Installing whisper.cpp with Homebrew…");
            await RunAsync(brew, ["install", "whisper-cpp"], ct);
        }
        if (LocalModel() is null)
        {
            progress.Report("Downloading the speech model (about 148 MB)…");
            Directory.CreateDirectory(ModelFolder);
            var target = Path.Combine(ModelFolder, DefaultModel);
            using var res = await http.CreateClient(Llm.LlmClientFactory.HttpClientName).GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            await using (var file = File.Create(target + ".part"))
                await (await res.Content.ReadAsStreamAsync(ct)).CopyToAsync(file, ct);
            File.Move(target + ".part", target, overwrite: true);
        }
        return (await StatusAsync()).how;
    }
}
