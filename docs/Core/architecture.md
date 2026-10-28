# Core

Core is the single business-logic object in the Desktop process. `CoreFactory` keeps the only instance and always returns it; Core
performs its asynchronous initialization itself.

```csharp
var core = CoreFactory.GetInstance(new CoreOptions(workDirectory, baseModelCatalogPath));
await core.InitializeAsync(cancellationToken);
```

A repeated initialization call does not create a second Core and does not repeat finished preparation. Concurrent calls await the
shared initialization. The options of the instance are fixed when it is created; an incompatible configuration is an error.

Only interfaces from `MautVision.Core.Interfaces` and its sections are public. Implementations are `internal` and live in the root
folders of their aspects; `Core` and `CoreFactory` are in the project root. Core does not depend on WPF, WebView2 or React.

Top-level managers have separate duties:

| Manager | Responsibility |
|---|---|
| `Projects` | projects and their child managers ([projects](projects.md)) |
| `BaseModels` | base model catalog and weights ([base models](base-models.md)) |
| `Jobs` | queued and long-running jobs of Core and other layers ([jobs](jobs.md)) |
| `Media` | video through ffprobe and ffmpeg from `CoreOptions.FfmpegDirectory`; parts shared by images and backgrounds |
| `Python` | the worker executor for training and model checks, launched as described by `CoreOptions.PythonWorker` |
| `TrainingPresets` | training parameter presets shared by all projects |
| `Hardware` | GPUs and a load snapshot of the computer ([hardware](hardware.md)) |
| `Screen` | monitors and screenshots for model checks |

Managers interact through contracts and stay independent of each other; a scenario that spans several managers is coordinated by a
job. The composition is built inside Core; an external consumer never assembles it. There is no separate use-case layer.
