namespace MautVision.Desktop.Controllers.System.ResponseDto;

public sealed record PingResponseDto(string Message, string Host, DateTimeOffset Utc);
