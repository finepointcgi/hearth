# Locus — a private, local-LLM chat assistant

A Grok-style assistant (witty personas, Think mode, DeepSearch, **teams of bots that talk to each other**,
tools, MCP, memory, documents, image generation, voice) that runs on **your own models** via Ollama,
LM Studio, llama.cpp, vLLM, Jan, LocalAI — or on **Claude** (through Claude Code or the Anthropic API) and any
OpenAI-compatible endpoint (OpenAI, OpenRouter, Groq…). Written in C# / Blazor with one shared UI across every platform.

| Platform | Project | Tech |
|---|---|---|
| Windows, macOS, iOS, Android | `src/Locus.App` | .NET MAUI Blazor Hybrid |
| Linux (also Win/Mac) | `src/Locus.Desktop` | Photino.Blazor native window |
| Any browser / home server | `src/Locus.Web` | Blazor Server (self-hosted) |

```
src/Locus.Core     LLM clients (Ollama, OpenAI-compatible, Claude API, Claude Code), agent/tool loop, tools, MCP, RAG, storage
src/Locus.UI       Shared Razor components, CSS, JS — every host renders this
src/Locus.Computer The bots' browser (Playwright) — desktop and web hosts only
tests/             Unit tests
```

## Features
- Streaming replies with tokens/sec stats, stop, regenerate, edit-and-resend
- **Think** toggle: native Ollama `think` + `<think>` tag / `reasoning_content` parsing, collapsible thought process
- **DeepSearch**: optional, via your own SearXNG instance, with cited source chips
- **Personas**: Regular, Fun (sarcastic/irreverent), Concise, Coder, Tutor + custom instructions
- Image attachments for vision models (llava, gemma3, qwen2.5vl, llama3.2-vision…), downscaled on device
- Chat history saved as JSON on the device, search, pin, rename, auto-titles, copy chat as Markdown
- Multiple server profiles (switch between your PC's Ollama, a GPU box's vLLM, etc.)
- Markdown with tables and code blocks with copy buttons; model HTML and non-http links are blocked
- Dark/light theme, mobile drawer layout, safe-area aware

## Bots (AI teammates)
Inspired by xAI's Grok Bot, rebuilt for local models. Open **Bots** in the sidebar (or **✨ Add starter team**).

- **A bot** has a name, emoji avatar and colour, a role, its own instructions, an optional model override,
  which tools it may use, its own memory, and routines. Up to 50 bots.
- **1:1 chats**: click a bot in the sidebar.
- **Group chats** (2–6 bots): you write normally and Locus picks who answers (a quick routing call to the
  model), or you address someone with `@Name` / `@everyone`. Bots hand work to each other by @mentioning
  a teammate, who replies next. At most 8 bot replies per message (Settings → `MaxBotHops`) and two per
  bot, so they can't loop forever.
- **Bot-to-bot messages**: with tools on, a bot can `message_bot` any teammate. The message and reply
  appear in the recipient's own chat (marked unread) and come back to the sender as a tool result.
- **Coordinator / "chief of staff"**: flag a bot as coordinator and it splits requests, delegates to
  specialists and combines their results.
- **Abilities**: each bot has simple on/off switches — web search, browser, computer use, files, code,
  images, documents, skills, teamwork (messaging other bots) and MCP tools. A switch that also needs
  setting up elsewhere (e.g. a SearXNG URL) shows a hint linking there.
- **Its own browser**: by default every bot gets a separate browser profile with its own cookies and
  sign-ins (**🔑 Open its browser to sign in** in the bot editor), or it can use the shared one.
- **Its own memory**: every bot remembers across all its chats. With **Learn automatically** on, after
  each reply it quietly saves anything worth keeping and shows "🧠 Remembered: …" under the reply. Facts
  about *you* can also go to the shared memory so other bots know them (both switchable per bot). You
  can add, edit, search and clear a bot's memory in its editor; older memories beyond the prompt limit
  are found with the `recall_memory` tool.
- **Routines & triggers**: prompts a bot runs on its own. Results land in its chat as unread, with the
  triggering event attached. Tools needing approval are skipped when nobody is watching.

  | Trigger | Fires when | Where |
  |---|---|---|
  | ⏰ Schedule | every N minutes, daily or weekdays at a time | everywhere |
  | 🪝 Webhook | something POSTs to `/hooks/{id}?token=SECRET` (GitHub, Home Assistant, IFTTT, cron, scripts) | web host; desktop on `http://localhost:5299` |
  | 📁 Folder | a file matching a pattern is added/changed (PDF, text and code are read) | desktop / web with local access |
  | 📰 Feed | a new item appears in an RSS/Atom feed (GitHub releases, blogs, YouTube, Reddit…) | everywhere |
  | 🌐 Page change | a web page's readable text changes (the diff is passed along) | everywhere |

  Webhooks need the routine's secret (`?token=` or an `X-Locus-Token` header); a wrong one gets 403.
  Feeds and pages record a baseline on the first check, so you only hear about *new* things.
  **▶ Test run / Check now** fires one immediately. Triggers run while the app is open; the web host
  runs them whenever the server is up.

**Parallel helpers**: a coordinator can message several bots in one turn; they work at the same time.

## Bot computer
Like Grok Bot's computer, but on your machine: **🖥 Computer** in the sidebar.

- **Shared browser**: one persistent Chromium profile used by every bot (one tab each). Click
  **🔑 Show browser to sign in**, log in to the sites your bots should use, and the sign-ins stick.
- **Browser tools**: `browser_open`, `browser_snapshot`, `browser_scroll`, `browser_back` and
  `browser_screenshot` run freely; `browser_click` and `browser_type` ask first (unless you turn on
  auto-approve). Pages are described to the model as text plus numbered elements, so it works with
  non-vision models. Bots can type into password fields (turn on *Protect password and payment fields* to block that).
- **Live view** of what the bot's browser is showing, refreshed every 1.5 s.
- **Workspace folder**: `list_files`, `read_file`, `write_file` (sandboxed to the folder), browser
  downloads land in `workspace/downloads`, and optional `run_command` (shell, asks every time).
- Runs headless and closes after 15 idle minutes. First use needs a browser: click **Install browser**
  (Chromium, ~150 MB) or choose your installed Chrome/Edge in the Computer settings.
- Available in the desktop app and the web host with `Locus:AllowLocalProcesses`; not on iOS/Android
  (workspace file tools still work there).

### Computer use (desktop control)
Turn on **Computer → Computer use** to let bots see the screen and drive the mouse and keyboard in any app: `desktop_screenshot`, `desktop_click`, `desktop_type`, `desktop_key`, `desktop_scroll`, `desktop_drag`, `desktop_open`. Every action returns a fresh screenshot that vision models see (`qwen2.5vl`, `gemma3`, `llama3.2-vision`…). Actions ask first unless you turn on auto-approve. Fail-safe: park the mouse in the top-left corner and the next action is refused; ■ in the chat stops the run.
- macOS: grant Screen Recording + Accessibility to the app running Locus (there's a "Request permissions" button).
- Windows: works out of the box.
- Linux: X11 + `xdotool` + a screenshot tool (`scrot`, `gnome-screenshot` or ImageMagick).

### Sandboxed computer (one per bot)
In a bot's settings, turn on **Computer use** and pick **Its own sandbox**. The bot then gets a private Linux
desktop (Chromium, a terminal) in a Docker container instead of your screen. It uses the same `desktop_*` tools,
but it can't see your screen or files, so its actions don't ask first (typing a saved login still does).
- Needs Docker Desktop. The image (`locus-desktop:2`) builds automatically the first time; that takes a few minutes.
- The bot's editor shows a **live view** (noVNC on 127.0.0.1 behind a random password). You can click and type
  in it too, e.g. to sign in to a site for the bot. Its home folder (and sign-ins) persists in a Docker volume.
- It stops after the Computer page's idle time and starts again on demand. **Reset** (or deleting the bot) removes
  the container and its files.

### Remote computers (SSH)
**Computer → Remote computers**: add `user@host` (or an `~/.ssh/config` alias) with a note on what it is. Bots with the
**Code** ability get `run_remote` and can run commands there. Locus uses your SSH keys only (never a password prompt),
asks before each command by default (🛡 Permissions → Remote computers), and **Test** checks the connection.

### Saved logins
On the Computer page, add logins (name, site, username, password). Bots sign in with `browser_fill_login` (bot browser) or `desktop_type_login` (any app) without the password ever reaching the model or the chat. Passwords are encrypted with AES-GCM using a key file in the app data folder (owner-only permissions), which keeps them out of chats and logs but doesn't protect them from someone who can read your user account's files.

## Always on
**Background service** — keeps Locus running after you close the terminal, starts at login, restarts if it
crashes. Without a password it only listens on this computer; with `LOCUS_PASSWORD` set, other devices on your
network can reach it (and must sign in).
```bash
scripts/install-service-macos.sh                 # macOS LaunchAgent  (--uninstall, --dry-run)
scripts/install-service-linux.sh                 # systemd user unit  (--uninstall, --dry-run)
.\scripts\install-service-windows.ps1            # Windows logon task (-Uninstall)
LOCUS_PASSWORD=pick-one LOCUS_PORT=5290 scripts/install-service-macos.sh
```
**Docker** — the easiest way to run Locus on a server or NAS, next to your models:
```bash
LOCUS_PASSWORD=pick-one docker compose up -d     # → http://<server>:8080
```
The image includes Chromium (bot browser) and Python (Code ability); data lives in the `locus-data` volume
(`LOCUS_DATA_DIR=/data`). Model servers on the Docker host are reachable at `http://host.docker.internal:<port>`.
Desktop control isn't available inside a container.

**Backup model servers** — Settings → *Backup servers*: if the active server can't be reached, Locus retries
with the next one (using its default model) and says so in the reply. Connecting to a dead server now gives up
after 10 seconds.

**Health alerts** — Locus checks the model server every 2 minutes and messages your linked Telegram/Discord
chats when it goes down or comes back (also on the Today dashboard). `GET /health` reports status for Docker or
uptime monitors.

**Password** — set `Locus:Password` (env `Locus__Password`) to require a sign-in for the web UI. `/health` and
the token-protected `/hooks/…` webhooks stay open.

## Tasks (long-running work)
**🎯 Tasks → New task**, or `/task <goal>` from Telegram/Discord/Slack. The bot plans the work
(`task_plan`), works through it in the background in cycles (`task_step`), and finishes with a report
(`task_done`). It survives restarts. When it needs a decision it asks (`task_ask`); answer on the task page
or with `/answer`. Milestones are pushed to the chat that started the task. Pause, resume or cancel any time;
each task has a cycle budget so it can't run forever.

## Permissions (🛡)
One page to decide what bots may do, per area (web, browser, desktop, files, code, Sheets, Gmail,
sending email, calendar, memory, teamwork, bot inbox, each MCP server…), separately for reading and
changing: **Allow** (no prompt), **Ask**, **Block** (the tool is hidden from bots) or the default.
**Careful** sets everything that changes things to Ask. Every tool call is written to an **audit log**
(filter by bot, text and days).

## Phone app
Open Locus in your phone's browser and **Add to Home Screen**: it installs as an app (PWA). Connections →
**Phone** shows QR codes for your local network and for [Tailscale](https://tailscale.com) (use it from
anywhere without opening ports). Set `LOCUS_PASSWORD` first; the page warns if you haven't.

## Heartbeat (proactive check-ins)
**📊 Today → 💓 Heartbeat.** Every 15 min – 4 h, Locus (or a bot you pick) goes through a checklist you write —
"anything due today, important email, finished tasks, things I asked you to watch" — using its tools, and messages you
(here and on your linked chats) **only when something needs your attention**. Quiet check-ins leave no trace; quiet hours
are respected; **Check in now** runs one immediately.

## Today dashboard
**📊 Today** in the sidebar: what needs your OK (approve or deny right there), problems (failed
routines and triggers, MCP servers that won't connect, no model), unread bot chats, each bot's replies
today and next scheduled routine, and a live activity feed. The sidebar shows a red badge when
something is waiting on you.

## Telegram & Discord
**🔗 Connections → Telegram / Discord.** Paste a bot token (setup steps are on the page), click
**Get pairing code** and send the code to your bot — only paired chats (and only the person who paired)
can talk to Locus.
- Just message it — or send a **photo** (vision models see it), a **file** (PDF/text/code is read; big ones go
  to Documents) or a **voice note** (transcribed via your speech-to-text server in Settings → Voice).
- `/bots`, `/bot Name` (switch who answers), `/groups` + `/group Name` (talk to a group of bots — each reply is
  labelled), `@Name …` (ask one bot once), `/new`, `/stop`, `/notify on|off`.
- Every reply has **🔄 Again · ✂️ Shorter · ➕ More** buttons.
- Tool approvals arrive as **Allow / Deny** buttons — including approvals from chats on your computer,
  so you can OK things when you're away.
- Routine results are pushed to linked chats.
- Telegram uses long polling and Discord the gateway, so no public URL or port forwarding is needed.
  Discord needs **Message Content Intent** turned on in the Developer Portal.

## Slack
**Connections → Slack.** Create the app from the manifest on the page (one paste), install it, then paste
the **bot token** (`xoxb-…`) and an **app-level token** (`xapp-…`, scope `connections:write`). It uses Socket
Mode, so no public URL is needed. Pair with a code like Telegram; then DM the app or @mention it in a channel.
Buttons, approvals, images and files work the same way.

## Bot inbox (email)
**Connections → Bot inbox** gives a bot its own email address (use a separate mailbox; for Gmail, an
app password with imap/smtp.gmail.com). Only **allowed senders** (addresses or `@domain.com`) get answers;
everything else is marked read and ignored. Each email thread is one conversation. Replies wait for your OK
(on the dashboard, Telegram…) unless you set **Bot inbox → change** to Allow under 🛡 Permissions.

## iMessage & Signal
- **iMessage** (Locus on a Mac): sign the Mac's Messages app in to its own Apple ID, give the app running Locus
  **Full Disk Access** (it reads new messages from the Messages database), then Connections → iMessage → **Turn on**
  and pair from your phone. Replies go out through Messages.
- **Signal**: run the bridge (`docker compose --profile signal up -d`, i.e. `bbernhard/signal-cli-rest-api`), link a
  Signal account to it by QR code, then enter the bridge address and number under Connections → Signal.
- Neither has buttons, so choices arrive as "Reply 1 Allow · 2 Deny" — answer with the number or the word.

## Gmail & Google Calendar
Turn on **Gmail** and/or **Calendar** under Connections → Google, then **Reconnect to grant access**.
- Gmail tools: `gmail_search` (Gmail search syntax), `gmail_read`, `gmail_draft` (replies stay in the
  thread), `gmail_send`, `gmail_organize` (archive, read/unread, star, label). **Sending always asks**
  unless you separately allow it. Each bot has a Gmail switch (off by default).
- Calendar tools: `calendar_events`, `calendar_create` (invites attendees), `calendar_delete`.
- Triggers: **"New email arrives"** (any Gmail search) and **"A calendar event is coming up"** (N
  minutes before — e.g. meeting prep).
- Gmail needs "Sign in with Google" (service accounts can't read a personal inbox).

## Show-and-learn
**⚡ Skills → 🎬 Teach by showing.** Pick a browser (shared or a bot's own), click **Start recording**, do
the task once in the window that opens, then **Stop**. Locus records clicks, typed text, choices, Enter
and page changes (passwords are never recorded), lets you remove mistakes, and writes a reusable skill
from it — using your model to turn specific values into placeholders, or a plain step list if no model
is available. Bots can then run it (`use_skill`) or you can type `/skill-name`.

## Google Sheets
**🔗 Connections → Google Sheets.** Two ways to connect:
- **Sign in with Google** (recommended on desktop): create a free "Desktop app" OAuth client in your own
  Google Cloud project (the page walks you through it, ~3 minutes), paste its ID and secret, click
  **Sign in with Google**. Bots can then use any sheet your account can open.
- **Service account key** (works on phones too): paste a service-account JSON key, then share each
  spreadsheet with the service account's email.

Tokens and keys are encrypted on disk with the same local key as saved logins.

Bots get `sheets_find`, `sheets_info`, `sheets_read` (tables with real row numbers and column letters),
`sheets_write`, `sheets_append`, `sheets_create`, `sheets_add_tab` and `sheets_clear`. Spreadsheets can be
referred to by URL, ID or name; values are entered as if typed, so formulas work. Reading never asks;
changes ask first unless you allow them on the Connections page. Each bot has a **Google Sheets** switch.

**Trigger:** give a bot a routine with **"A Google Sheet changes"** (spreadsheet + optional range, checked
every N minutes) and it receives new or edited rows with the header — e.g. "for each new lead, research
the company and fill in column F".

## Skills
Reusable prompts shared by you and every bot (**⚡ Skills** in the sidebar; 8 templates included:
summarize, action-items, proofread, explain-code, translate, eli5, daily-briefing, triage).
- Type `/` in any chat to pick one; whatever follows the name is its input (`/summarize <notes>`).
- Routines can use them too: a webhook routine whose prompt is just `/triage`.
- With tools on, bots see the list and load a skill themselves (`use_skill`) when it fits.
- Click ⚡ under any message you sent to turn it into a skill.

### Bots write their own skills
Like Hermes, bots learn from experience (on by default; **Skills → Bots write their own skills**):
- After a reply that took several tool calls, Locus asks the model whether the method is worth keeping. If so it saves
  it as a skill (or fixes the skill it used, if the steps turned out wrong). Bots can also do this themselves
  with `save_skill` and `improve_skill`.
- Every bot sees the saved skills and is told to check them before working something out from scratch; each skill
  shows who wrote it and how often it's been used.
- Every change keeps the previous version: **↶** undoes it. Turn on **Let me review them first** to approve new
  and changed skills before bots can use them. The 🛡 Permissions page has a **Skills** row too.

### Portable skills (SKILL.md)
Skills use the open `SKILL.md` format (frontmatter `name` / `description`, then Markdown instructions, plus any bundled
files), the same one Claude, Hermes and OpenClaw use. **Skills → Import & export**: paste a GitHub folder or file link
(a single skill or a folder of skills), a raw SKILL.md link or a .zip link, or upload a .md/.zip. Imported skills wait for
your review, and the import scan flags risky content (download-and-run commands, hidden base64, sending keys somewhere,
"ignore previous instructions", invisible characters, bundled scripts). **Export all** downloads a .zip, and approved
skills are kept as files in the `skills` folder in Locus's data folder, where other agents can read them.

Small models get confused in group chats; `qwen3:8b`+ or similar works much better.

## Tools (agent mode)
Toggle **🛠 Tools** in the composer. The model calls tools, Locus runs them and feeds results back
(up to 8 rounds). Each call shows as an expandable card; anything that can change things or run code
asks **Allow / Deny** first (configurable in **Tools & MCP**).

| Tool | What it does | Needs |
|---|---|---|
| 🧮 calculate | Exact math (safe parser, no code exec) | — |
| 🕒 get_current_datetime | Date/time in any time zone | — |
| 📏 convert_units | Length, mass, volume, temp, speed, data… | — |
| 🔎 web_search | Search with numbered citations | SearXNG URL |
| 🌐 fetch_url | Read a web page or PDF as text | — |
| 📚 search_documents | Semantic search over your library | documents + embedding model |
| 🧠 save_memory / delete_memory | Long-term facts about you | Memory on |
| 🎨 generate_image | Local Stable Diffusion | A1111/Forge or ComfyUI |
| 🐍 run_python | Runs Python on your computer (asks first) | desktop, opt-in |
| 🔌 MCP tools | Anything from MCP servers | see below |

Tool calling needs a model that supports it: `qwen3`, `llama3.1`/`3.2`, `mistral-small`, `gpt-oss`…
Ollama models without support fall back to plain chat automatically.

**DeepSearch** with tools on = multi-step research (several searches, reads pages, cites [n]).

### MCP servers
**Tools & MCP → Add server** or **Import JSON** (Claude Desktop / Cursor `mcpServers` format):
```json
{ "mcpServers": {
  "files": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/Users/me/Documents"] },
  "remote": { "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer …" } }
} }
```
Local (stdio) servers run on desktop (Photino, Windows, Mac). iOS/Android use HTTP MCP servers.
The web host disables local processes unless you set `"Locus": { "AllowLocalProcesses": true }`.

### Documents (RAG)
`ollama pull nomic-embed-text`, then add PDFs/notes/code on the **Documents** page. Small files attached
in the composer are pasted into the message; large ones are indexed automatically.

### Memory
Everything Locus remembers lives in **one SQLite database** (`locus.db` in the data folder): shared facts about you,
each bot's own notes (kept apart by a scope column), your profile, and a search index of every chat. Facts and chats
are found by **keywords and by meaning** ("my car" finds "the Subaru loan"): on desktop and the web host a small
built-in embedding model (all-MiniLM-L6-v2, ~23 MB, downloaded once, runs on the CPU) handles that. On phones, or if
it can't download, Locus uses your server's embedding model (Settings → Documents) or falls back to keywords. Memory → Recall by meaning turns it off; **Export** saves everything as JSON. Storage sits behind an `IMemoryProvider`
interface, so a different backend (e.g. a hosted memory service) can replace it. Older JSON memory files are imported
once and kept as `*.before-sqlite.bak`.
- **Your profile**: a short summary of you that every chat starts with (Memory page). **🧹 Tidy up now** (and
  automatically after ~15 new facts) suggests an updated profile, merges duplicate facts and drops outdated ones — you
  untick what to keep and apply.
- **Past chats**: bots search earlier conversations with `search_past_chats` / `read_past_chat` when you refer to
  something from before (a bot only sees chats it was part of).
The model saves facts with `save_memory`; view, edit or wipe them on the **Memory** page.

### Image generation
Settings → Image generation: AUTOMATIC1111/Forge (`--api`, port 7860) or ComfyUI (port 8188, pick a
checkpoint). Ask for a picture, or type `/imagine a lighthouse at dusk, oil painting`.

### Voice
- 🎙 Dictation: native on-device recognition in the MAUI apps; Web Speech API in browsers. For private,
  high-quality transcription everywhere set an OpenAI-compatible Whisper server (e.g. speaches).
- 🔊 Read-aloud: system voices, or an OpenAI-compatible TTS server (e.g. Kokoro-FastAPI on port 8880).
- 🎧 **Live conversation**: the headphones button next to the mic. Talk hands-free: it listens, notices when
  you stop talking, answers out loud, and listens again. Tap the circle to interrupt; **End** to hang up.
  With a speech-to-text server it records and transcribes; otherwise it uses the browser's recognition.
  For fully local voices in Docker: `docker compose --profile voice up -d`, then set
  `http://speaches:8000` (speech-to-text) and `http://kokoro:8880` (text-to-speech).

## Providers
Add them in **Settings → Model servers** (one-click buttons for each). You can keep several and switch the
active one. Each bot can also run on **its own model server** with its own model (bot page → Model), e.g. a
writer on Claude and everyone else on your local oMLX. If a bot's server can't be reached it falls back to the
active server, then your backup servers. Bots on different servers can still message each other.

| Provider | Address | Key | Notes |
|---|---|---|---|
| Ollama | `http://localhost:11434` | — | local; best for Documents embeddings |
| LM Studio / llama.cpp / vLLM / Jan / LocalAI | `http://localhost:1234/v1` etc. | optional | OpenAI-compatible |
| **Claude Code** | blank (auto-detects `claude`) or a path | — | uses your Claude Code login (Claude subscription). Desktop / web-with-local-access only |
| **Claude API** | `https://api.anthropic.com` | Anthropic API key | official Anthropic SDK; pay per use |
| OpenAI | `https://api.openai.com/v1` | `sk-…` | |
| OpenRouter | `https://openrouter.ai/api/v1` | OpenRouter key | hundreds of models |
| Groq | `https://api.groq.com/openai/v1` | Groq key | |
| Any OpenAI-compatible | its base URL | if needed | Together, Mistral, Fireworks, DeepSeek… |

- **Claude Code**: run `claude` in a terminal once and sign in (`/login`); Locus reuses that login. Each reply
  runs `claude -p` in a separate empty folder with Claude Code's own tools, MCP servers and settings switched
  off, so only Locus's tools are used (offered through a small `<tool_calls>` text protocol, with the usual
  approval cards). Models: `opus`, `sonnet`, `haiku` (latest of each) or full IDs. Images and thinking work;
  each reply starts the CLI, so it's a bit slower than the API.
- **Claude API**: streams through the official Anthropic C# SDK with adaptive thinking (summaries shown under
  "Thought process"; **Think** raises effort to high), native tool calling, images, and server-side refusal
  fallbacks (`fallbacks: "default"`), so a request a safety classifier declines is re-served on a suitable model
  instead of stopping.
- **OpenAI-compatible cloud APIs**: if a model rejects `max_tokens` or a custom temperature, Locus retries with
  `max_completion_tokens` / the default temperature automatically.
- Claude can't make embeddings: while a Claude provider is active, **Documents** uses your first local
  (Ollama/OpenAI-compatible) provider for them.
- Cloud providers send your messages (and tool results) to that company; local providers keep everything on
  your machine.

## 1. Run a model server
```bash
# https://ollama.com
ollama pull llama3.2          # small & fast
ollama pull qwen3:8b          # Think mode + tool calling (recommended)
ollama pull nomic-embed-text  # for Documents
ollama pull gemma3:4b         # supports images
```
Phones/tablets need to reach your computer over the LAN:
```bash
OLLAMA_HOST=0.0.0.0 ollama serve
```
then in **Settings** set the URL to `http://<your-computer-LAN-IP>:11434`
(Android emulator: `http://10.0.2.2:11434`). In LM Studio enable "Serve on local network".

## 2. Run the app

**Web (quickest, any OS):**
```bash
dotnet run --project src/Locus.Web
```

**Linux / Windows / macOS desktop (Photino):**
```bash
dotnet run --project src/Locus.Desktop
```
Linux needs WebKitGTK: `sudo apt install libwebkit2gtk-4.1-0` (Fedora: `webkit2gtk4.1`).

**MAUI (iOS, Android, Windows, macOS):** install the workload once:
```bash
sudo dotnet workload install maui
```
- Android: `dotnet build src/Locus.App -t:Run -f net10.0-android` (needs Android SDK + JDK 17)
- iOS simulator / Mac: needs full Xcode. `dotnet build src/Locus.App -t:Run -f net10.0-ios` / `-f net10.0-maccatalyst`
- Windows: `dotnet build src/Locus.App -t:Run -f net10.0-windows10.0.19041.0`

## DeepSearch setup (optional)
```bash
docker run -d -p 8080:8080 searxng/searxng
```
Enable JSON in its `settings.yml` (`search: formats: [html, json]`), then set `http://localhost:8080` in Settings.

## Notes
- Data lives in the app data folder (`~/.local/share/Locus`, `%LOCALAPPDATA%\Locus`, or the app sandbox on mobile).
- The web host is single-user (one shared chat history); keep it on localhost or behind your own auth.
- `fetch_url` and MCP HTTP servers make requests from wherever Locus runs (the server, for the web host).
- Cleartext HTTP is allowed on Android/iOS so LAN servers work; use HTTPS if you expose a server beyond your LAN.
