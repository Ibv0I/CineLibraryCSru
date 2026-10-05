using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using CineLibraryCS.Models;
using CineLibraryCS.Services;
using System.Runtime.InteropServices.WindowsRuntime;

namespace CineLibraryCS.Views;

public sealed partial class TvShowCard : UserControl
{
    public static readonly DependencyProperty ShowProperty =
        DependencyProperty.Register(nameof(Show), typeof(TvShowListItem), typeof(TvShowCard),
            new PropertyMetadata(null, OnShowChanged));

    public TvShowListItem? Show
    {
        get => (TvShowListItem?)GetValue(ShowProperty);
        set => SetValue(ShowProperty, value);
    }

    // v3.7.0: card size follows the All TV shows S / M / L / XL picker.
    // TvShowsPage reloads the list after a change, so every card re-applies it.
    // v4.0.0: starts from the saved choice, so show rows on other pages (lists,
    // Favorites, To Watch) match it before All TV shows has been opened.
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
        PointerEntered += (_, _) => { CardLift.Y = -4; CardBorder.Translation = new System.Numerics.Vector3(0, 0, 24); };
        PointerExited  += (_, _) => { CardLift.Y = 0;  CardBorder.Translation = new System.Numerics.Vector3(0, 0, 0); };
    }

    private static void OnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TvShowCard c && e.NewValue is TvShowListItem s) c.Populate(s);
    }

    private void Populate(TvShowListItem s)
    {
        TitleText.Text = s.Title;
        MetaText.Text = s.YearText;
        ProgressText.Text = s.ProgressText;
        FavBadge.Visibility = s.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
        WatchedBadge.Visibility = s.FullyWatched ? Visibility.Visible : Visibility.Collapsed;

        if (s.IsMissing) { StatusText.Text = "MISSING"; StatusBadge.Visibility = Visibility.Visible; }
        else if (!s.IsOnline) { StatusText.Text = "OFFLINE"; StatusBadge.Visibility = Visibility.Visible; }
        else StatusBadge.Visibility = Visibility.Collapsed;

        CardRoot.Width = CardWidth;
        CardRoot.Height = CardHeight;
        // Progress bar fill: card width minus the info strip's padding.
        ProgressFill.Width = Math.Max(0, (CardWidth - 20) * s.ProgressFraction);

        LoadPosterAsync(s.LocalPoster);
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
