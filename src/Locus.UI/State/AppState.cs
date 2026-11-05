using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;

namespace Locus.UI.State;

/// <summary>Per-user UI state (scoped: one per window in hybrid apps, one per circuit on the web).</summary>
public sealed class AppState : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ConversationStore _conversations;
    private readonly ILlmClientFactory _factory;
    private readonly BotStore _bots;
    private readonly RoutineRunner _routines;
    private Task? _init;

    public AppState(SettingsStore settings, ConversationStore conversations, ILlmClientFactory factory,
        BotStore bots, RoutineRunner routines, SkillStore skills, Locus.Core.Services.Channels.ChannelHub channels, TaskRunner tasks, BotInbox inbox, HeartbeatRunner heartbeat, SkillPorter porter, MemoryCurator curator, ChatHistoryIndex history, PushNotifier notifier, BackupService backups, ChatRuns runs, FollowUpRunner followUps, LinkedFolders folders, EventWebhooks webhooks)
    {
        _settings = settings;
        _conversations = conversations;
        _factory = factory;
        _bots = bots;
        _routines = routines;
        _conversations.Changed += OnConversationsChanged;
        _settings.Changed += OnSettingsChanged;
        _bots.Changed += OnBotsChanged;
        _skills = skills;
        _channels = channels;
        _tasks = tasks;
        _inbox = inbox;
        _heartbeat = heartbeat;
        _porter = porter;
        _curator = curator;
        _history = history;
        _notifier = notifier;
        _backups = backups;
        _runs = runs;
        _followUps = followUps;
        _folders = folders;
        _webhooks = webhooks;
        _runs.RunsChanged += NotifyChanged;
        _skills.Changed += OnSkillsChanged;
    }

    private readonly SkillStore _skills;
    private readonly Locus.Core.Services.Channels.ChannelHub _channels;
    private readonly TaskRunner _tasks;
    private readonly BotInbox _inbox;
    private readonly HeartbeatRunner _heartbeat;
    private readonly SkillPorter _porter;
    private readonly MemoryCurator _curator;
    private readonly ChatHistoryIndex _history;
    private readonly PushNotifier _notifier;
    private readonly BackupService _backups;
    private readonly ChatRuns _runs;
    private readonly FollowUpRunner _followUps;
    private readonly LinkedFolders _folders;
    private readonly EventWebhooks _webhooks;

    /// <summary>Chats with a reply in progress (shown in the chat list).</summary>
    public bool IsWorking(string id) => _runs.IsRunning(id);

    private async void OnSkillsChanged()
    {
        Skills = await _skills.ListAsync();
        NotifyChanged();
    }

    public List<Bot> Bots { get; private set; } = [];
    public List<Skill> Skills { get; private set; } = [];
    public Bot? BotById(string? id) => id is null ? null : Bots.FirstOrDefault(b => b.Id == id);

    private async void OnBotsChanged()
    {
        Bots = await _bots.ListAsync();
        NotifyChanged();
    }

    public AppSettings Settings { get; private set; } = new();
    public IReadOnlyList<ModelInfo> Models { get; private set; } = [];
    public string? ModelError { get; private set; }
    public bool LoadingModels { get; private set; }
    public string? SelectedModel { get; set; }
    public bool Think { get; set; }
    public bool DeepSearch { get; set; }
    public bool UseTools { get; set; } = true;
    /// <summary>Capabilities of the selected model, when the server reports them (Ollama).</summary>
    public ModelCapabilities? Capabilities { get; private set; }
    public string PersonaId { get; set; } = Personas.Default.Id;
    public List<ConversationSummary> Conversations { get; private set; } = [];
    public bool SidebarOpen { get; set; }

    public bool CanSearch => !string.IsNullOrWhiteSpace(Settings.SearxngUrl);
    public bool CanImagine => Settings.ImageGen.Backend != ImageBackend.None;

    public async Task SelectModelAsync(string? model)
    {
        SelectedModel = model;
        Capabilities = null;
        NotifyChanged();
        if (model is null) return;
        Capabilities = await _factory.Create(Settings.ActiveProvider).GetCapabilitiesAsync(model);
        NotifyChanged();
    }

    public event Action? Changed;
    public void NotifyChanged() => Changed?.Invoke();

    public Task EnsureInitializedAsync() => _init ??= InitAsync();

    private async Task InitAsync()
    {
        Settings = await _settings.GetAsync();
        PersonaId = Settings.DefaultPersonaId;
        UseTools = Settings.Tools.EnabledByDefault;
        Conversations = await _conversations.ListAsync();
        Bots = await _bots.ListAsync();
        Skills = await _skills.ListAsync();
        _routines.EnsureStarted();
        _tasks.EnsureStarted();
        _inbox.EnsureStarted();
        _heartbeat.EnsureStarted();
        _porter.EnsureStarted();
        _curator.EnsureStarted();
        _history.EnsureStarted();
        _notifier.EnsureStarted();
        _backups.EnsureStarted();
        _runs.EnsureStarted(); // replies cut off by a restart carry on
        _followUps.EnsureStarted();
        _folders.EnsureStarted();
        _webhooks.EnsureStarted();
        _ = _channels.EnsureStartedAsync().ContinueWith(_ => { }); // Telegram/Discord; errors show on Connections
        NotifyChanged();
        await RefreshModelsAsync();
    }

    public async Task RefreshModelsAsync()
    {
        LoadingModels = true;
        ModelError = null;
        FallbackNotice = null;
        NotifyChanged();
        try
        {
            var provider = Settings.ActiveProvider;
            Models = await _factory.Create(provider).ListModelsAsync();
            if (SelectedModel is null || Models.All(m => m.Id != SelectedModel))
                SelectedModel = Models.FirstOrDefault(m => m.Id == provider.DefaultModel)?.Id
                    ?? Models.FirstOrDefault()?.Id;
            _ = SelectModelAsync(SelectedModel);
            if (Models.Count == 0)
                ModelError = provider.Kind == ProviderKind.Ollama
                    ? "Connected, but no models are installed. Run: ollama pull llama3.2"
                    : "Connected, but the server reports no loaded models.";
        }
        catch (Exception ex)
        {
            Models = [];
            ModelError = ex.Message;
            await UseFallbackModelsAsync();
        }
        finally
        {
            LoadingModels = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// The active server is down: list models from the first backup server that answers, so chats still work
    /// (each reply then says it came from the backup).
    /// </summary>
    private async Task UseFallbackModelsAsync()
    {
        foreach (var id in Settings.FallbackProviderIds)
        {
            var backup = Settings.Providers.FirstOrDefault(p => p.Id == id);
            if (backup is null || backup.Id == Settings.ActiveProvider.Id) continue;
            try
            {
                var models = await _factory.Create(backup).ListModelsAsync();
                if (models.Count == 0) continue;
                Models = models;
                SelectedModel = models.FirstOrDefault(m => m.Id == backup.DefaultModel)?.Id ?? models[0].Id;
                ModelError = null;
                FallbackNotice = $"{Settings.ActiveProvider.Name} can't be reached — using {backup.Name} for now.";
                return;
            }
            catch { /* try the next backup */ }
        }
    }

    /// <summary>Set when the model list comes from a backup server because the active one is down.</summary>
    public string? FallbackNotice { get; private set; }

    private async void OnConversationsChanged()
    {
        Conversations = await _conversations.ListAsync();
        NotifyChanged();
    }

    private async void OnSettingsChanged()
    {
        Settings = await _settings.GetAsync();
        await RefreshModelsAsync();
    }

    public void Dispose()
    {
        _conversations.Changed -= OnConversationsChanged;
        _settings.Changed -= OnSettingsChanged;
        _bots.Changed -= OnBotsChanged;
        _skills.Changed -= OnSkillsChanged;
        _runs.RunsChanged -= NotifyChanged;
    }
}
