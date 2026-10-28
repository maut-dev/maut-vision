# Directories, packaging and installer

The application directory contains what ships with the program: exe, dll, the web build, application data and the initial user data
set. It is determined through `AppContext.BaseDirectory` and is replaced as a whole on update.

The work directory contains the mutable data of the user and projects. In a normal start it is `%LOCALAPPDATA%\MautVision`, in a
development start `.dev/DevWorkDirectory`. Weights, the WebView2 profile and datasets are large and bound to the computer, so the work
directory is not in the roaming profile.

```text
Application directory/
    MautVision.Desktop.exe
    *.dll
    LICENSE                  AGPL-3.0 text
    THIRD-PARTY-NOTICES.txt  third-party components, their licences and the source code address
    web/
    catalog/
        yolo-catalog.json    base model catalog, read at every start
    appdata/                 initial user data; absent while the set is empty
    tools/ffmpeg/            ffmpeg.exe, ffprobe.exe, their av*/sw* libraries, LICENSE.txt (LGPL v3), VERSION.txt (version, source, SHA-256)
    python/maut-training-worker/   the built Python worker (PyInstaller onedir, ≈ 3.4 GB, ≈ 4,800 files); part of the installer

Work directory/
    projects/<projectId>/project.json
    projects/.import-<guid>/    unfinished import; removed after the library is shown
    projects/.deleted-<id>-<guid>/   a deleted project whose files were locked
    last-project.json           the last opened project
    host-settings.json          host facts: the tray notification was shown
    BaseModels/weights/<fileName>
    training/presets.json       training parameter presets shared by projects
    logs/                       application log
    WebView2/
    .initial-data-prepared
```

Core does not determine directories itself: Desktop passes the work directory, the model catalog path, the ffmpeg folder and the Python
worker launch description in `CoreOptions` ([Python worker](../Python/worker.md#launch)).

```csharp
var catalog = Path.Combine(AppContext.BaseDirectory, "catalog", "yolo-catalog.json");
var core = CoreFactory.GetInstance(new CoreOptions(workDirectory, catalog)
{
    FfmpegDirectory = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg"),
});
```

## Application data and user data

Application data (`catalog/` in the repository root) is not copied to the work directory: a program update brings the new catalog
immediately. User data is created in the work directory and is never overwritten by an update.

The initial user data (`appdata/` in the repository root) is part of the build. Desktop copies it to the work directory at startup while
the `.initial-data-prepared` marker is absent. Existing files are not replaced; the marker is written after a successful copy. After that,
deleted files are not restored, and new files of the set in an update are not delivered to already prepared directories. The set is
currently empty (`.gitkeep` is not part of the build).

## Startup and delivery

```powershell
.\start.ps1
.\start.ps1 -WorkDirectory 'C:\temp\MautVisionWork'
.\build-installer.ps1 -PublishOnly
```

Desktop accepts the override `--work-directory`, and for the Python worker `--python` with `--python-source` or `--worker-executable`;
`--base-model-catalog` is allowed only in smoke checks. Scripts pass paths in quotes and double trailing `\`, otherwise `"D:\"` escapes
the closing quote. The installer places the whole publish directory and the worker in the application directory; the work directory
belongs to the user and by default stays after uninstallation. Publish and installation checks run separately.

## Publishing

`dotnet publish` of Desktop produces a self-contained ReadyToRun build without trimming and single-file: trimming breaks WPF, WinForms and
WebView2, and single-file slows startup with extraction. These properties are set in the project only for publishing (`_IsPublishing`); a
normal build and `start.ps1` do not get them. Only Russian and English .NET resources are kept (`SatelliteResourceLanguages`). There is
one program version — `Version` in `Directory.Build.props`: it goes into assemblies, exe properties, the startup log and the installer.

```powershell
.\build-installer.ps1 -PublishOnly          # publish only, into .dev/publish/<guid>
dotnet publish ... -p:PublishReadyToRun=false   # a variant for measurements; a global property overrides the project
```

Native libraries of packages are published only for win-x64 next to the exe (for example, `libSkiaSharp.dll` for images). Symbols of
native libraries are not shipped: `Directory.Build.targets` excludes `libSkiaSharp.pdb` (about 96 MB) from build and publish.

## Installer

The installer is one `Setup.exe` built with Inno Setup 6.5+ (`packaging/MautVision.iss`) by `build-installer.ps1`: publish into a clean
`.dev/publish`, the Python worker from `.dev/python-worker`, the WebView2 bootstrapper, ISCC compilation into `.dev/installers`. The
worker is accepted only if its `BUILD.txt` describes the current sources ([Python worker](../Python/worker.md#build)); otherwise the
build stops. Inno Setup is installed by the developer; the script looks for `ISCC.exe` in `PATH`, `Program Files` and
`%LOCALAPPDATA%\Programs\Inno Setup 6`. At the end the script prints the size and the SHA-256 of the installer; the hash is published
next to the download link.

```powershell
.\build-installer.ps1                       # MautVision-Setup-<version>.exe, lzma2/ultra64
.\build-installer.ps1 -Fast -TestInstall    # fast compression, separate AppId and name for tools/verify-install.ps1
```

- **Installation.** Per user without administrator rights into `{autopf}\MautVision` (`%LOCALAPPDATA%\Programs\MautVision`); the folder
  can be changed. Russian language, the AGPL-3.0 licence page, a Start menu shortcut, an optional desktop shortcut, launch after
  installation. No code signing and no auto-update: a new version is installed over the old one.
- **Update.** Folders the version replaces as a whole (`python`, `web`, `catalog`, `tools\ffmpeg`, `runtimes`) are removed first so that
  files of the previous version do not remain. A running application is closed by Restart Manager (`CloseApplications=yes`, without
  restart).
- **Compression.** lzma2/ultra64 solid; the worker (≈ 3.4 GB) is a separate solid block, the WebView2 bootstrapper is the first file of
  the archive so that extracting it does not decompress the rest.
- **WebView2.** The embedded Evergreen bootstrapper runs with `/silent /install` only if the runtime is absent: the `pv` version is
  missing in `HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-…}` and in the same HKCU key. `tools/get-webview2.ps1`
  downloads the bootstrapper into `.dev/tools/webview2`; the file is not pinned by hash (Microsoft updates it at a permanent address), so a
  valid Authenticode signature of Microsoft Corporation is checked instead.
- **Uninstallation.** The application directory, the uninstall key and the shortcuts are removed. The work directory
  `%LOCALAPPDATA%\MautVision` stays unless the user answers "Yes" to the question about removing projects and data (default "No"). Silent
  uninstallation (`/VERYSILENT`) does not touch data. Uninstallation does not start while the application runs.

## Shutdown requested by Windows

The application holds the named mutexes `MautVision-Running` and `Global\MautVision-Running` while the process lives, regardless of the
work directory; the uninstaller uses them to detect a running program. The single-instance mutex per work directory
([Desktop](architecture.md#window-tray-and-single-instance)) works separately.

A Windows session end and the installer's Restart Manager (`WM_QUERYENDSESSION`, `WM_ENDSESSION`, `Application.SessionEnding`) end the
application immediately: without hiding to the tray, the background jobs question or the unsaved changes check. Running exports and
imports are cancelled with a wait of up to 2 s. Closing the window hidden in the tray also means exit: the user cannot close it, so it is a
request of another program.

```text
WM_QUERYENDSESSION (ENDSESSION_CLOSEAPP) → cancel transfers ≤ 2 s → window closed → Shutdown
```

AppMutex is not used in the installer: Inno checks it before Restart Manager, and a silent update over a running application would stop.
The uninstaller checks the same mutexes in `[Code]`.

## ffmpeg

Video frames are read by ffmpeg and ffprobe — an LGPL build for win-x64 with shared libraries (BtbN FFmpeg-Builds, variant
`win64-lgpl-shared`, without GPL or non-free parts): ffmpeg and ffprobe share one set of DLLs, so the delivery is noticeably smaller than a
static pair (≈ 167 MB in 11 files versus ≈ 268 MB). Binaries are not stored in git: `tools/get-ffmpeg.ps1` downloads the pinned archive
from the pinned address into a new separate folder, checks the pinned SHA-256 and extracts only `ffmpeg.exe`, `ffprobe.exe`, the libraries
they load (`avcodec`, `avdevice`, `avfilter`, `avformat`, `avutil`, `swresample`, `swscale`) and `LICENSE.txt` into `.dev/tools/ffmpeg`;
nothing from the archive is executed, and `ffplay.exe` and development files are not taken. The libraries lie next to the exe and are
loaded from its folder. `start.ps1` (and through it `smoke.ps1` and `build-installer.ps1`) calls the script; a repeated call downloads
nothing if the version in `VERSION.txt` matches. The Desktop build copies the files to `tools/ffmpeg/` of the output and the publish, and
`build-installer.ps1` checks they are present.

```powershell
.\tools\get-ffmpeg.ps1    # .dev/tools/ffmpeg: ffmpeg.exe, ffprobe.exe, *.dll, LICENSE.txt, VERSION.txt
```

A new version means a new address, version name and SHA-256 in the script. The address points to a monthly build
(`autobuild-YYYY-MM-30/31`); BtbN keeps such releases for a long time.

Licence: ffmpeg and its libraries are distributed under the GNU LGPL v3; `LICENSE.txt` and `VERSION.txt` with the build and source
addresses (the FFmpeg commit and the BtbN scripts) lie next to them. The libraries ship as separate DLLs and can be replaced. The
application runs ffmpeg as a separate process with an argument list, without a shell, only for local files (`-protocol_whitelist file`),
and modifies neither the programs nor the libraries.
