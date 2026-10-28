param(
    [switch]$SkipBuild,
    # Другой exe приложения (Release-публикация, установленная программа) вместо Debug-сборки.
    [string]$Application = (Join-Path $PSScriptRoot 'src/MautVision.Desktop/bin/Debug/net10.0-windows/win-x64/MautVision.Desktop.exe'),
    # Масштаб страницы WebView2 (1.25, 1.5) через --force-device-scale-factor: проверка вёрстки при 125 и 150 %.
    [double]$DeviceScaleFactor = 0,
    # Только native WebView2 smoke, без Core smoke.
    [switch]$NativeOnly
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dev/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $PSScriptRoot
try
{
    if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'start.ps1') -NoLaunch }
    else { & (Join-Path $PSScriptRoot 'tools/get-ffmpeg.ps1') }
    $Application = [IO.Path]::GetFullPath($Application)
    if (-not (Test-Path -LiteralPath $Application)) { throw "Нет приложения: $Application" }
    if (-not $NativeOnly)
    {
        dotnet run --project tests/MautVision.Core.Smoke --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Core smoke failed.' }
    }

    $taskTempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $taskSmokeRoot = Join-Path $taskTempParent ('maut-sharp-smoke-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $taskSmokeRoot | Out-Null
    # Creating a project requires an installed base model. The smoke never downloads weights:
    # a file of the catalogue size is reported as installed, content is verified only before use.
    $taskWeights = Join-Path $taskSmokeRoot 'BaseModels/weights'
    New-Item -ItemType Directory -Path $taskWeights | Out-Null
    $taskWeightsFile = [IO.File]::Create((Join-Path $taskWeights 'yolo26n.pt'))
    try { $taskWeightsFile.SetLength(5544453) } finally { $taskWeightsFile.Dispose() }
    # Training verifies the weights by SHA-256 before the worker gets them: the smoke catalogue names the hash of the seeded file.
    $taskCatalogPath = Join-Path $PSScriptRoot 'catalog/yolo-catalog.json'
    $taskCatalogText = [IO.File]::ReadAllText($taskCatalogPath)
    $taskOriginalHash = (($taskCatalogText | ConvertFrom-Json) | Where-Object { $_.id -eq 'yolo26n' }).sha256
    $taskSeededHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $taskWeights 'yolo26n.pt')).Hash.ToLowerInvariant()
    $taskCatalog = Join-Path $taskSmokeRoot 'smoke-catalog.json'
    [IO.File]::WriteAllText($taskCatalog, $taskCatalogText.Replace($taskOriginalHash, $taskSeededHash))
    # The fake worker speaks protocol v1 without PyTorch; it is built with the solution.
    $taskFakeWorker = Join-Path $PSScriptRoot 'tests/MautVision.FakeWorker/bin/Debug/net10.0/MautVision.FakeWorker.exe'
    if (-not (Test-Path -LiteralPath $taskFakeWorker)) { throw 'The fake worker is not built.' }
    New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot '.dev') -Force | Out-Null
    $taskReport = Join-Path $PSScriptRoot '.dev/webview-smoke.json'
    # The report must belong to this run, including failures before WebView initialization.
    if (Test-Path -LiteralPath $taskReport) { Remove-Item -LiteralPath $taskReport }
    try
    {
        $taskExecutable = $Application
        $taskPreviousArguments = $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
        if ($DeviceScaleFactor -gt 0)
        {
            # The started application inherits the variable; WebView2 reads it when its environment is created.
            $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--force-device-scale-factor=' + $DeviceScaleFactor.ToString([Globalization.CultureInfo]::InvariantCulture)
        }
        # A backslash before the closing quote escapes it (C:\work\" or D:\"), so trailing backslashes are doubled.
        $taskQuote = { param([string]$Path) '"' + ($Path -replace '(\\+)$', '$1$1') + '"' }
        try
        {
            $taskProcess = Start-Process -FilePath $taskExecutable -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -ArgumentList @('--work-directory', (& $taskQuote $taskSmokeRoot), '--smoke-report', (& $taskQuote $taskReport), '--base-model-catalog', (& $taskQuote $taskCatalog), '--worker-executable', (& $taskQuote $taskFakeWorker))
        }
        finally
        {
            $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = $taskPreviousArguments
        }
        if (-not $taskProcess.WaitForExit(300000))
        {
            # Process.Kill(bool) is unavailable in Windows PowerShell 5.1; taskkill also stops WebView2 children.
            & taskkill.exe /PID $taskProcess.Id /T /F | Out-Null
            $taskProcess.WaitForExit()
            throw 'WebView2 smoke timed out.'
        }
        if (-not (Test-Path -LiteralPath $taskReport)) { throw 'WebView2 did not write a smoke report.' }
        $taskResult = Get-Content -LiteralPath $taskReport -Raw | ConvertFrom-Json
        if ($taskProcess.ExitCode -ne 0 -or -not $taskResult.success)
        {
            Get-Content -LiteralPath $taskReport -Raw | Write-Output
            throw 'Native WebView2 smoke failed.'
        }
        $taskScale = if ($DeviceScaleFactor -gt 0) { " at scale $DeviceScaleFactor" } else { '' }
        Write-Output ("PASS: native WebView2, React, C# bridge, ProjectManager ({0} checks){1}: {2}" -f $taskResult.checks.Count, $taskScale, $Application)
        Write-Output "Report: $taskReport"
    }
    finally
    {
        # Only the exact temporary directory created by this test may be removed.
        $taskResolvedRoot = [IO.Path]::GetFullPath($taskSmokeRoot)
        $taskExpectedPrefix = $taskTempParent.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $taskResolvedRoot.StartsWith($taskExpectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($taskResolvedRoot)).StartsWith('maut-sharp-smoke-'))
        {
            throw 'Refusing cleanup outside the smoke temporary directory.'
        }
        for ($taskAttempt = 0; $taskAttempt -lt 60; $taskAttempt++)
        {
            try
            {
                Remove-Item -LiteralPath $taskResolvedRoot -Recurse -Force -ErrorAction Stop
                break
            }
            catch
            {
                if ($taskAttempt -eq 59) { throw }
                # WebView2 subprocesses may briefly retain profile files after the host exits.
                Start-Sleep -Milliseconds 500
            }
        }
    }
}
finally
{
    Pop-Location
}


