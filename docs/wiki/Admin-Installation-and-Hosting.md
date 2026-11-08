# Installation and hosting

This page is for whoever installs and runs Hearth. For a first look, [Getting started](Getting-Started.md) is enough.

## Which host to run

Hearth has one shared interface (`src/Hearth.UI`) and several hosts:

| Host | Project | Use it for | Reachable from a phone? | Local tools* |
|---|---|---|---|---|
| **Web** (Blazor Server) | `src/Hearth.Web` | Background service, Docker, home server. **Recommended.** | Yes | With `AllowLocalProcesses` |
| **Desktop** (Photino window) | `src/Hearth.Desktop` | A native window on Linux, Windows or macOS | No | Yes |
| **MAUI app** | `src/Hearth.App` | Native iOS, Android, Windows, macOS | — | Desktop platforms only |

\* *Local tools* = the bot browser, desktop control, Python and shell, code sessions, other agents, local (stdio) MCP
servers, folder triggers, linked folders.

The web host is the one that runs routines around the clock, receives webhooks from other machines, serves the phone
app and the API. Most installs should use it, as a service or in Docker.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (not needed for Docker).
- A model server, local or cloud. See [Model servers](Admin-Model-Servers.md).
- Optional: Docker Desktop (sandboxes, voice, Signal, ntfy), Node.js (`npx`-based MCP servers), Python 3 (the Python
  tool).
- Linux desktop host: WebKitGTK — `sudo apt install libwebkit2gtk-4.1-0` (Fedora: `webkit2gtk4.1`).

## Run it once (to try)

```bash
dotnet run --project src/Hearth.Web          # http://localhost:5291
dotnet run --project src/Hearth.Desktop      # native window
```

To enable local tools when running the web host by hand:

```bash
dotnet run --project src/Hearth.Web -- --Hearth:AllowLocalProcesses=true
```

## Background service (always on)

Keeps Hearth running after you close the terminal, starts at login, and restarts it if it crashes. The scripts
publish a release build to an app folder and register it with the OS.

```bash
scripts/install-service-macos.sh                 # macOS LaunchAgent
scripts/install-service-linux.sh                 # systemd user unit
```

```powershell
.\scripts\install-service-windows.ps1            # Windows logon task
```

| | macOS | Linux | Windows |
|---|---|---|---|
| Installs to | `~/.hearth/app` | `~/.hearth/app` | `%LOCALAPPDATA%\Hearth-app` |
| Registered as | LaunchAgent `com.hearth.web` | `~/.config/systemd/user/hearth.service` | Scheduled task `Hearth` |
| Logs | `~/Library/Logs/Hearth/hearth.log` | `journalctl --user -u hearth` | Task Scheduler history |
| Remove | `--uninstall` | `--uninstall` | `-Uninstall` |
| Preview | `--dry-run` | `--dry-run` | — |

Options (environment variables for macOS/Linux, parameters for Windows):

| Option | Default | Meaning |
|---|---|---|
| `HEARTH_PORT` / `-Port` | `5290` | Port to listen on |
| `HEARTH_PASSWORD` / `-Password` | none | Require a sign-in. **Also makes Hearth listen on your network** (0.0.0.0); without it, only this computer can connect (127.0.0.1). |
| `HEARTH_LOCAL_TOOLS=0` / `-NoLocalTools` | on | Turn off local tools (Python, browser, stdio MCP…) |

Example: reachable from other devices, with a password:

```bash
HEARTH_PASSWORD=pick-one HEARTH_PORT=5290 scripts/install-service-macos.sh
```

**To update** after pulling new code, run the same install script again. Your data is not touched by install, update
or uninstall.

## Docker

The easiest way to run Hearth on a server or NAS, next to your models.

```bash
HEARTH_PASSWORD=pick-one docker compose up -d     # → http://<server>:8080
```

- The image includes Chromium (bot browser) and Python (Code ability), and has local tools turned on.
- Data lives in the `hearth-data` volume, mounted at `/data` (`HEARTH_DATA_DIR=/data`).
- Set `TZ` (e.g. `TZ=Europe/London`) so routine times are right.
- Model servers on the Docker host are reachable at `http://host.docker.internal:<port>`, e.g.
  `http://host.docker.internal:11434` for Ollama.
- `shm_size: 1gb` is set because Chromium needs more shared memory than Docker's default.
- Desktop control (computer use on the host's screen) isn't available inside a container.

Optional companion services in the same `docker-compose.yml`, enabled with profiles:

| Profile | Service | Use |
|---|---|---|
| `voice` | speaches (speech-to-text), kokoro (text-to-speech) | Local voices: `http://speaches:8000`, `http://kokoro:8880` |
| `signal` | signal-cli-rest-api on `127.0.0.1:8081` | [Signal](Chat-Apps.md#signal) |
| `ntfy` | ntfy on port 8082 | Private [phone notifications](Phone-and-Notifications.md#phone-notifications-ntfy) |

```bash
docker compose --profile voice --profile ntfy up -d
```

## Native apps (MAUI)

Install the workload once:

```bash
sudo dotnet workload install maui
```

| Target | Command | Needs |
|---|---|---|
| Android | `dotnet build src/Hearth.App -t:Run -f net10.0-android` | Android SDK + JDK 17 |
| iOS simulator | `dotnet build src/Hearth.App -t:Run -f net10.0-ios` | Full Xcode |
| Mac (Catalyst) | `dotnet build src/Hearth.App -t:Run -f net10.0-maccatalyst` | Full Xcode |
| Windows | `dotnet build src/Hearth.App -t:Run -f net10.0-windows10.0.19041.0` | — |

A phone app talking straight to a model server on your PC needs that server to listen on the network, e.g.
`OLLAMA_HOST=0.0.0.0 ollama serve`, then use `http://<your-PC's-LAN-IP>:11434` (Android emulator:
`http://10.0.2.2:11434`). In LM Studio, turn on *Serve on local network*. Cleartext HTTP is allowed so LAN servers
work; use HTTPS beyond your LAN.

## Configuration reference

| Setting | How to set it | Meaning |
|---|---|---|
| `Hearth:Password` | env `Hearth__Password`, or `HEARTH_PASSWORD` for the scripts / compose | Single sign-in password for the web UI |
| `Hearth:AllowLocalProcesses` | `--Hearth:AllowLocalProcesses=true` or env `Hearth__AllowLocalProcesses=true` | Enables local tools on the web host <a id="local-access"></a> |
| `HEARTH_DATA_DIR` | env | Where data is stored (overrides the default) |
| `--urls` | command line, or `ASPNETCORE_URLS` | Address and port to listen on |
| `TZ` | env | Time zone for routines (Docker) |

Older `LOCUS_*` variables and `Locus:` settings keys (from before the rename) are still read.

Everything else is set in the app and stored in `settings.json` in the data folder.

## The data folder

| Platform | Default location |
|---|---|
| macOS | `~/Library/Application Support/Hearth` |
| Linux | `~/.local/share/Hearth` |
| Windows | `%LOCALAPPDATA%\Hearth` |
| Docker | `/data` (volume `hearth-data`) |
| iOS / Android | The app's sandbox |

What's inside:

| Path | Contents |
|---|---|
| `settings.json` | All settings. API keys and MCP secrets in it are encrypted. |
| `vault.key` | The local encryption key (owner-only permissions). **Keep it with your backups.** |
| `hearth.db` | Memory, profile, people notes, chat search index (SQLite) |
| `usage.db` | Token and cost records |
| `bots.json`, `skills.json`, `channels.json`, `threads.json` | Bots, skills, chat-app links |
| `conversations/` | One JSON file per chat |
| `documents/` | Your document library and index |
| `skills/` | Approved skills as `SKILL.md` folders (readable by other agents) |
| `workspace/` | The bots' shared workspace; browser downloads in `workspace/downloads` |
| `browser-profile/`, `browser-profiles/` | The shared and per-bot browser sign-ins |
| `tasks/`, `backups/`, `models/` | Tasks, automatic backups, downloaded models (e.g. the embedding model) |
| `SOUL.md` | Hearth's personality file |
| `activity.jsonl` | The activity feed |

## Upgrading from Locus

Hearth used to be called Locus. On first start, an old `Locus` data folder and database are moved to the new names
automatically; old environment variables still work.
