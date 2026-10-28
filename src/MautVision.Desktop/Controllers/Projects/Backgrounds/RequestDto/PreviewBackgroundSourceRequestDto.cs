namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>
/// Frame of a selection file (SelectionToken from backgrounds.selectFiles, SourceIndex — index of the file in the selection) for the crop frame.
/// MaxSide — 64–4096, default 1600.
/// </summary>
public sealed record PreviewBackgroundSourceRequestDto(string ProjectId, string SelectionToken, int SourceIndex, int? MaxSide = null);
