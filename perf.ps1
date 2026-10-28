param(
    [int]$Runs = 5,
    [int]$WarmupRuns = 1,
    [switch]$SkipBuild,
    [switch]$SkipBench,
    [switch]$SkipDatasets,
    [switch]$SkipTransfer,
    [string]$Root = (Join-Path $PSScriptRoot '.dev/perf-library'),
    [int]$TimeoutSeconds = 120,
    # Замеряемый exe приложения (Release-публикация, установленная программа) вместо Debug-сборки.
    [string]$Application = (Join-Path $PSScriptRoot 'src/MautVision.Desktop/bin/Debug/net10.0-windows/win-x64/MautVision.Desktop.exe')
)

# Large library performance check: generates the library if needed (resumable), runs the Core benchmark and
# measures the application start until the UI reports the library is shown. Slow; not part of smoke.ps1.
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dev/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$LibraryReadyTargetMs = 2000
Push-Location $PSScriptRoot
try
{
    if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'start.ps1') -NoLaunch }
    $Root = [IO.Path]::GetFullPath($Root)
    $perfTool = Join-Path $PSScriptRoot 'tests/MautVision.Perf/bin/Debug/net10.0-windows/MautVision.Perf.exe'
    $application = [IO.Path]::GetFullPath($Application)
    if (-not (Test-Path -LiteralPath $application)) { throw "Нет приложения: $application" }
    $missed = $false

    # Completes a partial library and returns quickly when it is complete.
    & $perfTool generate --root $Root
    if ($LASTEXITCODE -ne 0) { throw 'Library generation failed.' }

    if (-not $SkipBench)
    {
        & $perfTool bench --root $Root --runs $Runs --out (Join-Path $PSScriptRoot '.dev/perf-bench.json')
        if ($LASTEXITCODE -eq 3) { $missed = $true }
        elseif ($LASTEXITCODE -ne 0) { throw 'Core benchmark failed.' }
    }

    if (-not $SkipDatasets)
    {
        # Plan, build, sample page, integrity check and ZIP export of the largest project, once (a build takes tens of seconds).
        & $perfTool bench-datasets --root $Root --out (Join-Path $PSScriptRoot '.dev/perf-datasets.json')
        if ($LASTEXITCODE -eq 3) { $missed = $true }
        elseif ($LASTEXITCODE -ne 0) { throw 'Dataset benchmark failed.' }
    }

    if (-not $SkipTransfer)
    {
        # Export of the largest project to one ZIP, import of it as a copy and deletion of the copy, once.
        & $perfTool bench-transfer --root $Root --out (Join-Path $PSScriptRoot '.dev/perf-transfer.json')
        if ($LASTEXITCODE -eq 3) { $missed = $true }
        elseif ($LASTEXITCODE -ne 0) { throw 'Transfer benchmark failed.' }
    }

    # A backslash before the closing quote escapes it (C:\work\" or D:\"), so trailing backslashes are doubled.
    $quote = { param([string]$Path) '"' + ($Path -replace '(\\+)$', '$1$1') + '"' }
    $logs = Join-Path $Root 'logs'
    $results = @()
    for ($run = 1; $run -le $WarmupRuns + $Runs; $run++)
    {
        $process = Start-Process -FilePath $application -WorkingDirectory $PSScriptRoot -WindowStyle Normal -PassThru `
            -ArgumentList @('--work-directory', (& $quote $Root), '--exit-when-ready')
        # Windows PowerShell reports ExitCode only if the handle was opened while the process ran.
        $null = $process.Handle
        if (-not $process.WaitForExit($TimeoutSeconds * 1000))
        {
            $process.Kill()
            $process.WaitForExit()
            throw "The application did not report a ready library within $TimeoutSeconds s."
        }
        if ($process.ExitCode -ne 0) { throw "The application exited with code $($process.ExitCode)." }
        $log = Get-ChildItem -LiteralPath $logs -Filter ("maut-vision-*-{0}.log" -f $process.Id) | Sort-Object LastWriteTime | Select-Object -Last 1
        if ($null -eq $log) { throw "No log of process $($process.Id) in $logs." }
        $text = [IO.File]::ReadAllText($log.FullName)
        $stages = [ordered]@{ run = $run; warmup = ($run -le $WarmupRuns) }
        foreach ($match in [regex]::Matches($text, 'Startup stage (\w+) reached in (\d+) ms'))
        {
            if (-not $stages.Contains($match.Groups[1].Value)) { $stages[$match.Groups[1].Value] = [int]$match.Groups[2].Value }
        }
        $peak = [regex]::Match($text, 'Peak working set of the host process: (\d+) MB')
        if ($peak.Success) { $stages['peak_working_set_mb'] = [int]$peak.Groups[1].Value }
        if (-not $stages.Contains('library_ready')) { throw "Run $run did not record library_ready: $($log.FullName)" }
        # Runs stay independent: the next one starts after the WebView2 processes of the profile have exited.
        # A long shutdown here means a pending browser task (it was 6.5 s while SmartScreen checked the UI host).
        $webViewProfile = [regex]::Escape((Join-Path $Root 'WebView2'))
        $shutdown = [Diagnostics.Stopwatch]::StartNew()
        while ($shutdown.Elapsed.TotalSeconds -lt 30 -and @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
            Where-Object { $_.CommandLine -match $webViewProfile }).Count -gt 0)
        {
            Start-Sleep -Milliseconds 100
        }
        $stages['webview_shutdown_ms'] = [int]$shutdown.Elapsed.TotalMilliseconds
        $results += [pscustomobject]$stages
        Write-Output ("Start {0}/{1}{2}: library ready in {3} ms." -f $run, ($WarmupRuns + $Runs), $(if ($run -le $WarmupRuns) { ' (warm-up)' } else { '' }), $stages['library_ready'])
    }

    $measured = @($results | Where-Object { -not $_.warmup })
    $names = @('loading_window_shown', 'core_initialized', 'main_window_shown', 'webview_ready', 'ui_navigation_completed', 'library_ready', 'peak_working_set_mb', 'webview_shutdown_ms')
    $median = { param([double[]]$Values) $sorted = $Values | Sort-Object; $count = $sorted.Count; if ($count % 2) { $sorted[($count - 1) / 2] } else { ($sorted[$count / 2 - 1] + $sorted[$count / 2]) / 2 } }
    Write-Output ''
    Write-Output ("Application start of {0} on {1} ({2} measured runs, {3} warm-up):" -f $application, $Root, $measured.Count, $WarmupRuns)
    Write-Output ('{0,-26}{1,10}{2,10}{3,10}' -f 'Stage (ms since start)', 'Median', 'Min', 'Max')
    $summary = [ordered]@{}
    foreach ($name in $names)
    {
        $values = @($measured | ForEach-Object { $_.$name } | Where-Object { $null -ne $_ })
        if ($values.Count -eq 0) { continue }
        $summary[$name] = & $median $values
        Write-Output ('{0,-26}{1,10}{2,10}{3,10}' -f $name, $summary[$name], ($values | Measure-Object -Minimum).Minimum, ($values | Measure-Object -Maximum).Maximum)
    }
    $verdict = if ($summary['library_ready'] -le $LibraryReadyTargetMs) { 'ok' } else { 'MISSED' }
    if ($verdict -ne 'ok') { $missed = $true }
    Write-Output ("Time to library ready: median {0} ms, target < {1} ms: {2}." -f $summary['library_ready'], $LibraryReadyTargetMs, $verdict)
    $report = Join-Path $PSScriptRoot '.dev/perf-startup.json'
    [ordered]@{ date = (Get-Date).ToUniversalTime().ToString('o'); application = $application; root = $Root; medians = $summary; runs = $results } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $report -Encoding UTF8
    Write-Output "Report: $report"
    if ($missed) { exit 3 }
}
finally
{
    Pop-Location
}
