$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$VenvRoot = Join-Path $Root ".venv_mureka"
$VenvPython = Join-Path $VenvRoot "Scripts\python.exe"
$Requirements = Join-Path $Root "requirements.txt"

function Get-BootstrapPython {
    if ($env:MUREKA_BOOTSTRAP_PYTHON_EXE) {
        return @{ File = $env:MUREKA_BOOTSTRAP_PYTHON_EXE; PrefixArgs = @() }
    }

    if ($env:MUREKA_PYTHON_EXE) {
        return @{ File = $env:MUREKA_PYTHON_EXE; PrefixArgs = @() }
    }

    $pyLauncher = Get-Command py -ErrorAction SilentlyContinue
    if ($pyLauncher) {
        return @{ File = $pyLauncher.Source; PrefixArgs = @("-3") }
    }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python -and $python.Source -notlike "*\WindowsApps\python.exe") {
        return @{ File = $python.Source; PrefixArgs = @() }
    }

    $codexPython = Join-Path $env:USERPROFILE ".cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
    if (Test-Path -LiteralPath $codexPython) {
        return @{ File = $codexPython; PrefixArgs = @() }
    }

    $uv = Get-Command uv -ErrorAction SilentlyContinue
    if ($uv) {
        Write-Host "Python 3 was not found on PATH. Trying uv-managed Python 3.12..." -ForegroundColor Yellow
        & $uv.Source python install 3.12
        $uvPython = (& $uv.Source python find 3.12 | Select-Object -First 1)
        if ($LASTEXITCODE -eq 0 -and $uvPython -and (Test-Path -LiteralPath $uvPython)) {
            return @{ File = $uvPython; PrefixArgs = @() }
        }
    }

    throw "Python 3 was not found. Install Python 3 or set MUREKA_BOOTSTRAP_PYTHON_EXE."
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
