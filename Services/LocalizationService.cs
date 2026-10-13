using System.Xml.Linq;

namespace CineLibraryCS.Services;

/// <summary>
/// Loads localized strings from the language-specific Resources.resw file.
/// The app is unpackaged WinUI 3, so this intentionally avoids
/// Windows.Globalization.ApplicationLanguages and the UWP ResourceLoader,
/// which can fail during startup in unpackaged deployments.
/// </summary>
public static class LocalizationService
{
    private static readonly object Sync = new();
    private static Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private static string _language = "ru-RU";

    public static void ApplyLanguage(string language)
    {
        var normalized = language.Equals("en-US", StringComparison.OrdinalIgnoreCase)
            ? "en-US" : "ru-RU";
        lock (Sync)
        {
            _language = normalized;
            _strings = LoadStrings(normalized);
        }
    }

    public static string Get(string key)
    {
        lock (Sync)
        {
            if (_strings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                return value;
            return key;
        }
    }

    private static Dictionary<string, string> LoadStrings(string language)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Strings", language, "Resources.resw");
            if (!File.Exists(path) && language != "en-US")
                path = Path.Combine(AppContext.BaseDirectory, "Strings", "ru-RU", "Resources.resw");
            if (!File.Exists(path)) return result;

            var doc = XDocument.Load(path);
            foreach (var entry in doc.Root?.Elements("data") ?? Enumerable.Empty<XElement>())
            {
                var key = (string?)entry.Attribute("name");
                var value = (string?)entry.Element("value");
                if (!string.IsNullOrWhiteSpace(key) && value is not null)
                    result[key] = value;
            }
        }
        catch
        {
            // Localization must never prevent the app from starting.
        }
        return result;
    }
}
