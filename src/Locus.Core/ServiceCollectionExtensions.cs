using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Locus.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers Locus core services. Hosts may register their own <see cref="IAppPaths"/> first.</summary>
    public static IServiceCollection AddLocusCore(this IServiceCollection services, PlatformInfo platform)
    {
        services.TryAddSingleton<IAppPaths, DefaultAppPaths>();
        services.AddSingleton(platform);
        // No overall timeout (replies stream for minutes), but give up connecting to a dead machine after 10s.
        services.AddHttpClient(LlmClientFactory.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) });
        services.AddHttpClient("mcp", c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<UsageStore>();
        services.TryAddSingleton<ServerEmbedder>();
        services.TryAddSingleton<IEmbedder>(sp => sp.GetRequiredService<ServerEmbedder>());
        services.TryAddSingleton<IMemoryProvider>(sp => new SqliteMemoryProvider(
            sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<IEmbedder>(), sp.GetRequiredService<SettingsStore>()));
        services.AddSingleton(sp => new MemoryStore(sp.GetRequiredService<IMemoryProvider>()));
        services.AddSingleton<BotStore>();
        services.AddSingleton<SkillStore>();
        services.AddSingleton<LoginVault>();
        services.AddSingleton<ActivityLog>();
        services.AddSingleton<ApprovalCenter>();
        services.AddSingleton<ModelPicker>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<TaskStore>();
        services.AddSingleton<TaskRunner>();
        services.AddSingleton<BotInbox>();
        services.AddSingleton<ChatHistoryIndex>();
        services.AddSingleton<HeartbeatRunner>();
        services.AddSingleton<PushNotifier>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<BotCheckRunner>();
        services.TryAddSingleton<IAppRestarter, NoRestarter>();
        services.AddSingleton<SkillPorter>();
        services.AddSingleton<MemoryCurator>();
        services.AddSingleton<Briefing>();
        services.AddSingleton<SelfCheck>();
        services.AddSingleton<SkillTeacher>();
        services.AddSingleton<Services.Channels.ChannelStore>();
        services.AddSingleton<Services.Channels.ChannelHub>();
        services.AddSingleton<Services.Google.GoogleAuth>();
        services.AddSingleton<Services.Google.SheetsClient>();
        services.AddSingleton<IToolProvider, SheetsToolProvider>();
        services.AddSingleton<Services.Google.WorkspaceClient>();
        services.AddSingleton<IToolProvider, GoogleWorkspaceToolProvider>();
        services.AddSingleton<TriggerStateStore>();
        services.AddSingleton<RoutineRunner>();
        services.AddSingleton<WebhookListener>();
        services.AddSingleton<DocumentStore>();
        services.AddSingleton<WebSearchService>();
        services.AddSingleton<ImageGenService>();
        services.AddSingleton<SpeechService>();
        services.AddSingleton<BuiltInToolProvider>();
        services.AddSingleton<McpManager>();
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<ChatService>();
        services.AddSingleton<ChatRuns>();
        services.AddSingleton<FollowUpStore>();
        services.AddSingleton<FollowUpRunner>();
        services.AddSingleton<IToolProvider, ProactiveToolProvider>();
        services.AddSingleton<Services.Phone.PhoneStore>();
        services.AddSingleton<Services.Phone.PhoneService>();
        services.AddSingleton<IToolProvider, PhoneToolProvider>();
        services.AddSingleton<Services.Microsoft.MicrosoftAuth>();
        services.AddSingleton<Services.Microsoft.GraphClient>();
        services.AddSingleton<IToolProvider, MicrosoftToolProvider>();
        services.AddSingleton<Services.Google.DriveClient>();
        services.AddSingleton<IToolProvider, GoogleDocsToolProvider>();
        services.AddSingleton<IToolProvider, ContactsToolProvider>();
        services.AddSingleton<LinkedFolders>();
        services.AddSingleton<ProjectContext>();
        services.AddSingleton<OpenAiApi>();
        services.AddSingleton<ContextReferences>();
        services.AddSingleton<AgentImporter>();
        services.AddSingleton<EventWebhooks>();
        services.AddSingleton<SkillCatalog>();
        services.AddSingleton(sp => new CodeSandbox(sp.GetRequiredService<PlatformInfo>()));
        services.AddSingleton<ProjectHistory>();
        services.AddSingleton<CodeTools>();
        services.AddSingleton<IToolProvider, ProjectToolProvider>();
        services.AddSingleton<IToolProvider, ScriptToolProvider>();
        services.AddSingleton<IToolProvider, LinkedFolderToolProvider>();
        services.TryAddSingleton<IBotComputer, NoBotComputer>();
        services.TryAddSingleton<IDesktopControl, NoDesktopControl>();
        services.TryAddSingleton<ISandboxDesktops, NoSandboxDesktops>();
        return services;
    }
}
