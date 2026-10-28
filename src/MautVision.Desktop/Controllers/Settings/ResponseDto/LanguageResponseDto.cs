namespace MautVision.Desktop.Controllers.Settings.ResponseDto;

/// <summary>
/// The interface language: Setting — system, ru or en as the user chose it; Language — ru or en it resolves to. Also the event
/// settings.languageChanged.
/// </summary>
public sealed record LanguageResponseDto(string Setting, string Language);