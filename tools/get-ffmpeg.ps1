# Скачивает закреплённую LGPL-сборку ffmpeg (shared) для win-x64 в .dev/tools/ffmpeg: только ffmpeg.exe, ffprobe.exe,
# библиотеки, которые они загружают, и лицензию.
# Архив проверяется по закреплённому SHA-256 и распаковывается без запуска чего-либо из него.
param([string]$Destination = (Join-Path $PSScriptRoot '../.dev/tools/ffmpeg'))

$ErrorActionPreference = 'Stop'
$taskVersion = 'n8.1.3-9-g29e619e767-20260930-lgpl-shared'
$taskUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-30-13-08/ffmpeg-n8.1.3-9-g29e619e767-win64-lgpl-shared-8.1.zip'
$taskSha256 = '3e47bda1607740550141e37c0e49d1e5182b34699f15adfd137ee266d346811a'
# ffmpeg.exe and ffprobe.exe link these libraries; ffplay.exe and the development files are not taken.
$taskFiles = @('ffmpeg.exe', 'ffprobe.exe', 'avcodec-62.dll', 'avdevice-62.dll', 'avfilter-11.dll', 'avformat-62.dll', 'avutil-60.dll',
    'swresample-6.dll', 'swscale-9.dll', 'LICENSE.txt')

$Destination = [IO.Path]::GetFullPath($Destination)
$taskMarker = Join-Path $Destination 'VERSION.txt'
$taskPresent = (Test-Path -LiteralPath $taskMarker) -and ((Get-Content -LiteralPath $taskMarker -TotalCount 1) -eq $taskVersion)
foreach ($taskName in $taskFiles) { if (-not (Test-Path -LiteralPath (Join-Path $Destination $taskName))) { $taskPresent = $false } }
if ($taskPresent) { return }

# The archive goes into its own new empty folder next to the destination; only the listed entries are extracted.
$taskParent = Split-Path -Parent $Destination
New-Item -ItemType Directory -Path $taskParent -Force | Out-Null
$taskDownload = Join-Path $taskParent ('ffmpeg-download-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskDownload | Out-Null
try
{
    $taskArchive = Join-Path $taskDownload 'ffmpeg.zip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $taskProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -UseBasicParsing -Uri $taskUrl -OutFile $taskArchive }
    finally { $ProgressPreference = $taskProgress }
    $taskActual = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($taskActual -ne $taskSha256) { throw "Архив ffmpeg не совпал с закреплённым SHA-256 ($taskActual)." }

    $taskStaging = Join-Path $taskDownload 'files'
    New-Item -ItemType Directory -Path $taskStaging | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $taskZip = [IO.Compression.ZipFile]::OpenRead($taskArchive)
    try
    {
        foreach ($taskName in $taskFiles)
        {
            $taskSuffix = if ($taskName -eq 'LICENSE.txt') { '/LICENSE.txt' } else { '/bin/' + $taskName }
            $taskEntry = @($taskZip.Entries | Where-Object { $_.FullName.EndsWith($taskSuffix) -and $_.FullName.Split('/').Count -eq $taskSuffix.Split('/').Count })
            if ($taskEntry.Count -ne 1) { throw "В архиве ffmpeg нет $taskName." }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry[0], (Join-Path $taskStaging $taskName), $true)
        }
    }
    finally { $taskZip.Dispose() }

    # Files of an earlier pinned build (for example, the static pair) must not stay next to the new ones.
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($taskName in $taskFiles) { Move-Item -LiteralPath (Join-Path $taskStaging $taskName) -Destination (Join-Path $Destination $taskName) -Force }
    $taskNotice = @(
        $taskVersion,
        '',
        'FFmpeg (ffmpeg.exe, ffprobe.exe and the av*/sw* libraries) is licensed under the GNU LGPL version 3 (LICENSE.txt); this build has no GPL or non-free parts.',
        "Binary: $taskUrl",
        "SHA-256 of the archive: $taskSha256",
        'Source code: https://github.com/FFmpeg/FFmpeg/commit/29e619e767 and the build scripts at https://github.com/BtbN/FFmpeg-Builds.',
        'Maut Vision runs these programs as separate processes and does not modify them or their libraries.'
    )
    Set-Content -LiteralPath $taskMarker -Value $taskNotice -Encoding ASCII
    Write-Output "ffmpeg ${taskVersion}: $Destination"
}
finally
{
    Remove-Item -LiteralPath $taskDownload -Recurse -Force -ErrorAction SilentlyContinue
}
