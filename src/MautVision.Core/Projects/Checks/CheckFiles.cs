using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Projects.Storage;

namespace MautVision.Core.Projects.Checks;

/// <summary>
/// Inputs of check job folders without copies: the weights of the version and the chosen file are hard links (a link shares the
/// data, so nothing is read or written); a source on another volume is given to the worker by its absolute path instead. The
/// weights are verified by size and SHA-256 before the worker gets them.
/// </summary>
internal static class CheckFiles
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    internal static readonly string[] VideoExtensions = [".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v"];

    /// <summary>
    /// input/model/best.pt for the worker: a hard link to the version's best.pt (a copy only on a volume without links), after its
    /// size and SHA-256 matched the version record (<c>training.artifact_corrupt</c> otherwise).
    /// </summary>
    internal static async Task<string> LinkWeightsAsync(string versionDirectory, ModelVersionFile weights, string jobDirectory, CancellationToken cancellationToken)
    {
        var source = Path.Combine(versionDirectory, weights.Name);
        var info = new FileInfo(source);
        if (!info.Exists || info.Length != weights.ByteSize) throw Corrupt(weights);
        await using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) != weights.Sha256) throw Corrupt(weights);
        }
        var model = Directory.CreateDirectory(Path.Combine(jobDirectory, "input", "model")).FullName;
        var target = Path.Combine(model, "best.pt");
        if (!TryHardLink(source, target)) File.Copy(source, target, overwrite: true);
        return "input/model/best.pt";
    }

    /// <summary>
    /// The chosen file as the worker reads it: a hard link input/source/source&lt;ext&gt; (relative), or the absolute path when the file
    /// is on another volume. Changed since it was chosen (size or time of change) — <c>checks.source_changed</c>.
    /// </summary>
    internal static (string? Relative, string? External) LinkSource(CheckSourceFile source, string jobDirectory)
    {
        RequireUnchanged(source);
        var folder = Directory.CreateDirectory(Path.Combine(jobDirectory, "input", "source")).FullName;
        var name = "source" + Path.GetExtension(source.Path).ToLowerInvariant();
        return TryHardLink(source.Path, Path.Combine(folder, name)) ? ("input/source/" + name, null) : (null, Path.GetFullPath(source.Path));
    }

    /// <summary>The file still has the size and the time of change it had when it was chosen.</summary>
    internal static void RequireUnchanged(CheckSourceFile source)
    {
        var info = new FileInfo(source.Path);
        if (!info.Exists) throw new CoreException("checks.prediction_source", $"The chosen file {Path.GetFileName(source.Path)} does not exist any more.");
        if (info.Length != source.Length || info.LastWriteTimeUtc != source.LastWriteTimeUtc.UtcDateTime)
            throw new CoreException("checks.source_changed", $"The chosen file {Path.GetFileName(source.Path)} changed after it was chosen.");
    }

    internal static async Task WriteJsonAsync(string path, JsonNode value, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, value.ToJsonString(Json), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>input/manifest.json: the protocol, the operation, the version and its classes in the order of model outputs.</summary>
    internal static JsonObject Manifest(string operationId, ModelVersion version, string? datasetName, string? sourceName) => new()
    {
        ["protocolVersion"] = Python.PythonWorkers.ProtocolVersion,
        ["operationId"] = operationId,
        ["model"] = new JsonObject { ["versionId"] = version.Id.ToString("D"), ["number"] = version.Number, ["architecture"] = version.Architecture },
        ["classes"] = new JsonArray(version.Classes.OrderBy(item => item.Index).Select(item => (JsonNode)new JsonObject
        {
            ["index"] = item.Index,
            ["classId"] = item.ClassId?.ToString("D"),
            ["name"] = item.Name,
        }).ToArray()),
        ["dataset"] = datasetName is null ? null : new JsonObject { ["name"] = datasetName },
        ["source"] = sourceName is null ? null : new JsonObject { ["name"] = sourceName },
    };

    /// <summary>Removes a finished job folder: all of it after success or cancel, inputs and output (not the logs) after a failure.</summary>
    internal static void Clean(string directory, bool keepLogs, Action<Exception> warn)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            if (!keepLogs)
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            foreach (var part in new[] { "input", "output" })
            {
                var path = Path.Combine(directory, part);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A file still open (antivirus, a viewer) keeps the folder; the next open of the project removes it.
            warn(error);
        }
    }

    private static CoreException Corrupt(ModelVersionFile file) => new("training.artifact_corrupt", $"Weights {file.Name} are missing or differ from their SHA-256.")
    {
        Details = new Dictionary<string, string> { ["name"] = file.Name },
    };

    private static bool TryHardLink(string source, string target)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (File.Exists(target)) File.Delete(target);
        return HardLink.TryCreate(target, source);
    }
}
