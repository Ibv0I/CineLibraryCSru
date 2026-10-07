using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CineМедиатекаCS.Models;

/// <summary>
/// v3.4.4 — one physical copy of a movie inside a duplicate group (Tools → Дубликаты).
/// </summary>
public class DupeCopy
{
    public int Id { get; set; }
    public string Название { get; set; } = "";
    public int? Год { get; set; }
    public string? DriveLabel { get; set; }
    public string VolumeSerial { get; set; } = "";
    public string? ПапкаRelPath { get; set; }
    public string? VideoFileRelPath { get; set; }
    public string? АудиоLanguages { get; set; }
    public int? VideoWidth { get; set; }
    public int? VideoHeight { get; set; }
    public string? VideoCodec { get; set; }
    public string? HdrType { get; set; }
    public string? Изменитьion { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? LocalPoster { get; set; }
    public bool IsMissing { get; set; }
    public bool IsВкл.line { get; set; }
    public string? CurrentLetter { get; set; }

    /// <summary>The copy worth keeping (best resolution / largest). The others
    /// in a possible-duplicate group are the removable candidates.</summary>
    public bool IsОставитьer { get; set; }

    // Prefer width (stable across aspect ratios: 1920→1080p) so a cropped
    // 2.35:1 file doesn't read as a different, lower resolution.
    public string ResolutionText
    {
        get
        {
            if (VideoWidth is int w && w > 0)
                return w >= 3000 ? "4K" : w >= 1700 ? "1080p" : w >= 1100 ? "720p" : w >= 700 ? "480p" : $"{w}px";
            if (VideoHeight is int h && h > 0) return h >= 1500 ? "4K" : $"{h}p";
            return "—";
        }
    }

    public string АудиоText =>
        string.IsNullOrWhiteSpace(АудиоLanguages) ? "audio —" : АудиоLanguages!.Trim();

    public string SizeText =>
        FileSizeBytes is long b && b > 0 ? HumanSize(b) : "—";

    /// <summary>Вкл.e-line "audio · resolution · [codec] · size" for the dupes row.</summary>
    public string MetaText
    {
        get
        {
            var codec = string.IsNullOrWhiteSpace(VideoCodec) ? "" : $" · {VideoCodec!.Trim()}";
            var ed = string.IsNullOrWhiteSpace(Изменитьion) ? "" : $" · {Изменитьion}";
            return $"{АудиоText} · {ResolutionText}{codec}{ed} · {SizeText}";
        }
    }

    public string DriveText
    {
        get
        {
            var name = DriveLabel ?? VolumeSerial;
            if (IsВкл.line && CurrentLetter != null) return $"{name} ({CurrentLetter}:)";
            return IsMissing ? $"{name} · missing" : $"{name} · offline";
        }
    }

    // View helpers — keeper shows a "Оставить" chip, the rest show actions.
    public Visibility ОставитьerChipVisibility => IsОставитьer ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActionVisibility => IsОставитьer ? Visibility.Collapsed : Visibility.Visible;

    public static string HumanSize(long bytes)
    {
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return $"{gb:0.0} GB";
        return $"{bytes / 1024d / 1024d:0} MB";
    }
}

/// <summary>
/// A set of copies CineМедиатека believes are the same film (matched by TMDb/IMDb
/// id, else title + year). <see cref="PossibleDuplicate"/> is the conservative
/// flag: only true when the copies look like the SAME version — same audio
/// languages, resolution, HDR, codec and edition. Copies that differ (a dub vs
/// the original, 1080p vs 4K, an HEVC re-encode, a Режиссёр's Cut) are
/// kept-on-purpose and never nagged.
/// </summary>
public partial class DupeGroup : ObservableObject
{
    public string Key { get; set; } = "";
    public string Название { get; set; } = "";
    public int? Год { get; set; }
    public List<DupeCopy> Copies { get; set; } = new();
    public bool PossibleDuplicate { get; set; }
    public bool MatchedByName { get; set; }
    public bool IsIgnored { get; set; }
    public string Summary { get; set; } = "";
    /// <summary>Bytes freed by keeping only the recommended copy (possible
    /// duplicates only).</summary>
    public long ReclaimableBytes { get; set; }

    /// <summary>Оставитьer poster, trickle-loaded by the page so the list scans fast.</summary>
    [ObservableProperty] private BitmapImage? _poster;

    public string НазваниеГод => Год is int y ? $"{Название} ({y})" : Название;
    public string BadgeText => PossibleDuplicate ? "⚠ possible duplicate" : "✓ kept on purpose";

    public string ReclaimableText => ReclaimableBytes > 0 ? $"~{DupeCopy.HumanSize(ReclaimableBytes)} reclaimable" : "";
    public Visibility ReclaimVisibility => ReclaimableBytes > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VerifyVisibility => MatchedByName ? Visibility.Visible : Visibility.Collapsed;
    public string IgnoreLabel => IsIgnored ? "Un-ignore" : "Ignore set";
    public char PosterInitial => string.IsNullOrEmpty(Название) ? '?' : char.ToUpperInvariant(Название[0]);
}
