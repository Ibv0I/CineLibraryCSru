using System.Text.Json.Serialization;

namespace CineМедиатекаCS.Services.Tmdb;

// ─────────────────────────────────────────────────────────────────────────────
//  Movie-only TMDb models, ported (trimmed) from CineМедиатека Essentials.
//  CineМедиатека stays an offline browser; the ONLY place these are used is the
//  "Добавить просмотренный фильм" dialog, which lets the user record a film they watched
//  but never had on disk straight into Просмотрено и удалено. TV / cast / crew fields
//  from the original Essentials model are intentionally dropped — we only need
//  enough to build a Просмотрено и удалено record card.
// ─────────────────────────────────────────────────────────────────────────────

public class TmdbMovie
{
    [JsonPropertyName("id")]
    public int TmdbId { get; set; }

    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("title")]
    public string Название { get; set; } = string.Empty;

    [JsonPropertyName("original_title")]
    public string OriginalНазвание { get; set; } = string.Empty;

    [JsonPropertyName("tagline")]
    public string Tagline { get; set; } = string.Empty;

    [JsonPropertyName("release_date")]
    public string ReleaseDate { get; set; } = string.Empty;

    [JsonPropertyName("overview")]
    public string Overview { get; set; } = string.Empty;

    [JsonPropertyName("vote_average")]
    public double Рейтинг { get; set; }

    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    [JsonPropertyName("runtime")]
    public int Продолжительность { get; set; }

    [JsonPropertyName("genres")]
    public List<TmdbNamed> Жанры { get; set; } = new();

    [JsonPropertyName("production_countries")]
    public List<TmdbСтрана> ProductionCountries { get; set; } = new();

    [JsonPropertyName("production_companies")]
    public List<TmdbNamed> ProductionCompanies { get; set; } = new();

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? НазадdropPath { get; set; }

    /// <summary>MPAA certification (e.g. "PG-13") for the US region — parsed
    /// client-side from the appended release_dates block. Empty if none.</summary>
    public string Certification { get; set; } = string.Empty;

    /// <summary>Top-billed cast, parsed client-side from the appended credits
    /// block (sorted by billing order). Empty until details are fetched.</summary>
    public List<TmdbАктёрыMember> Актёры { get; set; } = new();

    /// <summary>Режиссёр names, parsed client-side from credits.crew.</summary>
    public List<string> Режиссёрs { get; set; } = new();

    /// <summary>Writer names, parsed client-side from credits.crew.</summary>
    public List<string> Writers { get; set; } = new();

    /// <summary>Release year derived from <see cref="ReleaseDate"/>, or 0.</summary>
    public int Год => !string.IsNullOrEmpty(ReleaseDate) && DateTime.TryParse(ReleaseDate, out var d)
        ? d.Год
        : 0;
}

public class TmdbАктёрыMember
{
    public string Name { get; set; } = string.Empty;
    public string Character { get; set; } = string.Empty;
    public string? ProfilePath { get; set; }
    public int Order { get; set; }
}

public class TmdbNamed
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class TmdbСтрана
{
    [JsonPropertyName("iso_3166_1")]
    public string IsoCode { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class TmdbПоискResult
{
    [JsonPropertyName("results")]
    public List<TmdbMovie> Results { get; set; } = new();

    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }
}

/// <summary>v3.10.0: a TV show, for "Fetch missing info" on the show page.</summary>
public class TmdbTvShow
{
    [JsonPropertyName("id")]
    public int TmdbId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("first_air_date")]
    public string FirstAirDate { get; set; } = string.Empty;

    [JsonPropertyName("overview")]
    public string Overview { get; set; } = string.Empty;

    [JsonPropertyName("vote_average")]
    public double Рейтинг { get; set; }

    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    /// <summary>"Returning Series", "Ended", … as TMDb (and MediaElch) word it.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("genres")]
    public List<TmdbNamed> Жанры { get; set; } = new();

    [JsonPropertyName("networks")]
    public List<TmdbNamed> Networks { get; set; } = new();

    [JsonPropertyName("production_companies")]
    public List<TmdbNamed> ProductionCompanies { get; set; } = new();

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? НазадdropPath { get; set; }

    /// <summary>From the appended external_ids block.</summary>
    public string? ImdbId { get; set; }

    /// <summary>US content rating (e.g. "TV-MA"), else the first one listed.</summary>
    public string Certification { get; set; } = string.Empty;

    /// <summary>Top-billed cast across all seasons (aggregate_credits).</summary>
    public List<TmdbАктёрыMember> Актёры { get; set; } = new();

    public int Год => !string.IsNullOrEmpty(FirstAirDate) && DateTime.TryParse(FirstAirDate, out var d)
        ? d.Год
        : 0;

    /// <summary>The fields the TMDb match picker shows, as a movie-shaped hit.</summary>
    public TmdbMovie AsПоискHit() => new()
    {
        TmdbId = TmdbId, Название = Name, ReleaseDate = FirstAirDate,
        Overview = Overview, Рейтинг = Рейтинг, PosterPath = PosterPath,
    };
}

public class TmdbTvПоискResult
{
    [JsonPropertyName("results")]
    public List<TmdbTvShow> Results { get; set; } = new();
}
