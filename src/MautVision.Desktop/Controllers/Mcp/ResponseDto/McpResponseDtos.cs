namespace MautVision.Desktop.Controllers.Mcp.ResponseDto;

/// <summary>
/// The MCP settings and the state of the server (also the event mcp.stateChanged). Status: stopped, starting, running, port_busy, failed;
/// StatusCode — the error code of port_busy and failed. Endpoint — the URL clients connect to; ProxyPath — the stdio proxy for clients that
/// speak only stdio; ProxyAvailable — it is shipped with this build. Token is null until the server is turned on the first time.
/// </summary>
public sealed record McpStateResponseDto(bool Enabled, int Port, string AccessLevel, string? Token, bool RestrictFolders, string[] AllowedFolders,
    bool LaunchOnRequest, string Status, string? StatusCode, string Endpoint, string ProxyPath, bool ProxyAvailable);

/// <summary>
/// A tool call of the log (also the event mcp.callAdded): Client — the name the AI client gave; Arguments — JSON without file contents;
/// Error — the code of a failed call; Result — a short English summary.
/// </summary>
public sealed record McpCallResponseDto(long Sequence, DateTimeOffset Time, string Client, string Tool, string Arguments, bool Succeeded, string? Error,
    string? Result, long DurationMs);

/// <summary>
/// A question to the user before a tool acts (also the event mcp.confirmationRequested): Action — a key of mcp:confirm.actions, Values — its
/// placeholders; ExpiresAt — when the call gives up waiting.
/// </summary>
public sealed record McpConfirmationResponseDto(Guid Id, string Client, string Tool, string Action, IReadOnlyDictionary<string, string> Values,
    DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt);

/// <summary>Answered — the question was still open and took the answer.</summary>
public sealed record McpConfirmationAnswerResponseDto(bool Answered);

/// <summary>The event mcp.confirmationClosed: the question was answered, timed out or the server stopped.</summary>
public sealed record McpConfirmationClosedEventDto(Guid Id);

/// <summary>The event mcp.openProject: an AI client asked to show the project in the window.</summary>
public sealed record McpOpenProjectEventDto(string ProjectId);
