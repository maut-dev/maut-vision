using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop.Mcp;

/// <summary>The outcome of a call: a result or an error the client receives with isError.</summary>
internal sealed record McpCallOutcome(McpToolResult? Result, McpError? Error);

/// <summary>
/// The tools of the server and the rules every call passes: the tool exists, the access level allows its class, the arguments are read by
/// the tool itself, errors become codes with English texts, and each call goes to the call log. Confirmations are asked by the tools that
/// need them (deleting a project), through <see cref="McpToolCall.ConfirmAsync"/>.
/// </summary>
internal sealed class McpToolbox(IReadOnlyList<McpToolDefinition> tools, Func<McpSettings> settings, McpConfirmations confirmations, McpCallLog log, ILogger logger)
{
    private readonly Dictionary<string, McpToolDefinition> byName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    internal IReadOnlyList<McpToolDefinition> Tools { get; } = tools;

    internal async Task<McpCallOutcome> CallAsync(string name, IDictionary<string, JsonElement>? arguments, string client, string sessionKey,
        Func<double, double?, string?, Task>? progress, CancellationToken cancellationToken)
    {
        // Texts for the AI are English with invariant numbers whatever the language of Windows; the change stays in this call's flow.
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var started = Stopwatch.GetTimestamp();
        var time = DateTimeOffset.UtcNow;
        var call = new McpArguments(arguments);
        McpCallOutcome outcome;
        try
        {
            if (!byName.TryGetValue(name, out var tool))
                throw new McpToolException("mcp.tool_unknown", "There is no such tool.", new Dictionary<string, string> { ["tool"] = name });
            var level = settings().AccessLevel;
            if (!McpAccessLevels.Allows(level, tool.Class))
            {
                throw new McpToolException("mcp.access_denied", "The access level of the MCP settings does not allow this tool.",
                    new Dictionary<string, string> { ["level"] = level, ["required"] = tool.Class == McpToolClass.Write ? McpAccessLevels.ReadWrite : McpAccessLevels.Full });
            }
            var context = new McpToolCall(call, client, sessionKey, progress,
                (action, values) => confirmations.AskAsync(client, name, action, values, cancellationToken), cancellationToken);
            outcome = new McpCallOutcome(await tool.Handler(context), null);
        }
        catch (Exception error)
        {
            outcome = new McpCallOutcome(null, McpErrors.From(error, logger, name));
        }
        var duration = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        log.Add(time, client, name, call.Raw, outcome.Error is null, outcome.Error?.Code, outcome.Result?.Text is { } text ? Shorten(text) : null, duration);
        return outcome;
    }

    private static string Shorten(string text) => text.Length <= 300 ? text : text[..300] + "…";
}
