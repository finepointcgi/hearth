#!/usr/bin/env bash
# Hearth setup for macOS and Linux: one command from a fresh download to Hearth open in your browser.
#
#   ./setup.sh              asks a few questions (press Enter for the recommended answer)
#   ./setup.sh --yes        uses the recommended answers without asking
#   ./setup.sh --dry-run    shows what it would do and changes nothing
#   ./setup.sh --uninstall  stops the background service (your chats, bots and settings are kept)
#
# What it does, in order:
#   1. makes sure the .NET 10 SDK is installed (offers to install it)
#   2. finds a model server on this computer, or offers to install Ollama and download a model
#   3. asks how to run Hearth: always on in the background, just now in this terminal, or in Docker
#   4. asks whether your phone and other devices may reach it (then it needs a password)
#   5. optionally installs the bots' web browser (Chromium)
#   6. opens Hearth, where the Welcome page finishes the rest
#
# Options (env): HEARTH_PORT (default 5290).
set -euo pipefail

REPO="$(cd "$(dirname "$0")" && pwd)"
PORT="${HEARTH_PORT:-5290}"
YES=0; DRY=0
for a in "$@"; do
  case "$a" in
    -y|--yes) YES=1 ;;
    -n|--dry-run) DRY=1 ;;
    --uninstall) UNINSTALL=1 ;;
    -h|--help) sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $a (try --help)"; exit 2 ;;
  esac
done

OS="$(uname -s)"
case "$OS" in
  Darwin) PLATFORM=mac ;;
  Linux)  PLATFORM=linux ;;
  *) echo "This script is for macOS and Linux. On Windows run:  powershell -ExecutionPolicy Bypass -File setup.ps1"; exit 1 ;;
esac

# ---------- looks ----------
if [[ -t 1 ]]; then B=$'\033[1m'; D=$'\033[2m'; G=$'\033[32m'; Y=$'\033[33m'; R=$'\033[31m'; N=$'\033[0m'; else B=; D=; G=; Y=; R=; N=; fi
STEP=0
step() { STEP=$((STEP + 1)); printf '\n%s[%d/6] %s%s\n' "$B" "$STEP" "$1" "$N"; }
ok()   { printf '  %s✓%s %s\n' "$G" "$N" "$1"; }
warn() { printf '  %s!%s %s\n' "$Y" "$N" "$1"; }
fail() { printf '\n%s✗ %s%s\n' "$R" "$1" "$N"; exit 1; }
note() { printf '  %s%s%s\n' "$D" "$1" "$N"; }

# Runs a command, or only prints it with --dry-run.
run() {
  if [[ $DRY == 1 ]]; then
    local shown="$*"
    [[ -n "${PASSWORD:-}" ]] && shown="${shown//$PASSWORD/***}"
    printf '  %swould run:%s %s\n' "$D" "$N" "$shown"; return 0
  fi
  "$@"
}

# ask "Question" default(y/n) → returns 0 for yes
ask() {
  local q="$1" def="$2" hint reply
  if [[ $def == y ]]; then hint="[Y/n]"; else hint="[y/N]"; fi
  if [[ $YES == 1 || ! -t 0 ]]; then printf '  %s %s %s\n' "$q" "$hint" "$def"; [[ $def == y ]]; return; fi
  read -r -p "  $q $hint " reply || reply=""
  reply="$(printf '%s' "${reply:-$def}" | tr '[:upper:]' '[:lower:]')"
  [[ $reply == y* ]]
}

# choose "Question" default option1 option2 … → sets CHOICE to the option number
choose() {
  local q="$1" def="$2"; shift 2
  local i=1 o reply
  printf '  %s\n' "$q"
  for o in "$@"; do printf '    %s%d)%s %s\n' "$B" "$i" "$N" "$o"; i=$((i + 1)); done
  if [[ $YES == 1 || ! -t 0 ]]; then CHOICE=$def; printf '  → %s\n' "$def"; return; fi
  while true; do
    read -r -p "  Choose 1-$# [$def]: " reply || reply=""
    reply="${reply:-$def}"
    if [[ $reply =~ ^[0-9]+$ ]] && (( reply >= 1 && reply <= $# )); then CHOICE=$reply; return; fi
    echo "  Please type a number from 1 to $#."
  done
}

has() { command -v "$1" >/dev/null 2>&1; }
http_ok() { curl -fs --max-time 3 "$1" >/dev/null 2>&1; }

open_url() {
  if [[ $DRY == 1 ]]; then note "would open $1"; return; fi
  if [[ $PLATFORM == mac ]]; then open "$1"; elif has xdg-open; then xdg-open "$1" >/dev/null 2>&1 || true; fi
}

wait_for_hearth() {
  [[ $DRY == 1 ]] && return 0
  local i
  for i in $(seq 1 90); do http_ok "http://127.0.0.1:$1/health" && return 0; sleep 1; done
  return 1
}

if [[ -n "${UNINSTALL:-}" ]]; then
  if [[ $PLATFORM == mac ]]; then run "$REPO/scripts/install-service-macos.sh" --uninstall
  else run "$REPO/scripts/install-service-linux.sh" --uninstall; fi
  exit 0
fi

has curl || fail "curl is needed. Install it with your package manager and run this again."

printf '%sHearth setup%s\n' "$B" "$N"
note "Press Enter to take the recommended answer. Nothing is sent anywhere; everything runs on this computer."
[[ $DRY == 1 ]] && warn "Dry run: nothing will be installed or changed."

# Hearth may already be up (a previous setup).
if http_ok "http://127.0.0.1:$PORT/health"; then
  ok "Hearth is already running at http://localhost:$PORT"
  if ! ask "Run setup again anyway (to change how it runs or update it)?" n; then
    open_url "http://localhost:$PORT"; exit 0
  fi
fi

# ---------- 1. .NET ----------
step "Checking for .NET 10"
DOTNET=""
for c in "$(command -v dotnet 2>/dev/null || true)" "$HOME/.dotnet/dotnet" /usr/local/share/dotnet/dotnet /usr/share/dotnet/dotnet; do
  if [[ -n "$c" && -x "$c" ]] && "$c" --list-sdks 2>/dev/null | grep -q '^10\.'; then DOTNET="$c"; break; fi
done
if [[ -n "$DOTNET" ]]; then
  ok ".NET 10 SDK found ($DOTNET)"
else
  warn "Hearth needs the .NET 10 SDK (free, from Microsoft, about 250 MB)."
  ask "Install it now?" y || fail "Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run ./setup.sh again."
  if [[ $PLATFORM == mac ]] && has brew; then
    run brew install --cask dotnet-sdk
    DOTNET="$(command -v dotnet 2>/dev/null || echo /usr/local/share/dotnet/dotnet)"
  else
    # Microsoft's official installer, into ~/.dotnet (no admin rights needed).
    run bash -c "curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir \"$HOME/.dotnet\""
    DOTNET="$HOME/.dotnet/dotnet"
    note "Installed to ~/.dotnet. To use 'dotnet' in new terminals, add to your shell profile:"
    note "  export DOTNET_ROOT=\$HOME/.dotnet PATH=\$HOME/.dotnet:\$PATH"
  fi
  export DOTNET_ROOT="$(dirname "$DOTNET")"
  ok ".NET installed"
fi
export PATH="$(dirname "$DOTNET"):$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# ---------- 2. A model ----------
step "Finding an AI model to talk to"
FOUND=""
http_ok http://127.0.0.1:11434/api/tags      && FOUND="${FOUND}Ollama, "
http_ok http://127.0.0.1:1234/v1/models      && FOUND="${FOUND}LM Studio, "
http_ok http://127.0.0.1:8000/v1/models      && FOUND="${FOUND}oMLX, "
FOUND="${FOUND%, }"
HAS_CLAUDE=0; has claude && HAS_CLAUDE=1

ram_gb() {
  if [[ $PLATFORM == mac ]]; then echo $(( $(sysctl -n hw.memsize) / 1073741824 ))
  else awk '/MemTotal/ { printf "%d", $2 / 1048576 }' /proc/meminfo; fi
}
RAM="$(ram_gb 2>/dev/null || echo 8)"
# Bigger computers get the model that's good at tools and Think mode; smaller ones get a light one.
if (( RAM >= 16 )); then MODEL=qwen3:8b; MODEL_SIZE="about 5 GB"; else MODEL=llama3.2; MODEL_SIZE="about 2 GB"; fi

pull_models() {
  local have
  have="$(curl -fs --max-time 3 http://127.0.0.1:11434/api/tags 2>/dev/null || true)"
  if printf '%s' "$have" | grep -q "\"$MODEL"; then ok "$MODEL is already downloaded"
  elif ask "Download $MODEL ($MODEL_SIZE, a good fit for ${RAM} GB of memory)?" y; then run ollama pull "$MODEL"; fi
  printf '%s' "$have" | grep -q '"nomic-embed-text' || run ollama pull nomic-embed-text   # small; lets bots search your documents
}

start_ollama() {
  if [[ $PLATFORM == mac ]] && has brew && brew list ollama >/dev/null 2>&1; then run brew services start ollama
  elif [[ $PLATFORM == mac && -d /Applications/Ollama.app ]]; then run open -a Ollama
  elif has systemctl && systemctl list-unit-files ollama.service >/dev/null 2>&1; then run sudo systemctl start ollama
  else run bash -c "nohup ollama serve >/dev/null 2>&1 &"; fi
  if [[ $DRY == 0 ]]; then for _ in $(seq 1 20); do http_ok http://127.0.0.1:11434/api/tags && break; sleep 1; done; fi
}

if [[ -n "$FOUND" ]]; then
  ok "Found $FOUND running on this computer"
  [[ $FOUND == *Ollama* ]] && pull_models
elif has ollama; then
  ok "Ollama is installed"
  note "Starting it…"
  start_ollama
  pull_models
else
  warn "No model server is running on this computer."
  opts=("Install Ollama and download a model (recommended)")
  [[ $HAS_CLAUDE == 1 ]] && opts+=("Use Claude Code (found on this computer; uses your Claude subscription)")
  opts+=("I'll connect one myself on the Welcome page (LM Studio, a server elsewhere, OpenRouter…)")
  choose "How should Hearth think?" 1 "${opts[@]}"
  if [[ $CHOICE == 1 ]]; then
    if [[ $PLATFORM == mac ]]; then
      if has brew; then run brew install ollama
      else
        warn "Download Ollama from https://ollama.com/download, open it once, then run ./setup.sh again."
        open_url https://ollama.com/download; exit 0
      fi
    else
      run bash -c "curl -fsSL https://ollama.com/install.sh | sh"   # Ollama's official installer
    fi
    start_ollama
    pull_models
    ok "Ollama is ready"
  elif [[ $HAS_CLAUDE == 1 && $CHOICE == 2 ]]; then
    ok "Pick \"Claude Code\" on the Welcome page in a moment"
  else
    ok "You'll connect a model on the Welcome page"
  fi
fi

# ---------- 3. How to run ----------
step "How should Hearth run?"
HAS_DOCKER=0; has docker && docker info >/dev/null 2>&1 && HAS_DOCKER=1
opts=("Always on: starts when you log in, runs in the background (recommended)"
      "Just now, in this terminal (stops when you close it)")
[[ $HAS_DOCKER == 1 ]] && opts+=("In Docker (for a server or NAS; no desktop control)")
choose "Pick one:" 1 "${opts[@]}"
MODE=$CHOICE   # 1 service, 2 foreground, 3 docker
if [[ $MODE == 1 && $PLATFORM == linux ]] && ! systemctl --user status >/dev/null 2>&1; then
  warn "This system has no systemd user session, so Hearth will run in this terminal instead."; MODE=2
fi
[[ $MODE == 3 ]] && PORT=8080

# ---------- 4. Phone & other devices ----------
step "Phone and other devices"
PASSWORD=""
if [[ $MODE == 3 ]]; then
  note "In Docker, Hearth is reachable from your network, so it should have a password."
  WANT_REMOTE=y
else
  note "Without a password Hearth only answers on this computer, which is the safest."
  note "Tip: Tailscale gives your phone a private link without opening Hearth to your Wi-Fi (Connections → Phone)."
  if ask "Let your phone and other devices on your network open Hearth (needs a password)?" n; then WANT_REMOTE=y; else WANT_REMOTE=n; fi
fi
if [[ $WANT_REMOTE == y ]]; then
  if [[ $YES == 1 || ! -t 0 ]]; then
    PASSWORD="${HEARTH_PASSWORD:-}"
    [[ -z "$PASSWORD" ]] && warn "No password given (set HEARTH_PASSWORD to choose one); Hearth stays on this computer only." && [[ $MODE != 3 ]] && WANT_REMOTE=n
  else
    while true; do
      read -r -s -p "  Choose a password: " PASSWORD; echo
      [[ ${#PASSWORD} -ge 6 ]] || { echo "  At least 6 characters, please."; continue; }
      read -r -s -p "  Type it again: " p2; echo
      [[ "$PASSWORD" == "$p2" ]] && break
      echo "  They didn't match, try again."
    done
  fi
  [[ -n "$PASSWORD" ]] && ok "Password set. You'll sign in with it on other devices."
else
  ok "Hearth will only be reachable from this computer"
fi

# ---------- 5. Bot browser ----------
step "Bots' web browser"
INSTALL_BROWSER=n
if [[ $MODE == 3 ]]; then ok "Included in the Docker image"
else
  note "Bots can browse websites for you in their own Chromium browser (about 150 MB)."
  ask "Install it now? (You can also do this later on the Computer page.)" y && INSTALL_BROWSER=y
fi

# ---------- 6. Build & start ----------
step "Building and starting Hearth"
case $MODE in
  1)
    note "Building (the first time takes a minute or two)…"
    SVC="$REPO/scripts/install-service-macos.sh"; [[ $PLATFORM == linux ]] && SVC="$REPO/scripts/install-service-linux.sh"
    if [[ $DRY == 1 ]]; then
      note "would install the background service:"
      HEARTH_PORT="$PORT" HEARTH_PASSWORD="$PASSWORD" "$SVC" --dry-run | sed 's/^/    /'
    else
      HEARTH_PORT="$PORT" HEARTH_PASSWORD="$PASSWORD" "$SVC"
    fi
    if [[ $INSTALL_BROWSER == y ]]; then
      note "Installing the bots' browser…"
      run "$DOTNET" "$HOME/.hearth/app/Hearth.Web.dll" --install-browser || warn "Couldn't install it now; use Computer → Install browser later."
    fi
    ;;
  2)
    note "Building (the first time takes a minute or two)…"
    run "$DOTNET" build "$REPO/src/Hearth.Web" -c Release --nologo -v quiet -clp:ErrorsOnly
    if [[ $INSTALL_BROWSER == y ]]; then
      note "Installing the bots' browser…"
      run "$DOTNET" run --project "$REPO/src/Hearth.Web" -c Release --no-build -- --install-browser || warn "Couldn't install it now; use Computer → Install browser later."
    fi
    ;;
  3)
    note "Building the Docker image (the first time takes several minutes)…"
    (cd "$REPO" && run env HEARTH_PASSWORD="$PASSWORD" TZ="${TZ:-$( [[ -L /etc/localtime ]] && readlink /etc/localtime | sed 's|.*/zoneinfo/||' || echo UTC)}" docker compose up -d --build)
    ;;
esac

URL="http://localhost:$PORT"
finish() {
  printf '\n%s✅ Hearth is ready: %s%s\n' "$G$B" "$URL" "$N"
  note "The Welcome page will help you pick a model and meet your first bots."
  case $MODE in
    1) if [[ $PLATFORM == mac ]]; then note "Logs: ~/Library/Logs/Hearth/hearth.log"; else note "Logs: journalctl --user -u hearth -f"; fi
       note "Update after pulling new code: ./setup.sh --yes   ·   Stop and remove: ./setup.sh --uninstall" ;;
    3) note "Logs: docker compose logs -f   ·   Stop: docker compose down" ;;
  esac
  [[ -n "$PASSWORD" && $MODE != 3 ]] && note "On your phone, open http://<this computer's IP>:$PORT and sign in."
  [[ -n "$PASSWORD" && $MODE == 3 ]] && note "On your phone, open http://<this computer's IP>:8080 and sign in."
  [[ $PLATFORM == mac && $MODE != 3 ]] && note "For desktop control, grant Screen Recording + Accessibility to 'dotnet' in System Settings → Privacy & Security."
  return 0
}

if [[ $MODE == 2 ]]; then
  BIND=127.0.0.1; [[ -n "$PASSWORD" ]] && BIND=0.0.0.0
  if [[ $DRY == 1 ]]; then
    run "$DOTNET" run --project "$REPO/src/Hearth.Web" -c Release --no-build --urls "http://$BIND:$PORT" -- --Hearth:AllowLocalProcesses=true
    finish; exit 0
  fi
  # Open the browser once it answers, then keep Hearth in the foreground (Ctrl+C stops it).
  ( wait_for_hearth "$PORT" && open_url "$URL" && finish ) &
  printf '\n%sStarting Hearth. Press Ctrl+C to stop it.%s\n' "$D" "$N"
  export ASPNETCORE_ENVIRONMENT=Production
  [[ -n "$PASSWORD" ]] && export Hearth__Password="$PASSWORD"
  exec "$DOTNET" run --project "$REPO/src/Hearth.Web" -c Release --no-build --urls "http://$BIND:$PORT" -- --Hearth:AllowLocalProcesses=true
fi

if wait_for_hearth "$PORT"; then
  finish
  open_url "$URL"
else
  warn "Hearth was installed but hasn't answered at $URL yet. It may still be starting; check the logs:"
  if [[ $MODE == 3 ]]; then note "docker compose logs -f"
  elif [[ $PLATFORM == mac ]]; then note "~/Library/Logs/Hearth/hearth.log"
  else note "journalctl --user -u hearth -f"; fi
  exit 1
fi
