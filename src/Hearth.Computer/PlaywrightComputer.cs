using Hearth.Core.Models;
using Hearth.Core.Services;
using Microsoft.Playwright;

namespace Hearth.Computer;

/// <summary>
/// The bots' browsers. Each profile ("shared", or a bot id for bots with their own browser) is a persistent
/// Chromium profile on disk, so sign-ins survive restarts. Every bot works in its own tab. Profiles start on
/// first use and close after a period of inactivity.
/// </summary>
public sealed class PlaywrightComputer : IBotComputer, IAsyncDisposable
{
    private sealed class Profile
    {
        public required IBrowserContext Context { get; init; }
        public required bool Headless { get; init; }
        public Dictionary<string, IPage> Pages { get; } = [];
        public DateTime LastUse { get; set; } = DateTime.UtcNow;
    }

    private readonly IAppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly PlatformInfo _platform;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, Profile> _profiles = [];
    private IPlaywright? _pw;
    private string? _error;
    private (string profile, string owner)? _active;
    private string? _title;
    private readonly Timer _idleTimer;

    public PlaywrightComputer(IAppPaths paths, SettingsStore settings, PlatformInfo platform)
    {
        _paths = paths;
        _settings = settings;
        _platform = platform;
        _idleTimer = new Timer(_ => _ = StopIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public event Action? Changed;
    public string WorkspaceDirectory => Path.Combine(_paths.DataDirectory, "workspace");
    private string ProfileDirectory(string key) => key == "shared"
        ? Path.Combine(_paths.DataDirectory, "browser-profile")
        : Path.Combine(_paths.DataDirectory, "browser-profiles", Path.GetFileName(key));

    public ComputerStatus Status
    {
        get
        {
            if (!_platform.CanRunProcesses)
                return new(false, false, true, null, null, null, null,
                    "The bot computer needs local access: use the desktop app, or enable Hearth:AllowLocalProcesses on the web host.");
            var page = ActivePage();
            var running = _profiles.Keys.ToList();
            var headless = _active is { } a && _profiles.TryGetValue(a.profile, out var p) ? p.Headless : true;
            return new(true, running.Count > 0, headless, page?.Url, page is null ? null : _title, _active?.owner, _error, null, running);
        }
    }

    private IPage? ActivePage() =>
        _active is { } a && _profiles.TryGetValue(a.profile, out var p) && p.Pages.TryGetValue(a.owner, out var page) && !page.IsClosed
            ? page
            : _profiles.Values.SelectMany(p => p.Pages.Values).LastOrDefault(pg => !pg.IsClosed);

    internal async Task NoteActivityAsync(IPage page)
    {
        try { _title = await page.TitleAsync(); } catch { _title = null; }
        Changed?.Invoke();
    }

    public async Task StartAsync(bool headless, string profile = "shared")
    {
        await _lock.WaitAsync();
        try
        {
            var p = await EnsureProfileAsync(profile, headless, restartIfDifferent: true);
            if (p.Pages.Count == 0) p.Pages["you"] = p.Context.Pages.FirstOrDefault() ?? await p.Context.NewPageAsync();
            _active = (profile, p.Pages.Keys.First());
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    private async Task<Profile> EnsureProfileAsync(string key, bool headless, bool restartIfDifferent)
    {
        if (_profiles.TryGetValue(key, out var existing) && (!restartIfDifferent || existing.Headless == headless))
            return existing;
        if (existing is not null) await CloseProfileAsync(key);

        var s = (await _settings.GetAsync()).Computer;
        _error = null;
        try
        {
            _pw ??= await Playwright.CreateAsync();
            var dir = ProfileDirectory(key);
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(WorkspaceDirectory, "downloads"));
            var context = await _pw.Chromium.LaunchPersistentContextAsync(dir, new()
            {
                Headless = headless,
                Channel = string.IsNullOrWhiteSpace(s.Channel) ? null : s.Channel,
                ViewportSize = new() { Width = 1280, Height = 860 },
                AcceptDownloads = true,
                DownloadsPath = Path.Combine(WorkspaceDirectory, "downloads"),
            });
            var profile = new Profile { Context = context, Headless = headless };
            context.Close += (_, _) => { _profiles.Remove(key); Changed?.Invoke(); };
            _profiles[key] = profile;
            return profile;
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist") || ex.Message.Contains("install"))
        {
            _error = "No browser installed for the bots yet. Click \"Install browser\" on the Computer page " +
                     "(downloads Chromium, ~150 MB), or set the browser to your installed Chrome/Edge.";
            throw new InvalidOperationException(_error, ex);
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("ProcessSingleton") || ex.Message.Contains("already in use"))
        {
            _error = "That browser profile is already open in another window. Close it and try again.";
            throw new InvalidOperationException(_error, ex);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            throw;
        }
    }

    /// <summary>The tab a bot works in, in its browser profile (created on demand).</summary>
    public async Task<IPage> PageForAsync(string owner, string profileKey)
    {
        await _lock.WaitAsync();
        try
        {
            var s = (await _settings.GetAsync()).Computer;
            var profile = await EnsureProfileAsync(profileKey,
                _profiles.TryGetValue(profileKey, out var running) ? running.Headless : s.Headless, restartIfDifferent: false);
            profile.LastUse = DateTime.UtcNow;
            if (!profile.Pages.TryGetValue(owner, out var page) || page.IsClosed)
            {
                // Reuse the blank tab a persistent context opens with, or the one opened for signing in.
                page = profile.Context.Pages.FirstOrDefault(p => p.Url == "about:blank" && !profile.Pages.ContainsValue(p))
                       ?? await profile.Context.NewPageAsync();
                profile.Pages[owner] = page;
                page.Download += async (_, d) =>
                {
                    try { await d.SaveAsAsync(Path.Combine(WorkspaceDirectory, "downloads", Path.GetFileName(d.SuggestedFilename))); }
                    catch { /* surfaced by the tool that triggered it */ }
                };
            }
            _active = (profileKey, owner);
            return page;
        }
        finally
        {
            _lock.Release();
            Changed?.Invoke();
        }
    }

    public async Task<byte[]?> ScreenshotAsync()
    {
        var page = ActivePage();
        if (page is null) return null;
        try { return await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = 70, Timeout = 5000 }); }
        catch { return null; }
    }

    public async Task StopAsync()
    {
        await _lock.WaitAsync();
        try { foreach (var key in _profiles.Keys.ToList()) await CloseProfileAsync(key); }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    private async Task CloseProfileAsync(string key)
    {
        if (!_profiles.Remove(key, out var p)) return;
        if (_active?.profile == key) _active = null;
        try { await p.Context.CloseAsync(); } catch { }
    }

    private async Task StopIdleAsync()
    {
        if (_profiles.Count == 0) return;
        var idle = (await _settings.GetAsync()).Computer.IdleMinutes;
        if (idle <= 0) return;
        await _lock.WaitAsync();
        try
        {
            foreach (var (key, p) in _profiles.ToList())
                if (p.Headless && DateTime.UtcNow - p.LastUse > TimeSpan.FromMinutes(idle))
                    await CloseProfileAsync(key);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ show-and-learn recording

    private readonly List<RecordedStep> _recorded = [];
    private (string profile, IPage page)? _recording;
    public bool IsRecording => _recording is not null;
    public IReadOnlyList<RecordedStep> RecordedSteps { get { lock (_recorded) return _recorded.ToList(); } }

    private const string RecorderJs = """
        (() => {
          if (window.__hearthRecorderInstalled) return;
          window.__hearthRecorderInstalled = true;
          const send = (kind, el, value, sensitive) => {
            try { window.__hearthRecord(JSON.stringify({ kind, target: describe(el), value: value ?? null, sensitive: !!sensitive })); } catch {}
          };
          const label = el => {
            if (!el) return '';
            const aria = el.getAttribute && (el.getAttribute('aria-label') || el.getAttribute('title') || el.getAttribute('alt'));
            if (aria) return aria;
            if (el.id) { const l = document.querySelector(`label[for="${CSS.escape(el.id)}"]`); if (l) return l.innerText; }
            const wrap = el.closest && el.closest('label'); if (wrap) return wrap.innerText;
            return el.getAttribute && (el.getAttribute('placeholder') || el.getAttribute('name')) || (el.innerText || el.value || '');
          };
          const describe = el => {
            const tag = el.tagName.toLowerCase();
            const role = el.getAttribute('role') || (tag === 'a' ? 'link' : tag === 'button' ? 'button' : tag === 'select' ? 'dropdown'
              : tag === 'textarea' ? 'text box' : tag === 'input' ? ((el.type === 'submit' || el.type === 'button') ? 'button' : (el.type === 'checkbox' ? 'checkbox' : 'field')) : tag);
            const text = (label(el) || '').trim().replace(/\s+/g, ' ').slice(0, 70);
            return `the ${role}${text ? ` "${text}"` : ''}`;
          };
          const interesting = 'a,button,input,select,textarea,summary,[role=button],[role=link],[role=tab],[role=menuitem],[role=option],[role=checkbox],label';
          document.addEventListener('click', e => {
            const el = e.target.closest ? (e.target.closest(interesting) || e.target) : e.target;
            if (el.tagName === 'INPUT' && !['submit','button','checkbox','radio'].includes(el.type)) return;
            if (el.tagName === 'TEXTAREA' || el.tagName === 'SELECT') return;
            send('click', el);
          }, true);
          document.addEventListener('change', e => {
            const el = e.target;
            if (el.tagName === 'SELECT') send('select', el, el.options[el.selectedIndex]?.text);
            else if (el.tagName === 'TEXTAREA' || (el.tagName === 'INPUT' && !['checkbox','radio','submit','button'].includes(el.type)))
              send('type', el, el.type === 'password' ? null : el.value, el.type === 'password' || (el.autocomplete || '').includes('password'));
          }, true);
          document.addEventListener('keydown', e => {
            if (e.key === 'Enter' && (e.target.tagName === 'INPUT')) {
              const el = e.target;
              if (el.type !== 'password') send('type', el, el.value);
              send('key', el, 'Enter');
            }
          }, true);
        })();
        """;

    public async Task StartRecordingAsync(string profile, string? startUrl)
    {
        if (_recording is not null) await StopRecordingAsync();
        lock (_recorded) _recorded.Clear();
        await _lock.WaitAsync();
        IPage page;
        try
        {
            // HEARTH_RECORD_HEADLESS=1 lets automated tests record without opening a window.
            var headless = Environment.GetEnvironmentVariable("HEARTH_RECORD_HEADLESS") == "1";
            var p = await EnsureProfileAsync(profile, headless, restartIfDifferent: true);
            page = p.Context.Pages.FirstOrDefault(x => x.Url == "about:blank") ?? await p.Context.NewPageAsync();
            p.Pages["recording"] = page;
            await page.ExposeFunctionAsync("__hearthRecord", (string json) =>
            {
                var d = System.Text.Json.JsonDocument.Parse(json).RootElement;
                Add(d.GetProperty("kind").GetString()!, d.GetProperty("target").GetString() ?? "",
                    d.GetProperty("value").ValueKind == System.Text.Json.JsonValueKind.String ? d.GetProperty("value").GetString() : null,
                    page.Url, d.GetProperty("sensitive").GetBoolean());
            });
            await page.AddInitScriptAsync(RecorderJs);
            page.FrameNavigated += (_, f) =>
            {
                if (f != page.MainFrame || f.Url == "about:blank") return;
                lock (_recorded)
                    if (_recorded.LastOrDefault() is { Kind: "navigate" } last && last.Url == f.Url) return;
                Add("navigate", "", null, f.Url, false);
            };
            _recording = (profile, page);
            _active = (profile, "recording");
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
        if (!string.IsNullOrWhiteSpace(startUrl))
            await page.GotoAsync(startUrl.Contains("://") ? startUrl : "https://" + startUrl);
        else
            await page.EvaluateAsync(RecorderJs);
    }

    private void Add(string kind, string target, string? value, string url, bool sensitive)
    {
        lock (_recorded)
        {
            // Typing fires on Enter and again on change; keep one entry per field and value.
            if (kind == "type" && _recorded.LastOrDefault() is { Kind: "type" } prev && prev.Target == target)
                _recorded.RemoveAt(_recorded.Count - 1);
            if (kind == "type" && _recorded.TakeLast(3).Any(r => r.Kind == "type" && r.Target == target && r.Value == value))
                return;
            _recorded.Add(new RecordedStep(DateTimeOffset.Now, kind, target, value, url, sensitive));
        }
        Changed?.Invoke();
    }

    public async Task<List<RecordedStep>> StopRecordingAsync()
    {
        var rec = _recording;
        _recording = null;
        if (rec is { } r)
        {
            await _lock.WaitAsync();
            try { await CloseProfileAsync(r.profile); } // closes the visible window; sign-ins are kept on disk
            finally { _lock.Release(); }
        }
        Changed?.Invoke();
        return RecordedSteps.ToList();
    }

    /// <summary>Deletes a bot's own browser profile (cookies, sign-ins).</summary>
    public async Task ResetProfileAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            await CloseProfileAsync(key);
            var dir = ProfileDirectory(key);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    public Task<string> InstallBrowserAsync() => Task.Run(() =>
    {
        var code = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        return code == 0 ? "Chromium installed." : $"Install failed (exit code {code}).";
    });

    public async ValueTask DisposeAsync()
    {
        _idleTimer.Dispose();
        foreach (var key in _profiles.Keys.ToList()) await CloseProfileAsync(key);
        _pw?.Dispose();
    }
}
