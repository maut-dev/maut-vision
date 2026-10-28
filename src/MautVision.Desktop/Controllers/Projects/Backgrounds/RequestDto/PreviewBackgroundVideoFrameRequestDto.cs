namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>Кадр видео выбора (SelectionToken из backgrounds.selectVideo) на отметке TimestampMs для рамки. MaxSide — 64–4096, по умолчанию 1600.</summary>
public sealed record PreviewBackgroundVideoFrameRequestDto(string ProjectId, string SelectionToken, long TimestampMs, int? MaxSide = null);
