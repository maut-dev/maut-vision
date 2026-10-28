# Скачивает Evergreen bootstrapper Microsoft Edge WebView2 Runtime (MicrosoftEdgeWebview2Setup.exe, около 2 МБ) в .dev/tools/webview2
# для установщика. Bootstrapper не закрепляется по SHA-256: Microsoft обновляет его по постоянному адресу. Вместо хеша проверяется
# действительная подпись Authenticode, выданная Microsoft Corporation. Скачанный файл не запускается.
param(
    [string]$Destination = (Join-Path $PSScriptRoot '../.dev/tools/webview2'),
    # Скачать заново, даже если файл уже есть.
    [switch]$Refresh
)

$ErrorActionPreference = 'Stop'
$taskUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
$taskName = 'MicrosoftEdgeWebview2Setup.exe'

function Assert-MicrosoftSigned([string]$Path)
{
    $taskSignature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($taskSignature.Status -ne 'Valid') { throw "Подпись $Path недействительна: $($taskSignature.Status) $($taskSignature.StatusMessage)" }
    $taskSubject = $taskSignature.SignerCertificate.Subject
    if ($taskSubject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') { throw "$Path подписан не Microsoft Corporation: $taskSubject" }
}

$Destination = [IO.Path]::GetFullPath($Destination)
$taskTarget = Join-Path $Destination $taskName
if ((Test-Path -LiteralPath $taskTarget) -and -not $Refresh)
{
    Assert-MicrosoftSigned $taskTarget
    return
}

# The download goes into its own new empty folder next to the destination and moves only after the signature check.
$taskParent = Split-Path -Parent $Destination
New-Item -ItemType Directory -Path $taskParent -Force | Out-Null
$taskDownload = Join-Path $taskParent ('webview2-download-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskDownload | Out-Null
try
{
    $taskFile = Join-Path $taskDownload $taskName
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $taskProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -UseBasicParsing -Uri $taskUrl -OutFile $taskFile }
    finally { $ProgressPreference = $taskProgress }
    Assert-MicrosoftSigned $taskFile
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Move-Item -LiteralPath $taskFile -Destination $taskTarget -Force
    $taskVersion = (Get-Item -LiteralPath $taskTarget).VersionInfo.FileVersion
    Write-Output "WebView2 bootstrapper ${taskVersion}: $taskTarget"
}
finally
{
    Remove-Item -LiteralPath $taskDownload -Recurse -Force -ErrorAction SilentlyContinue
}
