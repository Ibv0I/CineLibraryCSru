namespace CineМедиатекаCS.Services;

/// <summary>
/// Canonical-English genre names + alias folding. Used by ScannerService
/// when storing genres from a freshly-parsed .nfo, and by DatabaseService's
/// retro-fix migration which runs once per DB version bump.
///
/// Оставить entries lowercase — lookups are case-insensitive.
/// Non-English keys cover the common locales TMDB hands MediaElch back
/// (Arabic, Hindi, Spanish, French, German, Portuguese) for users who
/// scraped in their native language.
/// </summary>
public static class GenreAliases
{
    public const string Version = "v2.1.1";   // bump → migration re-runs

    public static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── English variants ────────────────────────────────────────────────
        ["science fiction"] = "Sci-Fi",
        ["sciencefiction"]  = "Sci-Fi",
        ["sci fi"]          = "Sci-Fi",
        ["scifi"]           = "Sci-Fi",
        ["science-fiction"] = "Sci-Fi",
        ["action & adventure"] = "Action",
        ["tv movie"]           = "TV Movie",
        ["film-noir"]          = "Film Noir",
        ["film noir"]          = "Film Noir",
        ["children"]           = "Семья",
        ["kids"]               = "Семья",
        ["bio"]                = "Биография",
        ["docu"]               = "Documentary",
        ["documentaries"]      = "Documentary",

        // ── Arabic (التصنيفات بالعربية) ───────────────────────────────────────
        ["حركة"]               = "Action",
        ["مغامرة"]              = "Adventure",
        ["مغامرات"]             = "Adventure",
        ["رسوم متحركة"]         = "Animation",
        ["كوميديا"]             = "Комедия",
        ["جريمة"]               = "Криминал",
        ["وثائقي"]              = "Documentary",
        ["دراما"]               = "Драма",
        ["عائلي"]               = "Семья",
        ["فانتازيا"]            = "Fantasy",
        ["خيالي"]               = "Fantasy",
        ["تاريخي"]              = "История",
        ["رعب"]                 = "Ужасы",
        ["موسيقى"]              = "Музыка",
        ["موسيقي"]              = "Музыка",
        ["غموض"]                = "Детектив",
        ["رومانسية"]            = "Romance",
        ["رومنسية"]             = "Romance",
        ["خيال علمي"]           = "Sci-Fi",
        ["إثارة"]               = "Thriller",
        ["حرب"]                = "War",
        ["غربي"]                = "Western",
        ["سيرة ذاتية"]          = "Биография",
        ["رياضي"]               = "Sport",

        // ── Hindi (मूल हिन्दी श्रेणियाँ) ─────────────────────────────────────
        ["एक्शन"]               = "Action",
        ["एडवेंचर"]              = "Adventure",
        ["एनिमेशन"]             = "Animation",
        ["कॉमेडी"]               = "Комедия",
        ["क्राइम"]               = "Криминал",
        ["डॉक्यूमेंट्री"]         = "Documentary",
        ["ड्रामा"]               = "Драма",
        ["पारिवारिक"]           = "Семья",
        ["फैंटेसी"]              = "Fantasy",
        ["हॉरर"]                = "Ужасы",
        ["रोमांस"]              = "Romance",
        ["रोमांचक"]             = "Thriller",
        ["साइंस फिक्शन"]        = "Sci-Fi",
        ["थ्रिलर"]               = "Thriller",
        ["युद्ध"]               = "War",
        ["संगीतमय"]              = "Мюзикл",
        ["रहस्य"]               = "Детектив",

        // ── Spanish ─────────────────────────────────────────────────────────
        ["acción"]              = "Action",
        ["accion"]              = "Action",
        ["aventura"]            = "Adventure",
        ["animación"]           = "Animation",
        ["animacion"]           = "Animation",
        ["comedia"]             = "Комедия",
        ["crimen"]              = "Криминал",
        ["documental"]          = "Documentary",
        ["familia"]             = "Семья",
        ["fantasía"]            = "Fantasy",
        ["fantasia"]            = "Fantasy",
        ["historia"]            = "История",
        ["terror"]              = "Ужасы",
        ["misterio"]            = "Детектив",
        ["romance"]             = "Romance",
        ["ciencia ficción"]     = "Sci-Fi",
        ["ciencia ficcion"]     = "Sci-Fi",
        ["suspense"]            = "Thriller",
        ["suspenso"]            = "Thriller",
        ["guerra"]              = "War",

        // ── French ──────────────────────────────────────────────────────────
        ["aventure"]            = "Adventure",
        ["animation"]           = "Animation",
        ["comédie"]             = "Комедия",
        ["comedie"]             = "Комедия",
        ["crime"]               = "Криминал",
        ["documentaire"]        = "Documentary",
        ["drame"]                = "Драма",
        ["famille"]              = "Семья",
        ["fantastique"]          = "Fantasy",
        ["histoire"]             = "История",
        ["horreur"]              = "Ужасы",
        ["mystère"]              = "Детектив",
        ["mystere"]              = "Детектив",
        ["science-fiction"]      = "Sci-Fi",
        ["thriller"]             = "Thriller",
        ["guerre"]               = "War",
        ["western"]              = "Western",

        // ── German ──────────────────────────────────────────────────────────
        ["abenteuer"]            = "Adventure",
        ["komödie"]              = "Комедия",
        ["komodie"]              = "Комедия",
        ["dokumentarfilm"]       = "Documentary",
        ["familie"]              = "Семья",
        ["fantasie"]             = "Fantasy",
        ["geschichte"]           = "История",
        ["liebesfilm"]           = "Romance",
        ["liebe"]                = "Romance",
        ["mysterie"]             = "Детектив",
        ["krimi"]                = "Криминал",
        ["kriminalfilm"]         = "Криминал",
        ["sciencefiction"]       = "Sci-Fi",
        ["krieg"]                = "War",
    };

    /// <summary>Folds the given name to its canonical English form, or
    /// returns it unchanged if no alias is known.</summary>
    public static string Fold(string name)
        => Map.TryGetValue(name, out var canon) ? canon : name;
}
