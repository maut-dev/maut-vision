using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Jobs;
using MautVision.Core.Projects.Training;
using MautVision.Core.Python;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects.Checks;

/// <summary>
/// An evaluation of a model version on a dataset part: preparing (job folder with the dataset linked, the weights linked after their
/// SHA-256 matched, manifest and request) → evaluating (the worker's model.val) → the metrics are stored. It holds the GPU slot of the
/// queue for its whole duration; there is no retry — an evaluation that ended with the application is «Прервано».
/// </summary>
internal sealed class EvaluationHandler(IProjectsManager projects) : IJobHandler<ModelEvaluationInput>
{
    internal const string Kind = ChecksManager.EvaluationKind;

    public JobResource Resource => JobResource.Gpu;

    public bool Resumable => false;

    /// <summary>Smoke seam: awaited at "worker-exited" (the worker ended, its outcome not yet handled).</summary>
    internal Func<string, Guid, Task>? Pause { get; set; }

    public async Task ExecuteAsync(ModelEvaluationInput input, JobContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        var token = context.CancellationToken;
        var project = (Project)await projects.GetAsync(context.ProjectId, token).ConfigureAwait(false);
        var checks = project.ChecksManager;
        var id = context.JobId;
        var directory = checks.EvaluationDirectory(id);
        var succeeded = false;
        context.ReportProgress(null, "Подготовка");
        try
        {
            var version = await checks.Training.FindVersionForJobAsync(input.VersionId, token).ConfigureAwait(false)
                ?? throw new CoreException("training.version_not_found", $"Model version {input.VersionId} does not exist.");
            await checks.RegisterEvaluationAsync(id, input).ConfigureAwait(false);
            if (checks.Python.CheckRuntime() is { } missing) throw new CoreException("checks." + missing.Code, missing.Message);
            // A folder left by an earlier attempt is never reused.
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Directory.CreateDirectory(Path.Combine(directory, "output"));
            Directory.CreateDirectory(Path.Combine(directory, "logs"));
            var weights = version.Files.FirstOrDefault(file => file.Kind == ModelFileKind.WeightsPt)
                ?? throw new CoreException("training.artifact_corrupt", $"Model version {version.Id} has no best.pt.");
            var model = await CheckFiles.LinkWeightsAsync(await checks.Training.VersionDirectoryAsync(version.Id, token).ConfigureAwait(false), weights, directory, token)
                .ConfigureAwait(false);
            await checks.Datasets.MaterializeForJobAsync(input.DatasetVersionId, Path.Combine(directory, "input", "dataset"), token,
                new InputSize(input.Width, input.Height)).ConfigureAwait(false);
            var operationId = id.ToString("D");
            await CheckFiles.WriteJsonAsync(Path.Combine(directory, "input", "manifest.json"), CheckFiles.Manifest(operationId, version, input.Dataset.Name, null), token)
                .ConfigureAwait(false);
            var request = new JsonObject
            {
                ["protocolVersion"] = PythonWorkers.ProtocolVersion,
                ["operationId"] = operationId,
                ["operation"] = "evaluate",
                ["input"] = new JsonObject { ["dataset"] = "input/dataset", ["model"] = model, ["manifest"] = "input/manifest.json" },
                ["outputRoot"] = "output",
                ["params"] = new JsonObject
                {
                    ["architecture"] = input.Architecture,
                    ["imgsz"] = input.ImageSize,
                    ["batch"] = input.Batch,
                    ["device"] = input.Device,
                    ["split"] = input.Split == EvaluationSplit.Test ? "test" : "val",
                },
            };
            await CheckFiles.WriteJsonAsync(Path.Combine(directory, "request.json"), request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await checks.SetEvaluationPhaseAsync(id, "evaluating").ConfigureAwait(false);
            context.ReportProgress(null, "Идёт проверка");
            var outcome = await checks.Python.RunAsync(directory, operationId, "evaluate", worker => OnEventAsync(worker, context), WorkerChecks.ParseEvaluateAsync, token)
                .ConfigureAwait(false);
            if (Pause is { } pause) await pause("worker-exited", id).ConfigureAwait(false);
            switch (outcome.Kind)
            {
                case WorkerOutcomeKind.Cancelled:
                    throw new OperationCanceledException(token);
                case WorkerOutcomeKind.Failed:
                    throw new CoreException("checks." + outcome.Code, outcome.Message ?? "");
            }
            await checks.AcceptEvaluationAsync(id, input, version, outcome.Result!).ConfigureAwait(false);
            succeeded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await checks.SetEvaluationPhaseAsync(id, "finished").ConfigureAwait(false);
            throw;
        }
        catch (CoreException error)
        {
            context.Logger.LogWarning("Evaluation {EvaluationId} of project {ProjectId} failed with {Code}: {Message}", id, context.ProjectId, error.Code, error.Message);
            await checks.SetEvaluationPhaseAsync(id, "finished", error.Code, error.Message).ConfigureAwait(false);
            throw;
        }
        catch (Exception error) when (error is not JobDataException)
        {
            context.Logger.LogError(error, "Evaluation {EvaluationId} of project {ProjectId} failed.", id, context.ProjectId);
            var code = TrainingRunHandler.FailureCode(error).Replace("training.", "checks.", StringComparison.Ordinal);
            await checks.SetEvaluationPhaseAsync(id, "finished", code, error.Message).ConfigureAwait(false);
            throw new CoreException(code, error.Message, error);
        }
        finally
        {
            CheckFiles.Clean(directory, keepLogs: !succeeded, error => context.Logger.LogWarning(error, "Evaluation folder {EvaluationId} was not cleaned.", id));
        }
    }

    /// <summary>Stages of the worker become the progress text of the job; model.val reports no measurable progress.</summary>
    private static Task OnEventAsync(WorkerEvent worker, JobContext context)
    {
        if (worker is WorkerStageEvent stage)
        {
            context.ReportProgress(null, stage.Stage switch
            {
                "validating" => "Проверка датасета",
                "loading_model" => "Загрузка модели",
                "evaluating" => "Идёт проверка",
                _ => null,
            });
        }
        return Task.CompletedTask;
    }
}
