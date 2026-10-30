using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop.Mcp;

/// <summary>
/// One tool call: Sequence grows within the run; Arguments — the JSON of the arguments with base64 contents replaced by their size;
/// Error — the code of a failed call; Result — a short summary of a successful one.
/// </summary>
internal sealed record McpCallEntry(long Sequence, DateTimeOffset Time, string Client, string Tool, string Arguments, bool Succeeded, string? Error, string? Result,
    long DurationMs);

/// <summary>
/// The last 500 tool calls of this run, shown in the MCP settings; every call also goes to the application log. Arguments keep paths and
/// IDs, never file contents: long strings (base64) are replaced by their length.
/// </summary>
internal sealed class McpCallLog(ILogger logger)
{
    internal const int Capacity = 500;
    private const int MaxArgumentsLength = 2000;
    private const int MaxStringLength = 512;

    private readonly Lock gate = new();
    private readonly LinkedList<McpCallEntry> entries = new();
    private long sequence;

    /// <summary>Raised after a call was recorded; may come from any thread.</summary>
    internal event Action<McpCallEntry>? Added;

    internal McpCallEntry Add(DateTimeOffset time, string client, string tool, IReadOnlyDictionary<string, JsonElement>? arguments, bool succeeded, string? error,
        string? result, long durationMs)
    {
        var text = Describe(arguments);
        McpCallEntry entry;
        lock (gate)
        {
            entry = new McpCallEntry(++sequence, time, client, tool, text, succeeded, error, result, durationMs);
            entries.AddLast(entry);
            while (entries.Count > Capacity) entries.RemoveFirst();
        }
        if (succeeded) logger.LogInformation("MCP call {Tool} by {Client} succeeded in {DurationMs} ms; arguments {Arguments}.", tool, client, durationMs, text);
        else logger.LogWarning("MCP call {Tool} by {Client} failed with {Code} in {DurationMs} ms; arguments {Arguments}.", tool, client, error, durationMs, text);
        Added?.Invoke(entry);
        return entry;
    }

    /// <summary>Newest first; before — the Sequence of the last entry of the previous page.</summary>
    internal IReadOnlyList<McpCallEntry> List(int limit, long? before)
    {
        lock (gate)
        {
            return entries.Reverse().Where(entry => before is null || entry.Sequence < before).Take(Math.Clamp(limit, 1, Capacity)).ToArray();
        }
    }

    internal static string Describe(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return "{}";
        using var stream = new MemoryStream();
        // Paths and names stay readable (Cyrillic, <, >): the text goes to the log and the dialog, never into HTML or a script.
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in arguments)
            {
                writer.WritePropertyName(name);
                Write(writer, value, 0, IsContent(name));
            }
            writer.WriteEndObject();
        }
        var text = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        return text.Length <= MaxArgumentsLength ? text : text[..MaxArgumentsLength] + "…";
    }

    /// <summary>File contents sent inline (upload_file) are never logged, however short.</summary>
    private static bool IsContent(string name) => name.Equals("base64", StringComparison.OrdinalIgnoreCase);

    private static void Write(Utf8JsonWriter writer, JsonElement value, int depth, bool content = false)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString() ?? "";
                writer.WriteStringValue(!content && text.Length <= MaxStringLength ? text : $"<{text.Length} characters>");
                break;
            case JsonValueKind.Object when depth < 6:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, depth + 1, IsContent(property.Name));
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array when depth < 6:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(writer, item, depth + 1);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Object:
            case JsonValueKind.Array:
                writer.WriteStringValue("…");
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
