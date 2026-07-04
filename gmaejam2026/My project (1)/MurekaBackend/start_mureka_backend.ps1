$ErrorActionPreference = "Stop"

$backend = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:MUREKA_BACKEND_HOST = if ($env:MUREKA_BACKEND_HOST) { $env:MUREKA_BACKEND_HOST } else { "127.0.0.1" }
$env:MUREKA_BACKEND_PORT = if ($env:MUREKA_BACKEND_PORT) { $env:MUREKA_BACKEND_PORT } else { "8067" }
$setupScript = Join-Path $backend "setup_mureka_backend.ps1"
$venvPython = Join-Path $backend ".venv_mureka\Scripts\python.exe"

function Test-MurekaPythonReady {
    param([string]$PythonExe)
    if ([string]::IsNullOrWhiteSpace($PythonExe)) { return $false }
    if (-not (Test-Path -LiteralPath $PythonExe)) { return $false }

    try {
        & $PythonExe -c "import playwright.sync_api" *> $null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

Set-Location -LiteralPath $backend
Write-Host ""
Write-Host "Starting project Mureka imported automation backend on http://$env:MUREKA_BACKEND_HOST`:$env:MUREKA_BACKEND_PORT" -ForegroundColor Cyan
Write-Host "Backend folder: $backend"
Write-Host "Mode: imported mureka_web_automation.py route, no API key"
Write-Host ""

if ($env:MUREKA_FORCE_SETUP -or -not (Test-MurekaPythonReady $venvPython)) {
    Write-Host "Preparing backend dependencies for this computer..." -ForegroundColor Cyan
    & $setupScript
}

if (-not (Test-MurekaPythonReady $venvPython)) {
    throw "MUREKA backend Python is not ready after setup."
}

$env:MUREKA_PYTHON_EXE = $venvPython

& "$backend\server.ps1"
