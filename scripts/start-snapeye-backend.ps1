<#
.SYNOPSIS
    Idempotently starts the SnapEye backend stack (ChromaDB + FastAPI) via Podman Compose.

.DESCRIPTION
    Run manually, or register as a Windows Task Scheduler action (Trigger: At log on)
    so the backend comes up once per session, independent of the WPF app's lifecycle.
    Safe to run repeatedly - podman machine start / podman-compose up are both no-ops
    when the machine/stack is already running.
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repoRoot "podman-compose.yml"

# podman-compose is a Python wrapper — it still requires the `podman` CLI (Podman Desktop).
$podmanCmd = Get-Command podman -ErrorAction SilentlyContinue
if (-not $podmanCmd) {
    Write-Host ""
    Write-Host "ERROR: Podman is not installed or not on PATH." -ForegroundColor Red
    Write-Host ""
    Write-Host "This script needs Podman Desktop (includes the 'podman' command):" -ForegroundColor Yellow
    Write-Host "  https://podman-desktop.io/" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "After installing, restart PowerShell and run this script again." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "To run WITHOUT Podman (local dev), use Python instead:" -ForegroundColor Yellow
    Write-Host "  cd backend" -ForegroundColor White
    Write-Host "  python run_server.py" -ForegroundColor White
    Write-Host ""
    exit 1
}

Write-Host "Starting podman machine (no-op if already running)..."
try {
    podman machine start 2>$null
} catch {
    # Already running, or this is a rootful Linux Podman install with no "machine"
    # concept - either way, fall through and let compose up surface any real problem.
}

$composeCmd = Get-Command podman-compose -ErrorAction SilentlyContinue
if (-not $composeCmd) {
    Write-Host ""
    Write-Host "ERROR: podman-compose not found. Install it with:" -ForegroundColor Red
    Write-Host "  pip install podman-compose" -ForegroundColor White
    Write-Host ""
    exit 1
}

Write-Host "Starting SnapEye backend stack..."

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    # PowerShell 5.x Set-Content -Encoding utf8 writes a BOM; Podman rejects that in JSON.
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

# SnapEye only pulls public images (docker.io/chromadb/chroma, python:3.11-slim).
# If Docker Desktop / gcloud left a cred helper in ~/.docker/config.json with expired
# tokens, podman build/pull fails with "gcloud.auth.docker-helper ... invalid_grant".
# Use an isolated empty auth config for this run so public pulls don't touch gcloud.
$isolatedDockerConfig = Join-Path $env:TEMP "snapeye-podman-auth"
if (Test-Path $isolatedDockerConfig) {
    Remove-Item -Recurse -Force $isolatedDockerConfig
}
New-Item -ItemType Directory -Force -Path $isolatedDockerConfig | Out-Null
Write-Utf8NoBom (Join-Path $isolatedDockerConfig "config.json") '{"auths":{}}'
$env:DOCKER_CONFIG = $isolatedDockerConfig
Remove-Item Env:REGISTRY_AUTH_FILE -ErrorAction SilentlyContinue

Push-Location $repoRoot
$exitCode = 1
try {
    podman-compose -f $composeFile up -d --build
    $exitCode = $LASTEXITCODE
} finally {
    Pop-Location
}

if ($exitCode -ne 0) {
    Write-Host ""
    Write-Host "If you saw gcloud.auth.docker-helper / invalid_grant above, your global" -ForegroundColor Yellow
    Write-Host "Docker config has expired Google Cloud credentials. Either:" -ForegroundColor Yellow
    Write-Host "  gcloud auth login" -ForegroundColor White
    Write-Host "  or run the backend without Podman: cd backend; python run_server.py" -ForegroundColor White
    Write-Host ""
    Write-Error "podman-compose up failed (exit code $exitCode). See output above."
    exit $exitCode
}

Write-Host "SnapEye backend stack is up. Health: http://localhost:8080/health"
