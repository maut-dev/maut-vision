[CmdletBinding()]
param(
    # Интерпретатор с установленными requirements.txt и requirements-build.txt (PyInstaller 6.22.3).
    [Parameter(Mandatory = $true)][string]$Python,
    [switch]$InstallBuildDependencies
)

# Сборка worker в onedir: .dev/python-worker/maut-training-worker/maut-training-worker.exe и _internal.
# Скрипт ничего не устанавливает, кроме явного -InstallBuildDependencies (только сборщик, ML runtime не трогается).
# Без PyInstaller в выбранном интерпретаторе сборка не выполняется; dev-запуск (python -m worker) работает и без неё.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$repository = Split-Path -Parent $root
$spec = Join-Path $root 'maut-training-worker.spec'
$out = Join-Path $repository '.dev/python-worker'
$work = Join-Path $repository '.dev/python-worker-build'
. (Join-Path $PSScriptRoot 'worker-source-hash.ps1')

if ($InstallBuildDependencies)
{
    & $Python -m pip install -r (Join-Path $root 'requirements-build.txt')
    if ($LASTEXITCODE -ne 0) { throw "pip install завершился с кодом $LASTEXITCODE" }
}

& $Python -c "import importlib.util, sys; sys.exit(0 if importlib.util.find_spec('PyInstaller') else 3)"
if ($LASTEXITCODE -eq 3)
{
    throw "В интерпретаторе $Python нет PyInstaller. Установите requirements-build.txt в отдельное окружение сборки или запустите с -InstallBuildDependencies."
}
& $Python -c "import sys, platform, importlib.metadata as m; assert sys.platform == 'win32' and platform.machine().upper() in ('AMD64', 'X86_64'), 'Windows x64 runtime required'; assert m.version('pyinstaller') == '6.22.3', 'Install requirements-build.txt'; import torch, ultralytics, onnx, onnxslim, mss; print('Python:', sys.version); print({p: m.version(p) for p in ['torch', 'ultralytics', 'onnx', 'onnxslim', 'pyinstaller']})"
if ($LASTEXITCODE -ne 0) { throw 'Проверка Windows runtime не пройдена; установите requirements.txt и requirements-build.txt выбранным Python.' }

# Хеш и состояние git снимаются до сборки: BUILD.txt описывает исходники, из которых она началась.
$sourceHash = Get-WorkerSourceHash -Repository $repository
$commit = 'unknown'
$dirty = 'unknown'
if (Get-Command git -ErrorAction SilentlyContinue)
{
    $head = & git -C $repository rev-parse HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $head) { $commit = "$head".Trim() }
    $changes = & git -C $repository status --porcelain --untracked-files=all -- $WorkerSourceRoots 2>$null
    if ($LASTEXITCODE -eq 0) { $dirty = if ($changes) { 'true' } else { 'false' } }
}
$versions = & $Python -c "import sys, importlib.metadata as m; print('%d.%d.%d' % sys.version_info[:3]); print(m.version('pyinstaller'))"
if ($LASTEXITCODE -ne 0) { throw 'Не удалось прочитать версии Python и PyInstaller' }
$started = Get-Date

Push-Location $root
try
{
    & $Python -m PyInstaller --noconfirm --clean --distpath $out --workpath $work $spec
    if ($LASTEXITCODE -ne 0) { throw "PyInstaller завершился с кодом $LASTEXITCODE" }
}
finally
{
    Pop-Location
}

$exe = Join-Path $out 'maut-training-worker/maut-training-worker.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'PyInstaller не создал maut-training-worker.exe' }

$build = @(
    'format=1'
    "commit=$commit"
    "dirty=$dirty"
    "sourceHash=sha256:$sourceHash"
    "builtAt=$((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))"
    "buildSeconds=$([int]((Get-Date) - $started).TotalSeconds)"
    "python=$($versions[0])"
    "pyinstaller=$($versions[1])"
)
[System.IO.File]::WriteAllText((Join-Path $out 'maut-training-worker/BUILD.txt'), (($build -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Output "Worker: $exe"
Write-Output "Source: sha256:$sourceHash ($commit, dirty=$dirty)"
