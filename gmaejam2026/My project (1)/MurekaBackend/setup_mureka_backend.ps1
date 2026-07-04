$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$VenvRoot = Join-Path $Root ".venv_mureka"
$VenvPython = Join-Path $VenvRoot "Scripts\python.exe"
$Requirements = Join-Path $Root "requirements.txt"
$ToolsRoot = Join-Path $Root ".tools"
$LocalUvRoot = Join-Path $ToolsRoot "uv"
$LocalUvExe = Join-Path $LocalUvRoot "uv.exe"

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

function Test-BootstrapPython {
    param(
        [string]$File,
        [string[]]$PrefixArgs = @()
    )

    if ([string]::IsNullOrWhiteSpace($File)) { return $false }
    if ($File -like "*\WindowsApps\python.exe") { return $false }

    try {
        & $File @($PrefixArgs + @("-c", "import sys; raise SystemExit(0 if sys.version_info >= (3, 10) else 1)")) *> $null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

function Get-UvExecutable {
    if ($env:MUREKA_UV_EXE -and (Test-Path -LiteralPath $env:MUREKA_UV_EXE)) {
        return $env:MUREKA_UV_EXE
    }

    $uv = Get-Command uv -ErrorAction SilentlyContinue
    if ($uv) {
        return $uv.Source
    }

    if (Test-Path -LiteralPath $LocalUvExe) {
        return $LocalUvExe
    }

    Write-Host "uv was not found. Downloading a local uv bootstrapper..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path $LocalUvRoot | Out-Null
    $zipPath = Join-Path $ToolsRoot "uv-x86_64-pc-windows-msvc.zip"
    $url = "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip"

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    } catch {}

    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $zipPath
    Expand-Archive -LiteralPath $zipPath -DestinationPath $LocalUvRoot -Force
    $downloadedUv = Get-ChildItem -LiteralPath $LocalUvRoot -Recurse -Filter "uv.exe" -File | Select-Object -First 1
    if (-not $downloadedUv) {
        throw "Downloaded uv archive did not contain uv.exe."
    }

    if ($downloadedUv.FullName -ne $LocalUvExe) {
        Copy-Item -LiteralPath $downloadedUv.FullName -Destination $LocalUvExe -Force
    }

    return $LocalUvExe
}

function Get-UvManagedPython {
    $uv = Get-UvExecutable
    Write-Host "Installing uv-managed Python 3.12 for this project..."
    & $uv python install 3.12
    if ($LASTEXITCODE -ne 0) {
        throw "uv could not install Python 3.12."
    }

    $uvPython = (& $uv python find 3.12 2>$null | Select-Object -First 1)
    if ($LASTEXITCODE -eq 0 -and $uvPython -and (Test-BootstrapPython $uvPython)) {
        return $uvPython
    }

    throw "uv installed Python, but the Python executable could not be found."
}

function Get-BootstrapPython {
    $candidates = @()
    if ($env:MUREKA_BOOTSTRAP_PYTHON_EXE) { $candidates += @{ File = $env:MUREKA_BOOTSTRAP_PYTHON_EXE; PrefixArgs = @() } }
    if ($env:MUREKA_PYTHON_EXE) { $candidates += @{ File = $env:MUREKA_PYTHON_EXE; PrefixArgs = @() } }

    $pyLauncher = Get-Command py -ErrorAction SilentlyContinue
    if ($pyLauncher) { $candidates += @{ File = $pyLauncher.Source; PrefixArgs = @("-3") } }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) { $candidates += @{ File = $python.Source; PrefixArgs = @() } }

    $codexPython = Join-Path $env:USERPROFILE ".cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
    $candidates += @{ File = $codexPython; PrefixArgs = @() }

    foreach ($candidate in $candidates) {
        if (Test-BootstrapPython $candidate.File $candidate.PrefixArgs) {
            return $candidate
        }
    }

    $uvPython = Get-UvManagedPython
    return @{ File = $uvPython; PrefixArgs = @() }
}

function Invoke-BootstrapPython {
    param([string[]]$Arguments)

    $bootstrap = Get-BootstrapPython
    & $bootstrap.File @($bootstrap.PrefixArgs + $Arguments)
}

Write-Host ""
Write-Host "Preparing MUREKA backend dependencies in: $Root" -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $VenvPython)) {
    Write-Host "Creating local virtual environment: $VenvRoot"
    Invoke-BootstrapPython @("-m", "venv", $VenvRoot)
}

Write-Host "Installing Python packages..."
& $VenvPython -m pip install --disable-pip-version-check --upgrade pip
& $VenvPython -m pip install --disable-pip-version-check -r $Requirements

Write-Host "Installing Playwright browser runtime..."
& $VenvPython -m playwright install chromium

Write-Host "Verifying Playwright..."
& $VenvPython -c "import playwright.sync_api; print('MUREKA backend Python ready')"

Write-Host ""
Write-Host "Done. Unity will use: $VenvPython" -ForegroundColor Green
