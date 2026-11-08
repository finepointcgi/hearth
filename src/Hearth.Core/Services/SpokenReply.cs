using System.Diagnostics;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>
/// A reply read aloud for chat apps: the text-to-speech server when one is set (Settings → Voice), otherwise the
/// Mac's own voices (say). With ffmpeg it becomes an OGG/Opus voice note, which Telegram shows as a voice message.
/// </summary>
public sealed class SpokenReply(SpeechService speech, SettingsStore settingsStore, PlatformInfo platform)
{
    /// <summary>Longer replies are cut (with a note) so a voice note stays a reasonable length.</summary>
    public const int MaxChars = 1500;

    private static string? Find(string exe) =>
        new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" }.Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);

    public async Task<bool> AvailableAsync() =>
        !string.IsNullOrWhiteSpace((await settingsStore.GetAsync()).Voice.TtsUrl) || platform.CanRunProcesses && Find("say") is not null;

    /// <summary>The reply as audio (bytes, mime type), or null when there's no way to speak here.</summary>
    public async Task<(byte[] audio, string mime)?> SpeakAsync(string markdown, CancellationToken ct = default)
    {
        var text = SpeechService.ForSpeech(markdown).Trim();
        if (text.Length == 0) return null;
        if (text.Length > MaxChars) text = text[..text.LastIndexOf(' ', MaxChars)] + ". The rest is in the written reply.";
        var s = await settingsStore.GetAsync();
        var ffmpeg = platform.CanRunProcesses ? Find("ffmpeg") : null;
        var dir = Directory.CreateTempSubdirectory("hearth-voice-").FullName;
        try
        {
            string input;
            if (!string.IsNullOrWhiteSpace(s.Voice.TtsUrl))
            {
                var (audio, mime) = await speech.SynthesizeAsync(s.Voice, text, ct);
                if (ffmpeg is null) return (audio, mime);
                input = Path.Combine(dir, "in.mp3");
                await File.WriteAllBytesAsync(input, audio, ct);
            }
            else if (platform.CanRunProcesses && Find("say") is { } say)
            {
                input = Path.Combine(dir, "in.aiff");
                await RunAsync(say, ["-o", input, text], ct);
                if (ffmpeg is null) return (await File.ReadAllBytesAsync(input, ct), "audio/aiff");
            }
            else return null;
            var ogg = Path.Combine(dir, "reply.ogg");
            await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", input, "-c:a", "libopus", "-b:a", "32k", "-ac", "1", ogg], ct);
            return (await File.ReadAllBytesAsync(ogg, ct), "audio/ogg");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static async Task RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {Path.GetFileName(exe)}.");
        var stderr = p.StandardError.ReadToEndAsync(ct);
        _ = p.StandardOutput.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed: {(await stderr).Trim().Split('\n').LastOrDefault()}");
    }
}
