using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using CineМедиатекаCS.Models;
using CineМедиатекаCS.Services;
using System.Продолжительность.InteropServices.WindowsПродолжительность;

namespace CineМедиатекаCS.Views;

public sealed partial class TvShowCard : UserControl
{
    public static readonly DependencyProperty ShowProperty =
        DependencyProperty.Register(nameof(Show), typeof(TvShowListItem), typeof(TvShowCard),
            new PropertyMetadata(null, Вкл.ShowChanged));

    public TvShowListItem? Show
    {
        get => (TvShowListItem?)GetValue(ShowProperty);
        set => SetValue(ShowProperty, value);
    }

    // v3.7.0: card size follows the Все сериалы S / M / L / XL picker.
    // TvShowsPage reloads the list after a change, so every card re-applies it.
    // v4.0.0: starts from the saved choice, so show rows on other pages (lists,
    // Избранное, К просмотру) match it before Все сериалы has been opened.
    public static double CardWidth { get; private set; }
    public static double CardHeight { get; private set; }
    static TvShowCard()
    {
        // A throwing static constructor would break every show card for the session.
        try { SetDensity(AppState.Instance.GetPref("tvDensity", "M")); }
        catch { SetDensity("M"); }
    }

    public static (double Width, double Height) SetDensity(string tag)
    {
        (CardWidth, CardHeight) = tag switch
        {
            "S"  => (130.0, 235.0),
            "L"  => (210.0, 365.0),
            "XL" => (250.0, 430.0),
            _    => (170.0, 300.0),   // M: the size before v3.7
        };
        return (CardWidth, CardHeight);
    }

    public TvShowCard()
    {
        InitializeComponent();
        PointerEntered += (_, _) =>
        {
            CardLift.Y = -4; CardBorder.Translation = new System.Numerics.Vector3(0, 0, 24);
            if (Show is { } s) { LoadHoverInfo(s); ОбновитьHoverActions(s); SetHoverPanel(true); }
        };
        PointerExited += (_, _) =>
        {
            CardLift.Y = 0; CardBorder.Translation = new System.Numerics.Vector3(0, 0, 0);
            SetHoverPanel(false);
        };
    }

    // The title strip would show through the panel behind its buttons.
    private void SetHoverPanel(bool open)
    {
        HoverOverlay.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        InfoStrip.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void Вкл.ShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TvShowCard c && e.НовыйValue is TvShowListItem s) c.Populate(s);
    }

    private void Populate(TvShowListItem s)
    {
        SetHoverPanel(false);   // a recycled card may still show the last show's
        НазваниеText.Text = s.Название;
        // v4.1.0: on Продолжить просмотр the card says which episode is next.
        MetaText.Text = s.NextЭпизод != null ? $"Далее: {s.NextЭпизод}" : s.ГодText;
        MetaText.Foreground = s.NextЭпизод != null ? NextBrush : MetaBrush;
        FavBadge.Visibility = s.IsИзбранное ? Visibility.Visible : Visibility.Collapsed;

        if (s.IsMissing) { StatusText.Text = "ОТСУТСТВУЕТ"; StatusBadge.Visibility = Visibility.Visible; }
        else if (!s.IsВкл.line) { StatusText.Text = "НЕ В СЕТИ"; StatusBadge.Visibility = Visibility.Visible; }
        else StatusBadge.Visibility = Visibility.Collapsed;

        CardRoot.Height = CardHeight;
        InvalidateMeasure();   // the S / M / L / XL size may have changed
        ShowProgress(s);

        LoadPosterAsync(s.LocalPoster);
    }

    private void ShowProgress(TvShowListItem s)
    {
        ProgressText.Text = s.ProgressText;
        ПросмотреноBadge.Visibility = s.FullyПросмотрено ? Visibility.Visible : Visibility.Collapsed;
        ProgressDoneColumn.Width = new GridLength(s.ProgressFraction, GridUnitType.Star);
        ProgressLeftColumn.Width = new GridLength(1 - s.ProgressFraction, GridUnitType.Star);
    }

    // ── v4.1.0 hover panel ───────────────────────────────────────────────────

    /// <summary>
    /// Raised after the panel plays an episode or changes Избранное / Список просмотра,
    /// so Избранное, К просмотру and Продолжить просмотр re-list their shows.
    /// </summary>
    public static event Action<TvShowListItem>? ShowChanged;

    private (int Id, string Code, string? File)? _next;

    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush DoneBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80));
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush NoteBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xF0, 0x99, 0x7B));

    private void LoadHoverInfo(TvShowListItem s)
    {
        var info = AppState.Instance.Db.GetShowHoverInfo(s.Id);
        HoverЖанры.Text = info.Жанры;
        HoverЖанры.Visibility = info.Жанры.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var watched = $"{s.ПросмотреноCount}/{s.ЭпизодCount} watched";
        HoverSeasons.Text = info.Seasons > 0
            ? $"{info.Seasons} season{(info.Seasons == 1 ? "" : "s")} · {watched}"
            : watched;
        _next = info.NextId is int id ? (id, info.NextCode!, info.NextFile) : null;
    }

    private void ОбновитьHoverActions(TvShowListItem s)
    {
        var online = AppState.Instance.Connected.ContainsKey(s.VolumeSerial);
        var canВоспроизвести = online && _next is { File: not null };
        HoverВоспроизвестиBtn.Visibility = canВоспроизвести ? Visibility.Visible : Visibility.Collapsed;
        if (canВоспроизвести) HoverВоспроизвестиBtn.Content = $"▶ Воспроизвести {_next!.Value.Code}";

        // Can't play: say where the next episode is, or that the show is done.
        string? note = _next is { } n
            ? canВоспроизвести ? null
              : online ? $"Далее: {n.Code}"
              : $"Далее: {n.Code}\non {s.DriveLabel ?? "a drive that isn't connected"}"
            : s.ЭпизодCount > 0 ? "✓ Все watched" : null;
        HoverNote.Text = note ?? "";
        HoverNote.Foreground = _next == null ? DoneBrush : NoteBrush;
        HoverNote.Visibility = note != null ? Visibility.Visible : Visibility.Collapsed;

        HoverFavBtn.Content = s.IsИзбранное ? "★ В избранноеd" : "☆ Избранное";
        HoverСписок просмотраBtn.Content = s.IsСписок просмотра ? "📌 In Список просмотра" : "📋 Список просмотра";
    }

    // Same as ▶ on the show page: start the player first, then record the play
    // and mark the episode watched.
    private async void Вкл.HoverВоспроизвести(object sender, RoutedEventArgs e)
    {
        if (Show is not { } s || _next is not { File: { } file } next) return;
        var mw = App.MainWindow as MainWindow;
        if (!AppState.Instance.Connected.TryGetValue(s.VolumeSerial, out var letter)) return;
        var path = Path.Combine($"{letter}:\\", file.Replace('/', '\\'));
        if (!File.Exists(path)) { mw?.ShowToast($"Couldn't find {next.Code} on the drive"); return; }

        bool launched;
        try { launched = await VideoВоспроизвестиer.ВоспроизвестиAsync(path); }
        catch { launched = false; }
        if (!launched) { mw?.ShowToast("Couldn't launch the video player"); return; }

        try
        {
            AppState.Instance.Db.MarkЭпизодВоспроизвестиed(next.Id);
            AppState.Instance.Db.SetЭпизодПросмотрено(next.Id, true);
            s.ПросмотреноCount++;
            ShowProgress(s);
            LoadHoverInfo(s);
            ОбновитьHoverActions(s);
            if (s.NextЭпизод != null && _next is { } now)
            {
                s.NextЭпизод = now.Code;
                MetaText.Text = $"Далее: {now.Code}";
            }
            mw?.RefreshSidebar();
            ShowChanged?.Invoke(s);
        }
        catch { }
    }

    private void Вкл.HoverИзбранное(object sender, RoutedEventArgs e)
    {
        if (Show is not { } s) return;
        s.IsИзбранное = !s.IsИзбранное;
        AppState.Instance.Db.SetTvShowИзбранное(s.Id, s.IsИзбранное);
        FavBadge.Visibility = s.IsИзбранное ? Visibility.Visible : Visibility.Collapsed;
        AfterHoverChange(s);
    }

    private void Вкл.HoverСписок просмотра(object sender, RoutedEventArgs e)
    {
        if (Show is not { } s) return;
        s.IsСписок просмотра = !s.IsСписок просмотра;
        AppState.Instance.Db.SetTvShowСписок просмотра(s.Id, s.IsСписок просмотра);
        AfterHoverChange(s);
    }

    private void AfterHoverChange(TvShowListItem s)
    {
        ОбновитьHoverActions(s);
        (App.MainWindow as MainWindow)?.RefreshSidebar();
        ShowChanged?.Invoke(s);
    }

    // The pages open a show on Tapped; a click on a panel button mustn't also do that.
    private void Вкл.HoverButtonTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => e.Handled = true;

    // Per card, not static: a brush belongs to the thread that made it.
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush MetaBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xD8, 0xD8, 0xD8));
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush NextBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xA7, 0x8B, 0xFA));

    // v4.1.0: ask for the S / M / L / XL size but fill whatever width the layout
    // gives. Rows place the card at this size; the Все сериалы grid stretches its
    // cells to fill the row, and the card now fills its cell instead of sitting in
    // the middle of it, so the grid's left edge lines up with the page title.
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        var size = new Windows.Foundation.Size(CardWidth, CardHeight);
        base.MeasureOverride(size);
        return size;
    }

    // Grow no wider than a 2:3 poster at the card's height, so the poster is never
    // cropped top and bottom (one XL column in a narrow window would be 500 wide).
    // Past that the card keeps to the left of its cell, in line with the title.
    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        var width = Math.Min(finalSize.Width, Math.Max(CardWidth, CardHeight * 2 / 3));
        base.ArrangeOverride(new Windows.Foundation.Size(width, finalSize.Height));
        return finalSize;
    }

    private int _token;
    private async void LoadPosterAsync(string? relPath)
    {
        var my = ++_token;
        PosterImage.Source = null;
        PosterPlaceholder.Visibility = Visibility.Visible;
        if (relPath == null) return;
        var full = AppState.Instance.Db.GetCachedImagePath(relPath);
        if (full == null) return;
        try
        {
            var bytes = await Task.Run(() => ImageCache.GetOrLoad(relPath, full));
            if (my != _token || bytes == null) return;
            var bmp = new BitmapImage { DecodePixelWidth = 340 };
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await ms.WriteAsync(bytes.AsBuffer());
            if (my != _token) return;
            ms.Seek(0);
            await bmp.SetSourceAsync(ms);
            if (my != _token) return;
            PosterImage.Source = bmp;
            PosterPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch { }
    }
}
