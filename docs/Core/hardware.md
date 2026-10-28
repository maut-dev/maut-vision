# Hardware

`Hardware` reports the computer's hardware: the list of GPUs for choosing a training device and a load snapshot for the project
overview. The manager polls nothing on a timer: every snapshot answers a request.

```csharp
var snapshot = await core.Hardware.SampleAsync(cancellationToken);   // CPU, RAM, GPUs
var gpus = await core.Hardware.ListGpusAsync(refresh: false);        // training devices, cached
var bytes = core.Hardware.MeasureProcessMemory(processIds);          // process memory
```

## Sources

- **CPU** — Windows functions without processes or performance counters. Load is the difference of `GetSystemTimes` between two
  snapshots. The first snapshot and a snapshot after a pause longer than 10 s only record the reference point (`collecting`): the
  average load while the screen was hidden is not reported as the current one. The model comes from the registry, cores and threads
  from `GetLogicalProcessorInformationEx`; they are read once.
- **RAM** — `GlobalMemoryStatusEx`.
- **NVIDIA GPUs** — NVML (`nvml.dll`). The library is loaded from explicit driver paths (System32, `NVIDIA Corporation\NVSMI`),
  never through PATH, and initialized once per process on the first request. A value the card does not report is `null`.
- **Without NVML** — nvidia-smi from the same explicit paths with a time limit. The answer is cached for 5 s; concurrent requests
  wait for one launch.
- **Process memory** — the private working set (`GetProcessMemoryInfo`) where Windows reports it, otherwise the working set.

GPUs of other vendors are not read: the snapshot reports "unavailable" with a reason.

## States

Read errors are not thrown; they become a status with a reason code, and Desktop chooses the text by code.

| Code | When |
|---|---|
| `hardware.cpu_collecting` | the first load snapshot or a snapshot after a pause |
| `hardware.cpu_unavailable` | Windows did not report CPU times |
| `hardware.gpu_not_found` | the driver answered, there are no NVIDIA GPUs |
| `hardware.gpu_driver_missing` | neither NVML nor nvidia-smi is present |
| `hardware.gpu_timeout` | nvidia-smi did not answer in time |
| `hardware.gpu_failed` | NVML or nvidia-smi returned an error or damaged data |

## Polling

The page sets the rate: the "Your computer" panel requests a snapshot about every 2 s after the previous answer and only while the
window is visible. A minimized window hides WebView2, and the page gets `visibilityState = hidden` ([Desktop](../Desktop/architecture.md)).
A CPU and RAM sample must cost less than 1 ms.

Desktop sums the application memory: the host process, WebView2 processes and the processes of running Python workers with their
children (`IPythonRuntime.GetWorkerProcessIds`).
