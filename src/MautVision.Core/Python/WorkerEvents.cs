using System.Text.Json;

namespace MautVision.Core.Python;

/// <summary>A protocol violation of the worker: an event or result.json that the host cannot trust. Code without the area prefix.</summary>
internal sealed class WorkerProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>One stdout event of the worker (python/worker/runtime/events.py); Sequence grows strictly within a run.</summary>
internal abstract record WorkerEvent(long Sequence);

internal sealed record WorkerStageEvent(long Sequence, string Stage) : WorkerEvent(Sequence);

/// <summary>Progress inside an epoch, at most once a second.</summary>
internal sealed record WorkerBatchEvent(long Sequence, int Epoch, int Batch, int Batches) : WorkerEvent(Sequence);

/// <summary>A finished epoch: train losses, validation metrics (empty without validation), learning rate and fitness.</summary>
internal sealed record WorkerEpochEvent(long Sequence, WorkerEpoch Epoch, int Epochs) : WorkerEvent(Sequence);

/// <summary>Applied parameters: capture «requested» before training, «trainer» after the trainer initialised them.</summary>
internal sealed record WorkerEffectiveParamsEvent(long Sequence, JsonElement Parameters, string Capture) : WorkerEvent(Sequence);

/// <summary>A console line of the training, up to 2000 characters, machine paths removed by the worker.</summary>
internal sealed record WorkerLogEvent(long Sequence, string Message) : WorkerEvent(Sequence);

internal sealed record WorkerWarningEvent(long Sequence, string Code, string Message) : WorkerEvent(Sequence);

/// <summary>Versions of Python, torch, ultralytics and CUDA and the device name, sent before training.</summary>
internal sealed record WorkerEnvironmentEvent(long Sequence, JsonElement Environment) : WorkerEvent(Sequence);

/// <summary>Events of other operations (frame, progress of predict and live): passed on unparsed for their consumers.</summary>
internal sealed record WorkerOtherEvent(long Sequence, string Type, JsonElement Data) : WorkerEvent(Sequence);

/// <summary>An epoch of the history: train losses, metrics and validation losses, lr and fitness (null without validation).</summary>
internal sealed record WorkerEpoch(int Epoch, IReadOnlyDictionary<string, double> Losses, IReadOnlyDictionary<string, double> Metrics, double? Lr, double? Fitness);

/// <summary>
/// Parses stdout lines of the worker. The sequence must grow strictly; «started» must name this operation and protocol 1;
/// «finished» and unknown types are not passed on (result.json decides the outcome). A line longer than its type allows is a
/// violation: a regular event fits <see cref="RegularLineLimit"/>, an effectiveParams snapshot (the full class map) and a frame
/// with a preview are larger.
/// </summary>
internal static class WorkerEventParser
{
    internal const int RegularLineLimit = 64 * 1024;
    internal const int FrameLineLimit = 2 * 1024 * 1024;
    internal const int EffectiveParamsLineLimit = 16 * 1024 * 1024;

    /// <summary>The longest line of any type; the reader stops buffering past it.</summary>
    internal const int MaxLineLength = EffectiveParamsLineLimit;

    internal static (long Sequence, WorkerEvent? Event) Parse(ReadOnlyMemory<byte> line, long previousSequence, string operationId, int protocolVersion)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            throw new WorkerProtocolException("protocol_event", "Строка stdout работника не является JSON.");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new WorkerProtocolException("protocol_event", "Событие работника должно быть JSON-объектом.");
            if (!root.TryGetProperty("sequence", out var sequenceValue) || !sequenceValue.TryGetInt64(out var sequence) || sequence <= previousSequence)
            {
                throw new WorkerProtocolException("protocol_sequence", $"Нарушен порядок событий работника: sequence после {previousSequence}.");
            }
            var type = root.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String ? typeValue.GetString()! : "";
            var limit = type switch
            {
                "effectiveParams" => EffectiveParamsLineLimit,
                "frame" => FrameLineLimit,
                _ => RegularLineLimit,
            };
            if (line.Length > limit) throw new WorkerProtocolException("protocol_event", $"Строка события {type} работника превышает {limit / 1024} КиБ.");
            switch (type)
            {
                case "started":
                    if (!root.TryGetProperty("protocolVersion", out var version) || !version.TryGetInt32(out var number) || number != protocolVersion)
                        throw new WorkerProtocolException("protocol_version", $"Работник говорит на другой версии протокола, ожидается {protocolVersion}.");
                    if (String(root, "operationId") != operationId)
                        throw new WorkerProtocolException("result_operation_mismatch", "Работник исполняет другую операцию.");
                    return (sequence, null);
                case "stage":
                    return (sequence, new WorkerStageEvent(sequence, Truncate(String(root, "stage") is { Length: > 0 } stage ? stage : throw Invalid(type), 100)));
                case "batch":
                {
                    var epoch = Integer(root, "epoch", 1, type);
                    var batch = Integer(root, "batch", 0, type);
                    var batches = Integer(root, "batches", 0, type);
                    return (sequence, new WorkerBatchEvent(sequence, epoch, batch, batches));
                }
                case "epoch":
                {
                    var epoch = Integer(root, "epoch", 1, type);
                    var epochs = Integer(root, "epochs", 1, type);
                    if (epoch > epochs) throw Invalid(type);
                    return (sequence, new WorkerEpochEvent(sequence, ReadEpoch(root, epoch, "protocol_event"), epochs));
                }
                case "effectiveParams":
                {
                    if (!root.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object
                        || String(parameters, "architecture") is null || String(parameters, "device") is null)
                        throw Invalid(type);
                    var capture = String(root, "capture");
                    if (capture is not ("requested" or "trainer")) throw Invalid(type);
                    return (sequence, new WorkerEffectiveParamsEvent(sequence, parameters.Clone(), capture));
                }
                case "log":
                {
                    var message = String(root, "message");
                    if (string.IsNullOrEmpty(message) || message.Length > 2000) throw Invalid(type);
                    return (sequence, new WorkerLogEvent(sequence, message));
                }
                case "warning":
                {
                    var code = String(root, "code");
                    var message = String(root, "message");
                    if (code is null || message is null) throw Invalid(type);
                    return (sequence, new WorkerWarningEvent(sequence, Truncate(code, 100), Truncate(message, 2000)));
                }
                case "environment":
                    if (!root.TryGetProperty("environment", out var environment) || environment.ValueKind != JsonValueKind.Object) throw Invalid(type);
                    return (sequence, new WorkerEnvironmentEvent(sequence, environment.Clone()));
                case "frame" or "progress":
                    return (sequence, new WorkerOtherEvent(sequence, type, root.Clone()));
                default:
                    return (sequence, null);
            }
        }
    }

    /// <summary>An epoch record of an event or of history.jsonl: finite numbers only, null values skipped.</summary>
    internal static WorkerEpoch ReadEpoch(JsonElement root, int epoch, string code) => new(
        epoch,
        Numbers(root, "losses", code),
        Numbers(root, "metrics", code),
        OptionalNumber(root, "lr", code),
        OptionalNumber(root, "fitness", code));

    internal static IReadOnlyDictionary<string, double> Numbers(JsonElement root, string name, string code)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return result;
        if (value.ValueKind != JsonValueKind.Object) throw new WorkerProtocolException(code, $"{name}: ожидается объект чисел.");
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null) continue;
            if (property.Name.Length > 120 || property.Value.ValueKind != JsonValueKind.Number || !double.IsFinite(property.Value.GetDouble()))
                throw new WorkerProtocolException(code, $"{name}: значение {property.Name} не является конечным числом.");
            result[property.Name] = property.Value.GetDouble();
        }
        return result;
    }

    private static double? OptionalNumber(JsonElement root, string name, string code)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !double.IsFinite(value.GetDouble()))
            throw new WorkerProtocolException(code, $"{name}: ожидается конечное число или null.");
        return value.GetDouble();
    }

    internal static string? String(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Integer(JsonElement root, string name, int minimum, string type)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) && number >= minimum ? number : throw Invalid(type);

    private static WorkerProtocolException Invalid(string type) => new("protocol_event", $"Событие {type} работника с неверными полями.");

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}
