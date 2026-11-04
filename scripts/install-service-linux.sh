#!/usr/bin/env bash
# Runs Locus (web) as a systemd user service: starts at boot/login, restarts if it crashes.
#   scripts/install-service-linux.sh [--uninstall | --dry-run]
# Options (env): LOCUS_PORT (default 5290), LOCUS_PASSWORD, LOCUS_LOCAL_TOOLS=1
set -euo pipefail
NAME="locus"
APP_DIR="$HOME/.locus/app"
UNIT="$HOME/.config/systemd/user/$NAME.service"
PORT="${LOCUS_PORT:-5290}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="$(command -v dotnet)"

if [[ "${1:-}" == "--uninstall" ]]; then
  systemctl --user disable --now "$NAME" 2>/dev/null || true
  rm -f "$UNIT"; systemctl --user daemon-reload
  echo "Locus service removed (data untouched)."; exit 0
fi

EXTRA=""; [[ "${LOCUS_LOCAL_TOOLS:-1}" == "1" ]] && EXTRA="--Locus:AllowLocalProcesses=true"
# Without a password only this machine can reach Locus; with one, the network can too.
BIND="127.0.0.1"; [[ -n "${LOCUS_PASSWORD:-}" ]] && BIND="0.0.0.0"
CONTENT="[Unit]
Description=Locus (local AI assistant)
After=network-online.target

[Service]
WorkingDirectory=$APP_DIR
ExecStart=$DOTNET $APP_DIR/Locus.Web.dll --urls http://$BIND:$PORT $EXTRA
Restart=always
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
${LOCUS_PASSWORD:+Environment=Locus__Password=$LOCUS_PASSWORD}

[Install]
WantedBy=default.target"

if [[ "${1:-}" == "--dry-run" ]]; then echo "${CONTENT//$LOCUS_PASSWORD/***}"; exit 0; fi

"$DOTNET" publish "$REPO/src/Locus.Web/Locus.Web.csproj" -c Release -o "$APP_DIR" --nologo -v quiet
mkdir -p "$(dirname "$UNIT")"
printf '%s\n' "$CONTENT" > "$UNIT"; chmod 600 "$UNIT"
systemctl --user daemon-reload
systemctl --user enable --now "$NAME"
loginctl enable-linger "$USER" 2>/dev/null || echo "Tip: run 'sudo loginctl enable-linger $USER' so Locus runs even when you're logged out."
echo "✅ Locus service installed: http://localhost:$PORT · logs: journalctl --user -u $NAME -f"
