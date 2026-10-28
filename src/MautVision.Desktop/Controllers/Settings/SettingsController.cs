using MautVision.Desktop.Controllers.Settings.RequestDto;
using MautVision.Desktop.Controllers.Settings.ResponseDto;
using MautVision.Desktop.Host;

namespace MautVision.Desktop.Controllers.Settings;

/// <summary>Settings of the host: the interface language (host-settings.json), applied without a restart.</summary>
internal sealed class SettingsController(HostLanguage language)
{
    public Task<LanguageResponseDto> GetLanguageAsync(GetLanguageRequestDto request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Map(language));
    }

    /// <summary>Stores the setting; the tray and the page are told through settings.languageChanged.</summary>
    public Task<LanguageResponseDto> SetLanguageAsync(SetLanguageRequestDto request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        language.Set(request.Language);
        return Task.FromResult(Map(language));
    }

    internal static LanguageResponseDto Map(HostLanguage language) => new(language.Setting, language.Language);
}