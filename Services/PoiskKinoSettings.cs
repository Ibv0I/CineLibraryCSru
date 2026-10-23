namespace CineLibraryCS.Services;

/// <summary>PoiskKino API token, protected for the current Windows user.</summary>
public static class PoiskKinoSettings
{
    private const string PrefKey = "poiskkino_api_token";
    public static string ApiToken { get; private set; } = string.Empty;

    public static void Load()
    {
        var stored = AppState.Instance.GetPref(PrefKey, string.Empty);
        ApiToken = ProxySettings.UnprotectSecret(stored);
    }

    public static void Save(string token)
    {
        ApiToken = (token ?? string.Empty).Trim();
        AppState.Instance.SetPref(PrefKey, ProxySettings.ProtectSecret(ApiToken));
    }
}
