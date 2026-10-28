using System.Text.Json;
using System.Text.RegularExpressions;
using MautVision.Core.Interfaces;

namespace MautVision.Core.Training;

/// <summary>
/// Supported training parameters: the camelCase keys of the worker protocol (python/worker/protocol/request.py TrainParams), their
/// ranges and the defaults tuned in the worker — AdamW, cls 1.0, warmupBiasLr 0, no mosaic and flips, soft hsv, scale 0.1,
/// translate 0.05, momentum 0.9. lr0, warmupEpochs, patience and workers default to null: the worker computes them (lr0 = 0.001 ·
/// batch / 32, warm-up by epochs and batch, patience = max(10, min(40, epochs)), workers 0 on Windows). The architecture, imgsz
/// and ONNX export are set by the application; profile and compile are not supported by the pinned Windows runtime.
/// ui/src/api/projects/training/training-parameters.ts holds the same keys with labels, hints and groups.
/// </summary>
internal static partial class TrainingParameterRules
{
    internal const string InvalidCode = "training.parameter_invalid";

    private static readonly string[] Optimizers = ["auto", "SGD", "MuSGD", "Adam", "Adamax", "AdamW", "NAdam", "RAdam", "RMSProp"];

    private static readonly (string Key, string Default, Func<JsonElement, bool> Valid)[] Rules =
    [
        // Duration and mode.
        ("epochs", "100", value => Integer(value, 1, 100_000)),
        ("batch", "16", Batch),
        ("device", "\"0\"", value => value.ValueKind == JsonValueKind.String && DevicePattern().IsMatch(value.GetString()!)),
        ("time", "null", value => Null(value) || Number(value, 0, double.MaxValue, exclusiveMinimum: true)),
        ("patience", "null", value => Null(value) || Integer(value, 0, int.MaxValue)),
        ("amp", "true", value => value.ValueKind is JsonValueKind.True or JsonValueKind.False || Choice(value, "fp16", "bf16", "fp32")),
        ("pretrained", "true", Boolean),
        ("clsRemap", "true", Boolean),
        ("freeze", "null", Freeze),
        ("channelsLast", "null", value => Null(value) || Boolean(value)),
        ("verbose", "true", Boolean),
        // Optimizer and learning rate.
        ("optimizer", "\"AdamW\"", value => Choice(value, Optimizers)),
        ("lr0", "null", value => Null(value) || Number(value, 0, double.MaxValue, exclusiveMinimum: true)),
        ("lrf", "0.01", value => Number(value, 0, 1)),
        ("momentum", "0.9", value => Number(value, 0, 1)),
        ("weightDecay", "0.0005", value => Number(value, 0, double.MaxValue)),
        ("warmupEpochs", "null", value => Null(value) || Number(value, 0, double.MaxValue)),
        ("warmupMomentum", "0.8", value => Number(value, 0, 1)),
        ("warmupBiasLr", "0", value => Number(value, 0, double.MaxValue)),
        ("cosLr", "false", Boolean),
        // Loss.
        ("box", "7.5", value => Number(value, 0, double.MaxValue)),
        ("cls", "1", value => Number(value, 0, double.MaxValue)),
        ("clsPw", "0", value => Number(value, 0, double.MaxValue)),
        ("dfl", "1.5", value => Number(value, 0, double.MaxValue)),
        ("nbs", "64", value => Integer(value, 1, int.MaxValue)),
        // Data and loading.
        ("cache", "false", value => value.ValueKind is JsonValueKind.True or JsonValueKind.False || Choice(value, "ram", "disk")),
        ("workers", "null", value => Null(value) || Integer(value, 0, 256)),
        ("singleCls", "false", Boolean),
        ("rect", "false", Boolean),
        ("fraction", "1", Fraction),
        ("classes", "null", Classes),
        // Reproducibility.
        ("seed", "0", value => Integer(value, 0, int.MaxValue)),
        ("deterministic", "true", Boolean),
        // Augmentations.
        ("hsvH", "0.01", value => Number(value, 0, 1)),
        ("hsvS", "0.4", value => Number(value, 0, 1)),
        ("hsvV", "0.5", value => Number(value, 0, 1)),
        ("degrees", "0", value => Number(value, 0, 180)),
        ("translate", "0.05", value => Number(value, 0, 1)),
        ("scale", "0.1", Scale),
        ("shear", "0", value => Number(value, 0, 180)),
        ("perspective", "0", value => Number(value, 0, 1)),
        ("flipud", "0", value => Number(value, 0, 1)),
        ("fliplr", "0", value => Number(value, 0, 1)),
        ("bgr", "0", value => Number(value, 0, 1)),
        ("mosaic", "0", value => Number(value, 0, 1)),
        ("closeMosaic", "10", value => Integer(value, 0, int.MaxValue)),
        ("mixup", "0", value => Number(value, 0, 1)),
        ("cutmix", "0", value => Number(value, 0, 1)),
        ("multiScale", "0", value => Number(value, 0, double.MaxValue)),
        // Validation.
        ("val", "true", Boolean),
        ("split", "\"val\"", value => Choice(value, "train", "val", "test")),
        ("conf", "null", value => Null(value) || Number(value, 0, 1)),
        ("iou", "0.7", value => Number(value, 0, 1)),
        ("maxDet", "300", value => Integer(value, 1, int.MaxValue)),
        ("saveJson", "false", Boolean),
        // Saving and reports.
        ("save", "true", Boolean),
        ("savePeriod", "-1", value => Integer(value, -1, int.MaxValue)),
        ("plots", "true", Boolean),
    ];

    private static readonly Dictionary<string, (JsonElement Default, Func<JsonElement, bool> Valid)> ByKey = Rules.ToDictionary(
        rule => rule.Key, rule => (Parse(rule.Default), rule.Valid), StringComparer.Ordinal);

    /// <summary>Supported keys in registry order.</summary>
    internal static IReadOnlyList<string> Keys { get; } = Rules.Select(rule => rule.Key).ToArray();

    /// <summary>The tuned default of a key.</summary>
    internal static JsonElement Default(string key) => ByKey[key].Default;

    /// <summary>Checks the given keys only (a preset): an unknown key or a value out of range is <c>training.parameter_invalid</c> with the field.</summary>
    internal static Dictionary<string, JsonElement> Validate(IReadOnlyDictionary<string, JsonElement>? values)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (values is null) return result;
        foreach (var (key, value) in values)
        {
            if (!ByKey.TryGetValue(key, out var rule) || !rule.Valid(value)) throw Invalid(key);
            result[key] = value.Clone();
        }
        return result;
    }

    /// <summary>All supported keys: the given values over the tuned defaults, in registry order.</summary>
    internal static Dictionary<string, JsonElement> Complete(IReadOnlyDictionary<string, JsonElement>? values)
    {
        var given = Validate(values);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in Keys) result[key] = given.TryGetValue(key, out var value) ? value : ByKey[key].Default;
        return result;
    }

    internal static CoreException Invalid(string field) => new(InvalidCode, $"Training parameter {field} is not supported or out of range.")
    {
        Details = new Dictionary<string, string> { ["field"] = field },
    };

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool Null(JsonElement value) => value.ValueKind == JsonValueKind.Null;

    private static bool Boolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool Choice(JsonElement value, params string[] choices) => value.ValueKind == JsonValueKind.String && choices.Contains(value.GetString(), StringComparer.Ordinal);

    private static bool Number(JsonElement value, double minimum, double maximum, bool exclusiveMinimum = false)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            && (exclusiveMinimum ? number > minimum : number >= minimum) && number <= maximum;

    private static bool Integer(JsonElement value, long minimum, long maximum)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && Math.Floor(number) == number
            && number >= minimum && number <= maximum;

    /// <summary>-1 (AutoBatch), a share of GPU memory (0, 1) or a positive whole number.</summary>
    private static bool Batch(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            && (number == -1 || number is > 0 and < 1 || (number >= 1 && Math.Floor(number) == number && number <= 100_000));

    /// <summary>A share (0, 1] or a whole number of images, or one to three such values (the third may be 0).</summary>
    private static bool Fraction(JsonElement value)
    {
        static bool Item(JsonElement item, int index) => item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number) && double.IsFinite(number)
            && ((number == 0 && index == 2) || number is > 0 and <= 1 || (number > 1 && Math.Floor(number) == number));
        if (value.ValueKind == JsonValueKind.Number) return Item(value, 0);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 3) return false;
        return value.EnumerateArray().Select((item, index) => Item(item, index)).All(valid => valid);
    }

    /// <summary>null, a number of first layers, or layer indexes and module names like 23.cv2.</summary>
    private static bool Freeze(JsonElement value)
    {
        if (Null(value) || Integer(value, 0, 10_000)) return true;
        return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => Integer(item, 0, 10_000)
            || (item.ValueKind == JsonValueKind.String && ModulePattern().IsMatch(item.GetString()!)));
    }

    /// <summary>null (all classes) or a non-empty list of distinct output indexes; their presence in the dataset is checked by the run.</summary>
    private static bool Classes(JsonElement value)
    {
        if (Null(value)) return true;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0 || !value.EnumerateArray().All(item => Integer(item, 0, 100_000))) return false;
        var items = value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
        return items.Distinct().Count() == items.Length;
    }

    /// <summary>A non-negative gain, or a range [min, max] with 0 &lt; min ≤ max.</summary>
    private static bool Scale(JsonElement value)
    {
        if (Number(value, 0, double.MaxValue)) return true;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2) return false;
        var items = value.EnumerateArray().ToArray();
        return Number(items[0], 0, double.MaxValue, exclusiveMinimum: true) && Number(items[1], 0, double.MaxValue, exclusiveMinimum: true)
            && items[0].GetDouble() <= items[1].GetDouble();
    }

    [GeneratedRegex(@"\A(cpu|-1|[0-9]{1,3})\z", RegexOptions.CultureInvariant)]
    private static partial Regex DevicePattern();

    [GeneratedRegex(@"\A[0-9]+(?:\.[A-Za-z0-9_]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ModulePattern();
}
