# Хеш исходников Python worker и чтение BUILD.txt собранного onedir (docs/Python/worker.md, «BUILD.txt»).
# Запуск напрямую печатает хеш: .\python\scripts\worker-source-hash.ps1 [<корень репозитория>]
# Подключение функций: . .\python\scripts\worker-source-hash.ps1 — без param-блока, чтобы не затирать переменные вызывающего;
# в его области появляются функции ниже и $WorkerSourceRoots.

$WorkerSourceRoots = @(
    'python/worker',                       # каталог: все файлы, кроме __pycache__, *.pyc, *.pyo
    'python/worker_entry.py',
    'python/maut-training-worker.spec',
    'python/requirements.txt',
    'python/requirements-build.txt',
    'catalog/yolo-catalog.json'             # копируется в сборку как запасной каталог
)

function Get-WorkerSourceFiles
{
    param([Parameter(Mandatory = $true)][string]$Repository)

    $root = (Resolve-Path -LiteralPath $Repository).ProviderPath.TrimEnd('\', '/')
    $paths = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $WorkerSourceRoots)
    {
        $full = Join-Path $root $entry
        if (Test-Path -LiteralPath $full -PathType Container)
        {
            foreach ($file in Get-ChildItem -LiteralPath $full -Recurse -File -Force)
            {
                $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
                if ($relative -match '(^|/)__pycache__/' -or $relative -match '\.py[co]$') { continue }
                $paths.Add($relative)
            }
        }
        elseif (Test-Path -LiteralPath $full -PathType Leaf)
        {
            $paths.Add($entry)
        }
        else
        {
            throw "Нет исходника worker: $entry"
        }
    }
    $sorted = $paths.ToArray()
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    return $sorted
}

function Get-WorkerSourceHash
{
    # Строка манифеста на файл: "<sha256 содержимого, hex lowercase>  <путь от корня репозитория через />\n";
    # строки по ординальному порядку путей; результат — SHA-256 байтов манифеста в UTF-8, hex lowercase.
    param([Parameter(Mandatory = $true)][string]$Repository)

    $root = (Resolve-Path -LiteralPath $Repository).ProviderPath
    $manifest = New-Object System.Text.StringBuilder
    $sha = [System.Security.Cryptography.SHA256]::Create()
    foreach ($relative in Get-WorkerSourceFiles -Repository $root)
    {
        $content = [System.IO.File]::ReadAllBytes((Join-Path $root $relative))
        $normalized = New-Object System.IO.MemoryStream($content.Length)
        for ($i = 0; $i -lt $content.Length; $i++)
        {
            if ($content[$i] -eq 13 -and $i + 1 -lt $content.Length -and $content[$i + 1] -eq 10) { continue }
            $normalized.WriteByte($content[$i])
        }
        $hash = -join ($sha.ComputeHash($normalized.ToArray()) | ForEach-Object { $_.ToString('x2') })
        [void]$manifest.Append($hash).Append('  ').Append($relative).Append("`n")
    }
    try
    {
        $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($manifest.ToString())
        return -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })
    }
    finally
    {
        $sha.Dispose()
    }
}

function Read-WorkerBuildInfo
{
    # BUILD.txt: UTF-8, строки key=value; возвращает hashtable или $null, если файла нет.
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $info = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($Path, (New-Object System.Text.UTF8Encoding($false))))
    {
        $index = $line.IndexOf('=')
        if ($index -gt 0) { $info[$line.Substring(0, $index)] = $line.Substring($index + 1) }
    }
    return $info
}

function Assert-WorkerBuildCurrent
{
    # Отказ, если у onedir нет BUILD.txt или его sourceHash не совпадает с текущими исходниками.
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        # Папка maut-training-worker (рядом с maut-training-worker.exe).
        [Parameter(Mandatory = $true)][string]$WorkerDirectory
    )

    $info = Read-WorkerBuildInfo -Path (Join-Path $WorkerDirectory 'BUILD.txt')
    if ($null -eq $info) { throw "В $WorkerDirectory нет BUILD.txt: пересоберите worker (python/scripts/build-worker.ps1)." }
    if ($info['format'] -ne '1') { throw "Неизвестный формат BUILD.txt: $($info['format'])" }
    $current = 'sha256:' + (Get-WorkerSourceHash -Repository $Repository)
    if ($info['sourceHash'] -ne $current)
    {
        throw "Worker собран из других исходников (BUILD.txt $($info['sourceHash']), сейчас $current, сборка $($info['builtAt'])): пересоберите worker."
    }
    return $info
}

if ($MyInvocation.InvocationName -ne '.')
{
    $repositoryRoot = if ($args.Count -gt 0) { $args[0] } else { Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
    Get-WorkerSourceHash -Repository $repositoryRoot
}
