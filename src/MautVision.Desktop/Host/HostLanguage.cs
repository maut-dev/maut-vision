using System.Globalization;

namespace MautVision.Desktop.Host;

/// <summary>
/// The interface language: the setting (system, ru, en) kept in host-settings.json and the language it resolves to. «system» is Russian
/// for a ru-* user interface culture of Windows and English otherwise. A launch option (--language, smoke runs use ru) replaces the stored
/// setting for this run without saving it. Changes apply at once: host texts switch their culture and <see cref="Changed"/> tells the
/// tray and the page.
/// </summary>
internal sealed class HostLanguage
{
    internal const string System = "system";
    internal const string Russian = "ru";
    internal const string English = "en";

    private readonly Lock gate = new();
    private readonly HostSettings settings;
    private readonly CultureInfo systemCulture;
    private string setting;

    /// <param name="forced">The --language option of this run, or null.</param>
    /// <param name="systemCulture">The user interface culture of Windows at startup.</param>
    internal HostLanguage(HostSettings settings, string? forced, CultureInfo systemCulture)
    {
        this.settings = settings;
        this.systemCulture = systemCulture;
        setting = forced ?? (settings.Language is { } stored && IsSetting(stored) ? stored : System);
        Apply();
    }

    /// <summary>Raised on the thread that changed the setting.</summary>
    internal event Action? Changed;

    internal string Setting
    {
        get
        {
            lock (gate) return setting;
        }
    }

    /// <summary>ru or en.</summary>
    internal string Language => Resolve(Setting);

    internal static bool IsSetting(string value) => value is System or Russian or English;

    /// <summary>Host texts in the language of Windows, before the settings are read (an invalid command line).</summary>
    internal static void UseSystem(CultureInfo systemCulture)
        => Strings.Culture = systemCulture.TwoLetterISOLanguageName == Russian ? CultureInfo.GetCultureInfo("ru") : CultureInfo.InvariantCulture;

    /// <summary>Stores and applies a new setting; an unknown value is an <see cref="ArgumentException"/>.</summary>
    internal void Set(string value)
    {
        if (!IsSetting(value)) throw new ArgumentException("The language must be system, ru or en.", nameof(value));
        lock (gate)
        {
            setting = value;
            settings.SaveLanguage(value);
            Apply();
        }
        Changed?.Invoke();
    }

    private string Resolve(string value) => value switch
    {
        Russian => Russian,
        English => English,
        _ => systemCulture.TwoLetterISOLanguageName == "ru" ? Russian : English,
    };

    private void Apply() => Strings.Culture = Resolve(setting) == Russian ? CultureInfo.GetCultureInfo(Russian) : CultureInfo.InvariantCulture;
}
