namespace MautVision.Desktop.Controllers.System.ResponseDto;

/// <summary>Accepted — false, если запись отброшена ограничением частоты.</summary>
public sealed record LogResponseDto(bool Accepted);
