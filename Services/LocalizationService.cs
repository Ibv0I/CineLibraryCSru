using Windows.ApplicationModel.Resources;
using Windows.Globalization;

namespace CineLibraryCS.Services;

/// <summary>Shared access to localized UI resources.</summary>
public static class LocalizationService
{
    private static ResourceLoader? _loader;

    /// <summary>Call before creating controls that read localized strings.</summary>
    public static void ApplyLanguage(string language)
    {
        var normalized = language.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? "en-US" : "ru-RU";
        ApplicationLanguages.PrimaryLanguageOverride = normalized;
        _loader = null;
    }

    public static string Get(string key)
    {
        try
        {
            _loader ??= ResourceLoader.GetForViewIndependentUse();
            var value = _loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch { return key; }
    }
}
