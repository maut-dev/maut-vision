param(
    [switch]$NoLaunch,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$WorkDirectory = (Join-Path $PSScriptRoot '.dev/DevWorkDirectory'),
    # Интерпретатор окружения worker (python.exe venv); по умолчанию MAUT_PYTHON, затем .dev/python-venv (tools/setup-python.ps1),
    # затем собранный worker из .dev/python-worker.
    [string]$Python = $env:MAUT_PYTHON
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dev/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
Push-Location $PSScriptRoot
try
{
    $WorkDirectory = [IO.Path]::GetFullPath($WorkDirectory)
    if (-not (Test-Path -LiteralPath 'ui/node_modules/.bin/vite.cmd'))
    {
        npm ci --prefix ui --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось установить web dependencies.' }
    }
    # The pinned LGPL ffmpeg build is fetched once into .dev/tools/ffmpeg; the Desktop build copies it to tools/ffmpeg.
    & (Join-Path $PSScriptRoot 'tools/get-ffmpeg.ps1')
    npm --prefix ui run build
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать интерфейс.' }
    dotnet restore MautVision.slnx --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось восстановить .NET dependencies.' }
    dotnet build MautVision.slnx --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать приложение.' }

    if (-not $NoLaunch)
    {
        $taskApplication = Join-Path $PSScriptRoot "src/MautVision.Desktop/bin/$Configuration/net10.0-windows/win-x64/MautVision.Desktop.exe"
        # The application prepares the work directory itself; it must exist only to serve as the process directory.
        New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
        # A backslash before the closing quote escapes it (C:\work\" or D:\"), so trailing backslashes are doubled.
        $taskArgument = '"' + ($WorkDirectory -replace '(\\+)$', '$1$1') + '"'
        $taskQuote = { param([string]$Path) '"' + ($Path -replace '(\\+)$', '$1$1') + '"' }
        $taskArguments = @('--work-directory', $taskArgument)
        # Python worker: the interpreter with python/ sources (development), else a worker built by python/scripts/build-worker.ps1.
        $taskBuiltWorker = Join-Path $PSScriptRoot '.dev/python-worker/maut-training-worker/maut-training-worker.exe'
        $taskVenvPython = Join-Path $PSScriptRoot '.dev/python-venv/Scripts/python.exe'
        if (-not $Python -and (Test-Path -LiteralPath $taskVenvPython)) { $Python = $taskVenvPython }
        if ($Python)
        {
            $taskArguments += @('--python', (& $taskQuote ([IO.Path]::GetFullPath($Python))), '--python-source', (& $taskQuote (Join-Path $PSScriptRoot 'python')))
        }
        elseif (Test-Path -LiteralPath $taskBuiltWorker)
        {
            $taskArguments += @('--worker-executable', (& $taskQuote $taskBuiltWorker))
        }
        else
        {
            Write-Warning ('Python worker не найден: обучение и проверки будут недоступны. ' +
                'Создайте окружение: tools/setup-python.ps1, укажите venv параметром -Python <python.exe> или переменной MAUT_PYTHON, ' +
                'либо соберите worker: python/scripts/build-worker.ps1.')
        }
        Start-Process -FilePath $taskApplication -ArgumentList $taskArguments -WorkingDirectory $WorkDirectory -WindowStyle Normal
    }
}
finally
{
    Pop-Location
}
