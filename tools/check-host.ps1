# Проверка поведения host вне smoke: обычный запуск (настоящий трей) с временной рабочей директорией, mutex MautVision-Running,
# окно в пределах рабочей области монитора, крестик прячет окно в трей, а Restart Manager (так установщик закрывает приложение
# при обновлении) завершает его с кодом 0, после чего mutex исчезает.
param(
    [string]$Application = (Join-Path $PSScriptRoot '../src/MautVision.Desktop/bin/Debug/net10.0-windows/win-x64/MautVision.Desktop.exe'),
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$Application = [IO.Path]::GetFullPath($Application)
if (-not (Test-Path -LiteralPath $Application)) { throw "Нет приложения: $Application" }

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class MautHostCheck
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct UniqueProcess { public uint ProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME StartTime; }
    delegate bool EnumProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint session, uint files, string[] names, uint applications, UniqueProcess[] processes, uint services, string[] serviceNames);
    [DllImport("rstrtmgr.dll")] static extern int RmShutdown(uint session, uint flags, IntPtr callback);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint session);

    /// <summary>The visible top-level window of the process titled «Maut Vision» (the main window, not the loading one).</summary>
    public static IntPtr MainWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, parameter) =>
        {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner != processId) return true;
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString() == "Maut Vision" && IsWindowVisible(window)) { found = window; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Visible window bounds, the work area of its monitor (physical pixels) and the window DPI.</summary>
    public static int[] Bounds(IntPtr window)
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4));
        Rect frame;
        DwmGetWindowAttribute(window, 9, out frame, Marshal.SizeOf(typeof(Rect)));
        var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        GetMonitorInfo(MonitorFromWindow(window, 2), ref info);
        return new[] { frame.Left, frame.Top, frame.Right, frame.Bottom, info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom, (int)GetDpiForWindow(window) };
    }

    /// <summary>Asks Restart Manager to close the processes using the file, without force, as an installer does; returns the error code.</summary>
    public static int Shutdown(string file)
    {
        uint session;
        var key = new StringBuilder(64);
        var error = RmStartSession(out session, 0, key);
        if (error != 0) return error;
        try
        {
            error = RmRegisterResources(session, 1, new[] { file }, 0, null, 0, null);
            return error != 0 ? error : RmShutdown(session, 0, IntPtr.Zero);
        }
        finally
        {
            RmEndSession(session);
        }
    }
}
'@

function Test-Mutex([string]$Name)
{
    $taskMutex = $null
    $taskExists = [Threading.Mutex]::TryOpenExisting($Name, [ref]$taskMutex)
    if ($taskExists) { $taskMutex.Dispose() }
    return $taskExists
}

$taskOthers = @(Get-Process -Name 'MautVision.Desktop' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Application })
if ($taskOthers.Count -gt 0) { throw "Приложение $Application уже запущено (PID $($taskOthers.Id -join ', ')): Restart Manager закрыл бы и его." }

$taskTempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$taskRoot = Join-Path $taskTempParent ('maut-host-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot | Out-Null
# The one-time tray notice is already «shown»: the check does not pop a notification.
[IO.File]::WriteAllText((Join-Path $taskRoot 'host-settings.json'), '{"trayNoticeShown":true}')
$taskProcess = $null
try
{
    $taskQuote = '"' + ($taskRoot -replace '(\\+)$', '$1$1') + '"'
    $taskProcess = Start-Process -FilePath $Application -ArgumentList @('--work-directory', $taskQuote) -WorkingDirectory $taskRoot -PassThru
    $null = $taskProcess.Handle
    $taskWatch = [Diagnostics.Stopwatch]::StartNew()
    $taskReady = $false
    while ($taskWatch.Elapsed.TotalSeconds -lt $TimeoutSeconds -and -not $taskProcess.HasExited)
    {
        $taskLog = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'logs') -Filter ("maut-vision-*-{0}.log" -f $taskProcess.Id) -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($taskLog)
        {
            $taskStream = [IO.File]::Open($taskLog.FullName, 'Open', 'Read', 'ReadWrite')
            try { $taskText = (New-Object IO.StreamReader($taskStream)).ReadToEnd() } finally { $taskStream.Dispose() }
            if ($taskText -match 'Startup stage library_ready') { $taskReady = $true; break }
        }
        Start-Sleep -Milliseconds 200
    }
    if (-not $taskReady) { throw 'Приложение не показало библиотеку.' }
    Write-Output 'PASS: обычный запуск показал библиотеку.'

    if (-not (Test-Mutex 'MautVision-Running') -or -not (Test-Mutex 'Global\MautVision-Running')) { throw 'Нет mutex MautVision-Running или Global\MautVision-Running.' }
    Write-Output 'PASS: mutex MautVision-Running и Global\MautVision-Running существуют, пока приложение работает.'

    $taskWindow = [MautHostCheck]::MainWindow($taskProcess.Id)
    if ($taskWindow -eq [IntPtr]::Zero) { throw 'Не найдено главное окно.' }
    $taskBounds = [MautHostCheck]::Bounds($taskWindow)
    $taskInside = $taskBounds[0] -ge $taskBounds[4] -and $taskBounds[1] -ge $taskBounds[5] -and $taskBounds[2] -le $taskBounds[6] -and $taskBounds[3] -le $taskBounds[7]
    $taskDescription = 'окно {0},{1}–{2},{3}, рабочая область {4},{5}–{6},{7}, DPI {8}' -f ([object[]]$taskBounds)
    if (-not $taskInside) { throw "Окно выходит за рабочую область: $taskDescription." }
    Write-Output "PASS: окно в пределах рабочей области ($taskDescription)."

    [void][MautHostCheck]::PostMessage($taskWindow, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    $taskWatch.Restart()
    while ([MautHostCheck]::IsWindowVisible($taskWindow) -and $taskWatch.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
    Start-Sleep -Milliseconds 500
    if ([MautHostCheck]::IsWindowVisible($taskWindow) -or $taskProcess.HasExited) { throw 'Закрытие окна не спрятало его в трей.' }
    Write-Output 'PASS: закрытие окна прячет его в трей, приложение работает.'

    $taskError = [MautHostCheck]::Shutdown($Application)
    if ($taskError -ne 0) { throw "Restart Manager вернул ошибку $taskError." }
    if (-not $taskProcess.WaitForExit(30000)) { throw 'Restart Manager не закрыл приложение за 30 с.' }
    if ($taskProcess.ExitCode -ne 0) { throw "Приложение завершилось с кодом $($taskProcess.ExitCode)." }
    $taskText = [IO.File]::ReadAllText($taskLog.FullName)
    if ($taskText -notmatch 'the application exits') { throw 'В журнале нет записи о завершении по запросу Windows.' }
    if ((Test-Mutex 'MautVision-Running') -or (Test-Mutex 'Global\MautVision-Running'))
    {
        if (@(Get-Process -Name 'MautVision.Desktop' -ErrorAction SilentlyContinue).Count -eq 0) { throw 'Mutex MautVision-Running остался после выхода.' }
        Write-Output 'Mutex MautVision-Running держит другой запущенный экземпляр; его исчезновение не проверено.'
    }
    Write-Output 'PASS: Restart Manager закрыл приложение из трея с кодом 0.'
    Write-Output ('Журнал: ' + [regex]::Match($taskText, '[^\r\n]*the application exits[^\r\n]*').Value.Trim())
}
finally
{
    if ($taskProcess -and -not $taskProcess.HasExited)
    {
        & taskkill.exe /PID $taskProcess.Id /T /F | Out-Null
        $taskProcess.WaitForExit()
    }
    for ($taskAttempt = 0; $taskAttempt -lt 60; $taskAttempt++)
    {
        try
        {
            Remove-Item -LiteralPath $taskRoot -Recurse -Force -ErrorAction Stop
            break
        }
        catch
        {
            if ($taskAttempt -eq 59) { Write-Warning "Не удалось удалить $taskRoot." }
            # WebView2 subprocesses may briefly retain profile files after the host exits.
            Start-Sleep -Milliseconds 500
        }
    }
}
