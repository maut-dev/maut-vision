namespace MautVision.Desktop.Controllers.Settings.RequestDto;

public sealed record GetLanguageRequestDto;

/// <summary>Language: system, ru or en.</summary>
public sealed record SetLanguageRequestDto(string Language);