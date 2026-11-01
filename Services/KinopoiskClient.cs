using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using CineLibraryCS.Services.Tmdb;

namespace CineLibraryCS.Services;

/// <summary>Client for Kinopoisk API Unofficial. All requests use the application's shared proxy.</summary>
public sealed class KinopoiskClient : IDisposable
{
    private const string BaseUrl = "https://kinopoiskapiunofficial.tech/api";
    private readonly HttpClient _http;

    public KinopoiskClient(string? token = null)
    {
        var apiToken = string.IsNullOrWhiteSpace(token) ? KinopoiskSettings.ApiToken : token.Trim();
        if (string.IsNullOrWhiteSpace(apiToken))
            throw new InvalidOperationException("Укажи API-ключ КиноПоиска в настройках приложения.");
        _http = ProxySettings.CreateHttpClient(TimeSpan.FromSeconds(25));
        _http.DefaultRequestHeaders.Add("X-API-KEY", apiToken);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<List<TmdbMovie>> SearchMovieAsync(string title, int? year = null)
    {
        var query = Uri.EscapeDataString(title ?? string.Empty);
        var json = await _http.GetStringAsync($"{BaseUrl}/v2.1/films/search-by-keyword?keyword={query}&page=1");
        using var doc = JsonDocument.Parse(json);
        var results = new List<TmdbMovie>();
        if (!doc.RootElement.TryGetProperty("films", out var films) || films.ValueKind != JsonValueKind.Array)
            return results;
        foreach (var item in films.EnumerateArray())
        {
            var id = Int(item, "filmId", "kinopoiskId");
            if (id <= 0) continue;
            var releaseYear = Int(item, "year");
            if (year.HasValue && releaseYear > 0 && releaseYear != year.Value) continue;
            results.Add(new TmdbMovie
            {
                TmdbId = id,
                KinopoiskId = id,
                SourceProvider = "kinopoisk",
                Title = Str(item, "nameRu") is { Length: > 0 } ru ? ru : Str(item, "nameEn"),
                OriginalTitle = Str(item, "nameEn"),
                ReleaseDate = releaseYear > 0 ? $"{releaseYear:0000}-01-01" : string.Empty,
                Overview = Str(item, "description"),
                Rating = Double(item, "rating"),
                PosterPath = Str(item, "posterUrl"),
                BackdropPath = string.Empty,
            });
        }
        return results;
    }

    public async Task<TmdbMovie?> GetMovieDetailsAsync(int id)
    {
        var json = await _http.GetStringAsync($"{BaseUrl}/v2.2/films/{id}");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var year = Int(root, "year");
        var movie = new TmdbMovie
        {
            TmdbId = id,
            KinopoiskId = id,
            SourceProvider = "kinopoisk",
            Title = First(Str(root, "nameRu"), Str(root, "nameEn"), Str(root, "nameOriginal")),
            OriginalTitle = First(Str(root, "nameOriginal"), Str(root, "nameEn")),
            ReleaseDate = year > 0 ? $"{year:0000}-01-01" : string.Empty,
            Overview = Str(root, "description"),
            Tagline = Str(root, "shortDescription"),
            Rating = Double(root, "ratingKinopoisk", "ratingImdb"),
            VoteCount = Int(root, "ratingKinopoiskVoteCount"),
            Runtime = Int(root, "filmLength"),
            ImdbId = Str(root, "imdbId"),
            PosterPath = Str(root, "posterUrl"),
            BackdropPath = string.Empty,
            RussianCertification = Str(root, "ratingAgeLimits"),
        };
        if (root.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
            foreach (var g in genres.EnumerateArray()) { var name = Str(g, "genre"); if (!string.IsNullOrWhiteSpace(name)) movie.Genres.Add(new TmdbNamed { Name = name }); }
        if (root.TryGetProperty("countries", out var countries) && countries.ValueKind == JsonValueKind.Array)
            foreach (var c in countries.EnumerateArray()) { var name = Str(c, "country"); if (!string.IsNullOrWhiteSpace(name)) movie.ProductionCountries.Add(new TmdbCountry { Name = name }); }
        await LoadStaffAsync(movie, id);
        return movie;
    }

    public async Task<List<string>> GetAwardsAsync(int id)
    {
        var output = new List<string>();
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/v2.2/films/{id}/awards");
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return output;
            foreach (var item in items.EnumerateArray())
            {
                var name = First(Str(item, "name"), Str(item, "awardName"), Str(item, "award"));
                var category = First(Str(item, "nominationName"), Str(item, "category"));
                var year = Int(item, "year");
                var won = item.TryGetProperty("win", out var win) && win.ValueKind == JsonValueKind.True;
                var line = string.Join(" — ", new[] { year > 0 ? year.ToString(CultureInfo.InvariantCulture) : "", name, category, won ? "Победа" : "Номинация" }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(line)) output.Add(line);
            }
        }
        catch (HttpRequestException) { }
        return output.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<TmdbMovieImages> GetMovieImagesAsync(int id)
    {
        var posters = await GetImagesOfTypeAsync(id, "POSTER");
        var backdrops = await GetImagesOfTypeAsync(id, "FAN_ART");
        if (backdrops.Count == 0) backdrops = await GetImagesOfTypeAsync(id, "WALLPAPER");
        if (backdrops.Count == 0) backdrops = await GetImagesOfTypeAsync(id, "COVER");
        return new TmdbMovieImages(posters, backdrops);
    }

    private async Task<List<string>> GetImagesOfTypeAsync(int id, string type)
    {
        var output = new List<string>();
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/v2.2/films/{id}/images?type={type}&page=1");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                {
                    var url = First(Str(item, "imageUrl"), Str(item, "url"));
                    if (Uri.TryCreate(url, UriKind.Absolute, out _)) output.Add(url);
                }
        }
        catch (HttpRequestException) { }
        return output.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task LoadStaffAsync(TmdbMovie movie, int id)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/v1/staff?filmId={id}");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            var order = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = First(Str(item, "nameRu"), Str(item, "nameEn"));
                if (string.IsNullOrWhiteSpace(name)) continue;
                var profession = Str(item, "professionKey");
                if (profession.Contains("DIRECTOR", StringComparison.OrdinalIgnoreCase)) movie.Directors.Add(name);
                if (profession.Contains("WRITER", StringComparison.OrdinalIgnoreCase)) movie.Writers.Add(name);
                if (profession.Contains("ACTOR", StringComparison.OrdinalIgnoreCase))
                {
                    var photo = Str(item, "posterUrl");
                    movie.Cast.Add(new TmdbCastMember { Name = name, Character = Str(item, "description"), ProfilePath = photo, Order = order++ });
                }
            }
        }
        catch (HttpRequestException) { }
    }

    private static string Str(JsonElement e, params string[] names)
    {
        foreach (var name in names) if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString() ?? string.Empty;
        return string.Empty;
    }
    private static int Int(JsonElement e, params string[] names)
    {
        foreach (var name in names) if (e.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        }
        return 0;
    }
    private static double Double(JsonElement e, params string[] names)
    {
        foreach (var name in names) if (e.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return n;
        }
        return 0;
    }
    private static string First(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    public void Dispose() => _http.Dispose();
}
