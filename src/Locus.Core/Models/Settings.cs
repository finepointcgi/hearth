namespace Locus.Core.Models;

public enum ProviderKind
{
    /// <summary>Ollama native API (http://host:11434).</summary>
    Ollama,
    /// <summary>Any OpenAI-compatible server: LM Studio, llama.cpp server, vLLM, Jan, LocalAI, OpenAI, OpenRouter, Groq…</summary>
    OpenAICompatible,
    /// <summary>Claude via the Anthropic API (API key from console.anthropic.com).</summary>
    Anthropic,
    /// <summary>Claude via the local Claude Code CLI, using your Claude subscription login (desktop only).</summary>
    ClaudeCode,
}

public sealed class ProviderProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Ollama";
    public ProviderKind Kind { get; set; } = ProviderKind.Ollama;
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string? ApiKey { get; set; }
    public string? DefaultModel { get; set; }
    /// <summary>Optional prices (USD per million tokens) for cloud servers, for cost estimates.</summary>
    public decimal? InputPricePerMillion { get; set; }
    public decimal? OutputPricePerMillion { get; set; }
    /// <summary>The models' context window in tokens (null = a default for the kind of server); long chats are
    /// summarised to fit.</summary>
    public int? ContextTokens { get; set; }
    /// <summary>More API keys for the same server: calls take turns and move on when one is rate-limited
    /// (encrypted at rest).</summary>
    public List<string> ExtraApiKeys { get; set; } = [];

    /// <summary>This server with a different API key (for key pools).</summary>
    public ProviderProfile WithKey(string key) => new()
    {
        Id = Id, Name = Name, Kind = Kind, BaseUrl = BaseUrl, ApiKey = key, DefaultModel = DefaultModel,
        InputPricePerMillion = InputPricePerMillion, OutputPricePerMillion = OutputPricePerMillion, ContextTokens = ContextTokens,
    };
}

public sealed class AppSettings
{
    public List<ProviderProfile> Providers { get; set; } =
    [
        new() { Name = "Ollama", Kind = ProviderKind.Ollama, BaseUrl = "http://localhost:11434" },
        new() { Name = "LM Studio", Kind = ProviderKind.OpenAICompatible, BaseUrl = "http://localhost:1234/v1" },
    ];
    public string? ActiveProviderId { get; set; }
    public string DefaultPersonaId { get; set; } = Personas.Default.Id;
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 2048;
    /// <summary>When a reply hits the length limit, ask the model to continue it (up to twice) instead of stopping mid-sentence.</summary>
    public bool ContinueCutOffReplies { get; set; } = true;
    /// <summary>Context window requested from Ollama (num_ctx). 0 = server default.</summary>
    public int ContextLength { get; set; } = 8192;
    public bool ShowThinking { get; set; } = true;
    /// <summary>Optional self-hosted SearXNG instance for the DeepSearch toggle, e.g. http://localhost:8080.</summary>
    public string? SearxngUrl { get; set; }
    public int SearchResultCount { get; set; } = 5;
    public string? CustomSystemPrompt { get; set; }
    /// <summary>Backup model servers, tried in order when the active one can't be reached.</summary>
    public List<string> FallbackProviderIds { get; set; } = [];
    /// <summary>Permission overrides keyed "area.read"/"area.write" (see PermissionCatalog).</summary>
    public Dictionary<string, PermissionLevel> Permissions { get; set; } = [];
    public bool AutoTitle { get; set; } = true;
    /// <summary>How the sidebar groups chats: "time", "person", "topic" or "project".</summary>
    public string ChatGroupBy { get; set; } = "time";

    public ToolSettings Tools { get; set; } = new();
    public bool MemoryEnabled { get; set; } = true;
    /// <summary>In plain chats, quietly pick out things worth remembering after each reply.</summary>
    public bool MemoryAutoLearn { get; set; } = true;
    /// <summary>Find memories and past chats by meaning, not just matching words (needs an embedding model).</summary>
    public bool RecallByMeaning { get; set; } = true;
    public SkillLearningSettings SkillLearning { get; set; } = new();
    public Services.HeartbeatSettings Heartbeat { get; set; } = new();
    public Services.NotificationSettings Notifications { get; set; } = new();
    public Services.ApiSettings Api { get; set; } = new();
    public List<Services.OutgoingWebhook> Webhooks { get; set; } = [];
    public Services.BackupSettings Backup { get; set; } = new();
    /// <summary>First-run setup finished (or skipped because Locus was already in use).</summary>
    public bool SetupDone { get; set; }
    /// <summary>Bots act on clear next steps without asking, schedule their own follow-ups and hand big jobs to tasks.</summary>
    public bool TakeInitiative { get; set; } = true;
    /// <summary>Other computers bots may run commands on over SSH.</summary>
    public List<Tools.RemoteHost> RemoteHosts { get; set; } = [];
    public DocumentSettings Documents { get; set; } = new();
    public ImageGenSettings ImageGen { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public List<McpServerConfig> McpServers { get; set; } = [];
    /// <summary>Max bot replies triggered by one user message in a group (stops bots looping).</summary>
    public int MaxBotHops { get; set; } = 8;
    public ComputerSettings Computer { get; set; } = new();
    public GoogleSettings Google { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public ProviderProfile ActiveProvider =>
        Providers.FirstOrDefault(p => p.Id == ActiveProviderId) ?? Providers.FirstOrDefault()
        ?? new ProviderProfile();

    /// <summary>The server a bot runs on: its own choice if that server still exists, otherwise the active one.</summary>
    public ProviderProfile ProviderFor(Bot? bot) =>
        bot?.ProviderId is { } id && Providers.FirstOrDefault(p => p.Id == id) is { } own ? own : ActiveProvider;
}

public sealed class ToolSettings
{
    /// <summary>After reading web pages, email and the like, ask before actions that change things.</summary>
    public bool GuardUntrustedContent { get; set; } = true;
    /// <summary>Default state of the Tools toggle in the composer.</summary>
    public bool EnabledByDefault { get; set; } = true;
    public List<string> DisabledTools { get; set; } = [];
    /// <summary>Skip the approval prompt for tools that can change things (Python, write-capable MCP tools).</summary>
    public bool AutoApproveAll { get; set; }
    public bool PythonEnabled { get; set; }
    public string PythonPath { get; set; } = "";
    public int MaxRounds { get; set; } = 12;
    /// <summary>Tool rounds per reply in code sessions (exploring and fixing code takes many steps).</summary>
    public int CodeMaxRounds { get; set; } = 60;
    public SandboxSettings Sandbox { get; set; } = new();
    /// <summary>How many helper agents (delegate_task) may work at the same time; 0 turns helpers off.</summary>
    public int MaxHelpers { get; set; } = 3;
    /// <summary>
    /// Offer each reply only the tools it's likely to need (core tools always; email, calendar, documents… when the
    /// conversation is about them; the rest on request). Much faster on local models with many integrations.
    /// </summary>
    public bool PickToolsPerMessage { get; set; } = true;
}

/// <summary>Where bots run code: a Docker container (no approval needed) instead of directly on this computer.</summary>
public sealed class SandboxSettings
{
    /// <summary>Run Python and commands in a container when Docker is installed.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Image for code (a project folder can use its own, e.g. mcr.microsoft.com/dotnet/sdk:10.0).</summary>
    public string Image { get; set; } = "python:3.12-slim";
    /// <summary>Internet access inside the sandbox (for pip/npm/nuget installs).</summary>
    public bool Network { get; set; } = true;
    public int MemoryMb { get; set; } = 2048;
}

public sealed class DocumentSettings
{
    /// <summary>Embedding model on the active provider, e.g. nomic-embed-text (ollama pull nomic-embed-text).</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public int TopK { get; set; } = 6;
    public int ChunkSize { get; set; } = 1200;
    /// <summary>Attached documents shorter than this are pasted into the message instead of indexed.</summary>
    public int InlineLimit { get; set; } = 16000;
}

public enum ImageBackend { None, Automatic1111, ComfyUI }

public sealed class ImageGenSettings
{
    public ImageBackend Backend { get; set; } = ImageBackend.None;
    /// <summary>A1111/Forge: http://localhost:7860 · ComfyUI: http://localhost:8188</summary>
    public string Url { get; set; } = "http://localhost:7860";
    /// <summary>Checkpoint file name (required for ComfyUI, optional for A1111).</summary>
    public string? Model { get; set; }
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public int Steps { get; set; } = 25;
    public double CfgScale { get; set; } = 6.5;
    public string NegativePrompt { get; set; } = "blurry, low quality, watermark, text";
}

public sealed class VoiceSettings
{
    /// <summary>OpenAI-compatible speech-to-text server (faster-whisper-server/speaches, LocalAI…). Empty = on-device recognition.</summary>
    public string? SttUrl { get; set; }
    public string SttModel { get; set; } = "Systran/faster-whisper-small";
    /// <summary>OpenAI-compatible text-to-speech server (Kokoro-FastAPI, LocalAI…). Empty = system voices.</summary>
    public string? TtsUrl { get; set; }
    public string TtsModel { get; set; } = "kokoro";
    public string TtsVoice { get; set; } = "af_heart";
    public bool AutoReadAloud { get; set; }
    public string Language { get; set; } = "en-US";
}

public enum McpTransport { Stdio, Http }

public sealed class McpServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "server";
    public McpTransport Transport { get; set; } = McpTransport.Stdio;
    public string Command { get; set; } = "";
    /// <summary>Space-separated; quote arguments that contain spaces.</summary>
    public string Arguments { get; set; } = "";
    public Dictionary<string, string> Environment { get; set; } = [];
    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = [];
    public bool Enabled { get; set; } = true;
    /// <summary>Run this server's tools without asking.</summary>
    public bool AutoApprove { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public string Fingerprint => System.Text.Json.JsonSerializer.Serialize(this);
}

/// <summary>The bots' shared computer: a persistent browser profile plus a workspace folder.</summary>
public sealed class ComputerSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Run the browser without a window. Use "Show browser" on the Computer page to sign in to sites.</summary>
    public bool Headless { get; set; } = true;
    /// <summary>"" = Playwright's Chromium; "chrome" or "msedge" = your installed browser.</summary>
    public string Channel { get; set; } = "";
    /// <summary>Let bots click and type in the browser without asking each time.</summary>
    public bool AutoApproveActions { get; set; }
    /// <summary>Refuse to type into password / payment fields (off: bots may, e.g. to log in for you).</summary>
    public bool ProtectSensitiveFields { get; set; }
    /// <summary>Let bots see the screen and control the mouse and keyboard.</summary>
    public bool DesktopControl { get; set; }
    /// <summary>Let bots control the desktop without asking before each action.</summary>
    public bool AutoApproveDesktop { get; set; }
    /// <summary>Allow the run_command tool (shell commands in the workspace, always asks first).</summary>
    public bool AllowCommands { get; set; }
    public int IdleMinutes { get; set; } = 15;
}

public sealed class GoogleSettings
{
    /// <summary>Offer the Google Sheets tools to bots (once a Google account is connected).</summary>
    public bool SheetsEnabled { get; set; } = true;
    /// <summary>Offer Gmail tools (search, read, draft, send, label) once Google is connected with Gmail access.</summary>
    public bool GmailEnabled { get; set; }
    /// <summary>Offer Google Calendar tools once connected with Calendar access.</summary>
    public bool CalendarEnabled { get; set; }
    /// <summary>Let bots send email without asking (off: every send asks, even with other auto-approvals on).</summary>
    public bool AutoApproveSend { get; set; }
    /// <summary>Let bots change sheets/labels/events (write, append, create, clear) without asking each time.</summary>
    public bool AutoApproveEdits { get; set; }
    /// <summary>Testing only: send all Google API calls to this base URL instead of Google.</summary>
    public string? ApiBaseOverride { get; set; }
}
