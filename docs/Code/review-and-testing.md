# Review and testing

Checks confirm behaviour and responsibility boundaries. Their scope matches the change.

```powershell
.\start.ps1 -NoLaunch
.\smoke.ps1 -SkipBuild
npm --prefix ui run typecheck
cd python; python -I -m unittest discover -s tests -t .
```

DTO changes are checked on the C# and TypeScript sides together. For events, delivery and unsubscription matter; for startup — the
order of windows and error handling; for job recovery — a crash between steps and safe repetition.

`smoke.ps1` runs Core checks, the real page in WebView2 and the host with a fake Python worker (`tests/MautVision.FakeWorker`).
Application exit is imitated by a second Core instance on the same folder. The old instance is stopped at a `Pause` point of a
handler and ended with `EndForTestingAsync`: worker trees close and deferred writes (job progress, training log) no longer reach
`project.db`, as with a finished process. Time-based sleeps are not used instead.

```csharp
await died.Task.WaitAsync(WaitLimit);
await ((MautVision.Core.Core)dying).EndForTestingAsync();
var revived = await createCore(root, null);
```

Do not add tests that only repeat the implementation. Old results are never presented as a check of new code. Architecture
documents contain no test counts or readiness lists.

## Scale, installation and release

`smoke.ps1` and `perf.ps1` run the Debug build by default; `-Application` substitutes another exe — a Release publish or the
installed program. At the end smoke checks the layout: with a 1080p-monitor window at the run scale and with the minimum window,
the library, the catalog and the project sections do not scroll or overflow horizontally, and a Radix list opens inside the window.
`-DeviceScaleFactor` sets WebView2 `--force-device-scale-factor` and a window of that scale, so 125 % and 150 % are checked on any
monitor.

```powershell
.\smoke.ps1 -SkipBuild -NativeOnly -DeviceScaleFactor 1.5
.\smoke.ps1 -SkipBuild -NativeOnly -Application .dev\publish\<guid>\MautVision.Desktop.exe
.\tools\check-host.ps1                      # mutex, window in the work area, tray, closing through Restart Manager
.\build-installer.ps1 -Fast -TestInstall; .\tools\verify-install.ps1
```

The end-to-end run checks the user scenario on the installed program: `tools/run-e2e.ps1` silently installs a test build of the
installer into a short temporary folder (the program folder is at most 85 characters; an already installed test build is a
refusal), runs `tests/MautVision.E2E` and silently uninstalls the program in any outcome. E2E works through the public Core API
(`CoreFactory`) with the packaged worker, catalog and ffmpeg of the installation; Core is the repository build. Steps: project →
classes and variant images → backgrounds from images and a video frame → generation → dataset and integrity check → training of
yolo26n on GPU 0 (2 epochs, imgsz 320, batch 8) → version with best.pt and best.onnx → evaluation on Test, checks on an image, a
video and the screen (5 s) → model ZIP → project export and import. Inputs are synthetic (SkiaSharp, ffmpeg `testsrc2`); only the
base model (~5.5 MB) is downloaded unless `-BaseModelWeights` is given. Each step prints PASS/FAIL with its time; on failure — the
job error, the worker log folder and the Core log, and the run folder stays.

```powershell
.\tools\run-e2e.ps1                                     # build E2E, install, run, uninstall
.\tools\run-e2e.ps1 -BaseModelWeights <yolo26n.pt> -Keep
tests\MautVision.E2E\bin\Debug\net10.0\MautVision.E2E.exe --app-dir <program folder> --work-directory <empty folder>
```

`tools/check-host.ps1` starts the application normally with a temporary work directory and closes it through Restart Manager, as
the installer does. `tools/verify-install.ps1` checks the test build of the installer (its own AppId, name and folder): silent
installation into a folder with Cyrillic letters and spaces, files against the publish and the worker, the uninstall key and the
shortcut, smoke on the installed exe, an update over the running application with `/FORCECLOSEAPPLICATIONS`, and silent
uninstallation that keeps the work directory. Real monitors with different scales, moving the window between them and a clean
Windows installation are checked manually.

## Performance

Changes of storage, startup, lists and galleries are checked on a large library. `perf.ps1` creates it in `.dev/perf-library`
(40 projects, 250,000 JPEG files, 120,000 of them in one project, 10,000 finished jobs; about 2 GB, the first generation takes a
couple of minutes, a repeat adds what is missing), measures Core in separate processes and starts the application several times
until the library is ready. `smoke.ps1` does not include this measurement.

```powershell
.\perf.ps1                                  # build, generation, Core benchmark and startup
.\perf.ps1 -SkipBuild -SkipBench -Runs 3    # application startup only
```

| Measurement | Target |
|---|---|
| Startup until the library is shown (`library_ready`), warm disk | < 2 s |
| Opening a project (storage and job recovery) | < 1 s |
| First page of a list or gallery | < 300 ms |
| Export of a project with 120,000 images | ≥ 10,000 files/s |
| Import of the same archive | ≥ 2,500 files/s (bound by file creation, not the CPU) |

Desktop writes startup stages to the log as `Startup stage <stage> reached in <ms> ms since process start`; `library_ready` is
marked by the UI with `system.ready` after the first render of the project list. A missed target is a reason to find and remove the
bottleneck before merging, not to raise the threshold.
