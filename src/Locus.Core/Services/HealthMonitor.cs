using Locus.Core.Llm;

namespace Locus.Core.Services;

/// <summary>
/// Checks the active model server every couple of minutes. When it goes down or comes back, records it on the
/// dashboard and raises <see cref="Changed"/> (the Telegram/Discord hub pushes it to linked chats).
/// </summary>
public sealed class HealthMonitor(SettingsStore settingsStore, ILlmClientFactory factory, ActivityLog activity) : IDisposable
{
    private Timer? _timer;
    private int _busy;

    public bool? ModelServerUp { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastCheck { get; private set; }

    /// <summary>(up, message) whenever the state flips.</summary>
    public event Action<bool, string>? Changed;

    public void EnsureStarted() => _timer ??= new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2));

    public async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var settings = await settingsStore.GetAsync();
            var provider = settings.ActiveProvider;
            bool up;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await factory.Create(provider).ListModelsAsync(cts.Token);
                up = true;
                LastError = null;
            }
            catch (Exception ex)
            {
                up = false;
                LastError = ex.Message;
            }
            LastCheck = DateTimeOffset.Now;
            var was = ModelServerUp;
            ModelServerUp = up;
            if (was is null || was == up) { if (was is null && !up) await ReportAsync(false, provider.Name, settings); return; }
            await ReportAsync(up, provider.Name, settings);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private async Task ReportAsync(bool up, string name, Models.AppSettings settings)
    {
        var fallback = settings.FallbackProviderIds
            .Select(id => settings.Providers.FirstOrDefault(p => p.Id == id)?.Name).OfType<string>().FirstOrDefault();
        var message = up
            ? $"✅ {name} is reachable again."
            : $"⚠️ {name} can't be reached ({LastError})." + (fallback is null ? " Bots can't answer until it's back." : $" Using {fallback} meanwhile.");
        await activity.AddAsync(new Activity(DateTimeOffset.Now, up ? ActivityKind.Trigger : ActivityKind.Error, message, null, null, null, up));
        Changed?.Invoke(up, message);
    }

    public void Dispose() => _timer?.Dispose();
}
