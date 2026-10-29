using System.Text.Json;
using System.Text.Json.Serialization;
using MautVision.Core.Interfaces;

namespace MautVision.Core.Jobs;

/// <summary>
/// How a <see cref="CodedText"/> is kept in an existing text column (jobs.progress_text, jobs.label, training_log.message,
/// error_message and notes of training): a line with a code is the JSON object <c>{"code":"progress.epoch","args":{"epoch":"3"},"text":…}</c>,
/// a text without a code is stored as is. Rows written by version 1.0.0 hold only text and read back as text, so no schema step is needed.
/// </summary>
internal static class CodedTextJson
{
    private const string Marker = "{\"code\":";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Code is always written, so every stored object starts with <see cref="Marker"/>.</summary>
    private sealed record Stored([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Code, Dictionary<string, string>? Args, string? Text);

    internal static string Serialize(CodedText value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // A plain text that happens to look like a stored object is stored as an object too, so it reads back unchanged.
        if (value.Code is null && value.Text is { } text && !text.StartsWith(Marker, StringComparison.Ordinal)) return text;
        return JsonSerializer.Serialize(new Stored(value.Code, value.Args?.ToDictionary(StringComparer.Ordinal), value.Text), Json);
    }

    internal static string? SerializeOrNull(CodedText? value) => value is null ? null : Serialize(value);

    /// <summary>A stored value; text of an older version (or text that is not a valid object) comes back as a text without a code.</summary>
    internal static CodedText? Parse(string? stored)
    {
        if (stored is null) return null;
        if (!stored.StartsWith(Marker, StringComparison.Ordinal)) return CodedText.Plain(stored);
        try
        {
            var value = JsonSerializer.Deserialize<Stored>(stored, Json);
            if (value is not null && (value.Code is not null || value.Text is not null)) return new CodedText(value.Code, value.Args, value.Text);
        }
        catch (JsonException)
        {
        }
        return CodedText.Plain(stored);
    }
}
