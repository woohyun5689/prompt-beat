$ErrorActionPreference = "Stop"

$backend = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:MUREKA_BACKEND_HOST = if ($env:MUREKA_BACKEND_HOST) { $env:MUREKA_BACKEND_HOST } else { "127.0.0.1" }
$env:MUREKA_BACKEND_PORT = if ($env:MUREKA_BACKEND_PORT) { $env:MUREKA_BACKEND_PORT } else { "8067" }

Set-Location -LiteralPath $backend
Write-Host ""
Write-Host "Starting project Mureka imported automation backend on http://$env:MUREKA_BACKEND_HOST`:$env:MUREKA_BACKEND_PORT" -ForegroundColor Cyan
Write-Host "Backend folder: $backend"
Write-Host "Mode: imported mureka_web_automation.py route, no API key"
Write-Host ""

& "$backend\server.ps1"
