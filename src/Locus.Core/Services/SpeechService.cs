using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Locus.Core.Llm;
using Locus.Core.Models;

namespace Locus.Core.Services;

/// <summary>Optional server-side speech via OpenAI-compatible /v1/audio endpoints (Whisper, Kokoro…).</summary>
public sealed partial class SpeechService(IHttpClientFactory http)
{
    private HttpClient Client => http.CreateClient(LlmClientFactory.HttpClientName);

    private static string V1(string url)
    {
        var b = url.TrimEnd('/');
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b : b + "/v1";
    }

    public async Task<string> TranscribeAsync(VoiceSettings s, byte[] audio, string mimeType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.SttUrl)) throw new InvalidOperationException("No speech-to-text server configured.");
        var ext = mimeType.Contains("mp4") || mimeType.Contains("m4a") ? "m4a" : mimeType.Contains("ogg") ? "ogg" : mimeType.Contains("wav") ? "wav" : "webm";
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(mimeType.Split(';')[0]);
        form.Add(file, "file", $"speech.{ext}");
        form.Add(new StringContent(s.SttModel), "model");
        var lang = s.Language.Split('-')[0];
        if (lang.Length == 2) form.Add(new StringContent(lang), "language");
        using var res = await Client.PostAsync($"{V1(s.SttUrl)}/audio/transcriptions", form, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Transcription failed: {body}");
        try { return JsonNode.Parse(body)?["text"]?.ToString().Trim() ?? ""; }
        catch (System.Text.Json.JsonException) { return body.Trim(); }
    }

    public async Task<(byte[] audio, string mime)> SynthesizeAsync(VoiceSettings s, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.TtsUrl)) throw new InvalidOperationException("No text-to-speech server configured.");
        using var res = await Client.PostAsJsonAsync($"{V1(s.TtsUrl)}/audio/speech", new JsonObject
        {
            ["model"] = s.TtsModel,
            ["voice"] = s.TtsVoice,
            ["input"] = text,
            ["response_format"] = "mp3",
        }, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Speech failed: {await res.Content.ReadAsStringAsync(ct)}");
        return (await res.Content.ReadAsByteArrayAsync(ct), res.Content.Headers.ContentType?.MediaType ?? "audio/mpeg");
    }

    /// <summary>Strips Markdown/code so text reads naturally aloud.</summary>
    public static string ForSpeech(string markdown)
    {
        var t = CodeBlock().Replace(markdown, " (code omitted) ");
        t = Link().Replace(t, "$1");
        t = Citation().Replace(t, "");
        t = Symbols().Replace(t, "");
        return Whitespace().Replace(t, " ").Trim();
    }

    [GeneratedRegex(@"```[\s\S]*?```")] private static partial Regex CodeBlock();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")] private static partial Regex Link();
    [GeneratedRegex(@"\[\d+\]")] private static partial Regex Citation();
    [GeneratedRegex(@"[*_`#>|~]")] private static partial Regex Symbols();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
