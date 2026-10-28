namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>SelectionToken — токен из classes.selectAssetFiles; действует 30 минут и только на этой странице. OperationId — создаётся UI на одно действие; повтор с тем же значением вернёт ту же задачу.</summary>
public sealed record ImportClassAssetsRequestDto(
    string ProjectId,
    Guid ClassVariantId,
    string SelectionToken,
    string OperationId);
