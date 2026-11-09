# Troubleshooting

Start with the **Inbox → Overview**: most problems are listed under **Needs you** with a reason. The dot at the bottom of
the side panel shows whether the model server answers.

## Models

**"… can't be reached (Connection refused)"**
The model server isn't running, or the address is wrong. Start it (`ollama serve`, LM Studio's server…), check the
address in **Settings → Models**, and click **Test connection**. Remove servers you don't use so they stop showing up.
Add a **backup server** so chats keep working when one is down.

**No models in the model picker**
Click ↻ next to the picker. For Ollama, pull one first (`ollama pull qwen3:8b`). For OpenAI-compatible servers, check
the address ends in `/v1`.

**The bot ignores tools / says it can't search**
- Is **Tools** on in the chat (options menu)?
- Does the model support tool calling (🛠 next to its name)? Try `qwen3:8b` or larger.
- Does the bot have the ability on, and is the area not **Blocked** on Permissions?
- Web search needs a SearXNG address.

**Group chats go in circles or the wrong bot answers**
Use a bigger model (`qwen3:8b`+) for coordinators. `@mention` the bot you want. Lower **MaxBotHops** if replies run on.

**Replies stop mid-sentence**
Turn on **Continue replies that hit the length limit**, raise **Max reply tokens**, or the server's context length.

**Claude Code server doesn't work**
Run `claude` once in a terminal and sign in (`/login`). Claude Code needs the desktop app or the web host with local
access. If `claude` isn't on the service's PATH, enter its full path in the server's address field
(`which claude` shows it).

## Phone and network

**Phone can't open Hearth**
- The service listens only on this computer unless installed with `HEARTH_PASSWORD`. Reinstall with a password, or
  use Tailscale (`tailscale serve --bg 5290`).
- Check the computer's firewall allows the port.
- The desktop window alone can't be reached; use the service or Docker.

**Phone app can't reach my model server directly**
Ollama must listen on the network: `OLLAMA_HOST=0.0.0.0 ollama serve`, then use the computer's LAN IP, not
`localhost`. Android emulator: `http://10.0.2.2:11434`.

**Telegram/Discord/Slack bot doesn't answer**
Did you send the pairing code? Only the paired person is answered. Discord needs **Message Content Intent**. Slack
needs both the `xoxb-` and `xapp-` tokens.

**No phone notifications**
In the ntfy app, check you subscribed to the same topic and server; use **Send test**. On iPhone, allow notifications
for ntfy.

## Local tools

**Browser, code, MCP or "other agents" missing on the web host**
Start it with local access: `--Hearth:AllowLocalProcesses=true` (the install scripts do this by default; Docker has it
on).

**"Install browser" / browser won't start**
Click **Install browser** on the Computer page, or choose installed Chrome/Edge. In Docker, make sure `shm_size` is
`1gb`.

**Computer use does nothing on macOS**
Grant **Screen Recording** and **Accessibility** to the app running Hearth (for the service: `dotnet`), then restart
Hearth. Use **📸 Test screenshot**.

**macOS keeps asking for Screen Recording and Accessibility again**
macOS ties the permission to the app's signature. An ad-hoc signed build gets a new one every rebuild: build with
`scripts/build-mac-app.sh`, which signs with your Apple Development certificate so the permission sticks. Also check
which copy of Hearth got your message: the always-on service (`dotnet`) doesn't share the desktop app's permissions.

**Sandbox won't start**
Docker Desktop must be running. The first start builds `hearth-desktop:2`, which takes a few minutes.

**Linked folder in Documents/Desktop/Downloads is empty (macOS)**
Give the app running Hearth access in System Settings → Privacy & Security → Files and Folders (or Full Disk Access).

**Remote computer test fails**
Hearth uses SSH keys only. Check `ssh user@host` works from the same user account without a password prompt.

## Approvals and routines

**A routine ran but skipped a step**
Routines can't ask for approval. Allow that area on **Permissions**, or check if **Guard against hidden instructions**
blocked it after reading outside content (the routine's result says so).

**Webhook returns 403**
Wrong or missing `token`. Copy the URL again from the routine, or send `X-Hearth-Token`.

**Webhooks from other machines don't arrive (desktop app)**
The desktop app only accepts webhooks from the same computer. Use the web host.

**Routines didn't run overnight**
The desktop app only runs them while open. Install the background service or Docker.

## Data

**I deleted something by mistake**
Restore from a backup (Settings → Backup and restore). Your current data is kept in `hearth-before-restore-…`.
Memories have **Undo** on *What Hearth learned*; skills keep previous versions (↶).

**Reset Hearth completely**
Stop Hearth and move the [data folder](Admin-Installation-and-Hosting.md#the-data-folder) somewhere else. Hearth
starts fresh with the Welcome wizard.

## Still stuck?

Check the [logs](Admin-Monitoring.md#logs), and click ⏱ under a failing reply to see which step went wrong.
