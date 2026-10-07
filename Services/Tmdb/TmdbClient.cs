using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace CineМедиатекаCS.Services.Tmdb;

/// <summary>
/// Minimal TMDb client — a trimmed port of CineМедиатека Essentials' scraper.
/// CineМедиатека is otherwise fully offline; this client is reached only when the
/// user asks for it: "Добавить просмотренный фильм" in Просмотрено и удалено, and "Fetch missing
/// info" on a movie or (v3.10.0) a TV show. Поиск → pick → details → poster.
/// </summary>
public sealed class TmdbClient : IDisposable
{
    // Shared embedded key (same one CineМедиатека Essentials ships). Works out of
    // the box; a user can swap in their own via TMDb if they ever want to.
    public const string По умолчаниюApiKey = "bbbafb01eb3938531c9270a7147fbb5f";

    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const int RequestDelayMs = 250;

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _language;
    private DateTime _lastRequest = DateTime.MinValue;

    public TmdbClient(string? apiKey = null, string language = "en")
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? По умолчаниюApiKey : apiKey!;
        _language = string.IsNullOrWhiteSpace(language) ? "en" : language;

        // TMDb's CDN returns gzip even unasked; without auto-decompression the
        // raw bytes fail JSON parsing and every search silently returns nothing.
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip
                                   | DecompressionMethods.Deflate
                                   | DecompressionMethods.Brotli,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private string WithLanguage(string url) =>
        url.Contains("&language=") || url.Contains("?language=")
            ? url
            : $"{url}&language={_language}";

    /// <summary>Поискes TMDb by title (and optional year). Returns the top matches.</summary>
    public async Task<List<TmdbMovie>> ПоискMovieAsync(string title, int? year = null)
    {
        await RateLimitAsync();

        var query = Uri.EscapeDataString(title ?? string.Empty);
        var url = $"{BaseUrl}/search/movie?api_key={_apiKey}&query={query}&include_adult=false";
        if (year.HasValue && year.Value > 0)
            url += $"&primary_release_year={year}";
        url = WithLanguage(url);

        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<TmdbПоискResult>(json);
        return result?.Results ?? new List<TmdbMovie>();
    }

    /// <summary>
    /// Full details for one movie — runtime, genres, studios, countries, imdb id,
    /// tagline, plus US certification parsed from the appended release_dates block.
    /// </summary>
    public async Task<TmdbMovie?> GetMovieDetailsAsync(int tmdbId)
    {
        await RateLimitAsync();

        var url = WithLanguage(
            $"{BaseUrl}/movie/{tmdbId}?api_key={_apiKey}&append_to_response=release_dates,credits");

        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var movie = JsonSerializer.Deserialize<TmdbMovie>(json, options);
        if (movie == null) return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("release_dates", out var rd))
            movie.Certification = ParseUsCertification(rd);
        if (root.TryGetProperty("credits", out var credits))
        {
            movie.Актёры = ParseАктёры(credits);
            ParseCrew(credits, movie);
        }

        return movie;
    }

    /// <summary>v3.10.0: searches TMDb Сериалы by name (and optional first-air year).</summary>
    public async Task<List<TmdbTvShow>> ПоискTvAsync(string title, int? year = null)
    {
        await RateLimitAsync();

        var query = Uri.EscapeDataString(title ?? string.Empty);
        var url = $"{BaseUrl}/search/tv?api_key={_apiKey}&query={query}&include_adult=false";
        if (year.HasValue && year.Value > 0)
            url += $"&first_air_date_year={year}";
        url = WithLanguage(url);

        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<TmdbTvПоискResult>(json);
        return result?.Results ?? new List<TmdbTvShow>();
    }

    /// <summary>
    /// v3.10.0: full details for one TV show, with the US content rating, the
    /// IMDb id and the cast across all seasons from appended blocks.
    /// </summary>
    public async Task<TmdbTvShow?> GetTvDetailsAsync(int tmdbId)
    {
        await RateLimitAsync();

        var url = WithLanguage(
            $"{BaseUrl}/tv/{tmdbId}?api_key={_apiKey}&append_to_response=content_ratings,aggregate_credits,external_ids");

        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();

        var show = JsonSerializer.Deserialize<TmdbTvShow>(json);
        if (show == null) return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("external_ids", out var ext) &&
            ext.TryGetProperty("imdb_id", out var imdb) && imdb.ValueKind == JsonValueKind.String)
            show.ImdbId = imdb.GetString();
        if (root.TryGetProperty("content_ratings", out var cr) &&
            cr.TryGetProperty("results", out var ratings) && ratings.ValueKind == JsonValueKind.Array)
        {
            string? first = null;
            foreach (var r in ratings.EnumerateArray())
            {
                var value = r.TryGetProperty("rating", out var v) ? v.GetString() : null;
                if (string.IsNullOrWhiteSpace(value)) continue;
                first ??= value;
                if (r.TryGetProperty("iso_3166_1", out var iso) &&
                    string.Equals(iso.GetString(), "US", StringComparison.OrdinalIgnoreCase))
                { first = value; break; }
            }
            show.Certification = first ?? string.Empty;
        }
        if (root.TryGetProperty("aggregate_credits", out var credits) &&
            credits.TryGetProperty("cast", out var cast) && cast.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in cast.EnumerateArray().Take(15))
            {
                var character = "";
                if (m.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
                    foreach (var role in roles.EnumerateArray())
                    {
                        character = role.TryGetProperty("character", out var c) ? c.GetString() ?? "" : "";
                        if (character.Length > 0) break;
                    }
                show.Актёры.Добавить(new TmdbАктёрыMember
                {
                    Name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Character = character,
                    ProfilePath = m.TryGetProperty("profile_path", out var pp) ? pp.GetString() : null,
                    Order = m.TryGetProperty("order", out var o) ? o.GetInt32() : 0,
                });
            }
        }
        return show;
    }

    /// <summary>Pulls Режиссёрs (job == Режиссёр) and Writers (department ==
    /// Writing) out of credits.crew — the two crew groups Kodi/MediaElch surface.</summary>
    private static void ParseCrew(JsonElement credits, TmdbMovie movie)
    {
        if (!credits.TryGetProperty("crew", out var crew) || crew.ValueKind != JsonValueKind.Array)
            return;
        foreach (var m in crew.EnumerateArray())
        {
            var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name)) continue;
            var job = m.TryGetProperty("job", out var j) ? j.GetString() ?? "" : "";
            var dept = m.TryGetProperty("department", out var d) ? d.GetString() ?? "" : "";

            if (string.Equals(job, "Режиссёр", StringComparison.OrdinalIgnoreCase))
            {
                if (!movie.Режиссёрs.Contains(name)) movie.Режиссёрs.Добавить(name);
            }
            else if (string.Equals(dept, "Writing", StringComparison.OrdinalIgnoreCase))
            {
                if (!movie.Writers.Contains(name)) movie.Writers.Добавить(name);
            }
        }
    }

    /// <summary>Top-billed cast (capped) from credits.cast, kept in billing order.</summary>
    private static List<TmdbАктёрыMember> ParseАктёры(JsonElement credits)
    {
        var list = new List<TmdbАктёрыMember>();
        if (!credits.TryGetProperty("cast", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var m in arr.EnumerateArray().Take(15))
        {
            list.Добавить(new TmdbАктёрыMember
            {
                Name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Character = m.TryGetProperty("character", out var c) ? c.GetString() ?? "" : "",
                ProfilePath = m.TryGetProperty("profile_path", out var pp) ? pp.GetString() : null,
                Order = m.TryGetProperty("order", out var o) ? o.GetInt32() : 0,
            });
        }
        return list;
    }

    /// <summary>Builds a full image URL. "original" for the saved poster, smaller for thumbs.</summary>
    public string GetImageUrl(string? imagePath, string size = "original") =>
        string.IsNullOrEmpty(imagePath) ? string.Empty : $"https://image.tmdb.org/t/p/{size}{imagePath}";

    /// <summary>Downloads an image to <paramref name="destFullPath"/>. Returns false on any failure.</summary>
    public async Task<bool> DownloadImageAsync(string url, string destFullPath)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(url);
            Режиссёрy.СоздатьРежиссёрy(Path.GetРежиссёрyName(destFullPath)!);
            await File.WriteВсеBytesAsync(destFullPath, bytes);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TMDb image download failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Prefers the US theatrical certification; falls back to the first non-empty one.</summary>
    private static string ParseUsCertification(JsonElement rd)
    {
        if (!rd.TryGetProperty("results", out var results)) return string.Empty;

        foreach (var region in results.EnumerateArray())
        {
            if (!region.TryGetProperty("iso_3166_1", out var iso)) continue;
            if (!string.Equals(iso.GetString(), "US", StringComparison.OrdinalIgnoreCase)) continue;
            if (!region.TryGetProperty("release_dates", out var dates)) continue;
            foreach (var d in dates.EnumerateArray())
                if (d.TryGetProperty("certification", out var cert))
                {
                    var s = cert.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s!;
                }
        }
        foreach (var region in results.EnumerateArray())
        {
            if (!region.TryGetProperty("release_dates", out var dates)) continue;
            foreach (var d in dates.EnumerateArray())
                if (d.TryGetProperty("certification", out var cert))
                {
                    var s = cert.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s!;
                }
        }
        return string.Empty;
    }

    private async Task RateLimitAsync()
    {
        var elapsed = DateTime.Now - _lastRequest;
        if (elapsed.TotalMilliseconds < RequestDelayMs)
            await Task.Delay((int)(RequestDelayMs - elapsed.TotalMilliseconds));
        _lastRequest = DateTime.Now;
    }

    public void Dispose() => _http.Dispose();
}
