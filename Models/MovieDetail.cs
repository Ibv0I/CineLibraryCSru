using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CineМедиатекаCS.Models;

public partial class Actor : ObservableObject
{
    public string Name { get; set; } = "";
    public string? Role { get; set; }
    public string? Thumb { get; set; }
    public int SortOrder { get; set; }
    // Loaded lazily by the detail dialog if Thumb is a valid http(s) URL or
    // local path. Bound x:Bind Вкл.eWay so the avatar appears once decoded.
    [ObservableProperty] private BitmapImage? _thumbBitmap;

    /// <summary>
    /// First letter of first + last name (or just the first letter if one
    /// name). Used as the fallback shown behind the avatar Image — visible
    /// when Thumb is missing or fails to load.
    /// </summary>
    public string Initials
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return "?";
            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return parts[0].Substring(0, 1).ToUpperInvariant();
            return (parts[0].Substring(0, 1) + parts[^1].Substring(0, 1)).ToUpperInvariant();
        }
    }
}

public class MovieDetail
{
    public int Id { get; set; }
    public string Название { get; set; } = "";
    public string? OriginalНазвание { get; set; }
    public int? Год { get; set; }
    public double? Рейтинг { get; set; }
    public int? Продолжительность { get; set; }
    public string? Plot { get; set; }
    public string? Outline { get; set; }
    public string? Tagline { get; set; }
    public string? Mpaa { get; set; }
    public string? ImdbId { get; set; }
    public string? TmdbId { get; set; }
    public string? Premiered { get; set; }
    public string? Студия { get; set; }
    public string? Страна { get; set; }
    public string? LocalPoster { get; set; }
    public string? LocalFanart { get; set; }
    public bool IsMissing { get; set; }
    public bool IsИзбранное { get; set; }
    public bool IsПросмотрено { get; set; }
    public bool IsСписок просмотра { get; set; }
    public bool IsВкл.line { get; set; }
    public bool Воспроизвестиable { get; set; }
    public string? DriveLabel { get; set; }
    public string? CurrentLetter { get; set; }
    public string VolumeSerial { get; set; } = "";
    public string? ПапкаRelPath { get; set; }
    public string? VideoFileRelPath { get; set; }
    public string? Note { get; set; }
    // v2.2 — trailer + stream details + file info
    public string? Trailer { get; set; }
    public int? VideoWidth { get; set; }
    public int? VideoHeight { get; set; }
    public string? VideoCodec { get; set; }
    public string? VideoAspect { get; set; }
    public string? HdrType { get; set; }
    public string? АудиоCodec { get; set; }
    public string? АудиоChannels { get; set; }
    public string? АудиоLanguages { get; set; }
    public string? SubtitleLanguages { get; set; }
    public int? DurationSeconds { get; set; }
    public string? ContainerExt { get; set; }
    public long? FileSizeBytes { get; set; }

    public List<string> Жанры { get; set; } = new();
    public List<string> Режиссёрs { get; set; } = new();
    public List<string> Writers { get; set; } = new();
    public List<Actor> Actors { get; set; } = new();
    public List<string> Sets { get; set; } = new();
    public List<(string Source, double Value, int? Votes)> ВсеРейтингs { get; set; } = new();
    /// <summary>v2.9 — free-form personal tags assigned to this movie.</summary>
    public List<string> Tags { get; set; } = new();

    public string РейтингText => Рейтинг.HasValue ? $"★ {Рейтинг:F1}" : "";
    public string ПродолжительностьText => Продолжительность.HasValue ? $"{Продолжительность} min" : "";
    public string ImdbUrl => ImdbId != null ? $"https://www.imdb.com/title/{ImdbId}/" : "";
}
