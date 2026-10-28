namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>SelectionToken — token from classes.selectAssetFiles; valid for 30 minutes and only on this page. OperationId — created by the UI per action; a repeat with the same value returns the same job.</summary>
public sealed record ImportClassAssetsRequestDto(
    string ProjectId,
    Guid ClassVariantId,
    string SelectionToken,
    string OperationId);
