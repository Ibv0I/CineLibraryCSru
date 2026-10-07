using CommunityToolkit.Mvvm.ComponentModel;

namespace CineМедиатекаCS.Models;

/// <summary>
/// Card in the "Все сериалы" grid. Mirrors MovieListItem; watched
/// progress is a roll-up across all episodes.
/// </summary>
public partial class TvShowListItem : ObservableObject
{
    public int Id { get; set; }
    public string Название { get; set; } = "";
    public int? Год { get; set; }
    public double? Рейтинг { get; set; }
    public string? LocalPoster { get; set; }
    public bool IsMissing { get; set; }
    public string VolumeSerial { get; set; } = "";
    public string? DriveLabel { get; set; }
    public bool IsВкл.line { get; set; }
    public int ЭпизодCount { get; set; }
    public int ПросмотреноCount { get; set; }
    public string? ЖанрыCsv { get; set; }
    // v3.7.0: for the Все сериалы sort options (unix seconds, 0 = never).
    public long DateДобавитьed { get; set; }
    public long LastВоспроизвестиed { get; set; }
    // v4.1.0: "S01E04", the episode ▶ Воспроизвести next would start. Filled for the
    // Продолжить просмотр page only; the card shows "Далее: S01E04" in place of the year.
    public string? NextЭпизод { get; set; }

    [ObservableProperty] private bool _isИзбранное;
    [ObservableProperty] private bool _isСписок просмотра;
    [ObservableProperty] private bool _isSelected;

    public string ГодText => Год?.ToString() ?? "—";
    public string РейтингText => Рейтинг.HasValue ? $"★ {Рейтинг:F1}" : "";
    public string ProgressText => $"{ПросмотреноCount}/{ЭпизодCount}";
    public double ProgressFraction =>
        ЭпизодCount > 0 ? (double)ПросмотреноCount / ЭпизодCount : 0;
    public bool FullyПросмотрено => ЭпизодCount > 0 && ПросмотреноCount >= ЭпизодCount;
    public string StatusBadge => IsMissing ? "ОТСУТСТВУЕТ" : IsВкл.line ? "В СЕТИ" : "НЕ В СЕТИ";
}

/// <summary>A season tile inside a show — derived from tv_episodes.season.</summary>
public partial class TvSeason : ObservableObject
{
    public int ShowId { get; set; }
    public int Season { get; set; }
    public int ЭпизодCount { get; set; }
    public int ПросмотреноCount { get; set; }
    public string? PosterPath { get; set; } // show poster fallback

    public string SeasonLabel => Season == 0 ? "Specials" : $"Season {Season}";
    public string ProgressText => $"{ПросмотреноCount}/{ЭпизодCount}";
    public double ProgressFraction =>
        ЭпизодCount > 0 ? (double)ПросмотреноCount / ЭпизодCount : 0;
}

/// <summary>Вкл.e episode row inside a season.</summary>
public partial class TvЭпизодItem : ObservableObject
{
    public int Id { get; set; }
    public int ShowId { get; set; }
    public int Season { get; set; }
    public int Эпизод { get; set; }
    public string Название { get; set; } = "";
    public string? Plot { get; set; }
    public string? Aired { get; set; }
    public int? Продолжительность { get; set; }
    public double? Рейтинг { get; set; }
    public string? LocalThumb { get; set; }
    public string? VideoFileRelPath { get; set; }
    public string VolumeSerial { get; set; } = "";
    public bool IsВкл.line { get; set; }

    [ObservableProperty] private bool _isПросмотрено;
    // v2.9 — per-episode personal state. ObservableProperty so the card
    // updates instantly when toggled from the details dialog.
    [ObservableProperty] private bool _isИзбранное;
    [ObservableProperty] private string? _note;

    public string Code => $"S{Season:D2}E{Эпизод:D2}";
    public string Header => $"{Code} · {Название}";
    public string ПродолжительностьText => Продолжительность.HasValue ? $"{Продолжительность} min" : "";
    public string РейтингText => Рейтинг.HasValue ? $"★ {Рейтинг:F1}" : "";
}

/// <summary>Full episode detail incl. stream/file info — the episode dialog.</summary>
public class TvЭпизодDetail
{
    public int Id { get; set; }
    public int ShowId { get; set; }
    public int Season { get; set; }
    public int Эпизод { get; set; }
    public string Название { get; set; } = "";
    public string ShowНазвание { get; set; } = "";
    public string? Plot { get; set; }
    public string? Aired { get; set; }
    public double? Рейтинг { get; set; }
    public int? Продолжительность { get; set; }
    public string? VideoFileRelPath { get; set; }
    public string? LocalThumb { get; set; }
    public string? SubtitleLanguages { get; set; }
    public int? VideoWidth { get; set; }
    public int? VideoHeight { get; set; }
    public string? VideoCodec { get; set; }
    public string? HdrType { get; set; }
    public string? АудиоCodec { get; set; }
    public string? АудиоChannels { get; set; }
    public string? АудиоLanguages { get; set; }
    public int? DurationSeconds { get; set; }
    public string? ContainerExt { get; set; }
    public long? FileSizeBytes { get; set; }
    public bool IsПросмотрено { get; set; }
    // v2.9 — per-episode personal state.
    public bool IsИзбранное { get; set; }
    public string? Note { get; set; }
    public string VolumeSerial { get; set; } = "";

    public string Code => $"S{Season:D2}E{Эпизод:D2}";
    public string AiredText => string.IsNullOrEmpty(Aired) ? "" : Aired;
    public string ПродолжительностьText => Продолжительность.HasValue ? $"{Продолжительность} min" : "";
    public string РейтингText => Рейтинг.HasValue ? $"★ {Рейтинг:F1}" : "";

    /// <summary>e.g. "1080p", "2160p (4K)", "720p" from the height.</summary>
    public string? Resolution
    {
        get
        {
            if (!VideoHeight.HasValue) return null;
            int h = VideoHeight.Value;
            return h >= 2000 ? "2160p (4K)" : h >= 1060 ? "1080p" : h >= 700 ? "720p"
                 : h >= 570 ? "576p" : h >= 470 ? "480p" : $"{h}p";
        }
    }
    public string FileSizeText
    {
        get
        {
            if (!FileSizeBytes.HasValue) return "";
            double b = FileSizeBytes.Value;
            return b >= 1L<<30 ? $"{b/(1L<<30):F2} GB" : b >= 1L<<20 ? $"{b/(1L<<20):F0} MB" : $"{b/(1L<<10):F0} KB";
        }
    }
    public string DurationText
    {
        get
        {
            if (!DurationSeconds.HasValue || DurationSeconds.Value <= 0) return "";
            var t = TimeSpan.FromSeconds(DurationSeconds.Value);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m" : $"{t.Minutes}m {t.Seconds:D2}s";
        }
    }
}

/// <summary>Full show detail for the show header (poster, plot, cast).</summary>
public class TvShowDetail
{
    public int Id { get; set; }
    public string Название { get; set; } = "";
    public int? Год { get; set; }
    public double? Рейтинг { get; set; }
    public string? Plot { get; set; }
    public string? Mpaa { get; set; }
    public string? Студия { get; set; }
    public string? Status { get; set; }
    public string? Premiered { get; set; }
    public string? ImdbId { get; set; }
    public string? TmdbId { get; set; }
    public string? LocalPoster { get; set; }
    public string? LocalFanart { get; set; }
    public string VolumeSerial { get; set; } = "";
    public string? DriveLabel { get; set; }
    public string? ПапкаRelPath { get; set; }
    public bool IsИзбранное { get; set; }
    public bool IsСписок просмотра { get; set; }
    public string? Note { get; set; }
    public List<string> Жанры { get; set; } = new();
    public List<Actor> Actors { get; set; } = new();
    public int ЭпизодCount { get; set; }
    public int ПросмотреноCount { get; set; }
    /// <summary>v2.9 — free-form personal tags assigned to this show.</summary>
    public List<string> Tags { get; set; } = new();
}
