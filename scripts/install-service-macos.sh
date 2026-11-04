#!/usr/bin/env bash
# Runs Locus (web) in the background on macOS: starts at login, restarts if it crashes.
#   scripts/install-service-macos.sh               install / update
#   scripts/install-service-macos.sh --uninstall   remove
#   scripts/install-service-macos.sh --dry-run     show what would happen
# Options (env): LOCUS_PORT (default 5290), LOCUS_PASSWORD (optional login), LOCUS_LOCAL_TOOLS=1 (Python, MCP, browser).
set -euo pipefail

LABEL="com.locus.web"
APP_DIR="$HOME/.locus/app"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
LOG_DIR="$HOME/Library/Logs/Locus"
PORT="${LOCUS_PORT:-5290}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="$(command -v dotnet || echo /usr/local/share/dotnet/dotnet)"
DRY=0; [[ "${1:-}" == "--dry-run" ]] && DRY=1

if [[ "${1:-}" == "--uninstall" ]]; then
  launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
  rm -f "$PLIST"
  echo "Locus service removed. (App files remain in $APP_DIR; your data is untouched.)"
  exit 0
fi

xml_escape() { sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'; }

# Without a password, only this Mac can reach Locus; with one, other devices on your network can too.
BIND="127.0.0.1"; [[ -n "${LOCUS_PASSWORD:-}" ]] && BIND="0.0.0.0"
ARGS=("$DOTNET" "$APP_DIR/Locus.Web.dll" "--urls" "http://$BIND:$PORT")
[[ "${LOCUS_LOCAL_TOOLS:-1}" == "1" ]] && ARGS+=("--Locus:AllowLocalProcesses=true")

{
  cat <<XML
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$LABEL</string>
  <key>ProgramArguments</key>
  <array>
XML
  for a in "${ARGS[@]}"; do echo "    <string>$(printf '%s' "$a" | xml_escape)</string>"; done
  cat <<XML
  </array>
  <key>WorkingDirectory</key><string>$APP_DIR</string>
  <key>EnvironmentVariables</key>
  <dict>
    <key>ASPNETCORE_ENVIRONMENT</key><string>Production</string>
XML
  [[ -n "${LOCUS_PASSWORD:-}" ]] && echo "    <key>Locus__Password</key><string>$(printf '%s' "$LOCUS_PASSWORD" | xml_escape)</string>"
  cat <<XML
  </dict>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ThrottleInterval</key><integer>10</integer>
  <key>StandardOutPath</key><string>$LOG_DIR/locus.log</string>
  <key>StandardErrorPath</key><string>$LOG_DIR/locus.log</string>
</dict>
</plist>
XML
} > /tmp/locus-service.plist.$$

if [[ $DRY == 1 ]]; then
  echo "Would publish $REPO/src/Locus.Web to $APP_DIR and install $PLIST:"
  sed 's/\(Locus__Password<\/key><string>\).*</\1***</' /tmp/locus-service.plist.$$
  rm -f /tmp/locus-service.plist.$$
  exit 0
fi

echo "Building Locus…"
"$DOTNET" publish "$REPO/src/Locus.Web/Locus.Web.csproj" -c Release -o "$APP_DIR" --nologo -v quiet
mkdir -p "$LOG_DIR" "$(dirname "$PLIST")"
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
# launchd needs a moment to fully unload the old job before the new one can be loaded.
for _ in $(seq 1 20); do launchctl print "gui/$(id -u)/$LABEL" >/dev/null 2>&1 || break; sleep 0.5; done
mv /tmp/locus-service.plist.$$ "$PLIST"
chmod 600 "$PLIST"   # may contain the password
launchctl bootstrap "gui/$(id -u)" "$PLIST" 2>/dev/null || { sleep 2; launchctl bootstrap "gui/$(id -u)" "$PLIST"; }

for _ in $(seq 1 30); do curl -fs "http://127.0.0.1:$PORT/health" >/dev/null && break; sleep 1; done
if curl -fs "http://127.0.0.1:$PORT/health" >/dev/null; then
  echo "✅ Locus is running at http://localhost:$PORT and will start automatically at login."
else
  echo "⚠️  Installed, but Locus didn't answer yet. Check $LOG_DIR/locus.log"
fi
echo "Logs: $LOG_DIR/locus.log · Remove: $0 --uninstall"
echo "Computer use: grant Screen Recording + Accessibility to 'dotnet' in System Settings → Privacy & Security."
