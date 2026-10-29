namespace MautVision.Desktop.Controllers.System.RequestDto;

/// <summary>Unhandled UI error. Level — error or warning; Message up to 2000 characters, Stack up to 8000.</summary>
public sealed record LogRequestDto(string Level, string Message, string? Stack = null);
