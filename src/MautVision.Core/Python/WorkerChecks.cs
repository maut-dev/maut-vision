using System.Text.Json;
using System.Text.RegularExpressions;

namespace MautVision.Core.Python;

/// <summary>A detection of the worker: model output, its name, confidence and a box in fractions of the analysed frame (0..1).</summary>
internal sealed record WorkerDetection(int ClassIndex, string Name, double Confidence, double X, double Y, double Width, double Height);

/// <summary>
/// A frame event of predict and live: Index — video frame number (a running number on the screen), TimeMs, the analysed size,
/// detections, inference time and, at most once per preview interval, PreviewFile — a JPEG the worker wrote under the output root.
/// </summary>
internal sealed record WorkerFrame(long Index, long TimeMs, int Width, int Height, int InferenceMs, IReadOnlyList<WorkerDetection> Detections, string? PreviewFile);

/// <summary>Progress of a video: frames analysed of the planned ones, the position and the duration (null when unknown).</summary>
internal sealed record WorkerProgress(long FramesProcessed, long? FramesTotal, long? PositionMs, long? DurationMs);

internal sealed record WorkerClassSummary(int Index, string Name, long Frames, long Detections, double MaxConfidence);

internal sealed record WorkerSummary(long FramesProcessed, long FramesWithDetections, long Detections, IReadOnlyList<WorkerClassSummary> Classes);

internal sealed record WorkerSource(int Width, int Height, long? DurationMs, double? Fps);

/// <summary>An accepted predict or live result: the summary of every frame, what the worker saw in the input, its warnings.</summary>
internal sealed record WorkerPredictResult(WorkerSummary Summary, WorkerSource? Source, IReadOnlyList<WorkerWarning> Warnings);

internal sealed record WorkerClassMetrics(int Index, string Name, IReadOnlyDictionary<string, double> Metrics);

/// <summary>An accepted evaluate result: metrics of the part, per class, the images the worker counted in it and its environment.</summary>
internal sealed record WorkerEvaluateResult(IReadOnlyDictionary<string, double> Metrics, IReadOnlyList<WorkerClassMetrics> ClassMetrics, int? ImageCount,
    JsonElement Environment, IReadOnlyList<WorkerWarning> Warnings);

/// <summary>Frames, progress and results of the check operations (python/worker/operations/{evaluate,predict,live}).</summary>
internal static partial class WorkerChecks
{
    /// <summary>More detections than this in one frame is not a model output the host can show.</summary>
    internal const int MaxDetections = 100_000;

    internal static WorkerFrame ParseFrame(JsonElement root)
    {
        var detections = new List<WorkerDetection>();
        if (!root.TryGetProperty("detections", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > MaxDetections)
            throw Invalid("frame: detections должен быть массивом.");
        foreach (var item in items.EnumerateArray()) detections.Add(Detection(item, "frame"));
        string? preview = null;
        if (root.TryGetProperty("previewFile", out var file) && file.ValueKind == JsonValueKind.String)
        {
            preview = file.GetString()!;
            if (!PreviewPattern().IsMatch(preview)) throw Invalid("frame: previewFile вне папки previews/.");
        }
        return new WorkerFrame(Long(root, "index", 0), Long(root, "timeMs", 0), Integer(root, "width", 0), Integer(root, "height", 0),
            root.TryGetProperty("inferenceMs", out var inference) && inference.TryGetInt32(out var ms) && ms >= 0 ? ms : 0, detections, preview);
    }

    internal static WorkerProgress ParseProgress(JsonElement root) => new(Long(root, "framesProcessed", 0), OptionalLong(root, "framesTotal"),
        OptionalLong(root, "positionMs"), OptionalLong(root, "durationMs"));

    internal static Task<WorkerPredictResult> ParsePredictAsync(JsonElement root, string outputDirectory, CancellationToken cancellationToken)
        => Task.FromResult(AsResult(() => ReadPredict(root)));

    internal static Task<WorkerEvaluateResult> ParseEvaluateAsync(JsonElement root, string outputDirectory, CancellationToken cancellationToken)
        => Task.FromResult(AsResult(() => ReadEvaluate(root)));

    /// <summary>A malformed result.json is result_invalid, not an event violation.</summary>
    private static T AsResult<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (WorkerProtocolException error) when (error.Code == "protocol_event")
        {
            throw new WorkerProtocolException("result_invalid", error.Message);
        }
    }

    private static WorkerPredictResult ReadPredict(JsonElement root)
    {
        if (WorkerEventParser.String(root, "status") != "succeeded") throw Invalid("result.json: статус не означает успех.");
        if (!root.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Object) throw Invalid("result.summary отсутствует.");
        var classes = new List<WorkerClassSummary>();
        if (summary.TryGetProperty("classes", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                classes.Add(new WorkerClassSummary(Integer(item, "index", 0), WorkerEventParser.String(item, "name") ?? "", Long(item, "frames", 0),
                    Long(item, "detections", 0), Number(item, "maxConfidence")));
            }
        }
        WorkerSource? source = null;
        if (root.TryGetProperty("source", out var value) && value.ValueKind == JsonValueKind.Object)
        {
            source = new WorkerSource(Integer(value, "width", 0), Integer(value, "height", 0), OptionalLong(value, "durationMs"),
                value.TryGetProperty("fps", out var fps) && fps.ValueKind == JsonValueKind.Number && double.IsFinite(fps.GetDouble()) ? fps.GetDouble() : null);
        }
        return new WorkerPredictResult(new WorkerSummary(Long(summary, "framesProcessed", 0), Long(summary, "framesWithDetections", 0),
            Long(summary, "detections", 0), classes), source, Warnings(root));
    }

    private static WorkerEvaluateResult ReadEvaluate(JsonElement root)
    {
        if (WorkerEventParser.String(root, "status") != "succeeded") throw Invalid("result.json: статус не означает успех.");
        var metrics = WorkerEventParser.Numbers(root, "metrics", "result_invalid");
        var classes = new List<WorkerClassMetrics>();
        if (root.TryGetProperty("classMetrics", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw Invalid("classMetrics: ожидается объект.");
                classes.Add(new WorkerClassMetrics(Integer(item, "index", 0), WorkerEventParser.String(item, "name") ?? "",
                    WorkerEventParser.Numbers(item, "metrics", "result_invalid")));
            }
        }
        int? images = null;
        if (root.TryGetProperty("effectiveParams", out var effective) && effective.ValueKind == JsonValueKind.Object
            && effective.TryGetProperty("dataset", out var dataset) && dataset.ValueKind == JsonValueKind.Object)
        {
            var split = WorkerEventParser.String(effective, "split") == "test" ? "imagesTest" : "imagesVal";
            if (dataset.TryGetProperty(split, out var count) && count.TryGetInt32(out var number) && number >= 0) images = number;
        }
        var environment = root.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Object ? env.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        return new WorkerEvaluateResult(metrics, classes, images, environment, Warnings(root));
    }

    private static WorkerDetection Detection(JsonElement item, string where)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("box", out var box) || box.ValueKind != JsonValueKind.Object)
            throw Invalid($"{where}: обнаружение без рамки.");
        var name = WorkerEventParser.String(item, "name") ?? "";
        return new WorkerDetection(Integer(item, "classIndex", 0), name.Length <= 200 ? name : name[..200], Fraction(item, "confidence"), Fraction(box, "x"),
            Fraction(box, "y"), Fraction(box, "width"), Fraction(box, "height"));
    }

    private static IReadOnlyList<WorkerWarning> Warnings(JsonElement root)
    {
        var warnings = new List<WorkerWarning>();
        if (!root.TryGetProperty("warnings", out var items) || items.ValueKind != JsonValueKind.Array) return warnings;
        foreach (var warning in items.EnumerateArray())
        {
            if (warning.ValueKind != JsonValueKind.Object) continue;
            var code = WorkerEventParser.String(warning, "code") ?? "";
            var message = WorkerEventParser.String(warning, "message") ?? "";
            warnings.Add(new WorkerWarning(code.Length <= 100 ? code : code[..100], message.Length <= 2000 ? message : message[..2000]));
        }
        return warnings;
    }

    private static double Fraction(JsonElement root, string name)
    {
        var value = Number(root, name);
        // Rounding of the worker may step a hair outside 0..1.
        if (value is < -0.001 or > 1.001) throw Invalid($"{name}: значение вне 0..1.");
        return Math.Clamp(value, 0, 1);
    }

    private static double Number(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && double.IsFinite(value.GetDouble())
            ? value.GetDouble()
            : throw Invalid($"{name}: ожидается конечное число.");

    private static int Integer(JsonElement root, string name, int minimum)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) && number >= minimum ? number : throw Invalid($"{name}: ожидается целое ≥ {minimum}.");

    private static long Long(JsonElement root, string name, long minimum)
        => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) && number >= minimum ? number : throw Invalid($"{name}: ожидается целое ≥ {minimum}.");

    private static long? OptionalLong(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.TryGetInt64(out var number) && number >= 0 ? number
            : throw Invalid($"{name}: ожидается целое ≥ 0 или null.") : null;

    private static WorkerProtocolException Invalid(string message) => new("protocol_event", message);

    /// <summary>A preview the worker wrote: a plain file name inside output/previews/.</summary>
    [GeneratedRegex(@"\Apreviews/[A-Za-z0-9_-]{1,64}\.jpg\z", RegexOptions.CultureInvariant)]
    private static partial Regex PreviewPattern();
}
