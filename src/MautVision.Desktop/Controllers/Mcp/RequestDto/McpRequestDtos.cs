namespace MautVision.Desktop.Controllers.Mcp.RequestDto;

public sealed record GetMcpStateRequestDto;

/// <summary>
/// A change of the MCP settings from the settings dialog; null fields keep their values. AccessLevel: read_only, read_write or full;
/// Port: 1024–65535.
/// </summary>
public sealed record UpdateMcpSettingsRequestDto(bool? Enabled = null, int? Port = null, string? AccessLevel = null, bool? RestrictFolders = null,
    bool? LaunchOnRequest = null);

public sealed record RegenerateMcpTokenRequestDto;

/// <summary>"Add folder…": the native folder dialog; the chosen folder joins the allowed folders.</summary>
public sealed record AddMcpAllowedFolderRequestDto;

public sealed record RemoveMcpAllowedFolderRequestDto(string Path);

/// <summary>Limit: 1–500, 100 by default; Before: the sequence of the last call of the previous page.</summary>
public sealed record ListMcpCallsRequestDto(int? Limit = null, long? Before = null);

public sealed record ListMcpConfirmationsRequestDto;

/// <summary>The user's answer to a confirmation: Allow — "Allow", otherwise "Deny".</summary>
public sealed record AnswerMcpConfirmationRequestDto(Guid Id, bool Allow);
