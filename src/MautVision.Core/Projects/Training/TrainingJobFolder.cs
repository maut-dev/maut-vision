using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Python;

namespace MautVision.Core.Projects.Training;

/// <summary>
/// Prepares the job folder of a run (protocol v1 of python/worker): input/dataset — the dataset version materialized with hard
/// links, input/model/&lt;architecture&gt;.pt — a copy of the installed weights verified by SHA-256, input/manifest.json, and
/// request.json with relative paths and every parameter. Inputs are immutable while the worker runs.
/// </summary>
internal static class TrainingJobFolder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static async Task PrepareAsync(TrainingManager training, string directory, Guid runId, TrainingRunInput input, CancellationToken cancellationToken)
    {
        if (training.Python.CheckRuntime() is { } missing) throw new CoreException("training." + missing.Code, missing.Message);
        // A folder left by an earlier attempt of this run is never reused.
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        var model = Directory.CreateDirectory(Path.Combine(directory, "input", "model")).FullName;
        Directory.CreateDirectory(Path.Combine(directory, "output"));
        Directory.CreateDirectory(Path.Combine(directory, "logs"));

        var baseModel = await training.BaseModels.GetOrNullAsync(input.Architecture, cancellationToken).ConfigureAwait(false)
            ?? throw new CoreException("training.base_model_not_installed", $"Base model {input.Architecture} is not in the catalogue.");
        if (!await baseModel.VerifyIntegrityAsync(cancellationToken).ConfigureAwait(false))
            throw new CoreException("training.base_weights_missing", $"Weights of {input.Architecture} are missing or damaged.");
        var weightsName = input.Architecture + ".pt";
        var weights = Path.Combine(model, weightsName);
        string weightsSha256;
        await using (var stream = new FileStream(weights, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            await baseModel.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            weightsSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        var source = await training.Datasets.MaterializeForJobAsync(input.DatasetVersionId, Path.Combine(directory, "input", "dataset"), cancellationToken)
            .ConfigureAwait(false);
        var operationId = TrainingManager.OperationIdOf(runId);
        var manifest = new
        {
            protocolVersion = PythonWorkers.ProtocolVersion,
            operationId,
            dataset = new { versionId = source.VersionId, name = source.Name, manifestHash = source.ManifestHash },
            classes = source.Classes.Select(item => new { index = item.Index, classId = item.ClassId, name = item.Name }),
            baseWeights = new { architectureId = input.Architecture, fileName = weightsName, sha256 = weightsSha256 },
        };
        await WriteAsync(Path.Combine(directory, "input", "manifest.json"), JsonSerializer.SerializeToNode(manifest, Json)!, cancellationToken).ConfigureAwait(false);

        var parameters = new JsonObject
        {
            ["architecture"] = input.Architecture,
            ["imgsz"] = input.ImageSize,
            ["exportOnnx"] = true,
        };
        foreach (var (key, value) in input.Parameters) parameters[key] = JsonNode.Parse(value.GetRawText());
        var request = new JsonObject
        {
            ["protocolVersion"] = PythonWorkers.ProtocolVersion,
            ["operationId"] = operationId,
            ["operation"] = "train",
            ["input"] = new JsonObject
            {
                ["dataset"] = "input/dataset",
                ["model"] = "input/model/" + weightsName,
                ["manifest"] = "input/manifest.json",
            },
            ["outputRoot"] = "output",
            ["params"] = parameters,
        };
        await WriteAsync(Path.Combine(directory, "request.json"), request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAsync(string path, JsonNode value, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, value.ToJsonString(Json), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }
}
