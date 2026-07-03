$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$VenvRoot = Join-Path $Root ".venv_mureka"
$VenvPython = Join-Path $VenvRoot "Scripts\python.exe"
$Requirements = Join-Path $Root "requirements.txt"

function Get-BootstrapPython {
    if ($env:MUREKA_BOOTSTRAP_PYTHON_EXE) {
        return @{ File = $env:MUREKA_BOOTSTRAP_PYTHON_EXE; PrefixArgs = @() }
    }

    $pyLauncher = Get-Command py -ErrorAction SilentlyContinue
    if ($pyLauncher) {
        return @{ File = $pyLauncher.Source; PrefixArgs = @("-3") }
    }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python -and $python.Source -notlike "*\WindowsApps\python.exe") {
        return @{ File = $python.Source; PrefixArgs = @() }
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
