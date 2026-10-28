using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Jobs;
using MautVision.Core.Python;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects.Training;

/// <summary>
/// A training run: preparing (job folder with the materialized dataset, verified weights, manifest and request) → training (the
/// Python worker; events become progress of the job, live fields, epochs and log of the run) → acceptance (a verified result
/// becomes a model version). There is no automatic retry: a run that ended with the application is decided when the project is
/// opened again (<see cref="TrainingManager.RecoverAsync"/>). The run holds the GPU slot of the queue for its whole duration.
/// </summary>
internal sealed class TrainingRunHandler(IProjectsManager projects) : IJobHandler<TrainingRunInput>
{
    internal const string Kind = TrainingManager.Kind;

    public JobResource Resource => JobResource.Gpu;

    public bool Resumable => false;

    /// <summary>Smoke seam: awaited at "worker-exited" (the worker ended, its outcome not yet handled).</summary>
    internal Func<string, Guid, Task>? Pause { get; set; }

    public async Task ExecuteAsync(TrainingRunInput input, JobContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        var token = context.CancellationToken;
        var project = (Project)await projects.GetAsync(context.ProjectId, token).ConfigureAwait(false);
        var training = project.TrainingManager;
        var runId = context.JobId;
        var directory = training.JobDirectory(runId);
        var epochs = input.Parameters.TryGetValue("epochs", out var requested) && requested.TryGetInt32(out var count) ? count : 1;
        await training.RegisterRunAsync(runId).ConfigureAwait(false);
        context.ReportProgress(0, "Подготовка");
        var succeeded = false;
        var recorder = training.CreateRecorder(runId);
        try
        {
            recorder.Log(TrainingLogKind.System, "Подготовка папки задачи: датасет, базовые веса, параметры.");
            await TrainingJobFolder.PrepareAsync(training, directory, runId, input, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await recorder.FlushAsync().ConfigureAwait(false);
            await training.SetPhaseAsync(runId, "training", systemLine: "Запуск Python worker.").ConfigureAwait(false);
            context.ReportProgress(0, $"Эпоха 0 из {epochs}", 0, epochs);
            var outcome = await training.Python.RunAsync(directory, TrainingManager.OperationIdOf(runId), "train",
                worker => OnEventAsync(worker, recorder, context, ref epochs), WorkerResults.ParseTrainAsync, token).ConfigureAwait(false);
            if (Pause is { } pause) await pause("worker-exited", runId).ConfigureAwait(false);
            await recorder.FlushAsync().ConfigureAwait(false);
            switch (outcome.Kind)
            {
                case WorkerOutcomeKind.Cancelled:
                    throw new OperationCanceledException(token);
                case WorkerOutcomeKind.Failed:
                    throw new CoreException("training." + outcome.Code, outcome.Message ?? "");
            }
            context.ReportProgress(100, "Сохранение версии модели", epochs, epochs);
            await recorder.DisposeAsync().ConfigureAwait(false);
            var versionId = await training.AcceptAsync(runId, input, outcome.Result!, CancellationToken.None).ConfigureAwait(false);
            context.SetResult(new TrainingRunResult(versionId));
            succeeded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await Finish(null, null, "Обучение отменено.").ConfigureAwait(false);
            throw;
        }
        catch (CoreException error)
        {
            context.Logger.LogWarning("Training run {RunId} of project {ProjectId} failed with {Code}: {Message}", runId, context.ProjectId, error.Code, error.Message);
            await Finish(error.Code, error.Message, "Обучение завершилось с ошибкой.").ConfigureAwait(false);
            throw;
        }
        catch (Exception error) when (error is not JobDataException)
        {
            context.Logger.LogError(error, "Training run {RunId} of project {ProjectId} failed.", runId, context.ProjectId);
            var code = FailureCode(error);
            await Finish(code, error.Message, "Обучение завершилось с ошибкой.").ConfigureAwait(false);
            throw new CoreException(code, error.Message, error);
        }
        finally
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
            training.CleanJobFolder(runId, succeeded);
        }

        async Task Finish(string? code, string? message, string line)
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
            await training.SetPhaseAsync(runId, "finished", code, message, line).ConfigureAwait(false);
        }
    }

    /// <summary>Worker events: live fields and history of the run, progress of the job («Эпоха X из Y», batches inside the epoch).</summary>
    private static Task OnEventAsync(WorkerEvent worker, TrainingRunRecorder recorder, JobContext context, ref int epochs)
    {
        switch (worker)
        {
            case WorkerStageEvent stage:
                recorder.Stage(stage.Stage);
                break;
            case WorkerBatchEvent batch:
                recorder.Batch(batch);
                var total = recorder.TotalEpochs ?? epochs;
                var fraction = batch.Batches > 0 ? Math.Clamp((double)batch.Batch / batch.Batches, 0, 1) : 0;
                context.ReportProgress(Math.Clamp(100.0 * (batch.Epoch - 1 + fraction) / Math.Max(1, total), 0, 100), $"Эпоха {batch.Epoch} из {total}",
                    batch.Epoch - 1, total);
                break;
            case WorkerEpochEvent epoch:
                recorder.Epoch(epoch);
                epochs = epoch.Epochs;
                context.ReportProgress(Math.Clamp(100.0 * epoch.Epoch.Epoch / Math.Max(1, epoch.Epochs), 0, 100), $"Эпоха {epoch.Epoch.Epoch} из {epoch.Epochs}",
                    epoch.Epoch.Epoch, epoch.Epochs);
                break;
            case WorkerEffectiveParamsEvent effective:
                recorder.EffectiveParams(effective.Parameters);
                break;
            case WorkerEnvironmentEvent environment:
                recorder.Environment(environment.Environment);
                break;
            case WorkerLogEvent log:
                recorder.Log(TrainingLogKind.Log, log.Message);
                break;
            case WorkerWarningEvent warning:
                recorder.Log(TrainingLogKind.Warning, $"{warning.Code}: {warning.Message}");
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>training.disk_full, training.out_of_memory, otherwise training.execution_failed.</summary>
    internal static string FailureCode(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL; SQLITE_FULL.
            if (current is IOException io && (io.HResult & 0xFFFF) is 39 or 112) return "training.disk_full";
            if (current is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 13 }) return "training.disk_full";
            if (current is OutOfMemoryException) return "training.out_of_memory";
        }
        return "training.execution_failed";
    }
}
