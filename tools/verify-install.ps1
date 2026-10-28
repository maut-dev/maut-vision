# Проверка установщика на этой машине: тестовая сборка (build-installer.ps1 -TestInstall, отдельные AppId и имя) ставится тихо в
# папку с кириллицей и пробелами, файлы сверяются с payload, smoke идёт на установленном exe, обновление выполняется поверх запущенного
# приложения, затем тихое удаление убирает программу, ключ и ярлык и оставляет рабочую директорию.
param(
    [string]$Installer = (Join-Path $PSScriptRoot '../.dev/installers/MautVision-TestSetup.exe'),
    # Публикация, из которой собран установщик; по умолчанию единственная папка в .dev/publish.
    [string]$Payload,
    [string]$Worker = (Join-Path $PSScriptRoot '../.dev/python-worker/maut-training-worker'),
    [switch]$SkipSmoke,
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
$taskRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskAppId = '{6C0C2B8E-3F6B-4C55-9C34-0D5E1B2A7F41}'
$taskTitle = 'Maut Vision (проверка)'
$taskUninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${taskAppId}_is1"
$taskShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$taskTitle.lnk"
$taskVersion = ([xml](Get-Content -LiteralPath (Join-Path $taskRepository 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

$Installer = [IO.Path]::GetFullPath($Installer)
if (-not (Test-Path -LiteralPath $Installer)) { throw "Нет установщика $Installer. Соберите его: .\build-installer.ps1 -TestInstall -Fast" }
if (-not $Payload)
{
    $taskPayloads = @(Get-ChildItem -LiteralPath (Join-Path $taskRepository '.dev/publish') -Directory)
    if ($taskPayloads.Count -ne 1) { throw 'Укажите -Payload: в .dev/publish не одна публикация.' }
    $Payload = $taskPayloads[0].FullName
}
$Payload = [IO.Path]::GetFullPath($Payload)
$Worker = [IO.Path]::GetFullPath($Worker)
if (Test-Path -LiteralPath $taskUninstallKey) { throw "$taskTitle уже установлена: удалите её перед проверкой." }
$taskUserData = Join-Path $env:LOCALAPPDATA 'MautVision'
$taskUserDataBefore = Test-Path -LiteralPath $taskUserData

function Wait-Until([scriptblock]$Condition, [int]$Seconds, [string]$Label)
{
    $taskWatch = [Diagnostics.Stopwatch]::StartNew()
    while ($taskWatch.Elapsed.TotalSeconds -lt $Seconds)
    {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "Не дождались: $Label."
}

function Read-SharedText([string]$Path)
{
    $taskStream = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try { return (New-Object IO.StreamReader($taskStream)).ReadToEnd() } finally { $taskStream.Dispose() }
}

# Setup runs as a whole, without the shell: exit code 0 — success.
function Invoke-Setup([string]$Directory, [string]$Log, [string[]]$Extra = @())
{
    $taskArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=`"$Directory`"", "/LOG=`"$Log`"") + $Extra
    $taskProcess = Start-Process -FilePath $Installer -ArgumentList $taskArguments -PassThru
    $null = $taskProcess.Handle
    if (-not $taskProcess.WaitForExit($TimeoutSeconds * 1000)) { throw "Установщик не завершился за $TimeoutSeconds с." }
    return $taskProcess.ExitCode
}

$taskTempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$taskRoot = Join-Path $taskTempParent ('mvi-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$taskAppDir = Join-Path $taskRoot 'Мои программы\Maut Vision'
$taskWork = Join-Path $taskRoot 'Рабочая папка'
New-Item -ItemType Directory -Path $taskWork -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $taskWork 'host-settings.json'), '{"trayNoticeShown":true}')
$taskRunning = $null
try
{
    # 1. Silent install into a folder with Cyrillic letters and spaces.
    $taskWatch = [Diagnostics.Stopwatch]::StartNew()
    $taskCode = Invoke-Setup $taskAppDir (Join-Path $taskRoot 'install.log')
    if ($taskCode -ne 0) { throw "Установка завершилась с кодом $taskCode (журнал $taskRoot\install.log)." }
    Write-Output ("PASS: тихая установка в «{0}» за {1:N0} с." -f $taskAppDir, $taskWatch.Elapsed.TotalSeconds)

    # 2. The installed files are the payload and the worker, plus the uninstaller.
    $taskExpected = @{}
    foreach ($taskFile in Get-ChildItem -LiteralPath $Payload -Recurse -File) { $taskExpected[$taskFile.FullName.Substring($Payload.Length + 1)] = $taskFile.Length }
    foreach ($taskFile in Get-ChildItem -LiteralPath $Worker -Recurse -File)
    {
        $taskExpected['python\maut-training-worker\' + $taskFile.FullName.Substring($Worker.Length + 1)] = $taskFile.Length
    }
    $taskActual = @{}
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskAppDir -Recurse -File) { $taskActual[$taskFile.FullName.Substring($taskAppDir.Length + 1)] = $taskFile.Length }
    $taskMissing = @($taskExpected.Keys | Where-Object { -not $taskActual.ContainsKey($_) -or $taskActual[$_] -ne $taskExpected[$_] })
    $taskExtra = @($taskActual.Keys | Where-Object { -not $taskExpected.ContainsKey($_) -and $_ -notmatch '^unins\d{3}\.(exe|dat)$' })
    if ($taskMissing.Count -or $taskExtra.Count)
    {
        $taskMissingText = ($taskMissing | Select-Object -First 10) -join ', '
        $taskExtraText = ($taskExtra | Select-Object -First 10) -join ', '
        throw "Файлы не совпали с payload: нет или другой размер — $taskMissingText; лишние — $taskExtraText."
    }
    $taskWorkerExe = 'python\maut-training-worker\maut-training-worker.exe'
    if ((Get-FileHash -LiteralPath (Join-Path $Worker 'maut-training-worker.exe')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskAppDir $taskWorkerExe)).Hash)
    {
        throw 'SHA-256 установленного maut-training-worker.exe не совпал со сборкой.'
    }
    Write-Output ("PASS: {0} файлов совпали с payload и worker по путям и размерам, SHA-256 worker exe совпал." -f $taskExpected.Count)

    # 3. Uninstall key and Start menu shortcut.
    $taskKey = Get-ItemProperty -LiteralPath $taskUninstallKey
    if ($taskKey.DisplayVersion -ne $taskVersion -or $taskKey.DisplayName -ne $taskTitle -or
        [IO.Path]::GetFullPath($taskKey.InstallLocation).TrimEnd('\') -ne $taskAppDir)
    {
        throw "Ключ удаления не совпал: $($taskKey.DisplayName) $($taskKey.DisplayVersion) $($taskKey.InstallLocation)."
    }
    if (-not (Test-Path -LiteralPath $taskShortcut)) { throw "Нет ярлыка $taskShortcut." }
    $taskTarget = (New-Object -ComObject WScript.Shell).CreateShortcut($taskShortcut).TargetPath
    if ($taskTarget -ne (Join-Path $taskAppDir 'MautVision.Desktop.exe')) { throw "Ярлык указывает на $taskTarget." }
    Write-Output "PASS: ключ удаления (версия $taskVersion) и ярлык в меню «Пуск»."

    # 4. The smoke on the installed application.
    $taskExe = Join-Path $taskAppDir 'MautVision.Desktop.exe'
    if (-not $SkipSmoke)
    {
        & (Join-Path $taskRepository 'smoke.ps1') -SkipBuild -NativeOnly -Application $taskExe
        Write-Output 'PASS: smoke на установленном приложении.'
    }

    # 5. Update over the running application: Restart Manager closes it, the files are replaced.
    $taskQuote = '"' + ($taskWork -replace '(\\+)$', '$1$1') + '"'
    $taskRunning = Start-Process -FilePath $taskExe -ArgumentList @('--work-directory', $taskQuote) -WorkingDirectory $taskWork -PassThru
    $null = $taskRunning.Handle
    Wait-Until {
        $taskLog = Get-ChildItem -LiteralPath (Join-Path $taskWork 'logs') -Filter ("maut-vision-*-{0}.log" -f $taskRunning.Id) -ErrorAction SilentlyContinue | Select-Object -First 1
        $taskLog -and (Read-SharedText $taskLog.FullName) -match 'Startup stage library_ready'
    } 120 'запущенное приложение показало библиотеку'
    $taskCode = Invoke-Setup $taskAppDir (Join-Path $taskRoot 'update.log') @('/FORCECLOSEAPPLICATIONS')
    if ($taskCode -ne 0) { throw "Обновление завершилось с кодом $taskCode (журнал $taskRoot\update.log)." }
    if (-not $taskRunning.WaitForExit(30000)) { throw 'Обновление не закрыло запущенное приложение.' }
    $taskLogText = Read-SharedText (Get-ChildItem -LiteralPath (Join-Path $taskWork 'logs') -Filter ("maut-vision-*-{0}.log" -f $taskRunning.Id)).FullName
    if ($taskLogText -notmatch 'the application exits') { throw 'Приложение закрыто не по запросу Restart Manager (нет записи в журнале).' }
    Write-Output ("PASS: обновление поверх запущенного приложения закрыло его (код {0}) и завершилось с кодом 0." -f $taskRunning.ExitCode)
    $taskRunning = $null

    # 6. Silent uninstall: the program, its key and shortcut go, the work directory stays.
    $taskUninstaller = Join-Path $taskAppDir 'unins000.exe'
    $taskProcess = Start-Process -FilePath $taskUninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $taskRoot 'uninstall.log')`"") -PassThru
    $taskProcess.WaitForExit()
    # The uninstaller continues from a copy in TEMP after the first process ends.
    Wait-Until { -not (Test-Path -LiteralPath $taskUninstallKey) -and -not (Test-Path -LiteralPath (Join-Path $taskAppDir 'MautVision.Desktop.exe')) } 300 'удаление программы'
    Wait-Until { -not (Test-Path -LiteralPath $taskAppDir) } 60 'удаление папки программы'
    if (Test-Path -LiteralPath $taskShortcut) { throw 'Ярлык остался после удаления.' }
    if (-not (Test-Path -LiteralPath (Join-Path $taskWork 'logs'))) { throw 'Рабочая директория удалена вместе с программой.' }
    if ($taskUserDataBefore -and -not (Test-Path -LiteralPath $taskUserData)) { throw "Тихое удаление тронуло $taskUserData." }
    Write-Output 'PASS: тихое удаление убрало папку программы, ключ и ярлык; рабочая директория и данные пользователя остались.'
}
finally
{
    if ($taskRunning -and -not $taskRunning.HasExited)
    {
        & taskkill.exe /PID $taskRunning.Id /T /F | Out-Null
        $taskRunning.WaitForExit()
    }
    if (Test-Path -LiteralPath $taskUninstallKey)
    {
        Write-Warning "$taskTitle осталась установленной в $taskAppDir; удалите её через «Приложения» Windows."
    }
    else
    {
        for ($taskAttempt = 0; $taskAttempt -lt 60; $taskAttempt++)
        {
            try { Remove-Item -LiteralPath $taskRoot -Recurse -Force -ErrorAction Stop; break }
            catch { Start-Sleep -Milliseconds 500 }
        }
    }
}
