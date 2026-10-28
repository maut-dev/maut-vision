# Creates the development environment of the Python worker: .dev/python-venv with python/requirements.txt.
# start.ps1 picks it up automatically. The install downloads several GB (torch with CUDA 13).
#
#   .\tools\setup-python.ps1                       # Python 3.14 from the py launcher or PATH
#   .\tools\setup-python.ps1 -Python C:\Python314\python.exe -Build
#   .\tools\setup-python.ps1 -WhatIf               # show what would be done, change nothing
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    # A Python 3.14 interpreter (x64) used to create the venv; by default `py -3.14`, then `python` on PATH.
    [string]$Python,
    # Also install python/requirements-build.txt (PyInstaller) to build the worker with python/scripts/build-worker.ps1.
    [switch]$Build,
    # Remove an existing venv and create it again.
    [switch]$Force,
    # The venv folder; .dev/python-venv by default.
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$taskRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskRequirements = Join-Path $taskRepository 'python/requirements.txt'
$taskBuildRequirements = Join-Path $taskRepository 'python/requirements-build.txt'
if (-not $Destination) { $Destination = Join-Path $taskRepository '.dev/python-venv' }
$Destination = [IO.Path]::GetFullPath($Destination)
$taskVenvPython = Join-Path $Destination 'Scripts/python.exe'
$taskRequiredVersion = '3.14'

function Get-PythonVersion([string]$Executable)
{
    $taskOutput = & $Executable -c "import sys, platform; print('%d.%d' % sys.version_info[:2], platform.machine())" 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $taskOutput) { return $null }
    return ($taskOutput | Select-Object -First 1).Trim()
}

# 1. The interpreter: an explicit path, the py launcher, or python on PATH; it must be 3.14 x64.
if (-not $Python)
{
    if (Get-Command py.exe -ErrorAction SilentlyContinue)
    {
        $taskFound = & py.exe "-$taskRequiredVersion" -c "import sys; print(sys.executable)" 2>$null
        if ($LASTEXITCODE -eq 0 -and $taskFound) { $Python = ($taskFound | Select-Object -First 1).Trim() }
    }
    if (-not $Python)
    {
        $taskCommand = Get-Command python.exe -ErrorAction SilentlyContinue
        if ($taskCommand) { $Python = $taskCommand.Source }
    }
    if (-not $Python)
    {
        throw "Python $taskRequiredVersion was not found. Install it from https://www.python.org/downloads/ (x64) or pass -Python <python.exe>."
    }
}
if (-not (Test-Path -LiteralPath $Python)) { throw "Python not found: $Python" }
$taskVersion = Get-PythonVersion $Python
if (-not $taskVersion) { throw "Could not run $Python." }
$taskParts = $taskVersion -split ' '
if ($taskParts[0] -ne $taskRequiredVersion) { throw "Python $taskRequiredVersion is required, $Python is $($taskParts[0]). Pass -Python <python.exe>." }
if ($taskParts[1] -notin @('AMD64', 'x86_64')) { throw "A 64-bit Python is required, $Python is $($taskParts[1])." }
Write-Output "Python: $Python ($taskVersion)"
Write-Output "Environment: $Destination"

# 2. The venv.
if ((Test-Path -LiteralPath $Destination) -and $Force)
{
    if ($PSCmdlet.ShouldProcess($Destination, 'Remove the existing venv')) { Remove-Item -LiteralPath $Destination -Recurse -Force }
}
if (-not (Test-Path -LiteralPath $taskVenvPython))
{
    if ($PSCmdlet.ShouldProcess($Destination, "Create a venv with $Python"))
    {
        New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
        & $Python -m venv $Destination
        if ($LASTEXITCODE -ne 0) { throw "python -m venv failed with exit code $LASTEXITCODE." }
    }
}
else
{
    Write-Output 'The venv exists; packages are brought to the pinned versions.'
}

# 3. Packages: pip itself, the runtime and optionally the build tools.
$taskSteps = @(
    @{ Text = 'Upgrade pip'; Arguments = @('-m', 'pip', 'install', '--upgrade', 'pip') },
    @{ Text = 'Install python/requirements.txt (several GB)'; Arguments = @('-m', 'pip', 'install', '-r', $taskRequirements) }
)
if ($Build) { $taskSteps += @{ Text = 'Install python/requirements-build.txt'; Arguments = @('-m', 'pip', 'install', '-r', $taskBuildRequirements) } }
foreach ($taskStep in $taskSteps)
{
    if ($PSCmdlet.ShouldProcess($taskVenvPython, $taskStep.Text))
    {
        & $taskVenvPython @($taskStep.Arguments)
        if ($LASTEXITCODE -ne 0) { throw "$($taskStep.Text) failed with exit code $LASTEXITCODE." }
    }
}

# 4. The check: the ML runtime imports and CUDA is reported.
if ($PSCmdlet.ShouldProcess($taskVenvPython, 'Verify torch, ultralytics and CUDA'))
{
    & $taskVenvPython -c "import torch, ultralytics; cuda = torch.cuda.is_available(); print('torch', torch.__version__, '| ultralytics', ultralytics.__version__); print('CUDA available:', cuda, '|', torch.cuda.get_device_name(0) if cuda else 'training will run on the CPU')"
    if ($LASTEXITCODE -ne 0) { throw 'torch or ultralytics cannot be imported; see the pip output above.' }
    Write-Output "Done. start.ps1 uses $taskVenvPython automatically."
}
