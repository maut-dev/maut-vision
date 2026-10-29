namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>Frame of the selected video (SelectionToken from backgrounds.selectVideo) at the TimestampMs mark for the crop frame. MaxSide — 64–4096, default 1600.</summary>
public sealed record PreviewBackgroundVideoFrameRequestDto(string ProjectId, string SelectionToken, long TimestampMs, int? MaxSide = null);
