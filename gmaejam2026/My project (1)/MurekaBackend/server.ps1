$ErrorActionPreference = "Stop"

$HostName = if ($env:MUREKA_BACKEND_HOST) { $env:MUREKA_BACKEND_HOST } else { "127.0.0.1" }
$Port = if ($env:MUREKA_BACKEND_PORT) { [int]$env:MUREKA_BACKEND_PORT } else { 8067 }
$CreateUrl = if ($env:MUREKA_CREATE_URL) { $env:MUREKA_CREATE_URL } else { "https://mureka.ai/ko/create" }
$AutomationTimeoutSeconds = if ($env:MUREKA_AUTOMATION_TIMEOUT_SECONDS) { [double]$env:MUREKA_AUTOMATION_TIMEOUT_SECONDS } else { 600 }
$LoginWaitSeconds = if ($env:MUREKA_LOGIN_WAIT_SECONDS) { [double]$env:MUREKA_LOGIN_WAIT_SECONDS } else { 180 }
$WaitDownloadSeconds = if ($env:MUREKA_WAIT_SECONDS) { [double]$env:MUREKA_WAIT_SECONDS } else { 180 }
$PollSeconds = if ($env:MUREKA_POLL_SECONDS) { [double]$env:MUREKA_POLL_SECONDS } else { 2 }
$LookbackSeconds = if ($env:MUREKA_LOOKBACK_SECONDS) { [double]$env:MUREKA_LOOKBACK_SECONDS } else { 3 }
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$AutomationScript = Join-Path $Root "mureka_web_automation.py"
$Output = if ($env:MUREKA_OUTPUT_DIR) { $env:MUREKA_OUTPUT_DIR } else { Join-Path $Root "output" }
$DownloadDir = if ($env:MUREKA_DOWNLOAD_DIR) { $env:MUREKA_DOWNLOAD_DIR } else { Join-Path $Root "downloads" }
$ProfileDir = if ($env:MUREKA_BROWSER_PROFILE_DIR) { $env:MUREKA_BROWSER_PROFILE_DIR } else { Join-Path $Root "browser_profile" }
$JobsDir = if ($env:MUREKA_AUTOMATION_JOBS_DIR) { $env:MUREKA_AUTOMATION_JOBS_DIR } else { Join-Path $Root "jobs" }

New-Item -ItemType Directory -Force -Path $Output, $DownloadDir, $ProfileDir, $JobsDir | Out-Null

function Send-Json {
    param($Context, [int]$StatusCode, $Payload)
    $json = $Payload | ConvertTo-Json -Depth 16 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $Context.Response.StatusCode = $StatusCode
    $Context.Response.ContentType = "application/json; charset=utf-8"
    $Context.Response.Headers["Access-Control-Allow-Origin"] = "*"
    $Context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type"
    $Context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS"
    $Context.Response.ContentLength64 = $bytes.Length
    $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $Context.Response.Close()
}

function Send-File {
    param($Context, [string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $extension = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
    $contentType = switch ($extension) {
        ".mp3" { "audio/mpeg" }
        ".wav" { "audio/wav" }
        ".ogg" { "audio/ogg" }
        default { "application/octet-stream" }
    }
    $Context.Response.StatusCode = 200
    $Context.Response.ContentType = $contentType
    $Context.Response.Headers["Access-Control-Allow-Origin"] = "*"
    $Context.Response.ContentLength64 = $bytes.Length
    $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $Context.Response.Close()
}

function Read-RequestJson {
    param($Request)
    $reader = [System.IO.StreamReader]::new($Request.InputStream, $Request.ContentEncoding)
    try {
        $body = $reader.ReadToEnd()
    } finally {
        $reader.Dispose()
    }
    if ([string]::IsNullOrWhiteSpace($body)) { return @{} }
    return $body | ConvertFrom-Json
}

function Read-LogTail {
    param([string]$Path, [int]$MaxChars = 4000)
    if (-not (Test-Path -LiteralPath $Path)) { return "" }
    $text = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    if ($null -eq $text) { return "" }
    if ($text.Length -le $MaxChars) { return $text.Trim() }
    return $text.Substring($text.Length - $MaxChars).Trim()
}

function Quote-ProcessArgument {
    param([string]$Value)
    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

function Test-PythonPlaywright {
    param([string]$PythonExe)
    if ([string]::IsNullOrWhiteSpace($PythonExe)) { return $false }
    if ($PythonExe -like "*\WindowsApps\python.exe") { return $false }
    try {
        & $PythonExe -c "import playwright.sync_api" *> $null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

function Get-PythonExecutable {
    $candidates = @()
    if ($env:MUREKA_PYTHON_EXE) { $candidates += $env:MUREKA_PYTHON_EXE }
    $candidates += (Join-Path $Root ".venv_mureka\Scripts\python.exe")

    foreach ($candidate in $candidates) {
        if ((Test-Path -LiteralPath $candidate) -and (Test-PythonPlaywright $candidate)) {
            return $candidate
        }
    }

    throw "Python with Playwright was not found. Run MurekaBackend/setup_mureka_backend.ps1 or set MUREKA_PYTHON_EXE."
}

function Get-NewStableAudioFile {
    param([datetime]$Since)
    $patterns = @("*.mp3", "*.wav", "*.ogg")
    $files = foreach ($pattern in $patterns) {
        Get-ChildItem -LiteralPath $DownloadDir -Filter $pattern -File -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $Output -Filter $pattern -File -ErrorAction SilentlyContinue
    }
    $candidate = @($files | Where-Object { $_.LastWriteTime -ge $Since -and $_.Length -gt 0 } | Sort-Object LastWriteTime -Descending | Select-Object -First 1)[0]
    if ($null -eq $candidate) { return $null }
    $firstSize = $candidate.Length
    Start-Sleep -Milliseconds 900
    $candidate.Refresh()
    if ($candidate.Exists -and $candidate.Length -eq $firstSize) { return $candidate.FullName }
    return $null
}

function Get-BpmFromPrompt {
    param([string]$Prompt)
    $match = [regex]::Match($Prompt, "\b(8[0-9]|9[0-9]|1[0-7][0-9]|180)\s*bpm\b", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($match.Success) { return [int]$match.Groups[1].Value }
    return 128
}

function New-MurekaTrackFromImportedAutomation {
    param([string]$Prompt)
    if (-not (Test-Path -LiteralPath $AutomationScript)) {
        throw "Imported Mureka automation script was not found: $AutomationScript"
    }

    $python = Get-PythonExecutable
    $trackId = "mureka_" + [guid]::NewGuid().ToString("N").Substring(0, 12)
    $timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $jobPath = Join-Path $JobsDir "$trackId`_$timestamp.json"
    $outLog = Join-Path $JobsDir "$trackId`_$timestamp.out.log"
    $errLog = Join-Path $JobsDir "$trackId`_$timestamp.err.log"
    $startedAt = (Get-Date).AddSeconds(-[Math]::Max(0, $LookbackSeconds))

    $job = @{
        prompt = $Prompt
        download_dir = $DownloadDir
        profile_dir = $ProfileDir
        timeout_seconds = $AutomationTimeoutSeconds
        login_wait_seconds = $LoginWaitSeconds
        create_url = $CreateUrl
        headless = $false
    }
    ($job | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $jobPath -Encoding UTF8

    $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = $python
    $processInfo.Arguments = "$(Quote-ProcessArgument $AutomationScript) --job $(Quote-ProcessArgument $jobPath)"
    $processInfo.WorkingDirectory = $Root
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $processInfo.EnvironmentVariables["PYTHONUTF8"] = "1"

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $processInfo
    [void]$process.Start()

    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()
    $deadline = (Get-Date).AddSeconds([Math]::Max(1, $LoginWaitSeconds + $WaitDownloadSeconds + $AutomationTimeoutSeconds))
    $downloaded = $null

    while ((Get-Date) -lt $deadline) {
        $downloaded = Get-NewStableAudioFile $startedAt
        if ($downloaded) { break }

        if ($process.HasExited) {
            break
        }
        Start-Sleep -Seconds ([Math]::Max(1, [int]$PollSeconds))
    }

    if (-not $process.HasExited -and $downloaded) {
        try { $process.Kill() } catch {}
    } elseif (-not $process.HasExited) {
        try { $process.Kill() } catch {}
    }

    try { $process.WaitForExit(5000) | Out-Null } catch {}
    $stdout = $outTask.Result
    $stderr = $errTask.Result
    $stdout | Set-Content -LiteralPath $outLog -Encoding UTF8
    $stderr | Set-Content -LiteralPath $errLog -Encoding UTF8

    if (-not $downloaded) {
        $downloaded = Get-NewStableAudioFile $startedAt
    }

    if (-not $downloaded) {
        $details = (Read-LogTail $errLog)
        if ([string]::IsNullOrWhiteSpace($details)) { $details = Read-LogTail $outLog }
        if ([string]::IsNullOrWhiteSpace($details)) { $details = "No automation log output." }
        throw "Mureka web automation did not download audio. Logs: $outLog / $errLog. $details"
    }

    $extension = [System.IO.Path]::GetExtension($downloaded).ToLowerInvariant()
    if (@(".mp3", ".wav", ".ogg") -notcontains $extension) { $extension = ".mp3" }
    $outPath = Join-Path $Output "$trackId$extension"
    Copy-Item -LiteralPath $downloaded -Destination $outPath -Force

    return @{
        trackId = $trackId
        title = $trackId
        style = "mureka website"
        bpm = Get-BpmFromPrompt $Prompt
        audioUrl = "http://$HostName`:$Port/output/$([System.IO.Path]::GetFileName($outPath))"
        provider = "mureka_web_automation_imported"
        warning = "Imported E:/music mureka_web_automation.py route completed. Logs: $outLog"
    }
}

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://$HostName`:$Port/")
$listener.Start()

Write-Host "Project Mureka backend listening on http://$HostName`:$Port"
Write-Host "Automation script: $AutomationScript"
Write-Host "Download folder: $DownloadDir"
Write-Host "Output: $Output"
try {
    Write-Host "Python: $(Get-PythonExecutable)"
} catch {
    Write-Host "Python: not ready ($($_.Exception.Message))"
}

while ($listener.IsListening) {
    $context = $listener.GetContext()
    try {
        $path = $context.Request.Url.AbsolutePath
        if ($context.Request.HttpMethod -eq "OPTIONS") {
            $context.Response.StatusCode = 204
            $context.Response.Headers["Access-Control-Allow-Origin"] = "*"
            $context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type"
            $context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS"
            $context.Response.Close()
            continue
        }

        if ($context.Request.HttpMethod -eq "GET" -and $path -eq "/health") {
            $python = ""
            $pythonReady = $false
            try {
                $python = Get-PythonExecutable
                $pythonReady = $true
            } catch {
            }
            Send-Json $context 200 @{
                ok = $true
                provider = "mureka_web_automation_imported"
                automationScript = $AutomationScript
                pythonReady = $pythonReady
                python = $python
                browserProfile = $ProfileDir
                downloadDir = $DownloadDir
                output = $Output
                loginWaitSeconds = $LoginWaitSeconds
                automationTimeoutSeconds = $AutomationTimeoutSeconds
                port = $Port
            }
            continue
        }

        if ($context.Request.HttpMethod -eq "GET" -and $path.StartsWith("/output/")) {
            $filename = [System.IO.Path]::GetFileName([System.Uri]::UnescapeDataString($path.Substring(8)))
            $filePath = Join-Path $Output $filename
            if (Test-Path -LiteralPath $filePath) {
                Send-File $context $filePath
            } else {
                Send-Json $context 404 @{ error = "output file not found" }
            }
            continue
        }

        if ($context.Request.HttpMethod -eq "POST" -and $path -eq "/generate") {
            $body = Read-RequestJson $context.Request
            $prompt = "$($body.prompt)".Trim()
            if ([string]::IsNullOrWhiteSpace($prompt)) {
                Send-Json $context 400 @{ error = "prompt is empty" }
                continue
            }
            Send-Json $context 200 (New-MurekaTrackFromImportedAutomation $prompt)
            continue
        }

        Send-Json $context 404 @{ error = "not found" }
    } catch {
        Write-Host "Request failed: $($_.Exception.Message)"
        Send-Json $context 500 @{
            error = $_.Exception.Message
            provider = "mureka_web_automation_imported"
        }
    }
}
