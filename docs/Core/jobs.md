# Jobs

Jobs coordinate scenarios that span several managers and run long operations. A public interface in `Interfaces/Jobs` defines the job
kind and its input; an internal handler in `Jobs` holds the algorithm. JobsManager stores and executes the queue and contains no scenario
algorithms.

## Example job

The name and operation are illustrative.

```csharp
// Interfaces/Jobs — the contract and its stable key.
public sealed record ImportFilesInput(IReadOnlyList<string> Files);

[JobKind("example.import")]
public interface IImportFilesJob : IJobDefinition<ImportFilesInput>
{
}

// Jobs — the handler receives managers through its constructor.
internal sealed class ImportFilesHandler(IProjectsManager projects) : IJobHandler<ImportFilesInput>
{
    public bool Resumable => true;              // false by default
    public JobResource Resource => JobResource.Cpu;

    public async Task ExecuteAsync(ImportFilesInput input, JobContext context)
    {
        var start = context.Checkpoint<ImportCheckpoint>()?.Next ?? 0;
        for (var index = start; index < input.Files.Count; index++)
        {
            await context.RunStepAsync($"file:{index}", token => ImportAsync(input.Files[index], token));
            await context.SaveCheckpointAsync(new ImportCheckpoint(index + 1));
            context.ReportProgress(100.0 * (index + 1) / input.Files.Count, processed: index + 1, total: input.Files.Count);
        }
    }
}
```

Registration happens in the Core composition before projects open; a consumer enqueues a job by its contract:

```csharp
jobsManager.Register<IImportFilesJob, ImportFilesInput>(new ImportFilesHandler(projects));

var job = await core.Jobs.EnqueueAsync<IImportFilesJob, ImportFilesInput>(
    input, projectId: projectId, operationId: operationId, cancellationToken);
```

The `[JobKind]` key is stored in the job instead of the type name, so the interface can be renamed. Changing the key or removing a kind
is an incompatible change: such jobs get the `blocked` status on recovery, and their data is kept. The product has no demo handlers; kinds
for checks are registered by smoke.

## Storage and recovery

A job belongs to a project and is stored in its `project.db` (tables `jobs`, `job_steps`). There are no global jobs. Application startup
does not read jobs: on `IProjectsManager.OpenAsync` one query over a partial index finds unfinished jobs and puts them in order, after
which the project queue starts. Before opening, Jobs operations answer `jobs.project_not_open`.

| Saved state | After the project opens |
|---|---|
| `queued` | stays queued |
| `running`, `Resumable` kind | `queued`, continues from the checkpoint and skips confirmed steps |
| `running`, non-resumable kind | `interrupted` (`jobs.interrupted`) |
| `cancelRequested` set | `cancelled` |
| unknown kind | `blocked` (`jobs.kind_unknown`), data kept |

The project queue starts only after opening is complete: scenarios are recovered (a published training result is accepted, unfinished
builds are discarded) and the project is marked open. Therefore a recovered job never works with a closed project and never runs ahead of
recovery: for example, a queued training run will not take the number of a model version accepted during opening.

The process that ran a `running` job has already ended, so the job is not considered alive. Unreadable input or checkpoint gives `blocked`
with `jobs.input_invalid` or `jobs.checkpoint_invalid`.

## Handler context

- The `CancellationToken` is cancelled by user cancellation; `OperationCanceledException` after it gives `cancelled`.
- `ReportProgress(percent, message, processed, total)` does not block: reports are merged, saved and broadcast at most every 250 ms. A
  `null` percent means the amount is unknown. `message` is what the job is doing as a code of the UI catalog `progress` with values
  (`CodedText.Of("progress.epoch", ("epoch", 3), ("total", 10))`), never a text: the UI shows it in the language of the user.
- `SaveCheckpointAsync(value)` and `Checkpoint<T>()` — a durable continuation point in JSON: positions and IDs, not data.
- `SetResult(value)` — the job result (counters, short lists), saved with its last state, also after cancellation and failure, and
  delivered in `JobInfo.Result`. The job kind defines the result shape, e.g. `ImportClassAssetsResult`.
- `RunStepAsync(stepKey, action)` runs a step if it has no receipt and records the receipt after the action. A crash between the action
  and the receipt repeats the step, so the action must be safe to repeat. Receipts are removed when the job finishes.
- `CoreException` gives `failed` with its code; another exception gives `failed` with `jobs.failed` and a full log record.

## Lines for the user

Core never writes a text the user reads. Progress lines and labels are `CodedText(Code, Args, Text)`: a code `<namespace>.<key>` of the UI
catalogs with string values, or a text shown as is (a name, a line stored by version 1.0.0). Desktop sends it as `{ code, args, text }`;
the UI translates it in `lib/coded-text.ts`.

`jobs.progress_text` and `jobs.label` keep the existing columns: a line with a code is stored as the JSON object
`{"code":"progress.epoch","args":{"epoch":"3","total":"10"}}`, a text as is (`CodedTextJson`). Rows of 1.0.0 hold texts and read back as
texts, so no schema step is needed.

## Labels and active jobs

The enqueuer gives a job a label — what it works on. A name the user typed (a group, set, preset or dataset) is a text; words around data
are a code of the UI catalog `jobLabels` with values (`jobLabels.version` with `number`, `jobLabels.version_dataset`,
`jobLabels.class_variant`), so the UI composes "Версия 3" or "Version 3". The label is stored in the job and comes with every snapshot;
lists show it after the job kind.

```csharp
await core.Jobs.EnqueueAsync<IDatasetBuildJob, DatasetBuildInput>(input, projectId, operationId, label: CodedText.Plain(input.Specification.Name));
await core.Jobs.EnqueueAsync<IModelExportJob, ModelExportInput>(input, projectId, operationId, label: CodedText.Of("jobLabels.version", ("number", 3)));
var page = await core.Jobs.ListActiveAsync(projectId, limit: 30, after: cursor);   // Items, Total, CountsByKind, NextCursor
```

Active jobs — queued and running, cancelling included — come in queue order. `Total` and `CountsByKind` count all active jobs of the
project, not the page; both queries read the partial index `jobs_active`, so they do not depend on the history size. `WaitingForGpu`
marks a job queued behind the GPU.

Execution is in-process, by resource: GPU jobs one at a time, CPU jobs at most half of the cores (at least one), in enqueue order. The
GPU slot (`GpuSlot`) is shared with live model checks: a check takes it outside the queue or is refused, and GPU jobs wait until it is
released.

Admission of new jobs is defined by the Core composition: JobsManager asks it when enqueuing, inside the job write, with the kinds of the
already active and admitted jobs of the project, so the decision and the enqueue are atomic. That is how a project export is queued only
into a project without other work, and while it runs new jobs of the project are refused ([export and import](projects.md#export-and-import));
a project being deleted gets no new jobs.

## Long operations

A UI request waits for an answer for a limited time, so only short operations use request/response. An operation whose duration depends
on the amount of data, the network, the disk or the GPU is a job: import, generation, dataset build, training, model checks. An operation
that wholly belongs to one object and need not survive a restart may run in the background of that object under the same rules: an
immediate state, events, a separate cancellation. [Base model installation](base-models.md#installation) works this way.

```text
UI → <area>.start…(operationId, …) → job snapshot { id, status }       immediately
Core → job.changed event { id, status, progress, sequence }            while running
UI → jobs.get / jobs.list / jobs.listActive (projectId)                on open and reconnect
UI → jobs.cancel (projectId, jobId)                                    a separate command
```

- There is no generic `jobs.enqueue` in the API: a job is started by the method of a concrete feature, which returns the snapshot
  immediately. An execution error is stored in the job with status failed.
- The UI creates an `operationId` per user action. Enqueuing again with the same `operationId` in a project returns the existing job and
  does not create a second one.
- Each saved change increments `sequence` and is delivered as an event; the UI applies a snapshot only if its `sequence` is greater than
  the known one. A lost event does not break the screen: the state can always be requested.
- Cancellation is a separate idempotent command. Closing a screen or window does not cancel a job; application exit follows the recovery
  rules.
- Large data (previews, frames, logs) never travels in events; an event says it is ready, and the UI gets it with a separate request or
  through the resource channel.

A response timeout on the frontend does not cancel the Core operation, so a short operation must also be safe to repeat.
