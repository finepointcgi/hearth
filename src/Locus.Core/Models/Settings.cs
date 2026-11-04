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

    public ToolSettings Tools { get; set; } = new();
    public bool MemoryEnabled { get; set; } = true;
    /// <summary>In plain chats, quietly pick out things worth remembering after each reply.</summary>
    public bool MemoryAutoLearn { get; set; } = true;
    /// <summary>Find memories and past chats by meaning, not just matching words (needs an embedding model).</summary>
    public bool RecallByMeaning { get; set; } = true;
    public SkillLearningSettings SkillLearning { get; set; } = new();
    public Services.HeartbeatSettings Heartbeat { get; set; } = new();
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
    public int MaxRounds { get; set; } = 8;
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
