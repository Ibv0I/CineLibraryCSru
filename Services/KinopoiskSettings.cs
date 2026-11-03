using System.Text.Json;

namespace CineLibraryCS.Services;

/// <summary>Protected storage for multiple Kinopoisk API Unofficial keys.</summary>
public static class KinopoiskSettings
{
    private const string PrefKey = "kinopoisk_api_unofficial_tokens_v2";
    private const string LegacyPrefKey = "kinopoisk_api_unofficial_token";
    public static string ApiToken { get; private set; } = string.Empty;
    public static IReadOnlyList<string> ApiTokens { get; private set; } = Array.Empty<string>();

    public static void Load()
    {
        var stored = AppState.Instance.GetPref(PrefKey, string.Empty);
        var decoded = ProxySettings.UnprotectSecret(stored);
        List<string> tokens;
        try { tokens = JsonSerializer.Deserialize<List<string>>(decoded) ?? new(); }
        catch { tokens = new(); }
        if (tokens.Count == 0)
        {
            var legacy = ProxySettings.UnprotectSecret(AppState.Instance.GetPref(LegacyPrefKey, string.Empty));
            if (!string.IsNullOrWhiteSpace(legacy)) tokens.Add(legacy);
        }
        Set(tokens);
    }

    public static void Save(string token) => Save(new[] { token });

    public static void Save(IEnumerable<string> tokens)
    {
        Set(tokens);
        AppState.Instance.SetPref(PrefKey, ProxySettings.ProtectSecret(JsonSerializer.Serialize(ApiTokens)));
        // Keep the legacy preference in sync for older builds/downgrade compatibility.
        AppState.Instance.SetPref(LegacyPrefKey, ProxySettings.ProtectSecret(ApiToken));
    }

    private static void Set(IEnumerable<string> tokens)
    {
        ApiTokens = tokens.Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        ApiToken = ApiTokens.FirstOrDefault() ?? string.Empty;
    }
}
