using System.Collections.Concurrent;
using System.Security.Claims;
using Hearth.Core;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Hearth.Web;

/// <summary>
/// Separate accounts in one Hearth. Every account (the owner included) gets its own set of Hearth services over its own
/// data folder: bots, chats, memories, documents, connections, routines and chat apps are all its own. The app's
/// services forward to the set of whoever is signed in, so pages and endpoints need no changes. With accounts off,
/// everything goes to the owner's set — the original data folder, as before.
/// </summary>
public sealed class AccountHosts(IServiceProvider root, AccountStore store, Action<IServiceCollection, Func<bool>?> configure) : IAsyncDisposable
{
    public const string Claim = "hearth_account";
    private readonly ConcurrentDictionary<string, Lazy<ServiceProvider>> _hosts = new();

    private sealed class FixedPaths(string dir) : IAppPaths { public string DataDirectory => dir; }

    /// <summary>The services for an account (null = the owner, also when accounts are off).</summary>
    public IServiceProvider For(Account? account)
    {
        var dir = account is null || account.IsOwner ? store.RootDataDirectory : store.DataDirectoryFor(account);
        var key = account is null || account.IsOwner ? "owner" : account.Id;
        return _hosts.GetOrAdd(key, _ => new Lazy<ServiceProvider>(() => Build(dir, key == "owner" ? null : key))).Value;
    }

    public bool IsRunning(string accountId) => _hosts.ContainsKey(accountId);

    /// <param name="accountId">Someone other than the owner: they use this computer only while the owner allows it.</param>
    private ServiceProvider Build(string dir, string? accountId)
    {
        var newAccount = accountId is not null;
        Directory.CreateDirectory(dir);
        if (newAccount) SeedFromOwner(dir);
        var s = new ServiceCollection();
        s.AddSingleton<IAppPaths>(new FixedPaths(dir));
        s.AddSingleton(root.GetRequiredService<ILoggerFactory>());
        s.AddLogging();
        s.AddSingleton(root.GetRequiredService<IConfiguration>());
        s.AddSingleton(root.GetRequiredService<IHostApplicationLifetime>());
        s.AddSingleton(root.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>());
        configure(s, accountId is null ? null : () => store.MayUseComputer(accountId));
        var sp = s.BuildServiceProvider();
        if (newAccount) _ = SyncModelServersAsync(sp);
        AccountStartup.Start(sp);
        return sp;
    }

    /// <summary>A new account starts with the owner's encryption key, so the shared model servers' API keys open.</summary>
    private void SeedFromOwner(string dir)
    {
        var key = Path.Combine(store.RootDataDirectory, "vault.key");
        var mine = Path.Combine(dir, "vault.key");
        if (File.Exists(key) && !File.Exists(mine)) File.Copy(key, mine);
    }

    /// <summary>Model servers are shared: everyone uses the owner's list (and the owner's choice of default).</summary>
    public async Task SyncModelServersAsync(IServiceProvider account)
    {
        try
        {
            var owner = await For(null).GetRequiredService<SettingsStore>().GetAsync();
            var settings = account.GetRequiredService<SettingsStore>();
            var s = await settings.GetAsync();
            var before = System.Text.Json.JsonSerializer.Serialize(new { s.Providers, s.ActiveProviderId });
            s.Providers = System.Text.Json.JsonSerializer.Deserialize<List<ProviderProfile>>(System.Text.Json.JsonSerializer.Serialize(owner.Providers))!;
            if (s.Providers.All(p => p.Id != s.ActiveProviderId)) s.ActiveProviderId = owner.ActiveProviderId;
            if (before != System.Text.Json.JsonSerializer.Serialize(new { s.Providers, s.ActiveProviderId })) await settings.SaveAsync(s);
        }
        catch { /* tried again when the owner's settings change */ }
    }

    /// <summary>Starts every account's background work (routines, chat apps…) and keeps model servers in step.</summary>
    public void StartAll()
    {
        var owner = For(null);
        owner.GetRequiredService<SettingsStore>().Changed += () =>
        {
            foreach (var (key, host) in _hosts)
                if (key != "owner" && host.IsValueCreated) _ = SyncModelServersAsync(host.Value);
        };
        if (!store.Enabled) return;
        foreach (var a in store.Get().Accounts.Where(a => !a.IsOwner)) For(a);
    }

    public void PrepareForShutdown()
    {
        foreach (var h in _hosts.Values.Where(h => h.IsValueCreated))
            try { h.Value.GetRequiredService<ChatRuns>().PrepareForShutdown(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _hosts.Values.Where(h => h.IsValueCreated)) await h.Value.DisposeAsync();
    }

    /// <summary>
    /// Replaces the app's own Hearth singletons with ones that forward to the signed-in person's set. Called after
    /// the Hearth services are registered, before anything account-independent is added.
    /// </summary>
    public static void ForwardToAccounts(IServiceCollection services)
    {
        var groups = new Dictionary<Type, int>();
        for (var i = 0; i < services.Count; i++)
        {
            var d = services[i];
            if (d.Lifetime != ServiceLifetime.Singleton || d.IsKeyedService || d.ServiceType.IsGenericTypeDefinition) continue;
            if (d.ServiceType.Assembly.GetName().Name?.StartsWith("Hearth") != true) continue;
            var type = d.ServiceType;
            var index = groups.TryGetValue(type, out var n) ? n : 0;
            groups[type] = index + 1;
            services[i] = ServiceDescriptor.Scoped(type, sp =>
                sp.GetRequiredService<CurrentAccount>().Services.GetServices(type).ElementAt(index)!);
        }
    }
}

/// <summary>Who this page or request belongs to (from the sign-in cookie), and their services.</summary>
public sealed class CurrentAccount(IServiceProvider scope, AccountStore store, AccountHosts hosts) : ICurrentUser
{
    private Account? _account;
    private bool _resolved;

    public Account? Account
    {
        get
        {
            if (!_resolved) { _account = Resolve(); _resolved = true; }
            return _account;
        }
    }

    public IServiceProvider Services => hosts.For(Account);

    private Account? Resolve()
    {
        if (!store.Enabled) return null;
        var id = FromCircuit() ?? scope.GetService<IHttpContextAccessor>()?.HttpContext?.User.FindFirst(AccountHosts.Claim)?.Value;
        // Nobody signed in here: start-up, background work and open endpoints (health, the API, webhooks) are the owner's.
        if (id is null) return null;
        return store.Find(id) ?? throw new InvalidOperationException("This account no longer exists. Sign in again.");
    }

    /// <summary>Inside a page (a Blazor circuit), the user the circuit was opened with.</summary>
    private string? FromCircuit()
    {
        try
        {
            var task = scope.GetService<AuthenticationStateProvider>()?.GetAuthenticationStateAsync();
            return task is { IsCompletedSuccessfully: true } ? task.Result.User.FindFirst(AccountHosts.Claim)?.Value : null;
        }
        catch (InvalidOperationException) { return null; } // not in a circuit
    }

    public static ClaimsPrincipal Principal(Account a) =>
        new(new ClaimsIdentity([new Claim(AccountHosts.Claim, a.Id), new Claim(ClaimTypes.Name, a.Name)], "hearth"));
}

/// <summary>Each account's background work: routines, tasks, chat apps, memory tidy-up and the rest.</summary>
public static class AccountStartup
{
    public static void Start(IServiceProvider sp)
    {
        sp.GetRequiredService<RoutineRunner>().EnsureStarted();
        _ = sp.GetRequiredService<Briefing>().RefreshAsync();
        if (sp.GetRequiredService<Hearth.Core.Services.Agents.AgentHub>().Available)
        {
            sp.GetRequiredService<Hearth.Core.Services.Agents.AgentCare>().EnsureWatching();
            _ = sp.GetRequiredService<Hearth.Core.Services.Agents.AgentHub>().StatusAsync(); // look for installed agents in the background
        }
        sp.GetRequiredService<HealthMonitor>().EnsureStarted();
        sp.GetRequiredService<TaskRunner>().EnsureStarted(); // long-running tasks resume after a restart
        sp.GetRequiredService<BotInbox>().EnsureStarted();
        sp.GetRequiredService<HeartbeatRunner>().EnsureStarted();
        sp.GetRequiredService<SkillPorter>().EnsureStarted();
        sp.GetRequiredService<MemoryCurator>().EnsureStarted();
        // The starter skills, once.
        _ = Task.Run(async () =>
        {
            try
            {
                var settingsStore = sp.GetRequiredService<SettingsStore>();
                var current = await settingsStore.GetAsync();
                if (current.StarterSkillsAdded) return;
                await StarterSkills.AddMissingAsync(sp.GetRequiredService<SkillStore>());
                current.StarterSkillsAdded = true;
                await settingsStore.SaveAsync(current);
            }
            catch { /* they can be added from the Skills page */ }
        });
        sp.GetRequiredService<SelfCheck>().EnsureStarted(); // nightly self-check (off unless turned on in Settings)
        sp.GetRequiredService<ChatHistoryIndex>().EnsureStarted();
        sp.GetRequiredService<PushNotifier>().EnsureStarted();
        sp.GetRequiredService<LearningRecap>().EnsureStarted(); // the weekly "what Hearth learned"
        sp.GetRequiredService<BackupService>().EnsureStarted();
        sp.GetRequiredService<ChatRuns>().EnsureStarted(); // replies a restart interrupted resume
        sp.GetRequiredService<FollowUpRunner>().EnsureStarted(); // bots coming back to chats on their own
        sp.GetRequiredService<LinkedFolders>().EnsureStarted(); // folders linked on the Documents page stay indexed
        sp.GetRequiredService<CloudFolders>().EnsureStarted(); // Google Drive and OneDrive folders
        sp.GetRequiredService<EventWebhooks>().EnsureStarted(); // your webhooks hear about approvals, tasks, follow-ups…
        _ = sp.GetRequiredService<Hearth.Core.Services.Channels.ChannelHub>().EnsureStartedAsync().ContinueWith(_ => { });
    }
}
