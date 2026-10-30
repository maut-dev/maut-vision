using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MautVision.Desktop.Mcp;

/// <summary>
/// A tool of the server. Class decides what the access level allows and gives the MCP annotations: Read is readOnlyHint, Destructive is
/// destructiveHint. InputSchema and OutputSchema are JSON Schema objects; the description is written for an AI agent: when to use the tool
/// and how its IDs relate to other tools.
/// </summary>
internal sealed record McpToolDefinition(string Name, string Title, string Description, McpToolClass Class, bool Idempotent, JsonElement InputSchema,
    JsonElement? OutputSchema, Func<McpToolCall, Task<McpToolResult>> Handler)
{
    internal static McpToolDefinition Create(string name, string title, McpToolClass toolClass, bool idempotent, string description, string inputSchema,
        string? outputSchema, Func<McpToolCall, Task<McpToolResult>> handler)
        => new(name, title, string.Join(' ', description.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)), toolClass,
            idempotent, Schema(inputSchema), outputSchema is null ? null : Schema(outputSchema), handler);

    private static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>An image of a tool result (MCP image content).</summary>
internal sealed record McpImage(byte[] Data, string MimeType);

/// <summary>What a tool returns: the structured result (an object, matching the output schema), a short English text and images.</summary>
internal sealed record McpToolResult(JsonObject Structured, string Text, IReadOnlyList<McpImage>? Images = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A DTO of the controllers as the structured result, camelCase like the UI receives it.</summary>
    internal static JsonObject Object(object value) => JsonSerializer.SerializeToNode(value, Json) as JsonObject
        ?? throw new InvalidOperationException("A tool result must serialize to a JSON object.");

    internal static JsonNode? Node(object? value) => value is null ? null : JsonSerializer.SerializeToNode(value, Json);
}

/// <summary>
/// One call of a tool: the arguments, who called (the client name from the MCP handshake), the session for uploads, progress reporting
/// when the client passed a progressToken, and the confirmation in the application window.
/// </summary>
internal sealed class McpToolCall(McpArguments arguments, string client, string sessionKey, Func<double, double?, string?, Task>? progress,
    Func<string, IReadOnlyDictionary<string, string>, Task> confirm, CancellationToken cancellationToken)
{
    internal McpArguments Arguments { get; } = arguments;

    internal string Client { get; } = client;

    internal string SessionKey { get; } = sessionKey;

    internal CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>The client asked for progress (a progressToken came with the call).</summary>
    internal bool WantsProgress => progress is not null;

    /// <summary>notifications/progress: progress so far, the total when known and a short English message.</summary>
    internal Task ReportProgressAsync(double value, double? total, string? message) => progress?.Invoke(value, total, message) ?? Task.CompletedTask;

    /// <summary>Asks the user in the window; denial and timeout throw mcp.confirmation_denied and mcp.confirmation_timeout.</summary>
    internal Task ConfirmAsync(string action, IReadOnlyDictionary<string, string> values) => confirm(action, values);
}

/// <summary>Typed access to the arguments of a call; a missing or wrong argument is mcp.invalid_arguments with the argument name.</summary>
internal sealed class McpArguments(IDictionary<string, JsonElement>? values)
{
    private readonly IDictionary<string, JsonElement> values = values ?? new Dictionary<string, JsonElement>();

    internal IReadOnlyDictionary<string, JsonElement> Raw => values.AsReadOnly();

    internal bool Has(string name) => values.TryGetValue(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    internal JsonElement? Element(string name) => Has(name) ? values[name] : null;

    internal string String(string name) => OptionalString(name) ?? throw McpToolException.InvalidArgument(name, "required");

    internal string? OptionalString(string name)
    {
        if (!Has(name)) return null;
        var value = values[name];
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw McpToolException.InvalidArgument(name, "must be a string");
    }

    internal Guid Guid(string name) => OptionalGuid(name) ?? throw McpToolException.InvalidArgument(name, "required");

    internal Guid? OptionalGuid(string name)
    {
        var text = OptionalString(name);
        if (text is null) return null;
        return System.Guid.TryParse(text, out var id) ? id : throw McpToolException.InvalidArgument(name, "must be an ID (GUID)");
    }

    internal int Int(string name, int minimum, int maximum, int? fallback = null)
    {
        if (!Has(name)) return fallback ?? throw McpToolException.InvalidArgument(name, "required");
        var value = values[name];
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || number != Math.Floor(number))
            throw McpToolException.InvalidArgument(name, "must be an integer");
        if (number < minimum || number > maximum)
            throw McpToolException.InvalidArgument(name, string.Create(CultureInfo.InvariantCulture, $"must be from {minimum} to {maximum}"));
        return (int)number;
    }

    internal double Double(string name, double minimum, double maximum, double? fallback = null)
    {
        if (!Has(name)) return fallback ?? throw McpToolException.InvalidArgument(name, "required");
        var value = values[name];
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw McpToolException.InvalidArgument(name, "must be a number");
        if (number < minimum || number > maximum)
            throw McpToolException.InvalidArgument(name, string.Create(CultureInfo.InvariantCulture, $"must be from {minimum} to {maximum}"));
        return number;
    }

    internal bool Bool(string name, bool fallback)
    {
        if (!Has(name)) return fallback;
        var value = values[name];
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw McpToolException.InvalidArgument(name, "must be true or false"),
        };
    }

    /// <summary>A nested object as arguments of its own (view targets, region).</summary>
    internal McpArguments? Object(string name)
    {
        if (!Has(name)) return null;
        var value = values[name];
        if (value.ValueKind != JsonValueKind.Object) throw McpToolException.InvalidArgument(name, "must be an object");
        return new McpArguments(value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone()));
    }

    internal IReadOnlyList<McpArguments> Objects(string name, int minimum, int maximum)
    {
        if (!Has(name)) throw McpToolException.InvalidArgument(name, "required");
        var value = values[name];
        if (value.ValueKind != JsonValueKind.Array) throw McpToolException.InvalidArgument(name, "must be an array");
        var items = value.EnumerateArray().ToArray();
        if (items.Length < minimum || items.Length > maximum)
            throw McpToolException.InvalidArgument(name, string.Create(CultureInfo.InvariantCulture, $"must have from {minimum} to {maximum} items"));
        return items.Select((item, index) => item.ValueKind == JsonValueKind.Object
            ? new McpArguments(item.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone()))
            : throw McpToolException.InvalidArgument($"{name}[{index}]", "must be an object")).ToArray();
    }

    /// <summary>Rejects names the schema does not know, so a typo is not silently ignored.</summary>
    internal void Only(params string[] names)
    {
        foreach (var name in values.Keys)
        {
            if (!names.Contains(name, StringComparer.Ordinal)) throw McpToolException.InvalidArgument(name, "is not a parameter of this tool");
        }
    }
}
