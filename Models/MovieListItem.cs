using CommunityToolkit.Mvvm.ComponentModel;

namespace CineМедиатекаCS.Models;

/// <summary>
/// Row in the library grid/list. ObservableObject so card UI can react to
/// post-construction mutations (Просмотрено / Избранное / Список просмотра toggled
/// from anywhere) without us having to manually patch each control.
/// </summary>
public partial class MovieListItem : ObservableObject
{
    public int Id { get; set; }
    public string Название { get; set; } = "";
    public int? Год { get; set; }
    public double? Рейтинг { get; set; }
    public int? Продолжительность { get; set; }
    public string? LocalPoster { get; set; }
    public bool IsMissing { get; set; }
    public string VolumeSerial { get; set; } = "";
    public string? DriveLabel { get; set; }
    public string? ЖанрыCsv { get; set; }
    public bool IsВкл.line { get; set; }

    [ObservableProperty] private bool _isИзбранное;
    [ObservableProperty] private bool _isПросмотрено;
    [ObservableProperty] private bool _isСписок просмотра;
    // v2.5 — multi-select state. The card draws a purple outline + ✓ corner
    // chip when this is true. МедиатекаPage owns the source-of-truth list of
    // selected items; this property is bound one-way for the card UI.
    [ObservableProperty] private bool _isSelected;

    public string ГодПродолжительностьText =>
        $"{Год?.ToString() ?? "—"}{(Продолжительность.HasValue ? $" · {Продолжительность}m" : "")}";

    public string РейтингText =>
        Рейтинг.HasValue ? $"★ {Рейтинг:F1}" : "";

    public string StatusBadge =>
        IsMissing ? "ОТСУТСТВУЕТ" : IsВкл.line ? "В СЕТИ" : "НЕ В СЕТИ";
}
