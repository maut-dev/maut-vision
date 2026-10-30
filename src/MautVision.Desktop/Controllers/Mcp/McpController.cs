using System.IO;
using MautVision.Desktop.Controllers.Mcp.RequestDto;
using MautVision.Desktop.Controllers.Mcp.ResponseDto;
using MautVision.Desktop.Host;
using MautVision.Desktop.Mcp;

namespace MautVision.Desktop.Controllers.Mcp;

/// <summary>
/// The settings dialog "AI assistants (MCP)": settings, state, the call log and confirmations. Only the page of the window reaches these
/// methods; no MCP tool changes the MCP settings.
/// </summary>
internal sealed class McpController(McpService service, NativeDialogs dialogs)
{
    public Task<McpStateResponseDto> GetStateAsync(GetMcpStateRequestDto request, CancellationToken cancellationToken)
        => Task.FromResult(Map(service));

    public async Task<McpStateResponseDto> UpdateAsync(UpdateMcpSettingsRequestDto request, CancellationToken cancellationToken)
    {
        if (request.Port is { } port && !McpSettings.IsPort(port)) throw new ArgumentException("The port must be from 1024 to 65535.", nameof(request));
        if (request.AccessLevel is { } level && !McpAccessLevels.IsLevel(level)) throw new ArgumentException("Unknown access level.", nameof(request));
        await service.UpdateAsync(current => current with
        {
            Enabled = request.Enabled ?? current.Enabled,
            Port = request.Port ?? current.Port,
            AccessLevel = request.AccessLevel ?? current.AccessLevel,
            RestrictFolders = request.RestrictFolders ?? current.RestrictFolders,
            LaunchOnRequest = request.LaunchOnRequest ?? current.LaunchOnRequest,
        }, cancellationToken);
        return Map(service);
    }

    /// <summary>A new token; connected clients must use the new configuration.</summary>
    public async Task<McpStateResponseDto> RegenerateTokenAsync(RegenerateMcpTokenRequestDto request, CancellationToken cancellationToken)
    {
        await service.UpdateAsync(current => current with { Token = McpSettings.NewToken() }, cancellationToken);
        return Map(service);
    }

    public async Task<McpStateResponseDto> AddAllowedFolderAsync(AddMcpAllowedFolderRequestDto request, CancellationToken cancellationToken)
    {
        var folder = await dialogs.PickFolder(Strings.DialogMcpFolder, cancellationToken);
        if (string.IsNullOrEmpty(folder)) return Map(service);
        if (service.Settings.AllowedFolders.Count >= McpSettings.MaxAllowedFolders)
        {
            throw new MautVision.Core.Interfaces.CoreException("mcp.folders_limit", "Too many allowed folders.")
            {
                Details = new Dictionary<string, string> { ["limit"] = McpSettings.MaxAllowedFolders.ToString(global::System.Globalization.CultureInfo.InvariantCulture) },
            };
        }
        var full = Path.GetFullPath(folder);
        await service.UpdateAsync(current => current with { AllowedFolders = [.. current.AllowedFolders, full] }, cancellationToken);
        return Map(service);
    }

    public async Task<McpStateResponseDto> RemoveAllowedFolderAsync(RemoveMcpAllowedFolderRequestDto request, CancellationToken cancellationToken)
    {
        await service.UpdateAsync(current => current with
        {
            AllowedFolders = current.AllowedFolders.Where(folder => !folder.Equals(request.Path, StringComparison.OrdinalIgnoreCase)).ToArray(),
        }, cancellationToken);
        return Map(service);
    }

    public Task<McpCallResponseDto[]> ListCallsAsync(ListMcpCallsRequestDto request, CancellationToken cancellationToken)
        => Task.FromResult(service.CallLog.List(request.Limit ?? 100, request.Before).Select(Map).ToArray());

    public Task<McpConfirmationResponseDto[]> ListConfirmationsAsync(ListMcpConfirmationsRequestDto request, CancellationToken cancellationToken)
        => Task.FromResult(service.Confirmations.Pending.Select(Map).ToArray());

    public Task<McpConfirmationAnswerResponseDto> AnswerConfirmationAsync(AnswerMcpConfirmationRequestDto request, CancellationToken cancellationToken)
        => Task.FromResult(new McpConfirmationAnswerResponseDto(service.Confirmations.Answer(request.Id, request.Allow)));

    internal static McpStateResponseDto Map(McpService service)
    {
        var settings = service.Settings;
        var status = service.Status;
        return new McpStateResponseDto(settings.Enabled, settings.Port, settings.AccessLevel, settings.Token, settings.RestrictFolders, [.. settings.AllowedFolders],
            settings.LaunchOnRequest, status.State, status.ErrorCode, $"http://127.0.0.1:{settings.Port}{McpServerPath}", McpService.ProxyPath,
            File.Exists(McpService.ProxyPath));
    }

    /// <summary>The path of the endpoint, kept here so the settings never load the server types.</summary>
    internal const string McpServerPath = "/mcp";

    internal static McpCallResponseDto Map(McpCallEntry entry)
        => new(entry.Sequence, entry.Time, entry.Client, entry.Tool, entry.Arguments, entry.Succeeded, entry.Error, entry.Result, entry.DurationMs);

    internal static McpConfirmationResponseDto Map(McpConfirmation confirmation)
        => new(confirmation.Id, confirmation.Client, confirmation.Tool, confirmation.Action, confirmation.Values, confirmation.RequestedAt, confirmation.ExpiresAt);
}
