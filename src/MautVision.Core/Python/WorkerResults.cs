using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MautVision.Core.Python;

/// <summary>A file listed in result.json, checked on disk: Kind (weights_pt, onnx, plot, log), RelativePath inside the output root.</summary>
internal sealed record WorkerFile(string Kind, string RelativePath, string AbsolutePath, long ByteSize, string Sha256)
{
    internal string Name => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];
}

internal sealed record WorkerWarning(string Code, string Message);

/// <summary>
/// An accepted train result: what the weights record (Architecture), applied parameters, best.pt metrics, epochs completed and
/// early stop, the history from history.jsonl, artifacts and report files verified by size and SHA-256, environment, warnings.
/// </summary>
internal sealed record WorkerTrainResult(
    JsonElement Architecture,
    JsonElement EffectiveParams,
    IReadOnlyDictionary<string, double> Metrics,
    int EpochsCompleted,
    bool StoppedEarly,
    IReadOnlyList<WorkerEpoch> History,
    IReadOnlyList<WorkerFile> Artifacts,
    IReadOnlyList<WorkerFile> Reports,
    JsonElement Environment,
    IReadOnlyList<WorkerWarning> Warnings);

/// <summary>
/// Reading of output/result.json (python/worker/protocol/result.py). The envelope binds a result to its protocol version and
/// operation; a success is accepted only when every listed file lies inside the output root, is a regular non-empty file and
/// matches its size and SHA-256. A failed result yields the worker's code and Russian message.
/// </summary>
internal static partial class WorkerResults
{
    internal const string ResultFileName = "result.json";
    private const long MaxResultBytes = 64L * 1024 * 1024;

    /// <summary>The result document, or null when the file does not exist; unreadable JSON is result_invalid.</summary>
    internal static async Task<JsonDocument?> ReadAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(outputDirectory, ResultFileName);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
            if (stream.Length > MaxResultBytes) throw new WorkerProtocolException("result_invalid", "output/result.json слишком большой.");
            return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 64 }, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new WorkerProtocolException("result_invalid", "output/result.json не является корректным JSON.");
        }
    }

    /// <summary>Protocol version and operation of the result; anything else belongs to another run.</summary>
    internal static void CheckEnvelope(JsonElement root, string operationId, string operation, int protocolVersion)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new WorkerProtocolException("result_invalid", "result.json должен быть JSON-объектом.");
        if (!root.TryGetProperty("protocolVersion", out var version) || !version.TryGetInt32(out var number) || number != protocolVersion)
            throw new WorkerProtocolException("protocol_version", "result.json: неподдерживаемая или отсутствующая версия протокола.");
        if (WorkerEventParser.String(root, "operationId") != operationId)
            throw new WorkerProtocolException("result_operation_mismatch", "result.json относится к другой операции.");
        if (WorkerEventParser.String(root, "operation") != operation)
            throw new WorkerProtocolException("result_invalid", $"result.json: операция не {operation}.");
    }

    /// <summary>The worker's own failure (status failed with error.code and error.message); null for any other document.</summary>
    internal static (string Code, string Message)? ReadFailure(JsonElement root, string operationId, string operation, int protocolVersion)
    {
        try
        {
            CheckEnvelope(root, operationId, operation, protocolVersion);
        }
        catch (WorkerProtocolException)
        {
            return null;
        }
        if (WorkerEventParser.String(root, "status") != "failed" || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return null;
        var code = WorkerEventParser.String(error, "code");
        var message = WorkerEventParser.String(error, "message");
        if (code is null || message is null) return null;
        var cleaned = CodePattern().Replace(code, "");
        cleaned = cleaned.Length == 0 ? "unknown" : cleaned[..Math.Min(60, cleaned.Length)];
        return (cleaned, message.Length <= 2000 ? message : message[..2000]);
    }

    /// <summary>A successful train result with every file and the history verified.</summary>
    internal static async Task<WorkerTrainResult> ParseTrainAsync(JsonElement root, string outputDirectory, CancellationToken cancellationToken)
    {
        if (WorkerEventParser.String(root, "status") != "succeeded") throw Invalid("result.json: статус не означает успех.");
        if (!root.TryGetProperty("effectiveParams", out var effective) || effective.ValueKind != JsonValueKind.Object
            || WorkerEventParser.String(effective, "architecture") is null || WorkerEventParser.String(effective, "device") is null)
            throw Invalid("result.effectiveParams отсутствует или без architecture/device.");
        var outputRoot = Path.GetFullPath(outputDirectory);
        if (!root.TryGetProperty("artifacts", out var artifactsValue) || artifactsValue.ValueKind != JsonValueKind.Array || artifactsValue.GetArrayLength() == 0)
            throw Invalid("result.artifacts должен содержать хотя бы одну запись.");
        var artifacts = new List<WorkerFile>();
        foreach (var entry in artifactsValue.EnumerateArray())
        {
            var kind = entry.ValueKind == JsonValueKind.Object ? WorkerEventParser.String(entry, "kind") : null;
            if (kind is not ("weights_pt" or "onnx")) throw Invalid("Артефакт должен иметь kind weights_pt или onnx.");
            if (artifacts.Any(item => item.Kind == kind)) throw Invalid($"Артефакт {kind} указан дважды.");
            var file = await VerifyFileAsync(outputRoot, entry, kind, "Артефакт", cancellationToken).ConfigureAwait(false);
            if (!file.Name.EndsWith(kind == "onnx" ? ".onnx" : ".pt", StringComparison.OrdinalIgnoreCase))
                throw Invalid($"Артефакт {file.Name}: расширение не соответствует {kind}.");
            artifacts.Add(file);
        }
        if (!artifacts.Any(item => item.Kind == "weights_pt")) throw Invalid("В результате нет весов weights_pt.");
        var reports = new List<WorkerFile>();
        if (root.TryGetProperty("reports", out var reportsValue) && reportsValue.ValueKind != JsonValueKind.Null)
        {
            if (reportsValue.ValueKind != JsonValueKind.Array) throw Invalid("result.reports должен быть массивом.");
            foreach (var entry in reportsValue.EnumerateArray())
            {
                var kind = entry.ValueKind == JsonValueKind.Object ? WorkerEventParser.String(entry, "kind") : null;
                if (kind is not ("plot" or "log")) throw Invalid("Отчётный файл должен иметь kind plot или log.");
                var file = await VerifyFileAsync(outputRoot, entry, kind, "Отчётный файл", cancellationToken).ConfigureAwait(false);
                var extension = Path.GetExtension(file.Name).ToLowerInvariant();
                if (kind == "plot" ? extension is not (".png" or ".jpg" or ".jpeg") : extension is not (".log" or ".txt"))
                    throw Invalid($"Отчётный файл {file.Name}: неподходящее имя для {kind}.");
                if (reports.Any(item => item.Kind == kind && (kind == "log" || string.Equals(item.Name, file.Name, StringComparison.OrdinalIgnoreCase))))
                    throw Invalid(kind == "log" ? "В результате больше одного лога обучения." : $"Картинка {file.Name} указана дважды.");
                reports.Add(file);
            }
        }
        IReadOnlyList<WorkerEpoch> history;
        if (root.TryGetProperty("historyFile", out var historyFile) && historyFile.ValueKind == JsonValueKind.Object)
        {
            history = await ReadHistoryFileAsync(outputRoot, historyFile, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            history = root.TryGetProperty("history", out var inline) && inline.ValueKind == JsonValueKind.Array ? ReadHistory(inline.EnumerateArray()) : [];
        }
        var epochsCompleted = root.TryGetProperty("epochsCompleted", out var completed) && completed.TryGetInt32(out var count) && count >= 0 ? count : history.Count;
        var stoppedEarly = root.TryGetProperty("stoppedEarly", out var stopped) && stopped.ValueKind == JsonValueKind.True;
        var warnings = new List<WorkerWarning>();
        if (root.TryGetProperty("warnings", out var warningsValue) && warningsValue.ValueKind == JsonValueKind.Array)
        {
            foreach (var warning in warningsValue.EnumerateArray())
            {
                if (warning.ValueKind != JsonValueKind.Object) continue;
                var code = WorkerEventParser.String(warning, "code") ?? "";
                var message = WorkerEventParser.String(warning, "message") ?? "";
                warnings.Add(new WorkerWarning(code.Length <= 100 ? code : code[..100], message.Length <= 2000 ? message : message[..2000]));
            }
        }
        return new WorkerTrainResult(
            root.TryGetProperty("architecture", out var architecture) && architecture.ValueKind == JsonValueKind.Object ? architecture.Clone() : Empty(),
            effective.Clone(),
            WorkerEventParser.Numbers(root, "metrics", "result_invalid"),
            epochsCompleted,
            stoppedEarly,
            history,
            artifacts,
            reports,
            root.TryGetProperty("environment", out var environment) && environment.ValueKind == JsonValueKind.Object ? environment.Clone() : Empty(),
            warnings);
    }

    private static IReadOnlyList<WorkerEpoch> ReadHistory(IEnumerable<JsonElement> records)
    {
        var history = new List<WorkerEpoch>();
        var previous = 0;
        foreach (var record in records)
        {
            if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("epoch", out var epochValue) || !epochValue.TryGetInt32(out var epoch) || epoch <= previous)
                throw Invalid("История эпох: эпохи должны быть целыми и строго возрастать.");
            previous = epoch;
            history.Add(WorkerEventParser.ReadEpoch(record, epoch, "result_invalid"));
        }
        return history;
    }

    private static async Task<IReadOnlyList<WorkerEpoch>> ReadHistoryFileAsync(string outputRoot, JsonElement entry, CancellationToken cancellationToken)
    {
        if (!entry.TryGetProperty("count", out var countValue) || !countValue.TryGetInt32(out var count) || count < 0)
            throw Invalid("historyFile требует неотрицательный count.");
        var file = await VerifyFileAsync(outputRoot, entry, "history", "История эпох", cancellationToken, allowEmpty: count == 0).ConfigureAwait(false);
        var records = new List<JsonElement>();
        foreach (var line in await File.ReadAllLinesAsync(file.AbsolutePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                records.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
                throw Invalid("history.jsonl содержит строку не в формате JSON.");
            }
        }
        var history = ReadHistory(records);
        if (history.Count != count) throw Invalid("history.jsonl: число эпох не совпадает с result.json.");
        return history;
    }

    /// <summary>A path of result.json: segments of safe characters only, inside the output root, a regular file with the stated size and hash.</summary>
    private static async Task<WorkerFile> VerifyFileAsync(string outputRoot, JsonElement entry, string kind, string label, CancellationToken cancellationToken,
        bool allowEmpty = false)
    {
        var relative = WorkerEventParser.String(entry, "path") ?? throw Invalid($"{label} без относительного path.");
        var sha256 = WorkerEventParser.String(entry, "sha256");
        if (sha256 is null || !Sha256Pattern().IsMatch(sha256) || !entry.TryGetProperty("sizeBytes", out var sizeValue) || !sizeValue.TryGetInt64(out var size))
            throw Invalid($"{label} {relative} без sha256 или sizeBytes.");
        var segments = relative.Split('/');
        if (segments.Any(segment => !SegmentPattern().IsMatch(segment) || segment is "." or ".."))
            throw Invalid($"Недопустимый путь файла результата: {relative}.");
        var absolute = Path.GetFullPath(Path.Combine([outputRoot, .. segments]));
        if (!absolute.StartsWith(outputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Invalid($"Путь файла результата выходит за пределы output: {relative}.");
        // Every folder between the output root and the file must be a real folder: a link could lead outside.
        var current = outputRoot;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            var info = new FileInfo(current);
            var attributes = info.Exists || Directory.Exists(current) ? File.GetAttributes(current) : (FileAttributes?)null;
            if (attributes is null) throw new WorkerProtocolException("artifact_missing", $"{label} не найден: {relative}.");
            if ((attributes.Value & FileAttributes.ReparsePoint) != 0) throw Invalid($"{label} {relative} — ссылка, а не файл.");
        }
        var fileInfo = new FileInfo(absolute);
        if (!fileInfo.Exists || (!allowEmpty && fileInfo.Length == 0))
            throw new WorkerProtocolException("artifact_missing", $"{label} не является непустым файлом: {relative}.");
        string actual;
        await using (var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        if (actual != sha256 || fileInfo.Length != size)
            throw new WorkerProtocolException("artifact_integrity", $"SHA-256 или размер файла {relative} не совпадает с result.json.");
        return new WorkerFile(kind, string.Join('/', segments), absolute, size, actual);
    }

    private static JsonElement Empty()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static WorkerProtocolException Invalid(string message) => new("result_invalid", message);

    [GeneratedRegex(@"\A[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();

    [GeneratedRegex(@"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("[^a-z0-9_]", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
