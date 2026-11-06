using Locus.Core.Models;
using Locus.Core.Services;
using Microsoft.JSInterop;

namespace Locus.UI.State;

/// <summary>Dictation. Hosts can replace the browser implementation (MAUI uses native on-device recognition).</summary>
public interface IVoiceInput
{
    Task<bool> IsAvailableAsync(AppSettings settings);
    /// <summary>Why dictation can't work here, in words the user can act on (null when it can).</summary>
    Task<string?> UnavailableReasonAsync(AppSettings settings) => Task.FromResult<string?>(null);
    /// <summary>Called when listening fails after it started (microphone blocked, no internet…), with a readable message.</summary>
    Func<string, Task>? OnError { get => null; set { } }
    Task StartAsync(AppSettings settings, Func<string, Task> onPartial);
    /// <summary>Stops listening and returns the final transcript.</summary>
    Task<string> StopAsync(AppSettings settings);
}

/// <summary>Read-aloud. Uses a TTS server when configured, otherwise the platform's voices.</summary>
public interface ISpeechOutput
{
    Task SpeakAsync(AppSettings settings, string text);
    Task StopAsync();
}

public sealed class BrowserVoiceInput(IJSRuntime js, SpeechService speech) : IVoiceInput, IDisposable
{
    private DotNetObjectReference<BrowserVoiceInput>? _ref;
    private Func<string, Task>? _onPartial;
    private bool _recording;

    public Func<string, Task>? OnError { get; set; }

    public async Task<bool> IsAvailableAsync(AppSettings s) => await UnavailableReasonAsync(s) is null;

    public async Task<string?> UnavailableReasonAsync(AppSettings s)
    {
        var support = await js.InvokeAsync<VoiceSupport>("locus.voice.support");
        return WhyNot(support.Secure, string.IsNullOrWhiteSpace(s.Voice.SttUrl) ? support.Recognition : support.Recorder,
            !string.IsNullOrWhiteSpace(s.Voice.SttUrl));
    }

    /// <summary>The reason dictation can't start, or null.</summary>
    public static string? WhyNot(bool secure, bool supported, bool usesServer) =>
        !secure ? "Browsers only allow the microphone on a secure (https://) address or on localhost. On this computer open " +
                  "http://localhost:5290; on your phone use the Tailscale https link (Settings → Phone access)."
        : supported ? null
        : usesServer ? "This browser can't record audio."
        : "This browser has no built-in speech recognition (Firefox doesn't). Use Chrome, Edge or Safari, or set a " +
          "speech-to-text server in Settings → Voice.";

    /// <summary>Speech recognition error codes as something the user can fix.</summary>
    public static string Explain(string code) => code switch
    {
        "not-allowed" or "service-not-allowed" => "The microphone is blocked for this site. Allow it from the 🔒 icon in the address bar (and in System Settings → Privacy → Microphone for your browser), then try again.",
        "audio-capture" => "No microphone was found.",
        "network" => "Speech recognition couldn't reach its online service (Chrome sends audio to Google to transcribe). Check your internet, or set a local speech-to-text server in Settings → Voice.",
        "language-not-supported" => "Speech recognition doesn't support the language set in Settings → Voice.",
        _ => $"Speech recognition stopped ({code}).",
    };

    [JSInvokable] public Task OnRecognitionError(string code) => OnError?.Invoke(Explain(code)) ?? Task.CompletedTask;

    public async Task StartAsync(AppSettings s, Func<string, Task> onPartial)
    {
        _onPartial = onPartial;
        if (!string.IsNullOrWhiteSpace(s.Voice.SttUrl))
        {
            _recording = true;
            await js.InvokeVoidAsync("locus.voice.startRecording");
        }
        else
        {
            _recording = false;
            _ref ??= DotNetObjectReference.Create(this);
            await js.InvokeVoidAsync("locus.voice.startRecognition", _ref, s.Voice.Language);
        }
    }

    public async Task<string> StopAsync(AppSettings s)
    {
        if (!_recording) return await js.InvokeAsync<string>("locus.voice.stopRecognition");
        var clip = await js.InvokeAsync<AudioClip?>("locus.voice.stopRecording");
        if (clip is null || string.IsNullOrEmpty(clip.Base64)) return "";
        return await speech.TranscribeAsync(s.Voice, Convert.FromBase64String(clip.Base64), clip.Mime, CancellationToken.None);
    }

    [JSInvokable] public Task OnPartial(string text) => _onPartial?.Invoke(text) ?? Task.CompletedTask;

    public void Dispose() => _ref?.Dispose();

    private sealed record VoiceSupport(bool Recorder, bool Recognition, bool Secure);
    private sealed record AudioClip(string Base64, string Mime);
}

public sealed class BrowserSpeechOutput(IJSRuntime js, SpeechService speech) : ISpeechOutput
{
    public async Task SpeakAsync(AppSettings s, string text)
    {
        var clean = SpeechService.ForSpeech(text);
        if (clean.Length == 0) return;
        if (!string.IsNullOrWhiteSpace(s.Voice.TtsUrl))
        {
            var (audio, mime) = await speech.SynthesizeAsync(s.Voice, clean, CancellationToken.None);
            await js.InvokeVoidAsync("locus.voice.playAudio", Convert.ToBase64String(audio), mime);
        }
        else
        {
            await js.InvokeVoidAsync("locus.voice.speak", clean, s.Voice.Language);
        }
    }

    public async Task StopAsync() => await js.InvokeVoidAsync("locus.voice.stopSpeaking");
}
