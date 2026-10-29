namespace MautVision.Desktop.Controllers.System.ResponseDto;

/// <summary>Accepted — false if the entry was dropped by the rate limit.</summary>
public sealed record LogResponseDto(bool Accepted);
