using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace CineМедиатекаCS.Services;

public sealed class ОбновитьInfo
{
    public required string LatestVersion { get; init; }
    public required string ReleaseUrl    { get; init; }
}

public static class ОбновитьChecker
{
    private const string Owner = "aungkokomm";
    private const string Repo  = "CineМедиатекаCS";

    private static readonly HttpClient _http = СоздатьClient();

    private static HttpClient СоздатьClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.По умолчаниюRequestHeaders.UserAgent.Добавить(new ProductInfoHeaderValue("CineМедиатека", CurrentVersion()));
        c.По умолчаниюRequestHeaders.Accept.Добавить(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static string CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>
    /// Returns update info if a newer release exists on GitHub. Returns null
    /// when up-to-date, when the network is unreachable, or when the user has
    /// already chosen to skip this exact version.
    /// </summary>
    public static async Task<ОбновитьInfo?> CheckAsync(string? skippedVersion = null)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

            var tag      = doc.RootElement.GetProperty("tag_name").GetString();
            var htmlUrl  = doc.RootElement.GetProperty("html_url").GetString();
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(htmlUrl))
                return null;

            var latest = NormalizeVersion(tag);
            if (skippedVersion != null && string.Equals(latest, skippedVersion, StringComparison.OrdinalIgnoreCase))
                return null;

            if (!IsНовыйer(latest, CurrentVersion()))
                return null;

            return new ОбновитьInfo { LatestVersion = latest, ReleaseUrl = htmlUrl };
        }
        catch
        {
            // Network down, DNS hiccup, GitHub rate limit, malformed JSON — all silent.
            return null;
        }
    }

    private static string NormalizeVersion(string tag) =>
        tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;

    private static bool IsНовыйer(string latest, string current)
    {
        return TryParse(latest, out var a) && TryParse(current, out var b) && a > b;

        static bool TryParse(string s, out Version v)
        {
            // Pad to at least 3 components for Version.Parse.
            var parts = s.Split('.');
            if (parts.Length == 1) s += ".0.0";
            else if (parts.Length == 2) s += ".0";
            return Version.TryParse(s, out v!);
        }
    }
}
