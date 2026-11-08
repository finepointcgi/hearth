using System.Globalization;
using CommunityToolkit.Maui.Media;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.UI.State;
using Microsoft.JSInterop;

namespace Hearth.App;

/// <summary>On-device dictation through the OS speech recognizer (Android, iOS, macOS, Windows).</summary>
internal sealed class MauiVoiceInput : IVoiceInput
{
    private readonly ISpeechToText _stt = SpeechToText.Default;
    private Func<string, Task>? _onPartial;
    private TaskCompletionSource<string>? _final;
    private string _latest = "";

    public Task<bool> IsAvailableAsync(AppSettings settings) => Task.FromResult(true);

    public async Task StartAsync(AppSettings settings, Func<string, Task> onPartial)
    {
        if (!await _stt.RequestPermissions(CancellationToken.None))
            throw new InvalidOperationException("Microphone / speech permission was denied.");

        _onPartial = onPartial;
        _latest = "";
        _final = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stt.RecognitionResultUpdated += OnUpdated;
        _stt.RecognitionResultCompleted += OnCompleted;

        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(settings.Voice.Language); }
        catch (CultureNotFoundException) { culture = CultureInfo.CurrentCulture; }

        await _stt.StartListenAsync(new SpeechToTextOptions { Culture = culture, ShouldReportPartialResults = true },
            CancellationToken.None);
    }

    public async Task<string> StopAsync(AppSettings settings)
    {
        await _stt.StopListenAsync(CancellationToken.None);
        var result = _final is null ? _latest
            : await Task.WhenAny(_final.Task, Task.Delay(2000)) == _final.Task ? _final.Task.Result : _latest;
        _stt.RecognitionResultUpdated -= OnUpdated;
        _stt.RecognitionResultCompleted -= OnCompleted;
        return result.Trim();
    }

    private void OnUpdated(object? sender, SpeechToTextRecognitionResultUpdatedEventArgs e)
    {
        // Platforms differ: some report the full hypothesis, some just the newest words.
        _latest = e.RecognitionResult.StartsWith(_latest, StringComparison.Ordinal) || _latest.Length == 0
            ? e.RecognitionResult
            : _latest + " " + e.RecognitionResult;
        _ = MainThread.InvokeOnMainThreadAsync(() => _onPartial?.Invoke(_latest) ?? Task.CompletedTask);
    }

    private void OnCompleted(object? sender, SpeechToTextRecognitionResultCompletedEventArgs e) =>
        _final?.TrySetResult(e.RecognitionResult.IsSuccessful && !string.IsNullOrWhiteSpace(e.RecognitionResult.Text)
            ? e.RecognitionResult.Text!
            : _latest);
}

/// <summary>Read-aloud with native system voices, or a TTS server when configured.</summary>
internal sealed class MauiSpeechOutput(IJSRuntime js, SpeechService speech) : ISpeechOutput
{
    private CancellationTokenSource? _cts;

    public async Task SpeakAsync(AppSettings s, string text)
    {
        await StopAsync();
        var clean = SpeechService.ForSpeech(text);
        if (clean.Length == 0) return;
        if (!string.IsNullOrWhiteSpace(s.Voice.TtsUrl))
        {
            var (audio, mime) = await speech.SynthesizeAsync(s.Voice, clean, CancellationToken.None);
            await js.InvokeVoidAsync("hearth.voice.playAudio", Convert.ToBase64String(audio), mime);
            return;
        }

        _cts = new CancellationTokenSource();
        var locales = await TextToSpeech.Default.GetLocalesAsync();
        var lang = s.Voice.Language.Split('-')[0];
        var locale = locales.FirstOrDefault(l => $"{l.Language}-{l.Country}".Equals(s.Voice.Language, StringComparison.OrdinalIgnoreCase))
            ?? locales.FirstOrDefault(l => l.Language.StartsWith(lang, StringComparison.OrdinalIgnoreCase));
        _ = TextToSpeech.Default.SpeakAsync(clean, new SpeechOptions { Locale = locale }, _cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _cts = null;
        try { await js.InvokeVoidAsync("hearth.voice.stopSpeaking"); } catch (JSException) { }
    }
}
