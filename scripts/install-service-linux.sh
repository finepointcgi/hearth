#!/usr/bin/env bash
# Runs Hearth (web) as a systemd user service: starts at boot/login, restarts if it crashes.
#   scripts/install-service-linux.sh [--uninstall | --dry-run]
# Options (env): HEARTH_PORT (default 5290), HEARTH_PASSWORD, HEARTH_LOCAL_TOOLS=1
set -euo pipefail
NAME="hearth"
APP_DIR="$HOME/.hearth/app"
UNIT="$HOME/.config/systemd/user/$NAME.service"
PORT="${HEARTH_PORT:-5290}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="$(command -v dotnet)"

if [[ "${1:-}" == "--uninstall" ]]; then
  systemctl --user disable --now "$NAME" 2>/dev/null || true
  rm -f "$UNIT"; systemctl --user daemon-reload
  echo "Hearth service removed (data untouched)."; exit 0
fi

EXTRA=""; [[ "${HEARTH_LOCAL_TOOLS:-1}" == "1" ]] && EXTRA="--Hearth:AllowLocalProcesses=true"
# Without a password only this machine can reach Hearth; with one, the network can too.
BIND="127.0.0.1"; [[ -n "${HEARTH_PASSWORD:-}" ]] && BIND="0.0.0.0"
CONTENT="[Unit]
Description=Hearth (local AI assistant)
After=network-online.target

[Service]
WorkingDirectory=$APP_DIR
ExecStart=$DOTNET $APP_DIR/Hearth.Web.dll --urls http://$BIND:$PORT $EXTRA
Restart=always
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=HEARTH_SERVICE=1
${HEARTH_PASSWORD:+Environment=Hearth__Password=$HEARTH_PASSWORD}

[Install]
WantedBy=default.target"

if [[ "${1:-}" == "--dry-run" ]]; then echo "${CONTENT//$HEARTH_PASSWORD/***}"; exit 0; fi

"$DOTNET" publish "$REPO/src/Hearth.Web/Hearth.Web.csproj" -c Release -o "$APP_DIR" --nologo -v quiet
"$DOTNET" publish "$REPO/src/Hearth.Acp/Hearth.Acp.csproj" -c Release -o "$HOME/.hearth/acp" --nologo -v quiet  # editor bridge (Connections → More → API)
mkdir -p "$(dirname "$UNIT")"
printf '%s\n' "$CONTENT" > "$UNIT"; chmod 600 "$UNIT"
systemctl --user daemon-reload
systemctl --user enable --now "$NAME"
loginctl enable-linger "$USER" 2>/dev/null || echo "Tip: run 'sudo loginctl enable-linger $USER' so Hearth runs even when you're logged out."
echo "✅ Hearth service installed: http://localhost:$PORT · logs: journalctl --user -u $NAME -f"
