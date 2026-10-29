using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using MautVision.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop.Mcp;

/// <summary>An error a tool reports to the AI client: a stable code and safe details, like a Core error.</summary>
internal sealed class McpToolException(string code, string message, IReadOnlyDictionary<string, string>? details = null) : Exception(message)
{
    public string Code { get; } = code;

    public IReadOnlyDictionary<string, string>? Details { get; } = details;

    internal static McpToolException InvalidArgument(string argument, string reason)
        => new("mcp.invalid_arguments", $"Argument {argument}: {reason}", new Dictionary<string, string> { ["argument"] = argument, ["reason"] = reason });
}

/// <summary>A tool error as the client receives it: the code, the English text of the UI catalog and the details.</summary>
internal sealed record McpError(string Code, string Message, IReadOnlyDictionary<string, string>? Details);

/// <summary>
/// Turns exceptions into tool errors. The message is the English text of the code from the UI catalog <c>ui/src/locales/en/errors.json</c>,
/// embedded into the host at build time, so the AI reads the same words a user sees in the English interface. An unexpected exception
/// becomes <c>mcp.internal_error</c>; its stack goes only to the log.
/// </summary>
internal static partial class McpErrors
{
    private const string Generic = "generic";
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Texts = new(LoadTexts);

    internal static McpError From(Exception error, ILogger logger, string tool)
    {
        switch (error)
        {
            case McpToolException mcp:
                return Create(mcp.Code, mcp.Details);
            case CoreException core:
                return Create(core.Code, core.Details);
            case OperationCanceledException:
                return Create("mcp.cancelled", null);
            case JsonException:
            case FormatException:
                return Create("mcp.invalid_arguments", null);
            case ArgumentException argument:
                // Core rejects a malformed value (an ID that is not a GUID) before it reaches storage: the client sent it.
                logger.LogWarning("MCP tool {Tool} got an invalid argument: {Reason}", tool, argument.Message);
                return Create("mcp.invalid_arguments", argument.ParamName is { } name ? new Dictionary<string, string> { ["argument"] = name } : null);
            case FileNotFoundException or DirectoryNotFoundException:
                return Create("mcp.path_not_found", null);
            case UnauthorizedAccessException:
                return Create("mcp.path_access_denied", null);
            default:
                logger.LogError(error, "MCP tool {Tool} failed unexpectedly.", tool);
                return Create("mcp.internal_error", null);
        }
    }

    internal static McpError Create(string code, IReadOnlyDictionary<string, string>? details) => new(code, Text(code, details), details);

    /// <summary>The English text of a code with the details substituted; a code without a text gets the generic text of the catalog.</summary>
    internal static string Text(string code, IReadOnlyDictionary<string, string>? details)
    {
        var texts = Texts.Value;
        var template = Plural(texts, code, details) ?? (texts.TryGetValue(code, out var text) ? text : null)
            ?? (texts.TryGetValue(Generic, out var generic) ? generic : "The operation failed.");
        return Placeholder().Replace(template, match =>
        {
            if (details is null || !details.TryGetValue(match.Groups[1].Value, out var value)) return match.Value;
            // A detail named section is a key of errors.sections, as in the UI.
            return match.Groups[1].Value == "section" && texts.TryGetValue("sections." + value, out var section) ? section : value;
        });
    }

    /// <summary>English plurals of i18next: a detail count selects <c>_one</c> or <c>_other</c>.</summary>
    private static string? Plural(IReadOnlyDictionary<string, string> texts, string code, IReadOnlyDictionary<string, string>? details)
    {
        if (details is null || !details.TryGetValue("count", out var count)) return null;
        var form = double.TryParse(count, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == 1 ? "_one" : "_other";
        return texts.TryGetValue(code + form, out var text) ? text : null;
    }

    /// <summary>The flattened catalog: nested keys joined by dots, the namespace prefix dropped (errors.projects.not_found → projects.not_found).</summary>
    private static IReadOnlyDictionary<string, string> LoadTexts()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = typeof(McpErrors).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return result;
        using var document = JsonDocument.Parse(stream);
        Flatten(document.RootElement, "", result);
        return result;
    }

    internal const string ResourceName = "MautVision.Desktop.Mcp.errors.en.json";

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> result)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
            if (property.Value.ValueKind == JsonValueKind.Object) Flatten(property.Value, key, result);
            else if (property.Value.ValueKind == JsonValueKind.String) result[key] = property.Value.GetString()!;
        }
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}")]
    private static partial Regex Placeholder();
}
