namespace MautVision.Desktop.Controllers.System.RequestDto;

/// <summary>Необработанная ошибка UI. Level — error или warning; Message до 2000 символов, Stack до 8000.</summary>
public sealed record LogRequestDto(string Level, string Message, string? Stack = null);
