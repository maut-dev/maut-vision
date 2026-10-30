# Сквозной прогон на установленной программе: тестовая сборка установщика (build-installer.ps1 -TestInstall) тихо ставится в короткую
# временную папку, tests/MautVision.E2E проходит сценарий через Core API с упакованным Python worker из этой установки (проект →
# материалы → фоны → генерация → датасет → обучение на GPU → версия → проверки → выгрузки и перенос), затем программа тихо удаляется
# и проверяется, что от неё ничего не осталось — и при ошибке прогона.
param(
    [string]$Installer = (Join-Path $PSScriptRoot '../.dev/installers/MautVision-TestSetup.exe'),
    # Готовый yolo26n.pt вместо загрузки (сверяется по SHA-256 каталога); без него базовая модель скачивается (~5,5 МБ).
    [string]$BaseModelWeights,
    [string]$Device = '0',
    # Оставить рабочую папку прогона (проект, журнал Core, архивы); при ошибке она остаётся всегда.
    [switch]$Keep,
    [switch]$SkipBuild,
    # Тот же сценарий только через MCP: stdio-прокси запускает программу в трее, затем официальный клиент по HTTP (tests/MautVision.E2E/McpRun.cs).
    [switch]$Mcp,
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
$taskRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskAppId = '{6C0C2B8E-3F6B-4C55-9C34-0D5E1B2A7F41}'
$taskTitle = 'Maut Vision (проверка)'
$taskUninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${taskAppId}_is1"
$taskShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$taskTitle.lnk"

$Installer = [IO.Path]::GetFullPath($Installer)
if (-not (Test-Path -LiteralPath $Installer)) { throw "Нет установщика $Installer. Соберите его: .\build-installer.ps1 -TestInstall -Fast" }
if (Test-Path -LiteralPath $taskUninstallKey) { throw "$taskTitle уже установлена: удалите её перед прогоном." }
if ($BaseModelWeights) { $BaseModelWeights = [IO.Path]::GetFullPath($BaseModelWeights) }

$env:DOTNET_CLI_HOME = Join-Path $taskRepository '.dev/dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$taskE2E = Join-Path $taskRepository 'tests/MautVision.E2E/bin/Debug/net10.0/MautVision.E2E.exe'
if (-not $SkipBuild)
{
    & dotnet build (Join-Path $taskRepository 'tests/MautVision.E2E') --configuration Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Сборка tests/MautVision.E2E не удалась.' }
}
if (-not (Test-Path -LiteralPath $taskE2E)) { throw "Нет $taskE2E; запустите без -SkipBuild." }

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

# Short paths: the worker onedir has deep torch paths, the application folder stays within 85 characters.
$taskTempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$taskRoot = Join-Path $taskTempParent ('mve-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$taskAppDir = Join-Path $taskRoot 'Maut Vision'
$taskWork = Join-Path $taskRoot 'work'
if ($taskAppDir.Length -gt 85) { throw "Папка установки длиннее 85 символов: $taskAppDir. Задайте короткий TEMP." }
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskInstalled = $false
$taskPassed = $false
$taskWatch = [Diagnostics.Stopwatch]::StartNew()
try
{
    $taskArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=`"$taskAppDir`"", "/LOG=`"$(Join-Path $taskRoot 'install.log')`"")
    $taskProcess = Start-Process -FilePath $Installer -ArgumentList $taskArguments -PassThru
    $null = $taskProcess.Handle
    $taskInstalled = $true
    if (-not $taskProcess.WaitForExit($TimeoutSeconds * 1000)) { throw "Установщик не завершился за $TimeoutSeconds с." }
    if ($taskProcess.ExitCode -ne 0) { throw "Установка завершилась с кодом $($taskProcess.ExitCode) (журнал $taskRoot\install.log)." }
    Write-Output ("PASS: тихая установка в «{0}» за {1:N0} с." -f $taskAppDir, $taskWatch.Elapsed.TotalSeconds)

    $taskE2EArguments = @('--app-dir', $taskAppDir, '--work-directory', $taskWork, '--device', $Device)
    if ($BaseModelWeights) { $taskE2EArguments += @('--base-model-weights', $BaseModelWeights) }
    if ($Keep) { $taskE2EArguments += '--keep' }
    if ($Mcp) { $taskE2EArguments += '--mcp' }
    & $taskE2E @taskE2EArguments
    if ($LASTEXITCODE -ne 0) { throw "Сквозной прогон не прошёл (код $LASTEXITCODE)." }
    $taskPassed = $true
}
finally
{
    if ($taskInstalled -and (Test-Path -LiteralPath $taskUninstallKey))
    {
        $taskUninstaller = Join-Path $taskAppDir 'unins000.exe'
        $taskProcess = Start-Process -FilePath $taskUninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $taskRoot 'uninstall.log')`"") -PassThru
        $taskProcess.WaitForExit()
        # The uninstaller continues from a copy in TEMP after the first process ends.
        Wait-Until { -not (Test-Path -LiteralPath $taskUninstallKey) -and -not (Test-Path -LiteralPath (Join-Path $taskAppDir 'MautVision.Desktop.exe')) } 300 'удаление программы'
        Wait-Until { -not (Test-Path -LiteralPath $taskAppDir) } 60 'удаление папки программы'
        if (Test-Path -LiteralPath $taskShortcut) { throw 'Ярлык остался после удаления.' }
        Write-Output 'PASS: тихое удаление убрало папку программы, ключ удаления и ярлык.'
    }
    if (Test-Path -LiteralPath $taskUninstallKey)
    {
        Write-Warning "$taskTitle осталась установленной в $taskAppDir; удалите её через «Приложения» Windows."
    }
    elseif ($taskPassed -and -not $Keep)
    {
        for ($taskAttempt = 0; $taskAttempt -lt 60; $taskAttempt++)
        {
            try { Remove-Item -LiteralPath $taskRoot -Recurse -Force -ErrorAction Stop; break }
            catch { Start-Sleep -Milliseconds 500 }
        }
    }
    else
    {
        Write-Output "Папка прогона оставлена: $taskRoot"
    }
}
Write-Output ("PASS: сквозной прогон на установленной программе за {0:N0} с." -f $taskWatch.Elapsed.TotalSeconds)
