using Locus.Core.Models;
using Locus.Core.Services;
using Microsoft.JSInterop;

namespace Locus.UI.State;

/// <summary>Dictation. Hosts can replace the browser implementation (MAUI uses native on-device recognition).</summary>
public interface IVoiceInput
{
    Task<bool> IsAvailableAsync(AppSettings settings);
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

    public async Task<bool> IsAvailableAsync(AppSettings s)
    {
        var support = await js.InvokeAsync<VoiceSupport>("locus.voice.support");
        return string.IsNullOrWhiteSpace(s.Voice.SttUrl) ? support.Recognition : support.Recorder;
    }

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

    private sealed record VoiceSupport(bool Recorder, bool Recognition);
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
