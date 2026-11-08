# Hearth setup for Windows: one command from a fresh download to Hearth open in your browser.
#
#   powershell -ExecutionPolicy Bypass -File setup.ps1             asks a few questions (Enter = recommended)
#   powershell -ExecutionPolicy Bypass -File setup.ps1 -Yes        uses the recommended answers
#   powershell -ExecutionPolicy Bypass -File setup.ps1 -DryRun     shows what it would do, changes nothing
#   powershell -ExecutionPolicy Bypass -File setup.ps1 -Uninstall  removes the background task (data is kept)
#
# Steps: .NET 10 SDK → a model (finds one, or installs Ollama) → how to run (at logon / just now / Docker)
#        → phone access (password) → the bots' browser → build, start and open the Welcome page.
param([switch]$Yes, [switch]$DryRun, [switch]$Uninstall, [int]$Port = 5290)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$Repo = $PSScriptRoot
$Interactive = -not $Yes -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected
$script:Step = 0

function Step($t) { $script:Step++; Write-Host ""; Write-Host "[$($script:Step)/6] $t" -ForegroundColor White }
function Ok($t)   { Write-Host "  ✓ $t" -ForegroundColor Green }
function Warn($t) { Write-Host "  ! $t" -ForegroundColor Yellow }
function Note($t) { Write-Host "  $t" -ForegroundColor DarkGray }
function Fail($t) { Write-Host ""; Write-Host "✗ $t" -ForegroundColor Red; exit 1 }
function Has($c)  { [bool](Get-Command $c -ErrorAction SilentlyContinue) }

# Runs a command, or only prints it with -DryRun.
function Run([string]$exe, [string[]]$argv) {
  if ($DryRun) { Note "would run: $exe $($argv -join ' ')"; return }
  & $exe @argv
  if ($LASTEXITCODE -ne 0) { throw "$exe exited with code $LASTEXITCODE" }
}

function Ask([string]$q, [bool]$def) {
  $hint = if ($def) { "[Y/n]" } else { "[y/N]" }
  if (-not $Interactive) { Write-Host "  $q $hint $(if ($def) {'y'} else {'n'})"; return $def }
  $r = Read-Host "  $q $hint"
  if ([string]::IsNullOrWhiteSpace($r)) { return $def }
  return $r.Trim().ToLower().StartsWith("y")
}

function Choose([string]$q, [int]$def, [string[]]$opts) {
  Write-Host "  $q"
  for ($i = 0; $i -lt $opts.Count; $i++) { Write-Host "    $($i + 1)) $($opts[$i])" }
  if (-not $Interactive) { Write-Host "  → $def"; return $def }
  while ($true) {
    $r = Read-Host "  Choose 1-$($opts.Count) [$def]"
    if ([string]::IsNullOrWhiteSpace($r)) { return $def }
    $n = 0
    if ([int]::TryParse($r, [ref]$n) -and $n -ge 1 -and $n -le $opts.Count) { return $n }
    Write-Host "  Please type a number from 1 to $($opts.Count)."
  }
}

function HttpOk([string]$url) {
  try { Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3 | Out-Null; return $true } catch { return $false }
}

function OpenUrl([string]$url) { if ($DryRun) { Note "would open $url" } else { Start-Process $url } }

function WaitForHearth([int]$p) {
  if ($DryRun) { return $true }
  for ($i = 0; $i -lt 90; $i++) { if (HttpOk "http://127.0.0.1:$p/health") { return $true }; Start-Sleep 1 }
  return $false
}

function RefreshPath {
  $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
}

if ($Uninstall) {
  Run "powershell" @("-ExecutionPolicy", "Bypass", "-File", "$Repo\scripts\install-service-windows.ps1", "-Uninstall")
  exit 0
}

Write-Host "Hearth setup" -ForegroundColor White
Note "Press Enter to take the recommended answer. Everything runs on this computer."
if ($DryRun) { Warn "Dry run: nothing will be installed or changed." }

if (HttpOk "http://127.0.0.1:$Port/health") {
  Ok "Hearth is already running at http://localhost:$Port"
  if (-not (Ask "Run setup again anyway (to change how it runs or update it)?" $false)) { OpenUrl "http://localhost:$Port"; exit 0 }
}

# ---------- 1. .NET ----------
Step "Checking for .NET 10"
$haveDotnet = (Has dotnet) -and ((& dotnet --list-sdks 2>$null) -match '^10\.')
if ($haveDotnet) { Ok ".NET 10 SDK found" }
else {
  Warn "Hearth needs the .NET 10 SDK (free, from Microsoft, about 250 MB)."
  if (-not (Ask "Install it now?" $true)) { Fail "Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run setup.ps1 again." }
  if (-not (Has winget)) { Fail "winget isn't available. Install .NET 10 from https://dotnet.microsoft.com/download/dotnet/10.0 and run setup.ps1 again." }
  Run "winget" @("install", "--id", "Microsoft.DotNet.SDK.10", "-e", "--accept-source-agreements", "--accept-package-agreements")
  RefreshPath
  Ok ".NET installed"
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"

# ---------- 2. A model ----------
Step "Finding an AI model to talk to"
$found = @()
if (HttpOk "http://127.0.0.1:11434/api/tags") { $found += "Ollama" }
if (HttpOk "http://127.0.0.1:1234/v1/models") { $found += "LM Studio" }
if (HttpOk "http://127.0.0.1:8000/v1/models") { $found += "oMLX" }
$ramGb = [math]::Floor((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
if ($ramGb -ge 16) { $model = "qwen3:8b"; $modelSize = "about 5 GB" } else { $model = "llama3.2"; $modelSize = "about 2 GB" }

function PullModels {
  $have = try { (Invoke-WebRequest -Uri "http://127.0.0.1:11434/api/tags" -UseBasicParsing -TimeoutSec 3).Content } catch { "" }
  if ($have -match [regex]::Escape("`"$model")) { Ok "$model is already downloaded" }
  elseif (Ask "Download $model ($modelSize, a good fit for $ramGb GB of memory)?" $true) { Run "ollama" @("pull", $model) }
  if ($have -notmatch '"nomic-embed-text') { Run "ollama" @("pull", "nomic-embed-text") }  # small; lets bots search your documents
}

function StartOllama {
  if ($DryRun) { Note "would start Ollama"; return }
  $app = Join-Path $env:LOCALAPPDATA "Programs\Ollama\ollama app.exe"
  if (Test-Path $app) { Start-Process $app } else { Start-Process "ollama" -ArgumentList "serve" -WindowStyle Hidden }
  for ($i = 0; $i -lt 20; $i++) { if (HttpOk "http://127.0.0.1:11434/api/tags") { break }; Start-Sleep 1 }
}

if ($found.Count -gt 0) {
  Ok "Found $($found -join ', ') running on this computer"
  if ($found -contains "Ollama") { PullModels }
}
elseif (Has ollama) { Ok "Ollama is installed"; Note "Starting it…"; StartOllama; PullModels }
else {
  Warn "No model server is running on this computer."
  $hasClaude = Has claude
  $opts = @("Install Ollama and download a model (recommended)")
  if ($hasClaude) { $opts += "Use Claude Code (found on this computer; uses your Claude subscription)" }
  $opts += "I'll connect one myself on the Welcome page (LM Studio, a server elsewhere, OpenRouter…)"
  $c = Choose "How should Hearth think?" 1 $opts
  if ($c -eq 1) {
    if (-not (Has winget)) { OpenUrl "https://ollama.com/download"; Fail "Install Ollama from https://ollama.com/download, then run setup.ps1 again." }
    Run "winget" @("install", "--id", "Ollama.Ollama", "-e", "--accept-source-agreements", "--accept-package-agreements")
    RefreshPath
    StartOllama
    PullModels
    Ok "Ollama is ready"
  }
  elseif ($hasClaude -and $c -eq 2) { Ok "Pick `"Claude Code`" on the Welcome page in a moment" }
  else { Ok "You'll connect a model on the Welcome page" }
}

# ---------- 3. How to run ----------
Step "How should Hearth run?"
$hasDocker = (Has docker) -and ($(try { docker info 2>$null | Out-Null; $LASTEXITCODE -eq 0 } catch { $false }))
$opts = @("Always on: starts when you sign in to Windows, runs in the background (recommended)",
          "Just now, in this window (stops when you close it)")
if ($hasDocker) { $opts += "In Docker (for a server; no desktop control)" }
$mode = Choose "Pick one:" 1 $opts
if ($mode -eq 3) { $Port = 8080 }

# ---------- 4. Phone & other devices ----------
Step "Phone and other devices"
$password = ""
if ($mode -eq 3) { Note "In Docker, Hearth is reachable from your network, so it should have a password."; $remote = $true }
else {
  Note "Without a password Hearth only answers on this computer, which is the safest."
  Note "Tip: Tailscale gives your phone a private link without opening Hearth to your Wi-Fi (Connections → Phone)."
  $remote = Ask "Let your phone and other devices on your network open Hearth (needs a password)?" $false
}
if ($remote) {
  if (-not $Interactive) {
    $password = "$env:HEARTH_PASSWORD"
    if (-not $password) { Warn "No password given (set HEARTH_PASSWORD to choose one)." }
  } else {
    while ($true) {
      $p1 = [Net.NetworkCredential]::new("", (Read-Host "  Choose a password" -AsSecureString)).Password
      if ($p1.Length -lt 6) { Write-Host "  At least 6 characters, please."; continue }
      $p2 = [Net.NetworkCredential]::new("", (Read-Host "  Type it again" -AsSecureString)).Password
      if ($p1 -eq $p2) { $password = $p1; break }
      Write-Host "  They didn't match, try again."
    }
  }
  if ($password) { Ok "Password set. You'll sign in with it on other devices." }
} else { Ok "Hearth will only be reachable from this computer" }

# ---------- 5. Bot browser ----------
Step "Bots' web browser"
$installBrowser = $false
if ($mode -eq 3) { Ok "Included in the Docker image" }
else {
  Note "Bots can browse websites for you in their own Chromium browser (about 150 MB)."
  $installBrowser = Ask "Install it now? (You can also do this later on the Computer page.)" $true
}

# ---------- 6. Build & start ----------
Step "Building and starting Hearth"
$url = "http://localhost:$Port"
try {
  switch ($mode) {
    1 {
      Note "Building (the first time takes a minute or two)…"
      $svcArgs = @("-ExecutionPolicy", "Bypass", "-File", "$Repo\scripts\install-service-windows.ps1", "-Port", "$Port")
      if ($password) { $svcArgs += @("-Password", $password) }
      if ($DryRun) { Note "would run: scripts\install-service-windows.ps1 -Port $Port$(if ($password) {' -Password ***'})" }
      else { & powershell @svcArgs; if ($LASTEXITCODE -ne 0) { throw "service install failed" } }
      if ($installBrowser) {
        Note "Installing the bots' browser…"
        try { Run "dotnet" @("$env:LOCALAPPDATA\Hearth-app\Hearth.Web.dll", "--install-browser") } catch { Warn "Couldn't install it now; use Computer → Install browser later." }
      }
    }
    2 {
      Note "Building (the first time takes a minute or two)…"
      Run "dotnet" @("build", "$Repo\src\Hearth.Web", "-c", "Release", "--nologo", "-v", "quiet", "-clp:ErrorsOnly")
      if ($installBrowser) {
        Note "Installing the bots' browser…"
        try { Run "dotnet" @("run", "--project", "$Repo\src\Hearth.Web", "-c", "Release", "--no-build", "--", "--install-browser") } catch { Warn "Couldn't install it now; use Computer → Install browser later." }
      }
    }
    3 {
      Note "Building the Docker image (the first time takes several minutes)…"
      $env:HEARTH_PASSWORD = $password
      Push-Location $Repo
      try { Run "docker" @("compose", "up", "-d", "--build") } finally { Pop-Location }
    }
  }
} catch { Fail "Setup stopped: $($_.Exception.Message)" }

function Finish {
  Write-Host ""
  Write-Host "✅ Hearth is ready: $url" -ForegroundColor Green
  Note "The Welcome page will help you pick a model and meet your first bots."
  if ($mode -eq 1) { Note "Update after pulling new code: setup.ps1 -Yes   ·   Stop and remove: setup.ps1 -Uninstall" }
  if ($mode -eq 3) { Note "Logs: docker compose logs -f   ·   Stop: docker compose down" }
  if ($password) { Note "On your phone, open http://<this computer's IP>:$Port and sign in." }
}

if ($mode -eq 2) {
  $bind = if ($password) { "0.0.0.0" } else { "127.0.0.1" }
  $runArgs = @("run", "--project", "$Repo\src\Hearth.Web", "-c", "Release", "--no-build", "--urls", "http://${bind}:$Port", "--", "--Hearth:AllowLocalProcesses=true")
  if ($DryRun) { Note "would run: dotnet $($runArgs -join ' ')"; Finish; exit 0 }
  $env:ASPNETCORE_ENVIRONMENT = "Production"
  if ($password) { $env:Hearth__Password = $password }
  # Open the browser once Hearth answers; Hearth itself stays in this window (Ctrl+C stops it).
  Start-Job -ArgumentList $Port, $url -ScriptBlock {
    param($p, $u)
    for ($i = 0; $i -lt 90; $i++) {
      try { Invoke-WebRequest "http://127.0.0.1:$p/health" -UseBasicParsing -TimeoutSec 3 | Out-Null; Start-Process $u; break } catch { Start-Sleep 1 }
    }
  } | Out-Null
  Finish
  Write-Host ""; Note "Starting Hearth. Press Ctrl+C to stop it."
  & dotnet @runArgs
  exit $LASTEXITCODE
}

if (WaitForHearth $Port) { Finish; OpenUrl $url }
else {
  Warn "Hearth was installed but hasn't answered at $url yet. It may still be starting."
  if ($mode -eq 3) { Note "Check: docker compose logs -f" }
  exit 1
}
