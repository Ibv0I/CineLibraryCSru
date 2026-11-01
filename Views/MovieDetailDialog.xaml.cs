using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using CineLibraryCS.Models;
using CineLibraryCS.Services;
using CineLibraryCS.Services.Tmdb;
using Windows.Graphics;
using Windows.System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Xml;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CineLibraryCS.Views;

// A resizable Window-based detail view (replaces the old fixed ContentDialog).
public sealed partial class MovieDetailDialog : Window
{
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    private int _movieId;
    private MovieDetail? _movie;
    private bool _closed;
    private readonly bool _replaceMainWindow;
    private int _kinopoiskId;

    // v4.5.0: the movies of the grid this window was opened from, in order, so
    // ← and → step through them in place. _browseIndex is the last of them shown.
    private IReadOnlyList<int>? _browseIds;
    private int _browseIndex = -1;
    // Each load of a movie takes a new token; an older load (or its pictures)
    // that finishes later stands down instead of showing the wrong movie.
    private int _loadToken;

    /// <summary>
    /// v4.5.0: set by a page whose grid loads in pages (All movies loads 60 at a
    /// time). Given the grid a window was opened from, it returns every movie of
    /// that view, or null when the grid is already whole or isn't its own.
    /// </summary>
    public static Func<System.Collections.IEnumerable, Task<List<int>>?>? WholeGrid { get; set; }

    public event EventHandler? WatchlistChanged;

    /// <param name="grid">The movies of the grid or list it was opened from, for ← and →.</param>
    public MovieDetailDialog(int movieId, System.Collections.IEnumerable? grid = null, bool replaceMainWindow = true)
    {
        _movieId = movieId;
        _replaceMainWindow = replaceMainWindow;
        var ids = grid?.OfType<MovieListItem>().Select(x => x.Id).ToList();
        if (ids is { Count: > 1 } && IndexOf(ids, movieId) is var at and >= 0)
        {
            _browseIds = ids;
            _browseIndex = at;
            if (WholeGrid?.Invoke(grid!) is { } whole) _ = UseWholeGridAsync(whole);
        }
        InitializeComponent();
        RootGrid.Loaded += (_, _) => CineLibraryCS.Services.LocalizationService.TranslateTree(RootGrid);

        // Apply the app's chosen theme before the new window can render its first frame.
        // Otherwise Windows' light theme may briefly paint the detail window white.
        RootGrid.RequestedTheme = MainWindow.CurrentTheme;
        RootGrid.Background = new SolidColorBrush(
            MainWindow.CurrentTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(255, 250, 250, 252)
                : Windows.UI.Color.FromArgb(255, 10, 10, 12));

        // Custom titlebar drag region + Mica
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();

        // Window size — restore the last user-resized size if we have one,
        // otherwise fall back to a comfortable default (1100×800). Saved on
        // SizeChanged into prefs so resize sticks across sessions.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        int width = 1100, height = 800;
        var savedW = AppState.Instance.GetPref("detailDialogW", "");
        var savedH = AppState.Instance.GetPref("detailDialogH", "");
        if (int.TryParse(savedW, out var w) && w >= 700) width = w;
        if (int.TryParse(savedH, out var h) && h >= 500) height = h;
        appWindow.Resize(new SizeInt32(width, height));
        appWindow.Title = CineLibraryCS.Services.LocalizationService.TranslateText("Подробности фильма");

        // Ensure window is resizable and maximizable, then open maximized
        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.Maximize();
        }

        // Browser-style back mouse button returns to the library.
        RootGrid.PointerPressed += (_, e) =>
        {
            try
            {
                var props = e.GetCurrentPoint(RootGrid).Properties;
                if (props.IsXButton1Pressed)
                {
                    e.Handled = true;
                    Close();
                }
            }
            catch { }
        };

        // Esc closes the dialog
        var escAcc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.Escape
        };
        escAcc.Invoked += (_, a) => { a.Handled = true; Close(); };
        RootGrid.KeyboardAccelerators.Add(escAcc);
        // No floating "Esc" key tip following the pointer (same as MainWindow).
        RootGrid.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        // v4.5.0: ← / → for the previous / next movie (not while typing a note or tag)
        RootGrid.AddHandler(UIElement.KeyDownEvent,
            new Microsoft.UI.Xaml.Input.KeyEventHandler(OnRootKeyDown), handledEventsToo: true);

        // Persist size on close — using AppWindow.Changed catches the final
        // resized state regardless of how the window was dismissed.
        appWindow.Changed += (s, args) =>
        {
            if (!args.DidSizeChange) return;
            try
            {
                AppState.Instance.SetPref("detailDialogW", s.Size.Width.ToString());
                AppState.Instance.SetPref("detailDialogH", s.Size.Height.ToString());
            }
            catch { }
        };

        // Track close so async load that finishes after Esc doesn't try to
        // touch the destroyed window (COMException "operation identifier is
        // not valid").
        // Present details as a replacement for the library window: hide the
        // main window while details are open, then restore it on any close path.
        var mainWindow = App.MainWindow;
        nint mainWindowHandle = 0;
        try
        {
            if (_replaceMainWindow && mainWindow != null)
            {
                mainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                if (mainWindowHandle != 0)
                    ShowWindow(mainWindowHandle, SW_HIDE);
            }
        }
        catch { }
        Closed += (_, _) =>
        {
            _closed = true;
            try
            {
                if (_replaceMainWindow && mainWindowHandle != 0)
                {
                    ShowWindow(mainWindowHandle, SW_SHOW);
                    mainWindow?.Activate();
                }
            }
            catch { }
        };

        // Start DB load IMMEDIATELY (don't wait for Activated). Overlapping
        // with the window animation eliminates the visible "empty window"
        // delay that made the dialog feel slow.
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var token = ++_loadToken;
        var id = _movieId;
        var (movie, collection, byDirector) = await Task.Run(() =>
        {
            var db = AppState.Instance.Db;
            var mv = db.GetMovieDetail(id, AppState.Instance.Connected);
            var dir = mv?.Directors.FirstOrDefault();
            return (mv, db.GetCollectionStrip(id), dir == null ? new List<RelatedMovie>() : db.GetMoviesByDirector(dir, id));
        });

        if (movie == null || _closed || token != _loadToken) return;
        _movie = movie;
        try
        {
            PopulateUi(_movie);
            PopulateRelated(_movie, collection, byDirector);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Window was closed mid-populate — silently bail.
        }
    }

    private void PopulateUi(MovieDetail m)
    {
        UpdateBrowseNav();
        Title = m.Title;
        TitleBarText.Text = m.Title;
        DetailTitle.Text = m.Title;

        if (m.OriginalTitle != null && m.OriginalTitle != m.Title)
        {
            OriginalTitle.Text = m.OriginalTitle;
            OriginalTitle.Visibility = Visibility.Visible;
        }
        if (m.Tagline != null)
        {
            Tagline.Text = m.Tagline;
            TaglineWrap.Visibility = Visibility.Visible;
        }

        // Sticky bar title (v2.3) — shown when scrolled past hero
        StickyTitle.Text = m.Title;

        // External source links are displayed beside their corresponding ratings.
        _kinopoiskId = ReadKinopoiskIdFromNfo(m);

        // Chips
        ChipsPanel.Children.Clear();
        void AddChip(string text, string? bg = null)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(bg != null
                    ? Windows.UI.Color.FromArgb(0xFF,
                        Convert.ToByte(bg[1..3], 16), Convert.ToByte(bg[3..5], 16), Convert.ToByte(bg[5..7], 16))
                    : Windows.UI.Color.FromArgb(0xCC, 0x1E, 0x1E, 0x2E)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
            };
            border.Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0))
            };
            ChipsPanel.Children.Add(border);
        }

        if (m.IsMissing) AddChip(CineLibraryCS.Services.LocalizationService.TranslateText("MISSING"), "#3A1010");

        // Drive
        DriveStatusDot.Fill = new SolidColorBrush(m.IsOnline
            ? Windows.UI.Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E)
            : Windows.UI.Color.FromArgb(0xFF, 0x6B, 0x72, 0x80));
        DriveText.Text = m.DriveLabel + (m.CurrentLetter != null ? $" ({m.CurrentLetter}:)" : "");

        // Actions
        PlayBtn.IsEnabled = m.Playable;
        FolderBtn.IsEnabled = m.IsOnline;
        FavBtn.Content = m.IsFavorite ? "♥ В избранном" : "♡ В избранное";
        WatchedBtn.Content = m.IsWatched ? "✓ Просмотрен" : "○ Отметить просмотренным";
        WatchlistBtn.Content = m.IsWatchlist ? CineLibraryCS.Services.LocalizationService.TranslateText("📌 В списке просмотра") : CineLibraryCS.Services.LocalizationService.TranslateText("☐ Добавить в список просмотра");


        // Notes
        UpdateNoteUi(viewing: true);

        // Plot (fall back to outline — many MediaElch NFOs use <outline> only)
        var plotText = m.Plot ?? m.Outline;
        if (!string.IsNullOrWhiteSpace(plotText))
        {
            PlotText.Text = plotText;
            PlotSection.Visibility = Visibility.Visible;
            PlotDivider.Visibility = Visibility.Visible;
        }

        // Genres — clickable, filters library on click. v4.5.0 (#24): chips right
        // under the rating / year / runtime line, where TMDb shows them too.
        GenreChips.Children.Clear();
        GenreChips.Visibility = m.Genres.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in m.Genres.Take(3))
        {
            var captured = g;
            var chip = new Button
            {
                Content = g,
                FontSize = 12,
                MinHeight = 0,
                Padding = new Thickness(10, 3, 10, 3),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xA7, 0x8B, 0xFA)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x80, 0xA7, 0x8B, 0xFA)),
            };
            chip.Click += (_, _) => NavigateAndClose(mw => mw.NavigateLibraryByGenre(captured));
            GenreChips.Children.Add(chip);
        }

        // Directors — clickable
        if (m.Directors.Count > 0)
        {
            DirectorField.Visibility = Visibility.Visible;
            DirectorLinks.Children.Clear();
            foreach (var d in m.Directors)
            {
                var captured = d;
                var btn = new HyperlinkButton { Content = d, Padding = new Thickness(0), FontSize = 13 };
                btn.Click += (_, _) => NavigateAndClose(mw => mw.NavigateLibraryByDirector(captured));
                DirectorLinks.Children.Add(btn);
            }
        }

        // v4.5.0 (#24): writers, the first four; all of them on hover when there are more
        WriterField.Visibility = Visibility.Collapsed;

        // Tech badges + ratings + file info (v2.2)
        PopulateTechBadges(m);
        PopulateRatingsPanel(m);
        PopulateFileInfo(m);
        PopulateFinanceAwards(m);
        // Lists this movie is on (v2.5) — chip row with per-list ✕.
        RefreshListChips();
        // v2.9 — Free-form tags chip row + autocomplete.
        RefreshTagChips();

        // Studio — clickable HyperlinkButton (was a plain TextBlock)
        if (m.Studio != null)
        {
            StudioLabel.Visibility = Visibility.Visible;
            StudioText.Visibility = Visibility.Collapsed;
            StudioLink.Visibility = Visibility.Visible;
            // NFO may contain multiple studios; show only the first one below the poster.
            var firstStudio = m.Studio.Split(new[] { ',', ';', '/', '|', '\n', '\r' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            StudioLink.Content = firstStudio ?? m.Studio;
            // Detach prior handler in case dialog is reused
            StudioLink.Click -= OnStudioClick;
            StudioLink.Click += OnStudioClick;
        }

        // Cast — show the list right away; trickle thumbnail loads in
        // batches so the first paint isn't blocked by many parallel image
        // resolves. Pass the absolute movie folder so .actors/ thumbs are
        // discovered for online drives.
        if (m.Actors.Count > 0)
        {
            CastSection.Visibility = Visibility.Visible;
            CastDivider.Visibility = Visibility.Visible;
            // v4.4.1: the whole cast now; a very big one (up to a few hundred)
            // shows its first 24 until Show all, so the page and the photo
            // loading stay reasonable.
            var shown = m.Actors.Count > CastPreview ? m.Actors.Take(CastPreview).ToList() : m.Actors;
            CastRepeater.ItemsSource = shown;
            FitCastCardHeight(m.Actors);   // all of them, so Show all keeps the card height
            CastShowAllBtn.Content = $"{CineLibraryCS.Services.LocalizationService.TranslateText("Show all")} {m.Actors.Count}";
            CastShowAllBtn.Visibility = shown.Count < m.Actors.Count ? Visibility.Visible : Visibility.Collapsed;
            string? movieFolderAbs = null;
            if (m.IsOnline && m.CurrentLetter != null && m.FolderRelPath != null)
                movieFolderAbs = Path.Combine($"{m.CurrentLetter}:\\",
                    m.FolderRelPath.Replace('/', '\\'));
            _castFolderAbs = movieFolderAbs;
            _ = LoadCastThumbsAsync(shown, movieFolderAbs);
        }

        // Images
        // v4.2.0: decoded for the bigger poster and the full-width banner (the
        // window opens maximised, so its width is about the banner's in pixels)
        LoadImageAsync(m.LocalPoster, PosterImage, PosterPlaceholder, 520);
        LoadImageAsync(m.LocalFanart, HeroImage, null, Math.Clamp(Microsoft.UI.Windowing.AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))).Size.Width, 1920, 3840));

        // v3.4 — "Fetch missing info from TMDB" (and, later, Sync to drive).
    }

    // ── v3.4 TMDB enrichment ──────────────────────────────────────────────────

    /// <summary>Downloads original-size art into a data-folder subfolder named by
    /// tmdb id; returns the data-relative path, or null on failure.</summary>
    internal static async Task<string?> DownloadArtAsync(
        Services.Tmdb.TmdbClient client, string tmdbPath, string subfolder, int tmdbId)
    {
        var fileName = $"{tmdbId}-{Guid.NewGuid():N}.jpg";
        var full = Path.Combine(AppState.Instance.DataDir, subfolder, fileName);
        return await client.DownloadImageAsync(client.GetImageUrl(tmdbPath, "original"), full)
            ? $"{subfolder}/{fileName}" : null;
    }

    /// <summary>Downloads art keyed by its TMDb file name (used for actor photos);
    /// returns the data-relative path, or null on failure.</summary>
    internal static async Task<string?> DownloadArtRawAsync(
        Services.Tmdb.TmdbClient client, string tmdbPath, string subfolder, string size)
    {
        var fileName = tmdbPath.TrimStart('/');
        var full = Path.Combine(AppState.Instance.DataDir, subfolder, fileName);
        return await client.DownloadImageAsync(client.GetImageUrl(tmdbPath, size), full)
            ? $"{subfolder}/{fileName}" : null;
    }

    /// <summary>
    /// v3.8.0: the cast grid gives every card the same height, so size it for
    /// the longest-wrapping name and role, measured with the user's text size.
    /// A fixed 280 cut off the role under a two-line name.
    /// </summary>
    private void FitCastCardHeight(IReadOnlyList<Models.Actor> actors)
    {
        var font = (FontFamily)Application.Current.Resources["ContentControlThemeFontFamily"];
        double TextHeight(string? text, double size, Windows.UI.Text.FontWeight weight)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var probe = new TextBlock
            {
                Text = text, FontFamily = font, FontSize = size, FontWeight = weight,
                TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 2,
            };
            probe.Measure(new Windows.Foundation.Size(140, double.PositiveInfinity));
            return probe.DesiredSize.Height;
        }
        var name = actors.Max(a => TextHeight(a.Name, 13, Microsoft.UI.Text.FontWeights.SemiBold));
        var role = actors.Max(a => TextHeight(a.Role, 11, Microsoft.UI.Text.FontWeights.Normal));
        // 210 headshot + two 8 px gaps, plus a little slack for rounding.
        CastGridLayout.MinItemHeight = Math.Max(280, Math.Ceiling(210 + 8 + name + 8 + role + 4));
    }

    private static readonly string[] ActorThumbExts = { ".jpg", ".jpeg", ".png", ".tbn", ".webp" };

    /// <summary>
    /// Resolves an actor's thumbnail in priority order:
    ///   1. The movie folder's `.actors/` folder — MediaElch's standard
    ///      cache, named `First_Last.jpg` (underscores for spaces).
    ///   2. Inline `<thumb>` URL from the nfo (TMDb http URL or local path).
    /// Falls back to the initials TextBlock behind the Image when both miss.
    /// </summary>
    private static void LoadActorThumb(Models.Actor a, string? movieFolderAbs)
    {
        try
        {
            // 1) Local .actors folder
            if (!string.IsNullOrEmpty(movieFolderAbs))
            {
                var actorsDir = Path.Combine(movieFolderAbs, ".actors");
                if (Directory.Exists(actorsDir))
                {
                    var candidates = new[]
                    {
                        a.Name.Replace(' ', '_'),
                        a.Name,
                    };
                    foreach (var stem in candidates)
                    {
                        foreach (var ext in ActorThumbExts)
                        {
                            var p = Path.Combine(actorsDir, stem + ext);
                            if (File.Exists(p)) { ApplyBitmap(a, SafeFileUri(p)); return; }
                        }
                    }
                }
            }

            // 2) Inline thumb — an http URL, an absolute file path, or (for
            //    manual Watched & Gone records) a path relative to the portable
            //    data folder, resolved the same way posters are.
            if (!string.IsNullOrWhiteSpace(a.Thumb))
            {
                var raw = a.Thumb!.Trim();
                Uri? uri = null;
                if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    uri = new Uri(raw);
                else if (File.Exists(raw))
                    uri = SafeFileUri(raw);
                else
                {
                    var cached = Services.AppState.Instance.Db.GetCachedImagePath(raw);
                    if (cached != null) uri = SafeFileUri(cached);
                }
                if (uri != null) ApplyBitmap(a, uri);
            }
        }
        catch { }
    }

    /// <summary>
    /// Build a file:// Uri that won't get its path lopped off by '#' or
    /// '?' in the file name. BitmapImage.UriSource treats those as URL
    /// fragment / query delimiters, so a poster called "Wall·E #1.jpg"
    /// silently fails to load otherwise.
    /// </summary>
    private static Uri SafeFileUri(string absolutePath)
    {
        // new Uri() already percent-encodes '#', '?' and spaces correctly for a
        // Windows path. Pre-escaping them ourselves double-encoded the '%' (a
        // "#Bollywood Movies" folder became %2523Bollywood…), which broke every
        // actor photo whose path contained '#' or '?'. Let .NET do the encoding.
        return new Uri(absolutePath);
    }

    private static void ApplyBitmap(Models.Actor a, Uri uri)
    {
        // 280 px decode = ~2× the 140-wide rendered slot for HiDPI crispness
        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 280 };
        bmp.UriSource = uri;
        a.ThumbBitmap = bmp;
    }

    private const int CastPreview = 24;
    private string? _castFolderAbs;

    private void OnCastShowAll(object sender, RoutedEventArgs e)
    {
        if (_movie == null) return;
        CastRepeater.ItemsSource = _movie.Actors;
        CastShowAllBtn.Visibility = Visibility.Collapsed;
        _ = LoadCastThumbsAsync(_movie.Actors.Skip(CastPreview).ToList(), _castFolderAbs);
    }

    /// <summary>
    /// Trickle-loads actor thumbnails in small batches with a yield between
    /// each batch. Prevents many parallel image loads from blocking the
    /// first paint of the dialog.
    /// </summary>
    private async Task LoadCastThumbsAsync(IReadOnlyList<Models.Actor> actors, string? movieFolderAbs)
    {
        const int batchSize = 6;
        for (int i = 0; i < actors.Count; i++)
        {
            if (_closed) return;
            LoadActorThumb(actors[i], movieFolderAbs);
            if ((i + 1) % batchSize == 0)
                await Task.Delay(30);
        }
    }

    private async void LoadImageAsync(string? relPath, Image imgControl, FrameworkElement? placeholder, int decodeWidth)
    {
        var token = _loadToken;
        var bmp = await DecodeCachedAsync(relPath, decodeWidth);
        if (bmp == null || token != _loadToken) return;   // v4.5.0: or it's a movie ago
        imgControl.Source = bmp;
        if (placeholder != null) placeholder.Visibility = Visibility.Collapsed;
    }

    private static async Task<BitmapImage?> DecodeCachedAsync(string? relPath, int decodeWidth)
    {
        if (relPath == null) return null;
        var fullPath = AppState.Instance.Db.GetCachedImagePath(relPath);
        if (fullPath == null) return null;
        try
        {
            var bytes = await Task.Run(() => ImageCache.GetOrLoad(relPath!, fullPath));
            if (bytes == null) return null;
            var bmp = new BitmapImage { DecodePixelWidth = decodeWidth };
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await ms.WriteAsync(bytes.AsBuffer());
            ms.Seek(0);
            await bmp.SetSourceAsync(ms);
            return bmp;
        }
        catch { return null; }
    }

    // ── v4.5.0: previous / next, and the films under the cast ──────────────

    private static int IndexOf(IReadOnlyList<int> ids, int id)
    {
        for (int i = 0; i < ids.Count; i++) if (ids[i] == id) return i;
        return -1;
    }

    // The grid's other pages arrive a moment after the window opens.
    private async Task UseWholeGridAsync(Task<List<int>> whole)
    {
        List<int> all;
        try { all = await whole; }
        catch { return; }
        if (_closed || _browseIds == null) return;
        var at = IndexOf(all, _browseIds[_browseIndex]);
        if (at < 0) return;
        _browseIds = all;
        _browseIndex = at;
        if (_movie != null) UpdateBrowseNav();
    }

    private void OnPrevMovie(object sender, RoutedEventArgs e) => StepMovie(-1);
    private void OnNextMovie(object sender, RoutedEventArgs e) => StepMovie(+1);

    private void OnRootKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
        if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot)
            is TextBox or RichEditBox or AutoSuggestBox or ComboBox) return;
        e.Handled = true;
        StepMovie(e.Key == VirtualKey.Left ? -1 : +1);
    }

    private void StepMovie(int step)
    {
        if (_browseIds == null) return;
        // Away from the grid (a film picked from a strip below), → goes on from
        // the last grid movie shown and ← goes back to it.
        var at = IndexOf(_browseIds, _movieId);
        var next = at >= 0 ? at + step : step > 0 ? _browseIndex + 1 : _browseIndex;
        if (next < 0 || next >= _browseIds.Count) return;
        ShowMovie(_browseIds[next]);
    }

    /// <summary>Shows another movie in this window.</summary>
    private void ShowMovie(int movieId)
    {
        if (movieId == _movieId) return;
        _movieId = movieId;
        if (_browseIds != null && IndexOf(_browseIds, movieId) is var at and >= 0) _browseIndex = at;
        ResetForAnotherMovie();
        UpdateBrowseNav();
        _ = LoadAsync();
    }

    private void UpdateBrowseNav()
    {
        // Banner navigation and its counter are intentionally removed.
        StickyBrowseNav.Visibility = _browseIds != null ? Visibility.Visible : Visibility.Collapsed;
        if (_browseIds == null) return;
        var at = IndexOf(_browseIds, _movieId);
        bool canPrev = at >= 0 ? at > 0 : _browseIndex >= 0;
        bool canNext = (at >= 0 ? at : _browseIndex) + 1 < _browseIds.Count;
        StickyPrevBtn.IsEnabled = canPrev;
        StickyNextBtn.IsEnabled = canNext;
        StickyPrevBtn.Opacity = canPrev ? 1 : 0.4;
        StickyNextBtn.Opacity = canNext ? 1 : 0.4;
    }

    // Puts back everything PopulateUi only ever switches on, so nothing of the
    // last movie stays on screen when the next one hasn't got it.
    private void ResetForAnotherMovie()
    {
        _movie = null;
        _castFolderAbs = null;
        foreach (var e in new FrameworkElement[]
                 {
                     OriginalTitle, TaglineWrap, PlotSection, PlotDivider, DirectorField, WriterField,
                     StudioLabel, StudioText, StudioLink, CastSection, CastDivider, CastShowAllBtn,
                     CollectionSection, DirectorSection, FileInfoPanel, FinanceAwardsPanel, FiRuntimeBlock, FiReleaseBlock,
                     FiMpaaBlock, FiCountryBlock, FiSizeBlock, FiDurBlock, FiContainerBlock, FiLangBlock,
                     FiSubsBlock, AddTagBox,
                 })
            e.Visibility = Visibility.Collapsed;
        AddTagBtn.Visibility = Visibility.Visible;
        DirectorLinks.Children.Clear();
        ToolTipService.SetToolTip(WriterText, null);
        CastRepeater.ItemsSource = null;
        CollectionRepeater.ItemsSource = null;
        DirectorRepeater.ItemsSource = null;
        PosterImage.Source = null;
        PosterPlaceholder.Visibility = Visibility.Visible;
        HeroImage.Source = null;
        ContentScroller.ChangeView(null, 0, null, true);
    }

    private const int RelatedMax = 12;
    private string? _stripDirector;

    private void PopulateRelated(MovieDetail m, (string Name, List<RelatedMovie> Movies)? collection, List<RelatedMovie> byDirector)
    {
        var inCollection = new HashSet<int>();
        // The "Part of the ..." collection strip is intentionally hidden.
        CollectionSection.Visibility = Visibility.Collapsed;
        if (collection is { } c)
        {
            CollectionHeader.Text = c.Name.StartsWith("The ", StringComparison.OrdinalIgnoreCase)
                ? $"Part of {c.Name}" : $"Part of the {c.Name}";
            CollectionRepeater.ItemsSource = c.Movies;
            foreach (var x in c.Movies) inCollection.Add(x.Id);
            LoadRelatedPosters(c.Movies);
        }

        // Films already in the collection strip aren't repeated here
        var more = byDirector.Where(x => !inCollection.Contains(x.Id)).ToList();
        _stripDirector = m.Directors.FirstOrDefault();
        // Hide the "More by" director strip; keep the collection strip intact.
        DirectorSection.Visibility = Visibility.Collapsed;
        DirectorRepeater.ItemsSource = null;
    }

    private async void LoadRelatedPosters(IReadOnlyList<RelatedMovie> movies)
    {
        var token = _loadToken;
        foreach (var r in movies)
        {
            if (token != _loadToken || _closed) return;
            var bmp = await DecodeCachedAsync(r.LocalPoster, 240);
            if (bmp != null && token == _loadToken) r.PosterBitmap = bmp;
        }
    }

    private void OnRelatedClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int id }) ShowMovie(id);
    }

    private void OnDirectorSeeAll(object sender, RoutedEventArgs e)
    {
        if (_stripDirector is { } d) NavigateAndClose(mw => mw.NavigateLibraryByDirector(d));
    }

    // ── Actions ───────────────────────────────────────────────────────────

    private void OnStudioClick(object sender, RoutedEventArgs e)
    {
        if (_movie?.Studio == null) return;
        var s = _movie.Studio.Split(new[] { ',', ';', '/', '|', '\n', '\r' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? _movie.Studio;
        NavigateAndClose(mw => mw.NavigateLibraryByStudio(s));
    }

    private void OnActorClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string actor)
            NavigateAndClose(mw => mw.NavigateLibraryByActor(actor));
    }

    /// <summary>
    /// Apply a library filter via MainWindow then close this detail window.
    /// </summary>
    private void NavigateAndClose(Action<MainWindow> nav)
    {
        if (App.MainWindow is MainWindow mw) nav(mw);
        Close();
    }

    // ── Tech badges, ratings, trailer, file info (v2.2) ──────────────────────

    private void PopulateTechBadges(MovieDetail m)
    {
        TechBadges.Items.Clear();
        // Resolution
        var resLabel = ResolutionLabel(m.VideoWidth, m.VideoHeight);
        if (resLabel != null) AddTechBadge(resLabel, "#3B82F6"); // blue
        // Aspect ratio
        var aspLabel = NormalizeAspect(m.VideoAspect);
        if (aspLabel != null) AddTechBadge(aspLabel, "#475569"); // slate
        // Video codec
        if (!string.IsNullOrWhiteSpace(m.VideoCodec))
            AddTechBadge(m.VideoCodec.ToUpperInvariant(), "#2563EB"); // violet
        // HDR
        if (!string.IsNullOrWhiteSpace(m.HdrType) &&
            !m.HdrType.Equals("NONE", StringComparison.OrdinalIgnoreCase) &&
            !m.HdrType.Equals("None", StringComparison.OrdinalIgnoreCase))
            AddTechBadge(m.HdrType.ToUpperInvariant(), "#EAB308"); // amber
        // Audio codec + channels
        var aud = AudioLabel(m.AudioCodec, m.AudioChannels);
        if (aud != null) AddTechBadge(aud, "#10B981"); // emerald
    }

    private void AddTechBadge(string text, string hex)
    {
        // Gradient + thin top-highlight border for a polished embossed look
        var topColor = HexColor(hex);
        var bottomColor = DarkenColor(topColor, 0.85);
        var bg = new Microsoft.UI.Xaml.Media.LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
        };
        bg.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = topColor, Offset = 0 });
        bg.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = bottomColor, Offset = 1 });

        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = bg,
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 8, 3),
        };
        border.Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        TechBadges.Items.Add(border);
    }

    private static Windows.UI.Color HexColor(string hex)
    {
        if (hex.StartsWith("#")) hex = hex[1..];
        if (hex.Length == 6) hex = "FF" + hex;
        var a = Convert.ToByte(hex[..2], 16);
        var r = Convert.ToByte(hex.Substring(2, 2), 16);
        var g = Convert.ToByte(hex.Substring(4, 2), 16);
        var b = Convert.ToByte(hex.Substring(6, 2), 16);
        return Windows.UI.Color.FromArgb(a, r, g, b);
    }

    private static Windows.UI.Color DarkenColor(Windows.UI.Color c, double factor)
    {
        return Windows.UI.Color.FromArgb(c.A,
            (byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));
    }

    private static SolidColorBrush HexBrush(string hex)
    {
        if (hex.StartsWith("#")) hex = hex[1..];
        if (hex.Length == 6) hex = "FF" + hex;
        var a = Convert.ToByte(hex[..2], 16);
        var r = Convert.ToByte(hex.Substring(2, 2), 16);
        var g = Convert.ToByte(hex.Substring(4, 2), 16);
        var b = Convert.ToByte(hex.Substring(6, 2), 16);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
    }

    private static string? ResolutionLabel(int? w, int? h)
    {
        if (w == null && h == null) return null;
        // Some nfos record wrong values in <height> (especially for older
        // MediaElch scrapes). Use the larger dimension as the resolution
        // signal — for a landscape 1080p video, width=1920 is the source
        // of truth even if height got logged incorrectly.
        var px = Math.Max(w ?? 0, h ?? 0);
        // Width-based thresholds: 4K=3840, 1080p=1920, 720p=1280, SD<800
        return px switch
        {
            >= 3000 => "4K",
            >= 1700 => "HD 1080",
            >= 1100 => "HD 720",
            >= 600  => "SD",
            _       => null,
        };
    }

    private static string? NormalizeAspect(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v)) return raw;
        // Match well-known aspect ratios
        return v switch
        {
            >= 2.30 and <= 2.45 => "2.39:1",
            >= 2.20 and <= 2.30 => "2.21:1",
            >= 1.80 and <= 1.90 => "1.85:1",
            >= 1.75 and <= 1.80 => "16:9",
            >= 1.30 and <= 1.40 => "4:3",
            _ => $"{v:F2}:1",
        };
    }

    private static string? AudioLabel(string? codec, string? channels)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(codec)) parts.Add(codec.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(channels))
        {
            // MediaInfo can return either a channel count ("6") or a friendly
            // value that already contains a layout/suffix ("5.1 кан.").
            var normalized = System.Text.RegularExpressions.Regex.Replace(
                channels.Trim(), @"\s*(кан\.?|channels?)\s*$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim().Replace(',', '.');
            var lbl = normalized switch
            {
                "8" => "7.1",
                "7" => "6.1",
                "6" => "5.1",
                "2" => CineLibraryCS.Services.LocalizationService.TranslateText("Stereo"),
                "1" => "Mono",
                "7.1" or "6.1" or "5.1" or "4.1" or "3.1" or "2.1" => normalized,
                _ => normalized,
            };
            if (!string.IsNullOrWhiteSpace(lbl)) parts.Add(lbl);
        }
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private void PopulateRatingsPanel(MovieDetail m)
    {
        RatingsPanel.Children.Clear();
        if (m.AllRatings.Count == 0)
        {
            RatingsPanel.Visibility = Visibility.Collapsed;
            return;
        }
        RatingsPanel.Visibility = Visibility.Visible;
        foreach (var rt in m.AllRatings
            .OrderBy(rt => (rt.Source ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "kinopoisk" or "poiskkino" or "kinopoiskdev" => 0,
                "imdb" => 1,
                "tmdb" or "themoviedb" => 2,
                _ => 3,
            }))
        {
            var source = rt.Source?.Trim() ?? string.Empty;
            var sourceLabel = PrettySource(source);
            var accent = source.ToLowerInvariant() switch
            {
                "imdb" => Windows.UI.Color.FromArgb(0xFF, 0xF5, 0xC5, 0x18),
                "themoviedb" or "tmdb" => Windows.UI.Color.FromArgb(0xFF, 0x01, 0xB4, 0xE4),
                "kinopoisk" or "poiskkino" or "kinopoiskdev" => Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x8A, 0x00),
                _ => Windows.UI.Color.FromArgb(0xFF, 0xB0, 0xB0, 0xB0),
            };
            var stack = new StackPanel { Spacing = 3 };
            var sourceKey = source.ToLowerInvariant();
            var brandBackground = sourceKey switch
            {
                "imdb" => Windows.UI.Color.FromArgb(0xFF, 0xF5, 0xC5, 0x18),
                "tmdb" or "themoviedb" => Windows.UI.Color.FromArgb(0xFF, 0x03, 0x25, 0x41),
                "kinopoisk" or "poiskkino" or "kinopoiskdev" => Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x66, 0x00),
                _ => accent,
            };
            var sourceButton = new Button
            {
                Content = sourceKey switch { "imdb" => "IMDb", "tmdb" or "themoviedb" => "TMDb", "kinopoisk" or "poiskkino" or "kinopoiskdev" => "КиноПоиск", _ => sourceLabel },
                FontSize = sourceKey.StartsWith("kinopoisk") ? 10 : 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Padding = new Thickness(8, 2, 8, 2),
                MinHeight = 25,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(brandBackground),
                Foreground = new SolidColorBrush(sourceKey == "imdb" ? Windows.UI.Color.FromArgb(0xFF, 0x12, 0x12, 0x12) : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(sourceKey is "tmdb" or "themoviedb" ? Windows.UI.Color.FromArgb(0xFF, 0x01, 0xD2, 0xD3) : brandBackground),
                BorderThickness = new Thickness(sourceKey is "tmdb" or "themoviedb" ? 1 : 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            sourceButton.Click += async (_, _) => await OpenRatingSourceAsync(source);
            stack.Children.Add(sourceButton);
            stack.Children.Add(new TextBlock
            {
                Text = rt.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                       + (source.Equals("rottentomatoes", StringComparison.OrdinalIgnoreCase) ? "%" : ""),
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accent),
            });
            if (rt.Votes.HasValue && rt.Votes.Value > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = FormatVotes(rt.Votes.Value),
                    FontSize = 10,
                    Foreground = CineLibraryCS.Services.ThemeBrushes.Get("MutedBrush"),
                });
            }
            RatingsPanel.Children.Add(stack);
        }
    }

    private async Task OpenRatingSourceAsync(string source)
    {
        var key = source.ToLowerInvariant();
        string? url = key switch
        {
            "imdb" when !string.IsNullOrWhiteSpace(_movie?.ImdbId)
                => $"https://www.imdb.com/title/{_movie!.ImdbId}/",
            "tmdb" or "themoviedb" when int.TryParse(_movie?.TmdbId, out var id) && id > 0
                => $"https://www.themoviedb.org/movie/{id}",
            "kinopoisk" or "poiskkino" or "kinopoiskdev" when _kinopoiskId > 0
                => $"https://www.kinopoisk.ru/film/{_kinopoiskId}/",
            _ => null,
        };
        if (url == null) return;
        try { await Launcher.LaunchUriAsync(new Uri(url)); }
        catch { if (App.MainWindow is MainWindow mw) mw.ShowToast("Не удалось открыть страницу источника рейтинга."); }
    }

    private static string PrettySource(string src) => src.ToLowerInvariant() switch
    {
        "imdb" => "IMDb",
        "themoviedb" or "tmdb" => "TMDb",
        "kinopoisk" or "poiskkino" or "kinopoiskdev" => "КиноПоиск",
        "rottentomatoes" => "Rotten Tomatoes",
        "metacritic" => "Metacritic",
        "default" => "Рейтинг",
        _ => char.ToUpper(src[0]) + src.Substring(1),
    };

    private static string FormatVotes(int votes) => votes switch
    {
        >= 1_000_000 => $"{votes / 1_000_000.0:F1} млн голосов",
        >= 1_000     => $"{votes / 1_000.0:F1} тыс. голосов",
        _            => $"{votes} голосов",
    };

    private void PopulateFinanceAwards(MovieDetail movie)
    {
        string? budget = null, revenue = null;
        var awards = new List<string>();
        try
        {
            if (!string.IsNullOrWhiteSpace(movie.CurrentLetter) && !string.IsNullOrWhiteSpace(movie.FolderRelPath))
            {
                var folder = Path.Combine($"{movie.CurrentLetter}:\\", movie.FolderRelPath.Replace('/', '\\'));
                var nfoPath = ResolveNfoPath(folder, movie.VideoFileRelPath);
                if (nfoPath != null)
                {
                    var doc = new XmlDocument(); doc.Load(nfoPath);
                    budget = doc.SelectSingleNode("/movie/budget")?.InnerText;
                    revenue = doc.SelectSingleNode("/movie/revenue")?.InnerText;
                    // Some NFO providers write every award into one <awards> text node.
                    // Flatten each node into separate entries before classifying so one
                    // multiline blob cannot collapse all awards into a single icon.
                    var awardNodes = doc.SelectNodes("/movie/awards/award")?.OfType<XmlNode>()
                        .Select(x => x.InnerText).ToList() ?? new List<string>();
                    if (awardNodes.Count == 0 && doc.SelectSingleNode("/movie/awards") is XmlNode single
                        && !string.IsNullOrWhiteSpace(single.InnerText))
                        awardNodes.Add(single.InnerText);
                    awards = awardNodes
                        .SelectMany(SplitAwardEntries)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
        }
        catch { }

        BudgetDetailText.Text = FormatMoney(budget);
        RevenueDetailText.Text = FormatMoney(revenue);
        BudgetDetailText.Visibility = string.IsNullOrWhiteSpace(budget) ? Visibility.Collapsed : Visibility.Visible;
        RevenueDetailText.Visibility = string.IsNullOrWhiteSpace(revenue) ? Visibility.Collapsed : Visibility.Visible;

        // Award details are intentionally shown only in the compact icon tooltips.
        PopulateAwardIcons(awards);
        FinanceAwardsPanel.Visibility =
            (!string.IsNullOrWhiteSpace(budget) || !string.IsNullOrWhiteSpace(revenue))
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record AwardGroup(string Name, string IconAsset, int Wins, List<string> Entries);

    private static IEnumerable<string> SplitAwardEntries(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;

        // Preserve commas and periods inside award descriptions; split only on
        // explicit record separators commonly used by Kodi/tinyMediaManager NFOs.
        var chunks = raw.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split(new[] { '\n', '\u2022', '\u25cf', ';', '|', '\u001e' },
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var chunk in chunks)
        {
            var value = chunk.Trim().Trim('-', '–', '—', '•', ' ');
            if (value.Length > 0)
                yield return value;
        }
    }

    private void PopulateAwardIcons(List<string> awards)
    {
        AwardsIconsPanel.Children.Clear();
        if (awards.Count == 0)
        {
            AwardsIconsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var groups = awards
            .SelectMany(SplitAwardEntries)
            .GroupBy(GetAwardGroupName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var name = g.Key;
                var entries = g.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var wins = entries.Count(IsAwardWin);
                return new AwardGroup(name, GetAwardIcon(name), wins, entries);
            })
            .OrderBy(x => GetAwardSortOrder(x.Name))
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            // Compact, frameless award artwork with a small win counter anchored
            // to the lower-right corner of the image itself.
            var icon = new Grid
            {
                Width = 52,
                Height = 66,
                Margin = new Thickness(2, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.Children.Add(new Image
            {
                Width = 48,
                Height = 64,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                Source = new BitmapImage(new Uri($"ms-appx:///Assets/Awards/{group.IconAsset}")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (group.Wins > 0)
            {
                var count = new Border
                {
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Microsoft.UI.ColorHelper.FromArgb(238, 20, 20, 20)),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Microsoft.UI.ColorHelper.FromArgb(230, 218, 177, 83)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    MinWidth = 20,
                    Height = 20,
                    Padding = new Thickness(4, 0, 4, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                count.Child = new TextBlock
                {
                    Text = group.Wins.ToString(CultureInfo.InvariantCulture),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextBrush"]
                };
                icon.Children.Add(count);
            }

            ToolTipService.SetToolTip(icon, BuildAwardToolTip(group));
            int index = i;
            Grid.SetRow(icon, index / 9);
            Grid.SetColumn(icon, index % 9);
            AwardsIconsPanel.Children.Add(icon);
        }

        AwardsIconsPanel.Visibility = Visibility.Visible;
    }

    private static int GetAwardSortOrder(string name)
    {
        if (name.Equals("Оскар", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Equals("Золотой глобус", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Equals("BAFTA", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Equals("Венецианский кинофестиваль", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Equals("Золотая малина", StringComparison.OrdinalIgnoreCase)) return 100;
        return 10;
    }

    private static UIElement BuildAwardToolTip(AwardGroup group)
    {
        var wins = group.Entries.Where(IsAwardWin).ToList();
        var nominations = group.Entries.Where(IsAwardNomination).ToList();
        var other = group.Entries.Where(x => !IsAwardWin(x) && !IsAwardNomination(x)).ToList();
        nominations.AddRange(other);

        var content = new StackPanel
        {
            Spacing = 5,
            MinWidth = 230,
            MaxWidth = 390
        };
        content.Children.Add(new TextBlock
        {
            Text = group.Name,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        });

        AddAwardToolTipSection(content, $"Победы ({wins.Count})", wins, false);
        AddAwardToolTipSection(content, $"Номинации ({nominations.Count})", nominations, true);
        return content;
    }

    private static void AddAwardToolTipSection(StackPanel parent, string heading,
        List<string> entries, bool muted)
    {
        if (entries.Count == 0) return;
        var brush = muted
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(170, 165, 165, 165))
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextBrush"];
        var section = new StackPanel { Spacing = 2 };
        section.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = brush
        });
        foreach (var entry in entries)
        {
            var clean = Regex.Replace(entry, @"^\s*(победа|победитель|номинация|номинант|winner|won|nominee|nominated)\s*[:\-–—]?\s*", "",
                RegexOptions.IgnoreCase);
            section.Children.Add(new TextBlock
            {
                Text = "• " + clean,
                FontSize = 12,
                Foreground = brush,
                TextWrapping = TextWrapping.Wrap
            });
        }
        parent.Children.Add(section);
    }

    private static bool IsAwardNomination(string entry) =>
        ContainsAny(entry, "номинац", "nomination", "nominated", "nominee");

    private static bool IsAwardWin(string entry) =>
        ContainsAny(entry, "побед", "выиграл", "лауреат", "winner", "won ", "wins ", "award winner");

    private static string GetAwardGroupName(string line)
    {
        // Classify each individual record, not the whole <awards> text blob.
        if (ContainsAny(line, "оскар", "oscar", "academy award", "академия кинематографических искусств"))
            return "Оскар";
        if (ContainsAny(line, "золотой глобус", "golden globe", "golden globes"))
            return "Золотой глобус";
        if (ContainsAny(line, "bafta", "британская академия"))
            return "BAFTA";
        if (ContainsAny(line, "эмми", "emmy"))
            return "Эмми";
        if (ContainsAny(line, "канн", "cannes", "palme d'or", "золотая пальмовая ветвь"))
            return "Каннский кинофестиваль";
        if (ContainsAny(line, "венец", "venice", "leone d'oro", "золотой лев"))
            return "Венецианский кинофестиваль";
        if (ContainsAny(line, "сезар", "cesar"))
            return "Сезар";
        if (ContainsAny(line, "golden raspberry", "razzie", "золотая малина"))
            return "Золотая малина";
        if (ContainsAny(line, "critics choice", "critics' choice", "выбор критиков"))
            return "Critics Choice";
        if (ContainsAny(line, "screen actors guild", "sag award", "гильдия киноакт"))
            return "SAG Awards";
        if (ContainsAny(line, "producers guild", "pga award"))
            return "PGA Awards";
        if (ContainsAny(line, "directors guild", "dga award"))
            return "DGA Awards";
        if (ContainsAny(line, "writers guild", "wga award"))
            return "WGA Awards";
        if (ContainsAny(line, "independent spirit", "спирит"))
            return "Independent Spirit";
        if (ContainsAny(line, "saturn award", "премия сатурн"))
            return "Saturn Awards";
        if (ContainsAny(line, "annie award", "премия энни"))
            return "Annie Awards";
        if (ContainsAny(line, "european film award", "европейская кинопремия"))
            return "European Film Awards";
        if (ContainsAny(line, "goya award", "премия гойя"))
            return "Goya Awards";
        if (ContainsAny(line, "david di donatello"))
            return "David di Donatello";

        // Unknown organizations remain separate by their explicit award name;
        // never merge unrelated records into one generic "all awards" icon.
        var parts = line.Split(new[] { " — ", " – ", " - ", ": " },
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var candidate = parts.FirstOrDefault(x =>
            !int.TryParse(x, out _) &&
            !ContainsAny(x, "победа", "победитель", "номинация", "winner", "won ", "wins "));
        if (!string.IsNullOrWhiteSpace(candidate))
            return candidate.Length > 42 ? candidate[..42].Trim() : candidate;

        return "Другие награды";
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string GetAwardIcon(string name)
    {
        if (name.Equals("Оскар", StringComparison.OrdinalIgnoreCase)) return "oscar.png";
        if (name.Equals("Золотой глобус", StringComparison.OrdinalIgnoreCase)) return "golden-globe.png";
        if (name.Equals("BAFTA", StringComparison.OrdinalIgnoreCase)) return "bafta.png";
        if (name.Equals("Эмми", StringComparison.OrdinalIgnoreCase)) return "emmy.png";
        if (name.Equals("Каннский кинофестиваль", StringComparison.OrdinalIgnoreCase)) return "cannes.png";
        if (name.Equals("Венецианский кинофестиваль", StringComparison.OrdinalIgnoreCase)) return "venice.png";
        if (name.Equals("Сезар", StringComparison.OrdinalIgnoreCase)) return "cesar.png";
        if (name.Equals("Золотая малина", StringComparison.OrdinalIgnoreCase)) return "razzie.png";
        if (name.Equals("Critics Choice", StringComparison.OrdinalIgnoreCase)) return "critics-choice.png";
        if (name.Equals("SAG Awards", StringComparison.OrdinalIgnoreCase)) return "sag.png";
        if (name.Equals("PGA Awards", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("DGA Awards", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("WGA Awards", StringComparison.OrdinalIgnoreCase)) return "guild.png";
        if (name.Equals("Independent Spirit", StringComparison.OrdinalIgnoreCase)) return "spirit.png";
        if (name.Equals("Saturn Awards", StringComparison.OrdinalIgnoreCase)) return "saturn.png";
        if (name.Equals("Annie Awards", StringComparison.OrdinalIgnoreCase)) return "annie.png";
        if (name.Equals("European Film Awards", StringComparison.OrdinalIgnoreCase)) return "european.png";
        if (name.Equals("Goya Awards", StringComparison.OrdinalIgnoreCase)) return "goya.png";
        if (name.Equals("David di Donatello", StringComparison.OrdinalIgnoreCase)) return "david.png";
        return "generic.png";
    }

    private static string FormatMoney(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0)
            return "$" + value.ToString("N0", CultureInfo.InvariantCulture);
        return raw;
    }

    private void PopulateFileInfo(MovieDetail m)
    {
        bool any = false;
        // Runtime / Released / Rated / Country — folded in from the old
        // below-poster column so the row stays compact (v2.3.x).
        if (m.Runtime.HasValue && m.Runtime.Value > 0)
        {
            FiRuntime.Text = $"{m.Runtime.Value} мин";
            FiRuntimeBlock.Visibility = Visibility.Visible;
            any = true;
        }
        else FiRuntimeBlock.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(m.Premiered))
        {
            FiRelease.Text = DateTime.TryParse(m.Premiered, out var dt)
                ? dt.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture)
                : m.Premiered!;
            FiReleaseBlock.Visibility = Visibility.Visible;
            any = true;
        }
        else if (m.Year.HasValue)
        {
            FiRelease.Text = m.Year.Value.ToString();
            FiReleaseBlock.Visibility = Visibility.Visible;
            any = true;
        }
        if (!string.IsNullOrWhiteSpace(m.Mpaa))
        {
            FiMpaa.Text = m.Mpaa!;
            FiMpaaBlock.Visibility = Visibility.Visible;
            any = true;
        }
        if (!string.IsNullOrWhiteSpace(m.Country))
        {
            FiCountry.Text = m.Country!.Split(new[] { ',', ';', '/', '|', '\n', '\r' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? m.Country;
            FiCountryBlock.Visibility = Visibility.Visible;
            any = true;
        }
        if (m.FileSizeBytes.HasValue && m.FileSizeBytes.Value > 0)
        {
            FiSize.Text = FormatBytes(m.FileSizeBytes.Value);
            FiSizeBlock.Visibility = Visibility.Visible;
            any = true;
        }
        // Prefer the editable NFO runtime (minutes); fall back to the actual media duration.
        var durationMinutes = m.Runtime is > 0
            ? m.Runtime.Value
            : m.DurationSeconds is > 0
                ? Math.Max(1, (int)Math.Round(m.DurationSeconds.Value / 60.0))
                : 0;
        if (durationMinutes > 0)
        {
            FiDur.Text = $"{durationMinutes} мин";
            FiDurBlock.Visibility = Visibility.Visible;
            any = true;
        }
        if (!string.IsNullOrWhiteSpace(m.ContainerExt))
        {
            FiContainer.Text = m.ContainerExt.ToUpperInvariant();
            FiContainerBlock.Visibility = Visibility.Visible;
            any = true;
        }
        if (!string.IsNullOrWhiteSpace(m.AudioLanguages))
        {
            FiLangs.Text = m.AudioLanguages.Replace(",", " · ");
            FiLangBlock.Visibility = Visibility.Visible;
            any = true;
        }
        if (!string.IsNullOrWhiteSpace(m.SubtitleLanguages))
        {
            var subs = m.SubtitleLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries);
            FiSubs.Text = subs.Length switch
            {
                0 => "—",
                1 => subs[0],
                <= 3 => string.Join(" · ", subs),
                _ => $"Дорожек: {subs.Length}",
            };
            FiSubsBlock.Visibility = Visibility.Visible;
            any = true;
        }
        // Two columns keep the technical-information card compact in width and taller vertically; first row starts Released, Rated.
        var infoFields = new FrameworkElement[]
        {
            FiReleaseBlock, FiMpaaBlock, FiCountryBlock, FiSizeBlock,
            FiDurBlock, FiContainerBlock, FiLangBlock, FiSubsBlock
        }.Where(e => e.Visibility == Visibility.Visible).ToList();
        FileInfoGrid.Children.Clear();
        FileInfoGrid.RowDefinitions.Clear();
        int rowCount = Math.Max(1, (int)Math.Ceiling(infoFields.Count / 2.0));
        for (int r = 0; r < rowCount; r++)
            FileInfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < infoFields.Count; i++)
        {
            var field = infoFields[i];
            Grid.SetRow(field, i / 2);
            Grid.SetColumn(field, i % 2);
            FileInfoGrid.Children.Add(field);
        }
        FileInfoPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatBytes(long b)
    {
        const long KB = 1024L, MB = KB * 1024, GB = MB * 1024;
        if (b >= GB) return $"{b / (double)GB:F2} GB";
        if (b >= MB) return $"{b / (double)MB:F0} MB";
        if (b >= KB) return $"{b / (double)KB:F0} KB";
        return $"{b} B";
    }

    private static string FormatDuration(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
            : $"{t.Minutes}m {t.Seconds:D2}s";
    }

    // v3.7.1: the content column gets an explicit width. With MaxWidth alone,
    // WinUI centred it by the width it asked for, so on a movie without fanart
    // (nothing asks for the full width) it was pushed right and cut off.
    // v4.2.0 (#14): no 1100 cap any more. The page fills the window and picks
    // an arrangement from its width, so a portrait tablet and a 4K screen both
    // use the space they have.
    private void OnContentScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var inner = e.NewSize.Width - ContentScroller.Padding.Left - ContentScroller.Padding.Right;
        ContentStack.Width = Math.Max(0, inner);
        HeroBorder.Height = Math.Clamp(inner / 3.2, 220, 460);   // 1100 wide = the old 340
        ApplyLayout(e.NewSize.Width < 900 ? DetailLayout.Narrow
                  : e.NewSize.Width >= 1600 ? DetailLayout.Wide
                  : DetailLayout.Standard);
    }

    private enum DetailLayout { Unset, Narrow, Standard, Wide }
    private DetailLayout _layout;

    private void ApplyLayout(DetailLayout layout)
    {
        if (layout == _layout) return;
        _layout = layout;
        bool narrow = layout == DetailLayout.Narrow, wide = layout == DetailLayout.Wide;

        PosterFrame.Width  = narrow ? 150 : wide ? 260 : 200;
        PosterFrame.Height = PosterFrame.Width * 1.5;
        DetailTitle.FontSize   = narrow ? 26 : 34;
        DetailTitle.LineHeight = narrow ? 32 : 40;

        // Wide: everything up to the cast sits beside the poster. Buttons and plot
        // go under the title; genres, director, studio, file info and notes get a
        // column of their own. Otherwise they stack under the poster as before.
        foreach (var block in new FrameworkElement[] { ActionsBlock, FileInfoPanel, FinanceAwardsPanel, PlotBlock, NotesCard, FieldsBlock, TechRatingsRow })
            Detach(block);

        // Credits stay directly beneath the poster; genres are already in PosterColumn.
        // The overview is promoted into the title column immediately after title/original title/tagline.
        PosterColumn.Children.Add(FieldsBlock);
        var overviewIndex = Math.Min(3, MetaStack.Children.Count);
        MetaStack.Children.Insert(overviewIndex, PlotBlock);
        if (wide)
        {
            MetaStack.Children.Add(ActionsBlock);
            SideStack.Children.Add(TechRatingsRow);
            SideStack.Children.Add(FileInfoPanel);
            SideStack.Children.Add(FinanceAwardsPanel);
        }
        else
        {
            ContentStack.Children.Insert(ContentStack.Children.IndexOf(TopGrid) + 1, ActionsBlock);
            BodyStack.Children.Add(TechRatingsRow);
            BodyStack.Children.Add(FileInfoPanel);
            BodyStack.Children.Add(FinanceAwardsPanel);
        }
        // The title column stops at a readable width; the details column takes the rest.
        var main = TopGrid.ColumnDefinitions[1];
        var side = TopGrid.ColumnDefinitions[2];
        main.Width = new GridLength(wide ? 3 : 1, GridUnitType.Star);
        main.MaxWidth = wide ? 900 : double.PositiveInfinity;
        side.Width = wide ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        side.MinWidth = wide ? 620 : 0;
        // Keep the technical column visibly toward the right, with breathing room at the edge.
        SideStack.HorizontalAlignment = wide ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        SideStack.Margin = new Thickness(0, 140, wide ? 16 : 0, 0); // align right inset with PosterColumn (16 px)
        // At the top of the details column the divider has nothing to divide
        FieldsDivider.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        FieldsGrid.Margin = new Thickness(0, wide ? 0 : 20, 0, 0);
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.Parent is Panel parent) parent.Children.Remove(element);
    }

    // v4.2.0: Genres, Director and Studio side by side, or one under another
    // when the grid is narrow (a small window, or the wide layout's side column).
    private void OnFieldsGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool stack = e.NewSize.Width < 600;
        FieldsGrid.ColumnDefinitions[1].Width = stack ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FieldsGrid.ColumnDefinitions[2].Width = stack ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FieldsGrid.ColumnSpacing = stack ? 0 : 24;   // v4.5.0: a long Writer list ran into Studio
        for (int i = 0; i < FieldsGrid.Children.Count; i++)
        {
            var field = (FrameworkElement)FieldsGrid.Children[i];
            Grid.SetColumn(field, stack ? 0 : i);
            Grid.SetRow(field, stack ? i : 0);
            field.Margin = new Thickness(0, stack && i > 0 ? 14 : 0, 0, 0);
        }
    }

    // ── Sticky action bar + external link buttons (v2.3) ─────────────────

    private void OnContentScrolled(object sender, ScrollViewerViewChangedEventArgs e)
    {
        // Bar appears once the user is past the hero block (its height follows the width)
        StickyBar.Visibility = ContentScroller.VerticalOffset > HeroBorder.Height - 60
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnStickyClose(object sender, RoutedEventArgs e) => Close();

    private async void OnOpenTmdb(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(_movie?.TmdbId, out var tmdbId) || tmdbId <= 0) return;
        try { await Launcher.LaunchUriAsync(new Uri($"https://www.themoviedb.org/movie/{tmdbId}")); }
        catch (Exception) { /* Browser launch may be unavailable; keep the detail page responsive. */ }
    }

    private async void OnOpenImdb(object sender, RoutedEventArgs e)
    {
        if (_movie?.ImdbId == null) return;
        try { await Launcher.LaunchUriAsync(new Uri($"https://www.imdb.com/title/{_movie.ImdbId}/")); }
        catch
        {
            if (App.MainWindow is MainWindow mw)
                mw.ShowToast("Не удалось открыть IMDb — браузер недоступен?");
        }
    }

    private async void OnOpenKinopoisk(object sender, RoutedEventArgs e)
    {
        var url = _kinopoiskId > 0
            ? $"https://www.kinopoisk.ru/film/{_kinopoiskId}/"
            : $"https://www.kinopoisk.ru/index.php?kp_query={Uri.EscapeDataString(_movie?.Title ?? string.Empty)}";
        try { await Launcher.LaunchUriAsync(new Uri(url)); }
        catch
        {
            if (App.MainWindow is MainWindow mw)
                mw.ShowToast("Не удалось открыть КиноПоиск — браузер недоступен?");
        }
    }

    /// <summary>Finds a movie NFO regardless of whether it is named movie.nfo or after the movie/video.</summary>
    private static string? ResolveNfoPath(string folder, string? videoFileRelPath = null)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

        // Keep the Kodi/tinyMediaManager convention as the first choice.
        var standard = Path.Combine(folder, "movie.nfo");
        if (File.Exists(standard)) return standard;

        // Many libraries name the NFO after the containing movie folder or video file.
        var folderName = new DirectoryInfo(folder).Name;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(folderName)) candidates.Add(Path.Combine(folder, folderName + ".nfo"));
        if (!string.IsNullOrWhiteSpace(videoFileRelPath))
        {
            var videoName = Path.GetFileNameWithoutExtension(videoFileRelPath);
            if (!string.IsNullOrWhiteSpace(videoName)) candidates.Add(Path.Combine(folder, videoName + ".nfo"));
        }
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(candidate)) return candidate;

        // If there is exactly one NFO in the folder, it is unambiguous even if named differently.
        var nfoFiles = Directory.GetFiles(folder, "*.nfo", SearchOption.TopDirectoryOnly);
        return nfoFiles.Length == 1 ? nfoFiles[0] : null;
    }

    private static int ReadKinopoiskIdFromNfo(MovieDetail movie)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(movie.CurrentLetter) || string.IsNullOrWhiteSpace(movie.FolderRelPath))
                return 0;
            var folder = Path.Combine($"{movie.CurrentLetter}:\\", movie.FolderRelPath.Replace('/', '\\'));
            var path = ResolveNfoPath(folder, movie.VideoFileRelPath);
            if (path == null || !File.Exists(path)) return 0;
            var doc = new XmlDocument();
            doc.Load(path);
            var node = doc.SelectSingleNode("/movie/uniqueid[@type='kinopoisk' or @type='kinopoiskdev']")
                ?? doc.SelectSingleNode("/movie/uniqueid[@type='poiskkino']");
            return node != null && int.TryParse(node.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id : 0;
        }
        catch { return 0; }
    }

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_movie?.CurrentLetter == null || _movie.VideoFileRelPath == null) return;
        var letter = _movie.CurrentLetter;
        var videoPath = Path.Combine($"{letter}:\\", _movie.VideoFileRelPath.Replace('/', '\\'));
        if (File.Exists(videoPath))
        {
            // Stamp Continue Watching: we can't see real playback position once
            // the OS player takes over, so the row simply moves to the top of
            // the Continue Watching list and stays until the user marks it Watched.
            AppState.Instance.Db.MarkPlayed(_movie.Id);
            // Tell the host so its sidebar badge updates
            WatchlistChanged?.Invoke(this, EventArgs.Empty);
            await VideoPlayer.PlayAsync(videoPath);
        }
    }

    private async void OnEditNfo(object sender, RoutedEventArgs e)
    {
        var movie = _movie;
        if (movie == null || string.IsNullOrWhiteSpace(movie.CurrentLetter) || string.IsNullOrWhiteSpace(movie.FolderRelPath))
        {
            var missing = new ContentDialog { Title = "NFO недоступен", Content = "Не удалось определить папку фильма.", CloseButtonText = "ОК", XamlRoot = RootGrid.XamlRoot };
            await missing.ShowAsync();
            return;
        }

        var folder = Path.Combine($"{movie.CurrentLetter}:\\", movie.FolderRelPath.Replace('/', '\\'));
        var nfoPath = ResolveNfoPath(folder, movie.VideoFileRelPath);
        if (nfoPath == null || !File.Exists(nfoPath))
        {
            var candidates = Directory.Exists(folder) ? string.Join("\n", Directory.GetFiles(folder, "*.nfo", SearchOption.TopDirectoryOnly).Select(Path.GetFileName)) : "папка недоступна";
            var missing = new ContentDialog { Title = "NFO не найден", Content = $"Не удалось однозначно определить NFO в папке:\n{folder}\n\nНайдены файлы:\n{(string.IsNullOrWhiteSpace(candidates) ? "нет файлов .nfo" : candidates)}\n\nОжидается movie.nfo, файл с именем папки/видео или единственный .nfo в папке.", CloseButtonText = "ОК", XamlRoot = RootGrid.XamlRoot };
            await missing.ShowAsync();
            return;
        }

        try
        {
            var editor = new NfoEditorDialog(nfoPath, App.MainWindow ?? throw new InvalidOperationException("Главное окно приложения ещё не создано."));
            var result = await editor.ShowAsync();
            if (result != ContentDialogResult.Primary || !editor.Saved) return;

            // Rescan only this movie folder so edited NFO fields propagate to the database/UI.
            var driveRoot = $"{movie.CurrentLetter}:\\";
            var scanner = new ScannerService(AppState.Instance.Db);
            await scanner.ScanAsync(movie.VolumeSerial, driveRoot, null, CancellationToken.None, folder, incremental: false);
            await LoadAsync();
            WatchlistChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            var error = new ContentDialog { Title = "Ошибка редактора NFO", Content = ex.Message, CloseButtonText = "ОК", XamlRoot = RootGrid.XamlRoot };
            await error.ShowAsync();
        }
    }

    private async void OnEditArtwork(object sender, RoutedEventArgs e)
    {
        var movie = _movie;
        if (movie == null) return;
        string? movieFolder = null;
        if (!string.IsNullOrWhiteSpace(movie.CurrentLetter) && !string.IsNullOrWhiteSpace(movie.FolderRelPath))
            movieFolder = Path.Combine($"{movie.CurrentLetter}:\\", movie.FolderRelPath.Replace('/', '\\'));
        var sourceProvider = "tmdb";
        var isKinopoiskSource = false;
        var isPoiskKinoSource = false;
        var sourceId = 0;
        try
        {
            var nfoPath = movieFolder == null ? null : ResolveNfoPath(movieFolder, movie.VideoFileRelPath);
            if (!string.IsNullOrWhiteSpace(nfoPath) && File.Exists(nfoPath))
            {
                var nfo = new XmlDocument(); nfo.Load(nfoPath);
                var tmdbNode = nfo.SelectSingleNode("/movie/uniqueid[@type='tmdb' and @default='true']") ?? nfo.SelectSingleNode("/movie/tmdbid") ?? nfo.SelectSingleNode("/movie/uniqueid[@type='tmdb']");
                var poiskNode = nfo.SelectSingleNode("/movie/uniqueid[@type='poiskkino' and @default='true']");
                var kpNode = nfo.SelectSingleNode("/movie/uniqueid[@type='kinopoisk' and @default='true']") ?? nfo.SelectSingleNode("/movie/uniqueid[@type='kinopoiskdev' and @default='true']");
                if (tmdbNode == null && poiskNode == null && kpNode == null)
                    kpNode = nfo.SelectSingleNode("/movie/uniqueid[@type='kinopoisk']") ?? nfo.SelectSingleNode("/movie/uniqueid[@type='kinopoiskdev']");
                if (tmdbNode == null && poiskNode != null && int.TryParse(poiskNode.InnerText, out var pkId) && pkId > 0)
                { isPoiskKinoSource = true; sourceProvider = "poiskkino"; sourceId = pkId; }
                else if (tmdbNode == null && kpNode != null && int.TryParse(kpNode.InnerText, out var kpId) && kpId > 0)
                { isKinopoiskSource = true; sourceProvider = "kinopoisk"; sourceId = kpId; }
            }
        }
        catch { }
        var isKpSource = isKinopoiskSource || isPoiskKinoSource;
        if (!isKpSource && (!int.TryParse(movie.TmdbId, out sourceId) || sourceId <= 0))
        {
            var noId = new ContentDialog { Title = "Нет идентификатора фильма", Content = "Для выбора изображений у фильма должен быть указан TMDb ID или КиноПоиск ID в movie.nfo.", CloseButtonText = "ОК", XamlRoot = RootGrid.XamlRoot };
            await noId.ShowAsync();
            return;
        }
        if (isKinopoiskSource && string.IsNullOrWhiteSpace(KinopoiskSettings.ApiToken))
        {
            var noApi = new ContentDialog { Title = "Не настроен КиноПоиск API", Content = "Добавь API-ключ КиноПоиск API Unofficial в настройках, чтобы менять изображения фильма.", CloseButtonText = "ОК", XamlRoot = RootGrid.XamlRoot };
            await noApi.ShowAsync(); return;
        }
        if (isPoiskKinoSource && string.IsNullOrWhiteSpace(PoiskKinoSettings.ApiToken))
        {
            var noApi = new ContentDialog { Title = "Не настроен ПоискКино API", Content = "Добавь API-ключ poiskkino.dev в настройках, чтобы менять изображения фильма.", CloseButtonText = "ОК", XamlRoot = RootGrid.XamlRoot };
            await noApi.ShowAsync(); return;
        }

        try
        {
            var folderPoster = movieFolder == null ? null : Path.Combine(movieFolder, "poster.jpg");
            var folderFanart = movieFolder == null ? null : Path.Combine(movieFolder, "fanart.jpg");
            var cachedPoster = string.IsNullOrWhiteSpace(movie.LocalPoster) ? null : Path.Combine(AppState.Instance.DataDir, movie.LocalPoster.Replace('/', Path.DirectorySeparatorChar));
            var cachedFanart = string.IsNullOrWhiteSpace(movie.LocalFanart) ? null : Path.Combine(AppState.Instance.DataDir, movie.LocalFanart.Replace('/', Path.DirectorySeparatorChar));
            var localPoster = cachedPoster != null && File.Exists(cachedPoster) ? cachedPoster : folderPoster;
            var localFanart = cachedFanart != null && File.Exists(cachedFanart) ? cachedFanart : folderFanart;

            using var client = new TmdbClient(language: "ru-RU");
            var searchHit = new TmdbMovie
            {
                TmdbId = sourceId, KinopoiskId = isKpSource ? sourceId : 0,
                SourceProvider = sourceProvider, Title = movie.Title
            };
            // Existing artwork from the movie folder is shown initially. The poster or backdrop
            // is only replaced if the user opens its picker and selects a different image.
            var picker = new TmdbMoviePreviewDialog(client, searchHit,
                localPoster != null && File.Exists(localPoster) ? localPoster : null,
                localFanart != null && File.Exists(localFanart) ? localFanart : null,
                ownerWindow: this,
                initialTitle: movie.Title);
            // The action button uses its normal appearance while the picker is open;
            // blue is reserved for pointer-over/pressed states in its template.
            var result = await picker.ShowAsync();
            if (result != ContentDialogResult.Primary || picker.Movie == null) return;

            var edited = picker.Movie;
            if (!isKpSource && edited.KinopoiskRating.HasValue && movieFolder != null && Directory.Exists(movieFolder))
            {
                var ratingNfoPath = ResolveNfoPath(movieFolder, movie.VideoFileRelPath);
                if (ratingNfoPath != null) UpdateMovieNfoKinopoiskRating(ratingNfoPath, edited.KinopoiskId, edited.KinopoiskRating.Value, edited.KinopoiskVoteCount);
                AppState.Instance.Db.UpdateMovieRating(movie.Id, "kinopoisk", edited.KinopoiskRating.Value, edited.KinopoiskVoteCount);
                var ratings = _movie?.AllRatings;
                if (ratings != null)
                {
                    ratings.RemoveAll(r => r.Source.Equals("kinopoisk", StringComparison.OrdinalIgnoreCase));
                    ratings.Add(("kinopoisk", edited.KinopoiskRating.Value, edited.KinopoiskVoteCount));
                    if (_movie != null) PopulateRatingsPanel(_movie);
                }
            }
            var editedTitle = edited.Title?.Trim();
            if (!string.IsNullOrWhiteSpace(editedTitle) &&
                !string.Equals(editedTitle, movie.Title, StringComparison.Ordinal))
            {
                // The title editor changes only <title> in movie.nfo. All other
                // metadata remains untouched and will continue to be read from NFO.
                if (!string.IsNullOrWhiteSpace(movieFolder) && Directory.Exists(movieFolder))
                {
                    var titleNfoPath = ResolveNfoPath(movieFolder, movie.VideoFileRelPath);
                    if (titleNfoPath != null) UpdateMovieNfoTitle(titleNfoPath, editedTitle);
                }
                AppState.Instance.Db.UpdateMovieTitle(movie.Id, editedTitle);
                movie.Title = editedTitle;
                TitleBarText.Text = editedTitle;
                DetailTitle.Text = editedTitle;
                Title = editedTitle;
                WatchlistChanged?.Invoke(this, EventArgs.Empty);
            }

            string? newPosterRel = movie.LocalPoster;
            string? newFanartRel = movie.LocalFanart;
            if (picker.PosterWasChanged)
            {
                var posterFile = picker.SelectedPosterPath ?? picker.Movie.PosterPath;
                if (string.IsNullOrWhiteSpace(posterFile)) throw new InvalidOperationException("TMDb не предоставил постер для этого фильма.");
                newPosterRel = await DownloadArtAsync(client, posterFile, "posters", sourceId);
                if (newPosterRel == null) throw new IOException("Не удалось загрузить постер из TMDb.");
                if (movieFolder != null && Directory.Exists(movieFolder))
                    File.Copy(Path.Combine(AppState.Instance.DataDir, newPosterRel.Replace('/', Path.DirectorySeparatorChar)), Path.Combine(movieFolder, "poster.jpg"), true);
            }
            if (picker.BackdropWasChanged)
            {
                var backdropFile = picker.SelectedBackdropPath ?? picker.Movie.BackdropPath;
                if (string.IsNullOrWhiteSpace(backdropFile)) throw new InvalidOperationException("TMDb не предоставил баннер для этого фильма.");
                newFanartRel = await DownloadArtAsync(client, backdropFile, "fanart", sourceId);
                if (newFanartRel == null) throw new IOException("Не удалось загрузить баннер из TMDb.");
                if (movieFolder != null && Directory.Exists(movieFolder))
                    File.Copy(Path.Combine(AppState.Instance.DataDir, newFanartRel.Replace('/', Path.DirectorySeparatorChar)), Path.Combine(movieFolder, "fanart.jpg"), true);
            }

            AppState.Instance.Db.UpdateMovieArtwork(movie.Id, newPosterRel ?? string.Empty, newFanartRel ?? string.Empty);
            movie.LocalPoster = newPosterRel;
            movie.LocalFanart = newFanartRel;
            if (picker.PosterWasChanged)
            {
                PosterImage.Source = null;
                PosterPlaceholder.Visibility = Visibility.Visible;
                LoadImageAsync(movie.LocalPoster, PosterImage, PosterPlaceholder, 520);
            }
            if (picker.BackdropWasChanged)
            {
                HeroImage.Source = null;
                LoadImageAsync(movie.LocalFanart, HeroImage, null, Math.Clamp(Microsoft.UI.Windowing.AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))).Size.Width, 1920, 3840));
            }
        }
        catch (Exception ex)
        {
            var error = new ContentDialog { Title = "Ошибка изменения оформления", Content = ex.Message, CloseButtonText = "ОК", XamlRoot = RootGrid.XamlRoot };
            await error.ShowAsync();
        }
    }

    private static void UpdateMovieNfoKinopoiskRating(string nfoPath, int kinopoiskId, double rating, int? votes)
    {
        try
        {
            var document = new XmlDocument { PreserveWhitespace = true };
            if (File.Exists(nfoPath)) document.Load(nfoPath); else document.LoadXml("<movie />");
            var root = document.DocumentElement;
            if (root == null) return;
            var ratings = root.SelectSingleNode("ratings") as XmlElement;
            if (ratings == null) { ratings = document.CreateElement("ratings"); root.AppendChild(ratings); }
            var existing = ratings.SelectSingleNode("rating[@name='kinopoisk']") as XmlElement;
            if (existing != null) ratings.RemoveChild(existing);
            var entry = document.CreateElement("rating"); entry.SetAttribute("name", "kinopoisk"); entry.SetAttribute("max", "10"); entry.SetAttribute("default", "false");
            var value = document.CreateElement("value"); value.InnerText = rating.ToString("0.0", CultureInfo.InvariantCulture); entry.AppendChild(value);
            if (votes.HasValue) { var votesNode = document.CreateElement("votes"); votesNode.InnerText = votes.Value.ToString(CultureInfo.InvariantCulture); entry.AppendChild(votesNode); }
            ratings.AppendChild(entry);
            if (kinopoiskId > 0)
            {
                var id = root.SelectSingleNode("uniqueid[@type='kinopoisk']") as XmlElement;
                if (id == null) { id = document.CreateElement("uniqueid"); id.SetAttribute("type", "kinopoisk"); root.AppendChild(id); }
                id.SetAttribute("default", "false");
                id.InnerText = kinopoiskId.ToString(CultureInfo.InvariantCulture);
            }
            document.Save(nfoPath);
        }
        catch { }
    }

    private static void UpdateMovieNfoTitle(string nfoPath, string title)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        if (File.Exists(nfoPath))
            document.Load(nfoPath);
        else
            document.LoadXml("<movie />");

        var root = document.DocumentElement;
        if (root == null)
            throw new InvalidDataException("Не удалось прочитать movie.nfo.");

        var titleNode = root.SelectSingleNode("title");
        if (titleNode == null)
        {
            titleNode = document.CreateElement("title");
            root.PrependChild(titleNode);
        }
        titleNode.InnerText = title;
        document.Save(nfoPath);
    }

    private async void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_movie?.CurrentLetter == null || _movie.FolderRelPath == null) return;
        var folderPath = Path.Combine($"{_movie.CurrentLetter}:\\", _movie.FolderRelPath.Replace('/', '\\'));
        if (!Directory.Exists(folderPath)) return;

        var originalBackground = FolderBtn.Background;
        var originalForeground = FolderBtn.Foreground;
        var originalToolTip = ToolTipService.GetToolTip(FolderBtn);
        FolderBtn.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 59, 130, 246));
        FolderBtn.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        ToolTipService.SetToolTip(FolderBtn, null);
        try { await Launcher.LaunchFolderPathAsync(folderPath); }
        finally
        {
            FolderBtn.Background = originalBackground;
            FolderBtn.Foreground = originalForeground;
            ToolTipService.SetToolTip(FolderBtn, originalToolTip);
        }
    }

    private void OnToggleFav(object sender, RoutedEventArgs e)
    {
        if (_movie == null) return;
        AppState.Instance.Db.ToggleFavorite(_movie.Id);
        _movie.IsFavorite = !_movie.IsFavorite;
        FavBtn.Content = _movie.IsFavorite ? "♥ В избранном" : "♡ В избранное";
    }

         private void OnToggleWatched(object sender, RoutedEventArgs e)
         {
             if (_movie == null) return;
             AppState.Instance.Db.ToggleWatched(_movie.Id);
             _movie.IsWatched = !_movie.IsWatched;
             WatchedBtn.Content = _movie.IsWatched ? "✓ Просмотрен" : "○ Отметить просмотренным";
         }

         private void OnToggleWatchlist(object sender, RoutedEventArgs e)
         {
             if (_movie == null) return;
             AppState.Instance.Db.SetWatchlist(_movie.Id, !_movie.IsWatchlist);
             _movie.IsWatchlist = !_movie.IsWatchlist;
             WatchlistBtn.Content = _movie.IsWatchlist ? "📌 В списке просмотра" : CineLibraryCS.Services.LocalizationService.TranslateText("☐ Добавить в список просмотра");
             WatchlistChanged?.Invoke(this, EventArgs.Empty);
         }

        // ── Lists chips (v2.5) — chip-per-list with ✕ to remove. Rebuilt
        //   on every list-membership change (flyout toggle / chip remove). ─

        private void RefreshListChips()
        {
            ListChipsRepeater.Items.Clear();
            if (_movie == null)
            {
                ListChipsRepeater.Visibility = Visibility.Collapsed;
                return;
            }
            var all = AppState.Instance.Db.GetUserLists().ToDictionary(u => u.Id, u => u.Name);
            var membership = AppState.Instance.Db.GetUserListsForMovie(_movie.Id);
            if (membership.Count == 0)
            {
                ListChipsRepeater.Visibility = Visibility.Collapsed;
                return;
            }
            ListChipsRepeater.Visibility = Visibility.Visible;
            foreach (var listId in membership)
            {
                if (!all.TryGetValue(listId, out var listName)) continue;
                ListChipsRepeater.Items.Add(BuildListChip(listId, listName));
            }
        }

        private UIElement BuildListChip(int listId, string listName)
        {
            var border = new Border
            {
                Background = CineLibraryCS.Services.ThemeBrushes.Get("CardBrush"),
                BorderBrush = CineLibraryCS.Services.ThemeBrushes.Get("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 6, 4),
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            sp.Children.Add(new TextBlock
            {
                Text = $"📑 {listName}",
                FontSize = 12,
                Foreground = CineLibraryCS.Services.ThemeBrushes.Get("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var x = new Button
            {
                Content = "✕",
                FontSize = 10,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = CineLibraryCS.Services.ThemeBrushes.Get("MutedBrush"),
                Padding = new Thickness(4, 0, 4, 0),
                MinWidth = 18,
                MinHeight = 18,
            };
            ToolTipService.SetToolTip(x, $"Убрать из списка «{listName}»");
            x.Click += (_, _) =>
            {
                if (_movie == null) return;
                AppState.Instance.Db.RemoveMovieFromUserList(listId, _movie.Id);
                RefreshListChips();
                WatchlistChanged?.Invoke(this, EventArgs.Empty);
            };
            sp.Children.Add(x);
            border.Child = sp;
            return border;
        }

        // ── v2.9 Tags ────────────────────────────────────────────────────

        /// <summary>Rebuild the tag chip row from DB state. Mirrors RefreshListChips.</summary>
        private void RefreshTagChips()
        {
            TagChipsRepeater.Items.Clear();
            if (_movie == null) return;
            _movie.Tags = AppState.Instance.Db.GetTagNamesForMovie(_movie.Id);
            foreach (var name in _movie.Tags)
                TagChipsRepeater.Items.Add(BuildTagChip(name));
        }

        private UIElement BuildTagChip(string tagName)
        {
            var border = new Border
            {
                Background = CineLibraryCS.Services.ThemeBrushes.Get("ChipBrush"),
                BorderBrush = CineLibraryCS.Services.ThemeBrushes.Get("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 3, 4, 3),
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var label = new HyperlinkButton
            {
                Content = tagName,
                FontSize = 12,
                Padding = new Thickness(0),
                MinHeight = 0,
            };
            label.Click += (_, _) =>
            {
                if (_movie == null) return;
                NavigateAndClose(mw => mw.NavigateLibraryByTag(tagName));
            };
            sp.Children.Add(label);
            var x = new Button
            {
                Content = "✕",
                FontSize = 10,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = CineLibraryCS.Services.ThemeBrushes.Get("MutedBrush"),
                Padding = new Thickness(4, 0, 4, 0),
                MinWidth = 18,
                MinHeight = 18,
            };
            ToolTipService.SetToolTip(x, $"Удалить тег «{tagName}»");
            x.Click += (_, _) =>
            {
                if (_movie == null) return;
                var tagId = AppState.Instance.Db.EnsureTag(tagName);  // safe lookup-or-create
                // RemoveMovieTag raises PersonalStateChanged → AppState
                // auto-writes the movie sidecar with current tags.
                AppState.Instance.Db.RemoveMovieTag(_movie.Id, tagId);
                RefreshTagChips();
                WatchlistChanged?.Invoke(this, EventArgs.Empty);
            };
            sp.Children.Add(x);
            border.Child = sp;
            return border;
        }

        private void OnAddTagClick(object sender, RoutedEventArgs e)
        {
            AddTagBtn.Visibility = Visibility.Collapsed;
            AddTagBox.Text = "";
            AddTagBox.Visibility = Visibility.Visible;
            AddTagBox.Focus(FocusState.Programmatic);
        }

        private void OnAddTagBlur(object sender, RoutedEventArgs e)
        {
            // Collapse the input when it loses focus without a submission.
            // Brief delay so a click on a suggestion has time to fire.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!AddTagBox.FocusState.Equals(FocusState.Programmatic))
                {
                    AddTagBox.Visibility = Visibility.Collapsed;
                    AddTagBtn.Visibility = Visibility.Visible;
                }
            });
        }

        private void OnAddTagTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var q = (sender.Text ?? "").Trim();
            if (q.Length == 0) { sender.ItemsSource = null; return; }
            var existing = AppState.Instance.Db.GetAllTags()
                .Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Name)
                .Take(8)
                .ToList();
            sender.ItemsSource = existing;
        }

        private void OnAddTagSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (_movie == null) return;
            var raw = args.ChosenSuggestion as string ?? sender.Text;
            var name = (raw ?? "").Trim();
            if (name.Length == 0) return;
            try
            {
                var tagId = AppState.Instance.Db.EnsureTag(name);
                // AddMovieTag raises PersonalStateChanged → AppState auto-
                // writes the movie sidecar with the new tag set.
                AppState.Instance.Db.AddMovieTag(_movie.Id, tagId);
                RefreshTagChips();
                WatchlistChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Не удалось добавить тег: {ex.Message}");
            }
            sender.Text = "";
            AddTagBox.Visibility = Visibility.Collapsed;
            AddTagBtn.Visibility = Visibility.Visible;
        }

        // ── Lists (v1.9.2) — add/remove this movie to/from any user list ─────

        private void OnListsFlyoutOpening(object sender, object e)
        {
            ListsFlyout.Items.Clear();
            if (_movie == null) return;

            var lists = AppState.Instance.Db.GetUserLists();
            var membership = AppState.Instance.Db.GetUserListsForMovie(_movie.Id);

            if (lists.Count == 0)
            {
                var none = new MenuFlyoutItem { Text = CineLibraryCS.Services.LocalizationService.TranslateText("(списков пока нет)"), IsEnabled = false };
                ListsFlyout.Items.Add(none);
            }
            else
            {
                foreach (var ul in lists)
                {
                    var inList = membership.Contains(ul.Id);
                    var item = new ToggleMenuFlyoutItem
                    {
                        Text = ul.Name,
                        IsChecked = inList,
                    };
                    var capturedUl = ul;
                    item.Click += (_, _) =>
                    {
                        if (item.IsChecked)
                            AppState.Instance.Db.AddMovieToUserList(capturedUl.Id, _movie.Id);
                        else
                            AppState.Instance.Db.RemoveMovieFromUserList(capturedUl.Id, _movie.Id);
                        // Tell host so MY LISTS counts refresh
                        WatchlistChanged?.Invoke(this, EventArgs.Empty);
                        RefreshListChips();
                    };
                    ListsFlyout.Items.Add(item);
                }
            }

            ListsFlyout.Items.Add(new MenuFlyoutSeparator());
            var newItem = new MenuFlyoutItem { Text = "+ Новый список…" };
            newItem.Click += async (_, _) =>
            {
                var name = await PromptNewListName();
                if (string.IsNullOrWhiteSpace(name) || _movie == null) return;
                try
                {
                    var listId = AppState.Instance.Db.CreateUserList(name.Trim());
                    AppState.Instance.Db.AddMovieToUserList(listId, _movie.Id);
                    WatchlistChanged?.Invoke(this, EventArgs.Empty);
                    RefreshListChips();
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    if (App.MainWindow is MainWindow mw)
                        mw.ShowToast($"{CineLibraryCS.Services.LocalizationService.TranslateText("A list named")} «{name.Trim()}» {CineLibraryCS.Services.LocalizationService.TranslateText("already exists")}");
                }
            };
            ListsFlyout.Items.Add(newItem);
        }

        private async Task<string?> PromptNewListName()
        {
            var box = new TextBox { PlaceholderText = CineLibraryCS.Services.LocalizationService.TranslateText("Название списка") };
            var dlg = new ContentDialog
            {
                Title = CineLibraryCS.Services.LocalizationService.TranslateText("Новый список"),
                Content = box,
                PrimaryButtonText = CineLibraryCS.Services.LocalizationService.TranslateText("Create"),
                CloseButtonText = CineLibraryCS.Services.LocalizationService.TranslateText("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = MainWindow.CurrentTheme,
            };
            box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
            var result = await dlg.ShowAsync();
            return result == ContentDialogResult.Primary ? box.Text : null;
        }

        // ── Notes (v1.9, hybrid DB + sidecar) ────────────────────────────────

        /// <summary>
        /// Renders the Notes panel in either viewing or editing mode based on
        /// _movie.Note. Called after load and after save/cancel.
        /// </summary>
        private void UpdateNoteUi(bool viewing)
        {
            if (_movie == null) return;
            var hasNote = !string.IsNullOrWhiteSpace(_movie.Note);

            if (viewing)
            {
                NoteText.Text = _movie.Note ?? "";
                NoteText.Visibility = hasNote ? Visibility.Visible : Visibility.Collapsed;
                NoteEditor.Visibility = Visibility.Collapsed;
                NoteAddBtn.Visibility = hasNote ? Visibility.Collapsed : Visibility.Visible;
                NoteEditBtn.Visibility = hasNote ? Visibility.Visible : Visibility.Collapsed;
                NoteSaveBtn.Visibility = Visibility.Collapsed;
                NoteCancelBtn.Visibility = Visibility.Collapsed;
                NoteOfflineHint.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Editing mode
                NoteEditor.Text = _movie.Note ?? "";
                NoteText.Visibility = Visibility.Collapsed;
                NoteEditor.Visibility = Visibility.Visible;
                NoteAddBtn.Visibility = Visibility.Collapsed;
                NoteEditBtn.Visibility = Visibility.Collapsed;
                NoteSaveBtn.Visibility = Visibility.Visible;
                NoteCancelBtn.Visibility = Visibility.Visible;
                NoteOfflineHint.Visibility = _movie.IsOnline ? Visibility.Collapsed : Visibility.Visible;
                NoteEditor.Focus(FocusState.Programmatic);
            }
        }

        private void OnNoteEditStart(object sender, RoutedEventArgs e) => UpdateNoteUi(viewing: false);

        private void OnNoteCancel(object sender, RoutedEventArgs e) => UpdateNoteUi(viewing: true);

        private void OnNoteSave(object sender, RoutedEventArgs e)
        {
            if (_movie == null) return;
            var newNote = (NoteEditor.Text ?? "").Trim();

            // 1) DB always succeeds (even when drive is offline)
            AppState.Instance.Db.SetNote(_movie.Id, newNote);
            _movie.Note = string.IsNullOrEmpty(newNote) ? null : newNote;

            // 2) Best-effort sidecar write next to the .nfo. Skipped silently
            //    when drive offline / read-only / network glitch.
            TryWriteSidecar(_movie, newNote);

            UpdateNoteUi(viewing: true);
        }

        private static void TryWriteSidecar(MovieDetail m, string note)
        {
            if (!m.IsOnline || m.CurrentLetter == null || m.FolderRelPath == null) return;
            try
            {
                var folder = Path.Combine($"{m.CurrentLetter}:\\", m.FolderRelPath.Replace('/', '\\'));
                if (!Directory.Exists(folder)) return;
                var sidecar = Path.Combine(folder, ScannerService.NoteSidecarFileName);
                if (string.IsNullOrEmpty(note))
                {
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                }
                else
                {
                    File.WriteAllText(sidecar, note, System.Text.Encoding.UTF8);
                }
            }
            catch { /* sidecar is best-effort; DB is the source of truth */ }
        }
    }