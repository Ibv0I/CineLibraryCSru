using System.Globalization;
using System.Text.Json;
using CineLibraryCS.Services.Tmdb;

namespace CineLibraryCS.Services;

/// <summary>Client for poiskkino.dev. All API and image requests use the shared proxy.</summary>
public sealed class PoiskKinoClient : IDisposable
{
    private const string BaseUrl = "https://api.poiskkino.dev/v1.5";
    private readonly HttpClient _http;

    public PoiskKinoClient(string? token = null)
    {
        var apiToken = string.IsNullOrWhiteSpace(token) ? PoiskKinoSettings.ApiToken : token.Trim();
        if (string.IsNullOrWhiteSpace(apiToken))
            throw new InvalidOperationException("Укажи API-ключ ПоискКино в настройках приложения.");
        _http = ProxySettings.CreateHttpClient(TimeSpan.FromSeconds(25));
        _http.DefaultRequestHeaders.Add("X-API-KEY", apiToken);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<List<TmdbMovie>> SearchMovieAsync(string title, int? year = null)
    {
        var url = $"{BaseUrl}/movie/search?query={Uri.EscapeDataString(title ?? string.Empty)}&page=1&limit=20";
        using var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        if (!root.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array) return new();
        var results = new List<TmdbMovie>();
        foreach (var item in docs.EnumerateArray())
        {
            var id = Int(item, "id");
            if (id <= 0) continue;
            var releaseYear = Int(item, "year");
            if (year.HasValue && releaseYear > 0 && releaseYear != year.Value) continue;
            var external = Prop(item, "externalId");
            results.Add(new TmdbMovie
            {
                TmdbId = Int(external, "tmdb"),
                KinopoiskId = id,
                SourceProvider = "poiskkino",
                Title = First(Str(item, "name"), Str(item, "alternativeName"), Str(item, "enName")),
                OriginalTitle = First(Str(item, "alternativeName"), Str(item, "enName")),
                ReleaseDate = releaseYear > 0 ? $"{releaseYear:0000}-01-01" : string.Empty,
                Overview = First(Str(item, "description"), Str(item, "shortDescription")),
                Rating = Number(Prop(item, "rating"), "kp"),
                VoteCount = Int(Prop(item, "votes"), "kp"),
                KinopoiskRating = Number(Prop(item, "rating"), "kp"),
                KinopoiskVoteCount = Int(Prop(item, "votes"), "kp"),
                ImdbRating = NullableNumber(Prop(item, "rating"), "imdb"),
                ImdbVoteCount = NullableInt(Prop(item, "votes"), "imdb"),
                PosterPath = Str(Prop(item, "poster"), "url"),
                BackdropPath = Str(Prop(item, "backdrop"), "url"),
                ImdbId = Str(external, "imdb"),
                // Keep the external TMDb ID only for reference; source ID remains in KinopoiskId.
            });
        }
        return results;
    }

    public async Task<TmdbMovie?> GetMovieDetailsAsync(int id)
    {
        using var response = await _http.GetAsync($"{BaseUrl}/movie/{id}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var year = Int(root, "year");
        var external = Prop(root, "externalId");
        var movie = new TmdbMovie
        {
            TmdbId = Int(external, "tmdb"),
            KinopoiskId = id,
            SourceProvider = "poiskkino",
            Title = First(Str(root, "name"), Str(root, "alternativeName"), Str(root, "enName")),
            OriginalTitle = First(Str(root, "alternativeName"), Str(root, "enName")),
            ReleaseDate = year > 0 ? $"{year:0000}-01-01" : string.Empty,
            Overview = First(Str(root, "description"), Str(root, "shortDescription")),
            Tagline = Str(root, "slogan"),
            Rating = Number(Prop(root, "rating"), "kp"),
            VoteCount = Int(Prop(root, "votes"), "kp"),
            KinopoiskRating = Number(Prop(root, "rating"), "kp"),
            KinopoiskVoteCount = Int(Prop(root, "votes"), "kp"),
            ImdbRating = NullableNumber(Prop(root, "rating"), "imdb"),
            ImdbVoteCount = NullableInt(Prop(root, "votes"), "imdb"),
            Runtime = Int(root, "movieLength"),
            ImdbId = Str(external, "imdb"),
            PosterPath = Str(Prop(root, "poster"), "url"),
            BackdropPath = Str(Prop(root, "backdrop"), "url"),
            RussianCertification = Int(root, "ageRating") > 0 ? Int(root, "ageRating") + "+" : string.Empty,
        };
        if (root.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
            foreach (var item in genres.EnumerateArray()) { var name = Str(item, "name"); if (!string.IsNullOrWhiteSpace(name)) movie.Genres.Add(new TmdbNamed { Name = name }); }
        if (root.TryGetProperty("countries", out var countries) && countries.ValueKind == JsonValueKind.Array)
            foreach (var item in countries.EnumerateArray()) { var name = Str(item, "name"); if (!string.IsNullOrWhiteSpace(name)) movie.ProductionCountries.Add(new TmdbCountry { Name = name }); }
        if (root.TryGetProperty("persons", out var persons) && persons.ValueKind == JsonValueKind.Array)
        {
            var order = 0;
            foreach (var item in persons.EnumerateArray())
            {
                var name = First(Str(item, "name"), Str(item, "enName"));
                if (string.IsNullOrWhiteSpace(name)) continue;
                var profession = (Str(item, "profession") + " " + Str(item, "enProfession")).ToLowerInvariant();
                if (profession.Contains("режиссер") || profession.Contains("director")) movie.Directors.Add(name);
                if (profession.Contains("сценарист") || profession.Contains("writer")) movie.Writers.Add(name);
                if (profession.Contains("актер") || profession.Contains("актрис") || profession.Contains("actor"))
                    movie.Cast.Add(new TmdbCastMember { Name = name, Character = Str(item, "description"), ProfilePath = Str(item, "photo"), Order = order++ });
            }
        }
        return movie;
    }

    public async Task<TmdbMovieImages> GetMovieImagesAsync(int id)
    {
        var posters = new List<string>();
        var backdrops = new List<string>();
        try
        {
            var url = $"{BaseUrl}/image?movieId={id}&limit=100";
            using var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("docs", out var docs) && docs.ValueKind == JsonValueKind.Array)
                foreach (var item in docs.EnumerateArray())
                {
                    var image = Str(item, "url");
                    var type = Str(item, "type");
                    if (string.IsNullOrWhiteSpace(image) || !Uri.TryCreate(image, UriKind.Absolute, out _)) continue;
                    if (type.Equals("cover", StringComparison.OrdinalIgnoreCase)) posters.Add(image);
                    else if (type.Equals("backdrops", StringComparison.OrdinalIgnoreCase) || type.Equals("wallpaper", StringComparison.OrdinalIgnoreCase)) backdrops.Add(image);
                }
        }
        catch (HttpRequestException) { }
        if (posters.Count == 0 || backdrops.Count == 0)
        {
            var details = await GetMovieDetailsAsync(id);
            if (details != null)
            {
                if (posters.Count == 0 && !string.IsNullOrWhiteSpace(details.PosterPath)) posters.Add(details.PosterPath);
                if (backdrops.Count == 0 && !string.IsNullOrWhiteSpace(details.BackdropPath)) backdrops.Add(details.BackdropPath);
            }
        }
        return new TmdbMovieImages(posters.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), backdrops.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }


    /// <summary>Returns awards and nominations for a movie using the documented v1.5 endpoint.</summary>
    public async Task<List<string>> GetAwardsAsync(int movieId)
    {
        var output = new List<string>();
        string? next = null;
        // Cursor pagination; cap at 10 pages to respect the documented demo/free limit.
        for (var page = 0; page < 10; page++)
        {
            var query = $"movieId={movieId}&limit=250&selectFields=movieId&selectFields=winning&selectFields=nomination.title&selectFields=nomination.award.title&selectFields=nomination.award.year";
            if (!string.IsNullOrWhiteSpace(next)) query += "&next=" + Uri.EscapeDataString(next);
            using var response = await _http.GetAsync($"{BaseUrl}/movie/awards?{query}");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (!root.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array) break;
            foreach (var item in docs.EnumerateArray())
            {
                var nomination = Prop(item, "nomination");
                var award = Prop(nomination, "award");
                var awardName = Str(award, "title");
                var category = Str(nomination, "title");
                var year = Int(award, "year");
                var winning = Prop(item, "winning");
                var isWinner = winning.ValueKind == JsonValueKind.True ||
                    (winning.ValueKind == JsonValueKind.String && bool.TryParse(winning.GetString(), out var parsed) && parsed);
                var line = string.Join(" — ", new[] { year > 0 ? year.ToString(CultureInfo.InvariantCulture) : "", awardName, category, isWinner ? "Победа" : "Номинация" }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(line)) output.Add(line);
            }
            next = Str(root, "next");
            var hasNext = Prop(root, "hasNext");
            if (hasNext.ValueKind != JsonValueKind.True || string.IsNullOrWhiteSpace(next)) break;
        }
        return output.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<TmdbMovie?> GetMovieByImdbIdAsync(string imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId)) return null;
        var url = $"{BaseUrl}/movie?externalId.imdb={Uri.EscapeDataString(imdbId.Trim())}&limit=1";
        using var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array || docs.GetArrayLength() == 0) return null;
        var id = Int(docs[0], "id");
        return id > 0 ? await GetMovieDetailsAsync(id) : null;
    }

    private static JsonElement Prop(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
    private static string Str(JsonElement e, string name) { var v = Prop(e, name); return v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty; }
    private static int Int(JsonElement e, string name) { var v = Prop(e, name); if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n; if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n; return 0; }
    private static int? NullableInt(JsonElement e, string name) { var n = Int(e, name); return n > 0 ? n : null; }
    private static double Number(JsonElement e, string name) { var v = Prop(e, name); if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) return n; if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return n; return 0; }
    private static double? NullableNumber(JsonElement e, string name) { var n = Number(e, name); return n > 0 ? n : null; }
    private static string First(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    public void Dispose() => _http.Dispose();
}
