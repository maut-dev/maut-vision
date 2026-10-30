# Публикация (self-contained, ReadyToRun) и установщик Inno Setup: .dev/installers/MautVision-Setup-<версия>.exe.
param(
    # Только публикация .NET в .dev/publish/<guid>, без worker и установщика.
    [switch]$PublishOnly,
    [string]$InnoCompiler,
    # Быстрое сжатие для проверок (lzma2/fast вместо lzma2/ultra64).
    [switch]$Fast,
    # Сборка для tools/verify-install.ps1: другие AppId, имя и папка, установка не затрагивает настоящую.
    [switch]$TestInstall,
    # Composite ReadyToRun (runtime и приложение одним образом): вариант для замеров.
    [switch]$Composite
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dev/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if (-not $PublishOnly)
{
    if (-not $InnoCompiler)
    {
        $taskCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($taskCommand) { $InnoCompiler = $taskCommand.Source }
        else
        {
            # Inno Setup 6 installs per machine (Program Files) or per user (%LOCALAPPDATA%\Programs).
            foreach ($taskCandidate in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"))
            {
                if (Test-Path -LiteralPath $taskCandidate) { $InnoCompiler = $taskCandidate; break }
            }
        }
    }
    if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler))
    {
        throw 'Не найден Inno Setup 6 (ISCC.exe). Установите Inno Setup 6.5+, укажите -InnoCompiler либо используйте -PublishOnly для подготовки payload.'
    }
    # The worker must be built from the current sources (BUILD.txt sourceHash), otherwise the installer would ship an older one.
    $taskWorker = Join-Path $PSScriptRoot '.dev/python-worker/maut-training-worker'
    if (-not (Test-Path -LiteralPath (Join-Path $taskWorker 'maut-training-worker.exe')))
    {
        throw "Нет собранного Python worker в $taskWorker. Соберите его: python/scripts/build-worker.ps1."
    }
    . (Join-Path $PSScriptRoot 'python/scripts/worker-source-hash.ps1')
    $taskWorkerBuild = Assert-WorkerBuildCurrent -Repository $PSScriptRoot -WorkerDirectory $taskWorker
    Write-Output ("Python worker: commit {0}, built {1}." -f $taskWorkerBuild['commit'], $taskWorkerBuild['builtAt'])
    & (Join-Path $PSScriptRoot 'tools/get-webview2.ps1')
    $taskBootstrapper = Join-Path $PSScriptRoot '.dev/tools/webview2/MicrosoftEdgeWebview2Setup.exe'
}

# The one version of the release.
$taskVersion = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $taskVersion) { throw 'В Directory.Build.props нет Version.' }

# UI, ffmpeg and the Release build of the solution.
& (Join-Path $PSScriptRoot 'start.ps1') -NoLaunch -Configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

# Earlier payloads are several hundred megabytes each; only the new one stays.
$taskPublishRoot = Join-Path $PSScriptRoot '.dev/publish'
if (Test-Path -LiteralPath $taskPublishRoot)
{
    Get-ChildItem -LiteralPath $taskPublishRoot -Directory | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
}
$taskPublish = Join-Path $taskPublishRoot ([Guid]::NewGuid().ToString('N'))
$taskProject = Join-Path $PSScriptRoot 'src/MautVision.Desktop/MautVision.Desktop.csproj'
# Restore and publish in one step: a separate restore with --no-restore published package assemblies without ReadyToRun
# next to ReadyToRun application assemblies, and the host failed fast at start (0xC0000602) before writing its log.
$taskProperties = @('-p:SelfContained=true', '-p:PublishReadyToRun=true')
if ($Composite) { $taskProperties += '-p:PublishReadyToRunComposite=true' }
dotnet publish $taskProject --configuration Release --runtime win-x64 --output $taskPublish @taskProperties
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# The stdio MCP proxy (maut-vision-mcp.exe) runs on the self-contained runtime of the application: only its own files join the payload, and
# every assembly it shares with the application must already be there in the same or a newer version.
$taskProxyPublish = Join-Path $taskPublishRoot ('proxy-' + [Guid]::NewGuid().ToString('N'))
dotnet publish (Join-Path $PSScriptRoot 'src/MautVision.McpProxy/MautVision.McpProxy.csproj') --configuration Release --runtime win-x64 --output $taskProxyPublish -p:SelfContained=true
if ($LASTEXITCODE -ne 0) { throw 'Publish of the MCP proxy failed.' }
$taskProxyAdded = @()
foreach ($taskFile in Get-ChildItem -LiteralPath $taskProxyPublish -Recurse -File)
{
    $taskRelative = $taskFile.FullName.Substring($taskProxyPublish.Length + 1)
    $taskTarget = Join-Path $taskPublish $taskRelative
    if (-not (Test-Path -LiteralPath $taskTarget))
    {
        New-Item -ItemType Directory -Path (Split-Path -Parent $taskTarget) -Force | Out-Null
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTarget
        $taskProxyAdded += $taskRelative
        continue
    }
    if ($taskFile.Extension -ne '.dll') { continue }
    try { $taskNeeded = [Reflection.AssemblyName]::GetAssemblyName($taskFile.FullName).Version } catch { continue }
    $taskShipped = [Reflection.AssemblyName]::GetAssemblyName($taskTarget).Version
    if ($taskShipped -lt $taskNeeded) { throw "MCP proxy needs $taskRelative $taskNeeded, the application ships $taskShipped." }
}
Remove-Item -LiteralPath $taskProxyPublish -Recurse -Force
$taskUnexpected = @($taskProxyAdded | Where-Object { $_ -notlike 'maut-vision-mcp.*' })
if ($taskUnexpected.Count -gt 0) { throw ("MCP proxy brought files the application does not have: " + ($taskUnexpected -join ', ')) }
$taskProxyBytes = ($taskProxyAdded | ForEach-Object { (Get-Item -LiteralPath (Join-Path $taskPublish $_)).Length } | Measure-Object -Sum).Sum
Write-Output ("MCP proxy: {0} ({1:N0} KB)" -f ($taskProxyAdded -join ', '), ($taskProxyBytes / 1KB))
# It must start on that runtime: with an empty work directory it reports that MCP is off and ends with code 1.
$taskProxyWork = Join-Path ([IO.Path]::GetTempPath()) ('maut-proxy-start-' + [Guid]::NewGuid().ToString('N'))
$taskProxyError = Join-Path ([IO.Path]::GetTempPath()) ('maut-proxy-start-' + [Guid]::NewGuid().ToString('N') + '.txt')
try
{
    $taskProxy = Start-Process -FilePath (Join-Path $taskPublish 'maut-vision-mcp.exe') -ArgumentList @('--work-directory', "`"$taskProxyWork`"") -NoNewWindow -PassThru `
        -RedirectStandardError $taskProxyError
    $null = $taskProxy.Handle
    if (-not $taskProxy.WaitForExit(30000)) { $taskProxy.Kill(); throw 'The MCP proxy did not end within 30 s.' }
    $taskProxyText = Get-Content -LiteralPath $taskProxyError -Raw
    if ($taskProxy.ExitCode -ne 1 -or $taskProxyText -notmatch 'turned off') { throw "The MCP proxy did not start: exit code $($taskProxy.ExitCode), $taskProxyText" }
}
finally
{
    Remove-Item -LiteralPath $taskProxyError -Force -ErrorAction SilentlyContinue
}

# The published host must start: --exit-when-ready leaves after the main window is shown, in a throwaway work directory.
$taskStartWork = Join-Path ([IO.Path]::GetTempPath()) ('maut-publish-start-' + [Guid]::NewGuid().ToString('N'))
try
{
    $taskStart = Start-Process -FilePath (Join-Path $taskPublish 'MautVision.Desktop.exe') -ArgumentList @('--work-directory', "`"$taskStartWork`"", '--exit-when-ready') -PassThru
    if (-not $taskStart.WaitForExit(120000)) { & taskkill.exe /PID $taskStart.Id /T /F | Out-Null; throw 'The published application did not start within 2 minutes.' }
    if ($taskStart.ExitCode -ne 0) { throw ('The published application failed to start: exit code 0x{0:X8}.' -f $taskStart.ExitCode) }
}
finally
{
    Remove-Item -LiteralPath $taskStartWork -Recurse -Force -ErrorAction SilentlyContinue
}

# start.ps1 fetched the pinned ffmpeg; the payload must carry it, the web build and the licences.
$taskRequired = @('MautVision.Desktop.exe', 'maut-vision-mcp.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'web/index.html', 'catalog/yolo-catalog.json')
$taskRequired += @('ffmpeg.exe', 'ffprobe.exe', 'avcodec-62.dll', 'avdevice-62.dll', 'avfilter-11.dll', 'avformat-62.dll', 'avutil-60.dll',
    'swresample-6.dll', 'swscale-9.dll', 'LICENSE.txt', 'VERSION.txt') | ForEach-Object { "tools/ffmpeg/$_" }
foreach ($taskFile in $taskRequired)
{
    if (-not (Test-Path -LiteralPath (Join-Path $taskPublish $taskFile))) { throw "В публикации нет $taskFile." }
}
$taskFiles = @(Get-ChildItem -LiteralPath $taskPublish -Recurse -File)
Write-Output ("Payload: {0} ({1} files, {2:N0} MB, version {3})" -f $taskPublish, $taskFiles.Count, (($taskFiles | Measure-Object Length -Sum).Sum / 1MB), $taskVersion)
if ($PublishOnly) { return }

$taskInstallerDirectory = Join-Path $PSScriptRoot '.dev/installers'
New-Item -ItemType Directory -Path $taskInstallerDirectory -Force | Out-Null
$taskDefines = @("/DAppVersion=$taskVersion", "/DPublishDir=$taskPublish", "/DWorkerDir=$([IO.Path]::GetFullPath($taskWorker))",
    "/DWebView2Bootstrapper=$([IO.Path]::GetFullPath($taskBootstrapper))")
if ($Fast) { $taskDefines += '/DFast' }
if ($TestInstall) { $taskDefines += '/DTestInstall' }
$taskWatch = [Diagnostics.Stopwatch]::StartNew()
& $InnoCompiler @taskDefines ("/O" + $taskInstallerDirectory) (Join-Path $PSScriptRoot 'packaging/MautVision.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$taskName = if ($TestInstall) { 'MautVision-TestSetup.exe' } else { "MautVision-Setup-$taskVersion.exe" }
$taskInstaller = Get-Item -LiteralPath (Join-Path $taskInstallerDirectory $taskName)
Write-Output ("Installer: {0} ({1:N0} MB, compiled in {2:N0} s)" -f $taskInstaller.FullName, ($taskInstaller.Length / 1MB), $taskWatch.Elapsed.TotalSeconds)
# The hash is published next to the download link; users compare it with Get-FileHash.
$taskHash = (Get-FileHash -LiteralPath $taskInstaller.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($taskInstaller.FullName + '.sha256', ("{0} *{1}`n" -f $taskHash, $taskInstaller.Name), (New-Object Text.UTF8Encoding($false)))
Write-Output ("SHA-256: {0}" -f $taskHash)
