using Locus.Core.Models;
using Locus.Core.Services;
using Microsoft.JSInterop;

namespace Locus.UI.State;

public enum LivePhase { Off, Listening, Thinking, Speaking }

/// <summary>
/// Hands-free conversation: listen for one spoken turn, hand it to the chat, read the reply aloud, listen again.
/// Uses the speech-to-text server when one is set (recording with silence detection), otherwise browser recognition.
/// </summary>
public sealed class LiveVoice(IJSRuntime js, SpeechService speech) : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private DotNetObjectReference<LiveVoice>? _ref;

    public LivePhase Phase { get; private set; }
    /// <summary>What's being heard right now (browser recognition only).</summary>
    public string Heard { get; private set; } = "";
    /// <summary>Microphone level 0–1 while listening (recorder mode).</summary>
    public double Level { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;

    public bool Active => _cts is not null;

    private void Set(LivePhase p) { Phase = p; if (p != LivePhase.Listening) Level = 0; Changed?.Invoke(); }

    /// <summary>
    /// Runs the loop until <see cref="StopAsync"/>. <paramref name="respond"/> sends the text and returns the reply to speak.
    /// </summary>
    public async Task RunAsync(AppSettings settings, Func<string, Task<string?>> respond)
    {
        if (_cts is not null) return;
        using var cts = _cts = new CancellationTokenSource();
        _ref ??= DotNetObjectReference.Create(this);
        var useServer = !string.IsNullOrWhiteSpace(settings.Voice.SttUrl);
        Error = null;
        var emptyTurns = 0;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                Heard = "";
                Set(LivePhase.Listening);
                var turn = await js.InvokeAsync<Turn?>("locus.live.listen", cts.Token, useServer, settings.Voice.Language, _ref);
                if (cts.IsCancellationRequested) break;
                if (turn?.Error is { } err) { Error = err == "not-allowed" ? "Microphone access was blocked." : $"Speech recognition: {err}"; break; }
                string text;
                if (turn?.Base64 is { Length: > 0 } b64)
                {
                    Set(LivePhase.Thinking);
                    text = (await speech.TranscribeAsync(settings.Voice, Convert.FromBase64String(b64), turn.Mime ?? "audio/webm", cts.Token)).Trim();
                }
                else text = turn?.Text?.Trim() ?? "";
                // Whisper often hears silence as one of these.
                if (text.Length < 2 || text is "you" or "You" or "Thank you." or "." or "Bye.")
                {
                    if (turn is null && ++emptyTurns >= 3) break; // nobody's talking: hang up after ~2 minutes
                    continue;
                }
                emptyTurns = 0;
                Heard = text;
                Set(LivePhase.Thinking);
                var reply = await respond(text);
                if (cts.IsCancellationRequested) break;
                var spoken = reply is null ? "" : SpeechService.ForSpeech(reply);
                if (spoken.Length == 0) continue;
                Set(LivePhase.Speaking);
                if (!string.IsNullOrWhiteSpace(settings.Voice.TtsUrl))
                {
                    var (audio, mime) = await speech.SynthesizeAsync(settings.Voice, spoken, cts.Token);
                    await js.InvokeVoidAsync("locus.live.play", cts.Token, Convert.ToBase64String(audio), mime);
                }
                else await js.InvokeVoidAsync("locus.live.speak", cts.Token, spoken, settings.Voice.Language);
            }
        }
        catch (OperationCanceledException) { }
        catch (JSDisconnectedException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            try { await js.InvokeVoidAsync("locus.live.end"); } catch { /* page gone */ }
            Set(LivePhase.Off);
        }
    }

    /// <summary>Stops talking and listens right away (barge-in).</summary>
    public async Task InterruptAsync()
    {
        if (Phase == LivePhase.Speaking) await js.InvokeVoidAsync("locus.live.cancel");
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        try { await js.InvokeVoidAsync("locus.live.end"); } catch { /* page gone */ }
    }

    [JSInvokable] public void OnHeard(string text) { Heard = text; Changed?.Invoke(); }
    [JSInvokable] public void OnLevel(double level) { if (Math.Abs(level - Level) > 0.05) { Level = level; Changed?.Invoke(); } }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _ref?.Dispose();
    }

    private sealed record Turn(string? Text, string? Base64, string? Mime, string? Error);
}
