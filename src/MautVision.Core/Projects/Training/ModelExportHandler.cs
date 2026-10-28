using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects.Training;

/// <summary>
/// ZIP of a model version (the layout of the previous exporter): weights/best.pt and weights/best.onnx streamed from the version
/// folder with SHA-256 and size checked on the way, then manifest.json, classes.json and README.md. Entries have a fixed date and
/// a fixed order, so exporting the same version twice gives the same bytes. The archive is written to a temporary file next to the
/// destination and replaces it at the end; cancel and failure remove the temporary file.
/// </summary>
internal sealed class ModelExportHandler(IProjectsManager projects) : IJobHandler<ModelExportInput>
{
    internal const string Kind = TrainingManager.ExportKind;
    private const int CopyBuffer = 1 << 20;

    /// <summary>The date of every entry: the archive does not depend on when it was made.</summary>
    internal static readonly DateTimeOffset EntryTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Class names stay readable Cyrillic in the files a person opens.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public JobResource Resource => JobResource.Cpu;

    /// <summary>Smoke seam: awaited after the weights were written.</summary>
    internal Func<Guid, Task>? Pause { get; set; }

    internal static string FileName(int number) => $"model-v{number.ToString(CultureInfo.InvariantCulture)}.zip";

    internal static string TemporaryPath(string destination, Guid jobId) => destination + "." + jobId.ToString("N") + ".tmp";

    public async Task ExecuteAsync(ModelExportInput input, JobContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        var token = context.CancellationToken;
        var project = (Project)await projects.GetAsync(context.ProjectId, token).ConfigureAwait(false);
        var training = project.TrainingManager;
        var version = await training.FindVersionForJobAsync(input.VersionId, token).ConfigureAwait(false)
            ?? throw new CoreException("training.version_not_found", $"Model version {input.VersionId} does not exist.");
        var folder = await training.VersionDirectoryAsync(version.Id, token).ConfigureAwait(false);
        var weights = version.Files.Where(file => file.Kind is ModelFileKind.WeightsPt or ModelFileKind.Onnx)
            .OrderBy(file => file.Kind == ModelFileKind.WeightsPt ? 0 : 1).ToArray();
        var total = weights.Sum(file => file.ByteSize);
        long written = 0;
        context.ReportProgress(0, null, 0, total);
        var temporary = TemporaryPath(input.DestinationPath, context.JobId);
        var moved = false;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBuffer);
        try
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var file in weights)
                    {
                        AddWeights(zip, file, Path.Combine(folder, file.Name));
                    }
                    if (Pause is { } pause) await pause(context.JobId).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    AddText(zip, "manifest.json", Manifest(context.ProjectId, version, weights).ToJsonString(Json) + "\n");
                    AddText(zip, "classes.json", Classes(version).ToJsonString(Json) + "\n");
                    AddText(zip, "README.md", Readme(version, weights));
                }
                output.Flush();
            }
            var bytes = new FileInfo(temporary).Length;
            string sha256;
            await using (var archive = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, token).ConfigureAwait(false));
            }
            // The user confirmed the overwrite in «Сохранить как»; the old file is replaced only by a complete archive.
            File.Move(temporary, input.DestinationPath, overwrite: true);
            moved = true;
            context.SetResult(new ModelExportResult(version.Id, version.Number, Path.GetFileName(input.DestinationPath), bytes, sha256));
            context.ReportProgress(100, null, total, total);
            context.Logger.LogInformation("Model version {VersionId} of project {ProjectId} exported: {Bytes} bytes, sha256 {Sha256}.", version.Id,
                context.ProjectId, bytes, sha256);
        }
        catch (Exception error) when (error is not (CoreException or OperationCanceledException))
        {
            context.Logger.LogError(error, "Export of model version {VersionId} of project {ProjectId} failed.", input.VersionId, context.ProjectId);
            throw new CoreException(FailureCode(error), error.Message, error);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (!moved)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    context.Logger.LogWarning(error, "Temporary export file of job {JobId} was not removed.", context.JobId);
                }
            }
        }

        void AddWeights(ZipArchive zip, ModelVersionFile file, string path)
        {
            FileStream source;
            try
            {
                source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                throw Corrupt(file.Name, error);
            }
            using (source)
            {
                if (source.Length != file.ByteSize) throw Corrupt(file.Name, null);
                var entry = zip.CreateEntry("weights/" + file.Name, CompressionLevel.Optimal);
                entry.LastWriteTime = EntryTime;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var target = entry.Open();
                int read;
                while ((read = source.Read(buffer, 0, CopyBuffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, read);
                    target.Write(buffer, 0, read);
                    written += read;
                    context.ReportProgress(total == 0 ? 100 : 100.0 * written / total, null, written, total);
                }
                if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.Ordinal)) throw Corrupt(file.Name, null);
            }
        }

        CoreException Corrupt(string name, Exception? inner) => new("training.artifact_corrupt", $"File {name} of model version {input.VersionId} is missing or differs from its SHA-256.", inner)
        {
            Details = new Dictionary<string, string> { ["name"] = name },
        };
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;
        using var target = entry.Open();
        target.Write(new UTF8Encoding(false).GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal)));
    }

    /// <summary>manifest.json (schemaVersion 1): the passport of the version and its weights with SHA-256.</summary>
    internal static JsonObject Manifest(string projectId, ModelVersion version, IReadOnlyList<ModelVersionFile> weights) => new()
    {
        ["schemaVersion"] = 1,
        ["projectId"] = projectId,
        ["model"] = new JsonObject
        {
            ["versionId"] = version.Id.ToString("D"),
            ["version"] = version.Number,
            ["runId"] = version.RunId.ToString("D"),
            ["createdAt"] = version.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["architecture"] = version.Architecture,
            ["input"] = new JsonObject { ["width"] = version.Input.Width, ["height"] = version.Input.Height },
            ["imageSize"] = version.ImageSize,
        },
        ["classes"] = new JsonArray(version.Classes.OrderBy(item => item.Index).Select(item => (JsonNode)new JsonObject
        {
            ["index"] = item.Index,
            ["classId"] = item.ClassId?.ToString("D"),
            ["name"] = item.Name,
            ["sourceClassIds"] = new JsonArray(item.SourceClassIds.Select(id => (JsonNode)JsonValue.Create(id.ToString("D"))!).ToArray()),
        }).ToArray()),
        ["parameters"] = JsonSerializer.SerializeToNode(version.Parameters),
        ["effectiveParams"] = JsonNode.Parse(version.EffectiveParams.GetRawText()),
        ["epochsCompleted"] = version.EpochsCompleted,
        ["stoppedEarly"] = version.StoppedEarly,
        ["bestEpoch"] = version.BestEpoch,
        ["metrics"] = JsonSerializer.SerializeToNode(version.Metrics),
        ["dataset"] = new JsonObject
        {
            ["versionId"] = version.Dataset.VersionId.ToString("D"),
            ["name"] = version.Dataset.Name,
            ["manifestHash"] = version.Dataset.ManifestHash,
            ["images"] = new JsonObject
            {
                ["train"] = version.Dataset.Counts.Train,
                ["val"] = version.Dataset.Counts.Validation,
                ["test"] = version.Dataset.Counts.Test,
            },
        },
        ["artifacts"] = new JsonArray(weights.Select(file => (JsonNode)new JsonObject
        {
            ["kind"] = file.Kind == ModelFileKind.WeightsPt ? "weights_pt" : "onnx",
            ["path"] = "weights/" + file.Name,
            ["sha256"] = file.Sha256,
            ["byteSize"] = file.ByteSize,
        }).ToArray()),
        ["notes"] = new JsonArray(version.Notes.Select(note => (JsonNode)JsonValue.Create(note)!).ToArray()),
        ["environment"] = JsonNode.Parse(version.Environment.GetRawText()),
    };

    /// <summary>classes.json: the exact map of model outputs (class_index, name, class_id).</summary>
    internal static JsonObject Classes(ModelVersion version) => new()
    {
        ["schemaVersion"] = 1,
        ["modelVersionId"] = version.Id.ToString("D"),
        ["classes"] = new JsonArray(version.Classes.OrderBy(item => item.Index).Select(item => (JsonNode)new JsonObject
        {
            ["class_index"] = item.Index,
            ["name"] = item.Name,
            ["class_id"] = item.ClassId?.ToString("D"),
        }).ToArray()),
    };

    /// <summary>README.md in Russian: architecture, input, parameters, the class table, metrics and files with SHA-256.</summary>
    internal static string Readme(ModelVersion version, IReadOnlyList<ModelVersionFile> weights)
    {
        static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
        static string Value(System.Text.Json.JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Null => "авто",
            _ => value.GetRawText(),
        };
        string Parameter(string key) => version.EffectiveParams.ValueKind == JsonValueKind.Object && version.EffectiveParams.TryGetProperty(key, out var effective)
            ? Value(effective)
            : version.Parameters.TryGetValue(key, out var requested) ? Value(requested) : "—";
        var text = new StringBuilder();
        text.Append("# Модель, версия ").Append(version.Number).Append("\n\n");
        text.Append("Пакет выгружен из Maut Vision. Порядок классов ниже совпадает с номерами выходов модели;\n");
        text.Append("подробный паспорт — в manifest.json (архитектура, вход, параметры, метрики, sha256).\n");
        text.Append("Точная карта выходов — classes.json: class_index, name и class_id.\n\n");
        text.Append("- Архитектура: ").Append(version.Architecture).Append('\n');
        text.Append("- Вход: ").Append(version.Input.Width).Append(" × ").Append(version.Input.Height).Append(" (imgsz ").Append(version.ImageSize).Append(")\n");
        text.Append("- Обучение: эпох ").Append(version.EpochsCompleted).Append(version.StoppedEarly ? " (ранняя остановка)" : "")
            .Append(", batch ").Append(Parameter("batch")).Append(", lr0 ").Append(Parameter("lr0")).Append(", оптимизатор ").Append(Parameter("optimizer"))
            .Append(", устройство ").Append(Parameter("device")).Append('\n');
        text.Append("- Датасет: ").Append(version.Dataset.Name).Append(" (manifest ").Append(version.Dataset.ManifestHash).Append(")\n");
        text.Append("- Создана: ").Append(version.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)).Append("\n\n");
        text.Append("## Классы\n\n| Индекс | Название | ID класса |\n|---|---|---|\n");
        foreach (var item in version.Classes.OrderBy(item => item.Index))
        {
            text.Append("| ").Append(item.Index).Append(" | ").Append(Cell(item.Name)).Append(" | ").Append(item.ClassId?.ToString("D") ?? "—").Append(" |\n");
        }
        text.Append("\n## Метрики\n\n");
        if (version.Metrics.Count == 0) text.Append("- нет данных\n");
        foreach (var (name, value) in version.Metrics) text.Append("- ").Append(name).Append(": ").Append(value.ToString("0.000", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("\n## Файлы\n\n");
        foreach (var file in weights)
        {
            text.Append("- weights/").Append(file.Name).Append(" (").Append(file.ByteSize).Append(" байт, sha256 ").Append(file.Sha256).Append(")\n");
        }
        return text.ToString();
    }

    /// <summary>training.disk_full, otherwise training.export_failed (a destination that cannot be written, a lost drive).</summary>
    private static string FailureCode(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is IOException io && (io.HResult & 0xFFFF) is 39 or 112) return "training.disk_full";
        }
        return "training.export_failed";
    }
}
