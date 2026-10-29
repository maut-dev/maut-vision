using System.Globalization;
using System.Resources;

namespace MautVision.Desktop.Host;

/// <summary>
/// Texts of the host itself (loading window, tray, message boxes, native file dialogs) from Strings.resx (English) and Strings.ru.resx.
/// Culture follows the interface language (<see cref="HostLanguage"/>); the page has its own catalogs.
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager Resources = new("MautVision.Desktop.Host.Strings", typeof(Strings).Assembly);
    private static volatile CultureInfo culture = CultureInfo.InvariantCulture;

    /// <summary>The culture of the texts: ru or the neutral English one.</summary>
    internal static CultureInfo Culture
    {
        get => culture;
        set => culture = value;
    }

    internal static string LoadingWorkspace => Get(nameof(LoadingWorkspace));
    internal static string PreparingApplication => Get(nameof(PreparingApplication));
    internal static string TrayOpen => Get(nameof(TrayOpen));
    internal static string TrayExit => Get(nameof(TrayExit));
    internal static string TrayNotice => Get(nameof(TrayNotice));
    internal static string StartupFailedTitle => Get(nameof(StartupFailedTitle));
    internal static string StartupFailed => Get(nameof(StartupFailed));
    internal static string UnexpectedError => Get(nameof(UnexpectedError));
    internal static string WebBuildMissing => Get(nameof(WebBuildMissing));
    internal static string WebViewRuntimeMissing => Get(nameof(WebViewRuntimeMissing));
    internal static string LaunchValueExpected => Get(nameof(LaunchValueExpected));
    internal static string LaunchUnknownParameter => Get(nameof(LaunchUnknownParameter));
    internal static string LaunchPythonPair => Get(nameof(LaunchPythonPair));
    internal static string LaunchCatalogSmokeOnly => Get(nameof(LaunchCatalogSmokeOnly));
    internal static string LaunchLanguageInvalid => Get(nameof(LaunchLanguageInvalid));
    internal static string DialogAddImages => Get(nameof(DialogAddImages));
    internal static string DialogAddBackgrounds => Get(nameof(DialogAddBackgrounds));
    internal static string DialogBackgroundVideo => Get(nameof(DialogBackgroundVideo));
    internal static string DialogImportFiles => Get(nameof(DialogImportFiles));
    internal static string DialogImportFolder => Get(nameof(DialogImportFolder));
    internal static string DialogVideoFrames => Get(nameof(DialogVideoFrames));
    internal static string DialogCheckImage => Get(nameof(DialogCheckImage));
    internal static string DialogCheckVideo => Get(nameof(DialogCheckVideo));
    internal static string DialogExportDataset => Get(nameof(DialogExportDataset));
    internal static string DialogExportModel => Get(nameof(DialogExportModel));
    internal static string DialogExportProject => Get(nameof(DialogExportProject));
    internal static string DialogImportProject => Get(nameof(DialogImportProject));
    internal static string FilterImages => Get(nameof(FilterImages));
    internal static string FilterVideos => Get(nameof(FilterVideos));
    internal static string FilterProjectArchive => Get(nameof(FilterProjectArchive));
    internal static string FilterZip => Get(nameof(FilterZip));

    internal static string QuitQuestion(int count) => Format(nameof(QuitQuestion), count);
    internal static string LogDetails(string path) => Format(nameof(LogDetails), path);
    internal static string OpenFailed(string reason) => Format(nameof(OpenFailed), reason);
    internal static string InterfaceLoadFailed(object status) => Format(nameof(InterfaceLoadFailed), status);
    internal static string WebViewCrashed(object reason) => Format(nameof(WebViewCrashed), reason);

    private static string Get(string name)
        => Resources.GetString(name, culture) ?? throw new InvalidOperationException($"The host text {name} is missing.");

    private static string Format(string name, params object[] values) => string.Format(culture, Get(name), values);
}
