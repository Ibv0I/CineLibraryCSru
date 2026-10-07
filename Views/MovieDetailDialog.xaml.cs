using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using CineМедиатекаCS.Models;
using CineМедиатекаCS.Services;
using Windows.Graphics;
using Windows.System;
using System.Продолжительность.InteropServices.WindowsПродолжительность;

namespace CineМедиатекаCS.Views;

// A resizable Window-based detail view (replaces the old fixed ContentDialog).
public sealed partial class MovieDetailDialog : Window
{
    private readonly int _movieId;
    private MovieDetail? _movie;
    private bool _closed;

    public event EventHandler? Список просмотраChanged;

    public MovieDetailDialog(int movieId)
    {
        _movieId = movieId;
        InitializeComponent();

        // Custom titlebar drag region + Mica
        ExtendsContentIntoНазваниеBar = true;
        SetНазваниеBar(AppНазваниеBar);
        SystemНазадdrop = new MicaНазадdrop();

        // Window size — restore the last user-resized size if we have one,
        // otherwise fall back to a comfortable default (1100×800). Сохранитьd on
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
        appWindow.Название = "Информация о фильме";

        // Ensure window is resizable and maximizable, then open maximized
        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.Maximize();
        }

        // Esc closes the dialog
        var escAcc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.Escape
        };
        escAcc.Invoked += (_, a) => { a.Handled = true; Закрыть(); };
        RootGrid.KeyboardAccelerators.Добавить(escAcc);
        // No floating "Esc" key tip following the pointer (same as MainWindow).
        RootGrid.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

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

        // Inherit theme from main window
        if (RootGrid is FrameworkElement fe)
            fe.RequestedTheme = MainWindow.CurrentTheme;

        // Track close so async load that finishes after Esc doesn't try to
        // touch the destroyed window (COMException "operation identifier is
        // not valid").
        Закрытьd += (_, _) => _closed = true;

        // Start DB load IMMEDIATELY (don't wait for Activated). Overlapping
        // with the window animation eliminates the visible "empty window"
        // delay that made the dialog feel slow.
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _movie = await Task.Run(() =>
            AppState.Instance.Db.GetMovieDetail(_movieId, AppState.Instance.Connected));

        if (_movie == null || _closed) return;
        try { PopulateUi(_movie); }
        catch (System.Продолжительность.InteropServices.COMException)
        {
            // Window was closed mid-populate — silently bail.
        }
    }

    private void PopulateUi(MovieDetail m)
    {
        Название = m.Название;
        НазваниеBarText.Text = m.Название;
        DetailНазвание.Text = m.Название;

        if (m.OriginalНазвание != null && m.OriginalНазвание != m.Название)
        {
            OriginalНазвание.Text = m.OriginalНазвание;
            OriginalНазвание.Visibility = Visibility.Visible;
        }
        if (m.Tagline != null)
        {
            Tagline.Text = m.Tagline;
            TaglineWrap.Visibility = Visibility.Visible;
        }

        // Sticky bar title (v2.3) — shown when scrolled past hero
        StickyНазвание.Text = m.Название;

        // IMDb / TMDb top-right pill buttons (v2.3)
        ImdbLinkBtn.Visibility = !string.IsNullOrWhiteSpace(m.ImdbId) ? Visibility.Visible : Visibility.Collapsed;
        TmdbLinkBtn.Visibility = !string.IsNullOrWhiteSpace(m.TmdbId) ? Visibility.Visible : Visibility.Collapsed;

        // Chips
        ChipsPanel.Children.Clear();
        void ДобавитьChip(string text, string? bg = null)
        {
            var border = new Border
            {
                Назадground = new SolidColorBrush(bg != null
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
            ChipsPanel.Children.Добавить(border);
        }

        if (m.Рейтинг.HasValue)
        {
            // Prominent bright-yellow IMDB rating chip (first, before year/runtime)
            var ratingBorder = new Border
            {
                Назадground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFA, 0xCC, 0x15)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 3, 10, 3),
            };
            ratingBorder.Child = new TextBlock
            {
                Text = $"★ {m.Рейтинг:F1}",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x14, 0x00))
            };
            ChipsPanel.Children.Добавить(ratingBorder);
        }
        if (m.Год.HasValue) ДобавитьChip(m.Год.ToString()!);
        if (m.Продолжительность.HasValue) ДобавитьChip($"{m.Продолжительность} min");
        if (m.Mpaa != null) ДобавитьChip(m.Mpaa);
        if (m.IsMissing) ДобавитьChip("ОТСУТСТВУЕТ", "#3A1010");

        // Drive
        DriveStatusDot.Fill = new SolidColorBrush(m.IsВкл.line
            ? Windows.UI.Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E)
            : Windows.UI.Color.FromArgb(0xFF, 0x6B, 0x72, 0x80));
        DriveText.Text = m.DriveLabel + (m.CurrentLetter != null ? $" ({m.CurrentLetter}:)" : "");

        // Actions
        ВоспроизвестиBtn.IsEnabled = m.Воспроизвестиable;
        ПапкаBtn.IsEnabled = m.IsВкл.line;
        FavBtn.Content = m.IsИзбранное ? "★ В избранноеd" : "☆ Избранное";
        ПросмотреноBtn.Content = m.IsПросмотрено ? "✓ Просмотрено" : "○ Отметить просмотренным";
        Список просмотраBtn.Content = m.IsСписок просмотра ? "📌 In Список просмотра" : "☐ Добавить to Список просмотра";


        // Заметки
        ОбновитьNoteUi(viewing: true);

        // Plot (fall back to outline — many MediaElch NFOs use <outline> only)
        var plotText = m.Plot ?? m.Outline;
        if (!string.IsNullOrWhiteSpace(plotText))
        {
            PlotText.Text = plotText;
            PlotSection.Visibility = Visibility.Visible;
            PlotDivider.Visibility = Visibility.Visible;
        }

        // Жанры — clickable, filters library on click
        if (m.Жанры.Count > 0)
        {
            ЖанрыField.Visibility = Visibility.Visible;
            GenreLinks.Children.Clear();
            foreach (var g in m.Жанры)
            {
                var captured = g;
                var btn = new HyperlinkButton { Content = g, Padding = new Thickness(0), FontSize = 13 };
                btn.Click += (_, _) => NavigateAndЗакрыть(mw => mw.NavigateМедиатекаByGenre(captured));
                GenreLinks.Children.Добавить(btn);
            }
        }

        // Режиссёрs — clickable
        if (m.Режиссёрs.Count > 0)
        {
            РежиссёрField.Visibility = Visibility.Visible;
            РежиссёрLinks.Children.Clear();
            foreach (var d in m.Режиссёрs)
            {
                var captured = d;
                var btn = new HyperlinkButton { Content = d, Padding = new Thickness(0), FontSize = 13 };
                btn.Click += (_, _) => NavigateAndЗакрыть(mw => mw.NavigateМедиатекаByРежиссёр(captured));
                РежиссёрLinks.Children.Добавить(btn);
            }
        }

        // Tech badges + ratings + file info (v2.2)
        PopulateTechBadges(m);
        PopulateРейтингsPanel(m);
        PopulateFileInfo(m);
        // Lists this movie is on (v2.5) — chip row with per-list ✕.
        RefreshListChips();
        // v2.9 — Free-form tags chip row + autocomplete.
        RefreshTagChips();

        // Студия — clickable HyperlinkButton (was a plain TextBlock)
        if (m.Студия != null)
        {
            СтудияLabel.Visibility = Visibility.Visible;
            СтудияText.Visibility = Visibility.Collapsed;
            СтудияLink.Visibility = Visibility.Visible;
            СтудияLink.Content = m.Студия;
            // Detach prior handler in case dialog is reused
            СтудияLink.Click -= Вкл.СтудияClick;
            СтудияLink.Click += Вкл.СтудияClick;
        }

        // Актёры — show the list right away; trickle thumbnail loads in
        // batches so the first paint isn't blocked by many parallel image
        // resolves. Pass the absolute movie folder so .actors/ thumbs are
        // discovered for online drives.
        if (m.Actors.Count > 0)
        {
            АктёрыSection.Visibility = Visibility.Visible;
            АктёрыDivider.Visibility = Visibility.Visible;
            АктёрыRepeater.ItemsSource = m.Actors;
            FitАктёрыCardHeight(m.Actors);
            string? movieПапкаAbs = null;
            if (m.IsВкл.line && m.CurrentLetter != null && m.ПапкаRelPath != null)
                movieПапкаAbs = Path.Combine($"{m.CurrentLetter}:\\",
                    m.ПапкаRelPath.Replace('/', '\\'));
            _ = LoadАктёрыThumbsAsync(m.Actors, movieПапкаAbs);
        }

        // Images
        // v4.2.0: decoded for the bigger poster and the full-width banner (the
        // window opens maximised, so its width is about the banner's in pixels)
        LoadImageAsync(m.LocalPoster, PosterImage, PosterPlaceholder, 520);
        LoadImageAsync(m.LocalFanart, HeroImage, null, Math.Clamp(AppWindow.Size.Width, 1920, 3840));

        // v3.4 — "Получить недостающую информацию из TMDB" (and, later, Синхронизировать с диском).
        ОбновитьTmdbActions(m);
    }

    // ── v3.4 TMDB enrichment ──────────────────────────────────────────────────

    /// <summary>True when the movie is missing something a TMDB fetch can fill.</summary>
    private static bool HasGaps(MovieDetail m) =>
        string.IsNullOrWhiteSpace(m.LocalPoster) ||
        string.IsNullOrWhiteSpace(m.LocalFanart) ||
        (string.IsNullOrWhiteSpace(m.Plot) && string.IsNullOrWhiteSpace(m.Outline)) ||
        m.Actors.Count == 0 ||
        !m.Год.HasValue || !m.Продолжительность.HasValue || !m.Рейтинг.HasValue ||
        string.IsNullOrWhiteSpace(m.Студия);

    private bool IsArchiveRecord(MovieDetail m) => m.VolumeSerial == "__archive__";

    /// <summary>
    /// The Fetch button is shown on EVERY movie — partial scrapes are common and
    /// a heuristic kept hiding it on movies that really did have gaps. Вкл. a fully
    /// complete movie a fetch is simply a harmless no-op (everything is fill-only).
    /// </summary>
    private void ОбновитьTmdbActions(MovieDetail m)
    {
        FetchTmdbBtn.Visibility = Visibility.Visible;
        // Persisting fetched data to the drive is handled by the Диски page's
        // existing "Sync state to drive" action, so there's no per-movie Sync
        // button here (avoids a third button for the same kind of action).
        SyncDriveBtn.Visibility = Visibility.Collapsed;
        TmdbActionsRow.Visibility = Visibility.Visible;
        TmdbStatus.Visibility = Visibility.Collapsed;
    }

    private void TmdbBusyState(bool busy, string? status = null)
    {
        TmdbBusy.IsActive = busy;
        FetchTmdbBtn.IsEnabled = !busy;
        SyncDriveBtn.IsEnabled = !busy;
        if (status != null) { TmdbStatus.Text = status; TmdbStatus.Visibility = Visibility.Visible; }
    }

    private async void Вкл.FetchMissing(object sender, RoutedEventArgs e)
    {
        if (_movie == null) return;
        var m = _movie;
        using var client = new Services.Tmdb.TmdbClient();

        TmdbBusyState(true, "Looking up TMDb…");
        try
        {
            // Match: stored tmdb_id is exact; otherwise let the user confirm.
            Services.Tmdb.TmdbMovie? d = null;
            if (int.TryParse(m.TmdbId, out var tid) && tid > 0)
            {
                d = await client.GetMovieDetailsAsync(tid);
            }
            else
            {
                var picker = new TmdbPickerDialog(client, m.Название, m.Год)
                {
                    XamlRoot = (Content as FrameworkElement)?.XamlRoot
                };
                var pick = await picker.ShowAsync();
                if (pick != ContentDialogResult.Primary || picker.Picked == null)
                {
                    TmdbBusyState(false);
                    ОбновитьTmdbActions(m);
                    return;
                }
                d = await client.GetMovieDetailsAsync(picker.Picked.TmdbId);
            }

            if (d == null)
            {
                TmdbBusyState(false, "Couldn't reach TMDb. Try again.");
                return;
            }

            // Poster / fanart → portable cache, only if currently missing.
            string? posterRel = null, fanartRel = null;
            if (string.IsNullOrWhiteSpace(m.LocalPoster) && !string.IsNullOrEmpty(d.PosterPath))
                posterRel = await DownloadArtAsync(client, d.PosterPath!, "manual_posters", d.TmdbId);
            if (string.IsNullOrWhiteSpace(m.LocalFanart) && !string.IsNullOrEmpty(d.НазадdropPath))
                fanartRel = await DownloadArtAsync(client, d.НазадdropPath!, "manual_fanart", d.TmdbId);

            var studio = d.ProductionCompanies.Count > 0 ? d.ProductionCompanies[0].Name : null;
            var country = d.ProductionCountries.Count > 0 ? d.ProductionCountries[0].Name : null;

            AppState.Instance.Db.FillMovieGaps(
                m.Id,
                year: d.Год > 0 ? d.Год : null,
                rating: d.Рейтинг > 0 ? d.Рейтинг : null,
                votes: d.VoteCount > 0 ? d.VoteCount : null,
                runtime: d.Продолжительность > 0 ? d.Продолжительность : null,
                plot: string.IsNullOrWhiteSpace(d.Overview) ? null : d.Overview,
                tagline: string.IsNullOrWhiteSpace(d.Tagline) ? null : d.Tagline,
                mpaa: string.IsNullOrWhiteSpace(d.Certification) ? null : d.Certification,
                imdbId: string.IsNullOrWhiteSpace(d.ImdbId) ? null : d.ImdbId,
                tmdbId: d.TmdbId.ToString(),
                premiered: string.IsNullOrWhiteSpace(d.ReleaseDate) ? null : d.ReleaseDate,
                studio: studio, country: country,
                posterRel: posterRel, fanartRel: fanartRel);

            // Жанры / directors / writers — fill-only, so the detail window's
            // Жанры and Режиссёр rows aren't left blank on a partial scrape.
            var genreNames = new List<string>();
            foreach (var g in d.Жанры)
                if (!string.IsNullOrWhiteSpace(g.Name)) genreNames.Добавить(g.Name);
            AppState.Instance.Db.FillMovieGenreРежиссёрWriter(
                m.Id, genreNames, d.Режиссёрs, d.Writers);

            // Актёры — fetch photos for the top-billed cast and link them. Runs
            // even when the movie already has cast, because MediaElch NFOs often
            // store actor <thumb>s as TMDb http URLs (blank offline) or none at
            // all; we download portable copies and ДобавитьManualActors upgrades those
            // weak thumbs to the local cache.
            if (d.Актёры.Count > 0)
            {
                TmdbBusyState(true, "Fetching cast photos…");
                var actors = new List<(string, string?, int, string?)>();
                foreach (var c in d.Актёры)
                {
                    if (string.IsNullOrWhiteSpace(c.Name)) continue;
                    string? thumbRel = null;
                    if (!string.IsNullOrEmpty(c.ProfilePath))
                        thumbRel = await DownloadArtRawAsync(client, c.ProfilePath!, "manual_actors", "w185");
                    actors.Добавить((c.Name, string.IsNullOrWhiteSpace(c.Character) ? null : c.Character,
                                c.Order, thumbRel));
                }
                AppState.Instance.Db.ДобавитьManualActors(m.Id, actors);
            }

            // Reload from the DB and re-render in place.
            TmdbBusyState(false, "Обновитьd.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Fetch missing info failed: {ex.Message}");
            TmdbBusyState(false, "Something went wrong. Please try again.");
        }
    }

    /// <summary>Downloads original-size art into a data-folder subfolder named by
    /// tmdb id; returns the data-relative path, or null on failure.</summary>
    internal static async Task<string?> DownloadArtAsync(
        Services.Tmdb.TmdbClient client, string tmdbPath, string subfolder, int tmdbId)
    {
        var fileName = $"{tmdbId}-{Guid.НовыйGuid():N}.jpg";
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

    private void Вкл.SyncToDrive(object sender, RoutedEventArgs e)
    {
        // Phase B — writes fetched NFO + poster + .actors back to the movie's
        // folder when its drive is online. Wired up in the next build.
    }

    /// <summary>
    /// v3.8.0: the cast grid gives every card the same height, so size it for
    /// the longest-wrapping name and role, measured with the user's text size.
    /// A fixed 280 cut off the role under a two-line name.
    /// </summary>
    private void FitАктёрыCardHeight(IReadВкл.lyList<Models.Actor> actors)
    {
        var font = (FontСемья)Application.Current.Resources["ContentControlThemeFontСемья"];
        double TextHeight(string? text, double size, Windows.UI.Text.FontWeight weight)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var probe = new TextBlock
            {
                Text = text, FontСемья = font, FontSize = size, FontWeight = weight,
                TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 2,
            };
            probe.Measure(new Windows.Foundation.Size(140, double.PositiveInfinity));
            return probe.DesiredSize.Height;
        }
        var name = actors.Max(a => TextHeight(a.Name, 13, Microsoft.UI.Text.FontWeights.SemiBold));
        var role = actors.Max(a => TextHeight(a.Role, 11, Microsoft.UI.Text.FontWeights.Normal));
        // 210 headshot + two 8 px gaps, plus a little slack for rounding.
        АктёрыGridLayout.MinItemHeight = Math.Max(280, Math.Ceiling(210 + 8 + name + 8 + role + 4));
    }

    private static readonly string[] ActorThumbExts = { ".jpg", ".jpeg", ".png", ".tbn", ".webp" };

    /// <summary>
    /// Resolves an actor's thumbnail in priority order:
    ///   1. The movie folder's `.actors/` folder — MediaElch's standard
    ///      cache, named `First_Last.jpg` (underscores for spaces).
    ///   2. Inline `<thumb>` URL from the nfo (TMDb http URL or local path).
    /// Falls back to the initials TextBlock behind the Image when both miss.
    /// </summary>
    private static void LoadActorThumb(Models.Actor a, string? movieПапкаAbs)
    {
        try
        {
            // 1) Local .actors folder
            if (!string.IsNullOrEmpty(movieПапкаAbs))
            {
                var actorsDir = Path.Combine(movieПапкаAbs, ".actors");
                if (Режиссёрy.Exists(actorsDir))
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
                            if (File.Exists(p)) { ПрименитьBitmap(a, SafeFileUri(p)); return; }
                        }
                    }
                }
            }

            // 2) Inline thumb — an http URL, an absolute file path, or (for
            //    manual Просмотрено и удалено records) a path relative to the portable
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
                if (uri != null) ПрименитьBitmap(a, uri);
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
        // "#Bollywood Фильмы" folder became %2523Bollywood…), which broke every
        // actor photo whose path contained '#' or '?'. Let .NET do the encoding.
        return new Uri(absolutePath);
    }

    private static void ПрименитьBitmap(Models.Actor a, Uri uri)
    {
        // 280 px decode = ~2× the 140-wide rendered slot for HiDPI crispness
        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 280 };
        bmp.UriSource = uri;
        a.ThumbBitmap = bmp;
    }

    /// <summary>
    /// Trickle-loads actor thumbnails in small batches with a yield between
    /// each batch. Prevents many parallel image loads from blocking the
    /// first paint of the dialog.
    /// </summary>
    private async Task LoadАктёрыThumbsAsync(IReadВкл.lyList<Models.Actor> actors, string? movieПапкаAbs)
    {
        const int batchSize = 6;
        for (int i = 0; i < actors.Count; i++)
        {
            if (_closed) return;
            LoadActorThumb(actors[i], movieПапкаAbs);
            if ((i + 1) % batchSize == 0)
                await Task.Delay(30);
        }
    }

    private async void LoadImageAsync(string? relPath, Image imgControl, FrameworkElement? placeholder, int decodeWidth)
    {
        if (relPath == null) return;
        var fullPath = AppState.Instance.Db.GetCachedImagePath(relPath);
        if (fullPath == null) return;
        try
        {
            var bytes = await Task.Run(() => ImageCache.GetOrLoad(relPath!, fullPath));
            if (bytes == null) return;
            var bmp = new BitmapImage { DecodePixelWidth = decodeWidth };
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await ms.WriteAsync(bytes.AsBuffer());
            ms.Seek(0);
            await bmp.SetSourceAsync(ms);
            imgControl.Source = bmp;
            if (placeholder != null) placeholder.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    // ── Actions ───────────────────────────────────────────────────────────

    private void Вкл.СтудияClick(object sender, RoutedEventArgs e)
    {
        if (_movie?.Студия == null) return;
        var s = _movie.Студия;
        NavigateAndЗакрыть(mw => mw.NavigateМедиатекаByСтудия(s));
    }

    private void Вкл.ActorClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string actor)
            NavigateAndЗакрыть(mw => mw.NavigateМедиатекаByActor(actor));
    }

    /// <summary>
    /// Применить a library filter via MainWindow then close this detail window.
    /// </summary>
    private void NavigateAndЗакрыть(Action<MainWindow> nav)
    {
        if (App.MainWindow is MainWindow mw) nav(mw);
        Закрыть();
    }

    // ── Tech badges, ratings, trailer, file info (v2.2) ──────────────────────

    private void PopulateTechBadges(MovieDetail m)
    {
        TechBadges.Items.Clear();
        // Resolution
        var resLabel = ResolutionLabel(m.VideoWidth, m.VideoHeight);
        if (resLabel != null) ДобавитьTechBadge(resLabel, "#3B82F6"); // blue
        // Aspect ratio
        var aspLabel = NormalizeAspect(m.VideoAspect);
        if (aspLabel != null) ДобавитьTechBadge(aspLabel, "#475569"); // slate
        // Video codec
        if (!string.IsNullOrWhiteSpace(m.VideoCodec))
            ДобавитьTechBadge(m.VideoCodec.ToUpperInvariant(), "#7C3AED"); // violet
        // HDR
        if (!string.IsNullOrWhiteSpace(m.HdrType))
            ДобавитьTechBadge(m.HdrType.ToUpperInvariant(), "#EAB308"); // amber
        // Аудио codec + channels
        var aud = АудиоLabel(m.АудиоCodec, m.АудиоChannels);
        if (aud != null) ДобавитьTechBadge(aud, "#10B981"); // emerald
    }

    private void ДобавитьTechBadge(string text, string hex)
    {
        // Gradient + thin top-highlight border for a polished embossed look
        var topColor = HexColor(hex);
        var bottomColor = ТёмнаяenColor(topColor, 0.85);
        var bg = new Microsoft.UI.Xaml.Media.LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
        };
        bg.GradientStops.Добавить(new Microsoft.UI.Xaml.Media.GradientStop { Color = topColor, Выкл.set = 0 });
        bg.GradientStops.Добавить(new Microsoft.UI.Xaml.Media.GradientStop { Color = bottomColor, Выкл.set = 1 });

        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Назадground = bg,
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
        TechBadges.Items.Добавить(border);
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

    private static Windows.UI.Color ТёмнаяenColor(Windows.UI.Color c, double factor)
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

    private static string? АудиоLabel(string? codec, string? channels)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(codec)) parts.Добавить(codec.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(channels))
        {
            var lbl = channels switch
            {
                "8" => "7.1",
                "7" => "6.1",
                "6" => "5.1",
                "2" => "Stereo",
                "1" => "Mono",
                _ => $"{channels} ch",
            };
            parts.Добавить(lbl);
        }
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private void PopulateРейтингsPanel(MovieDetail m)
    {
        РейтингsPanel.Children.Clear();
        if (m.ВсеРейтингs.Count == 0)
        {
            РейтингsPanel.Visibility = Visibility.Collapsed;
            return;
        }
        РейтингsPanel.Visibility = Visibility.Visible;
        foreach (var rt in m.ВсеРейтингs)
        {
            var sourceLabel = PrettySource(rt.Source);
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Добавить(new TextBlock
            {
                Text = sourceLabel.ToUpperInvariant(),
                FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 150, Opacity = 0.65,
                Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("MutedBrush"),
            });
            stack.Children.Добавить(new TextBlock
            {
                Text = rt.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                       + (rt.Source.Equals("rottentomatoes", StringComparison.OrdinalIgnoreCase) ? "%" : ""),
                FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("TextBrush"),
            });
            if (rt.Votes.HasValue && rt.Votes.Value > 0)
            {
                stack.Children.Добавить(new TextBlock
                {
                    Text = FormatVotes(rt.Votes.Value),
                    FontSize = 10,
                    Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("MutedBrush"),
                });
            }
            РейтингsPanel.Children.Добавить(stack);
        }
    }

    private static string PrettySource(string src) => src.ToLowerInvariant() switch
    {
        "imdb" => "IMDb",
        "themoviedb" or "tmdb" => "TMDb",
        "rottentomatoes" => "Rotten Tomatoes",
        "metacritic" => "Metacritic",
        "default" => "Рейтинг",
        _ => char.ToUpper(src[0]) + src.Substring(1),
    };

    private static string FormatVotes(int votes) => votes switch
    {
        >= 1_000_000 => $"{votes / 1_000_000.0:F1}M votes",
        >= 1_000     => $"{votes / 1_000.0:F1}K votes",
        _            => $"{votes} votes",
    };

    private void PopulateFileInfo(MovieDetail m)
    {
        bool any = false;
        // Продолжительность / Дата выхода / Rated / Страна — folded in from the old
        // below-poster column so the row stays compact (v2.3.x).
        if (m.Продолжительность.HasValue && m.Продолжительность.Value > 0)
        {
            FiПродолжительность.Text = $"{m.Продолжительность.Value} min";
            FiПродолжительностьBlock.Visibility = Visibility.Visible;
            any = true;
        }
        if (!string.IsNullOrWhiteSpace(m.Premiered))
        {
            FiRelease.Text = DateTime.TryParse(m.Premiered, out var dt)
                ? dt.ToString("MMM d, yyyy")
                : m.Premiered!;
            FiReleaseBlock.Visibility = Visibility.Visible;
            any = true;
        }
        else if (m.Год.HasValue)
        {
            FiRelease.Text = m.Год.Value.ToString();
            FiReleaseBlock.Visibility = Visibility.Visible;
            any = true;
        }
        if (!string.IsNullOrWhiteSpace(m.Mpaa))
        {
            FiMpaa.Text = m.Mpaa!;
            FiMpaaBlock.Visibility = Visibility.Visible;
            any = true;
        }
        if (!string.IsNullOrWhiteSpace(m.Страна))
        {
            FiСтрана.Text = m.Страна!;
            FiСтранаBlock.Visibility = Visibility.Visible;
            any = true;
        }
        if (m.FileSizeBytes.HasValue && m.FileSizeBytes.Value > 0)
        {
            FiSize.Text = FormatBytes(m.FileSizeBytes.Value);
            FiSizeBlock.Visibility = Visibility.Visible;
            any = true;
        }
        if (m.DurationSeconds.HasValue && m.DurationSeconds.Value > 0)
        {
            FiDur.Text = FormatDuration(m.DurationSeconds.Value);
            FiDurBlock.Visibility = Visibility.Visible;
            any = true;
        }
        if (!string.IsNullOrWhiteSpace(m.ContainerExt))
        {
            FiContainer.Text = m.ContainerExt.ToUpperInvariant();
            FiContainerBlock.Visibility = Visibility.Visible;
            any = true;
        }
        if (!string.IsNullOrWhiteSpace(m.АудиоLanguages))
        {
            FiLangs.Text = m.АудиоLanguages.Replace(",", " · ");
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
                _ => $"{subs.Length} tracks",
            };
            FiSubsBlock.Visibility = Visibility.Visible;
            any = true;
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
    private void Вкл.ContentScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var inner = e.НовыйSize.Width - ContentScroller.Padding.Left - ContentScroller.Padding.Right;
        ContentStack.Width = Math.Max(0, inner);
        HeroBorder.Height = Math.Clamp(inner / 3.2, 220, 460);   // 1100 wide = the old 340
        ПрименитьLayout(e.НовыйSize.Width < 900 ? DetailLayout.Narrow
                  : e.НовыйSize.Width >= 1600 ? DetailLayout.Wide
                  : DetailLayout.Standard);
    }

    private enum DetailLayout { Unset, Narrow, Standard, Wide }
    private DetailLayout _layout;

    private void ПрименитьLayout(DetailLayout layout)
    {
        if (layout == _layout) return;
        _layout = layout;
        bool narrow = layout == DetailLayout.Narrow, wide = layout == DetailLayout.Wide;

        PosterFrame.Width  = narrow ? 150 : wide ? 260 : 200;
        PosterFrame.Height = narrow ? 218 : wide ? 377 : 290;
        DetailНазвание.FontSize   = narrow ? 26 : 34;
        DetailНазвание.LineHeight = narrow ? 32 : 40;

        // Wide: everything up to the cast sits beside the poster. Buttons and plot
        // go under the title; genres, director, studio, file info and notes get a
        // column of their own. Otherwise they stack under the poster as before.
        foreach (var block in new FrameworkElement[] { ActionsBlock, FileInfoPanel, PlotBlock, ЗаметкиCard, FieldsBlock })
            Detach(block);
        if (wide)
        {
            MetaStack.Children.Добавить(ActionsBlock);
            MetaStack.Children.Добавить(PlotBlock);
            SideStack.Children.Добавить(FieldsBlock);
            SideStack.Children.Добавить(FileInfoPanel);
            SideStack.Children.Добавить(ЗаметкиCard);
        }
        else
        {
            ContentStack.Children.Insert(ContentStack.Children.IndexOf(TopGrid) + 1, ActionsBlock);
            BodyStack.Children.Добавить(FileInfoPanel);
            BodyStack.Children.Добавить(PlotBlock);
            BodyStack.Children.Добавить(ЗаметкиCard);
            BodyStack.Children.Добавить(FieldsBlock);
        }
        // The title column stops at a readable width; the details column takes the rest.
        var main = TopGrid.ColumnDefinitions[1];
        var side = TopGrid.ColumnDefinitions[2];
        main.Width = new GridLength(wide ? 3 : 1, GridUnitType.Star);
        main.MaxWidth = wide ? 900 : double.PositiveInfinity;
        side.Width = wide ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        side.MinWidth = wide ? 340 : 0;
        // At the top of the details column the divider has nothing to divide
        FieldsDivider.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        FieldsGrid.Margin = new Thickness(0, wide ? 0 : 20, 0, 0);
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.Parent is Panel parent) parent.Children.Remove(element);
    }

    // v4.2.0: Жанры, Режиссёр and Студия side by side, or one under another
    // when the grid is narrow (a small window, or the wide layout's side column).
    private void Вкл.FieldsGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool stack = e.НовыйSize.Width < 600;
        FieldsGrid.ColumnDefinitions[1].Width = stack ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FieldsGrid.ColumnDefinitions[2].Width = stack ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        for (int i = 0; i < FieldsGrid.Children.Count; i++)
        {
            var field = (FrameworkElement)FieldsGrid.Children[i];
            Grid.SetColumn(field, stack ? 0 : i);
            Grid.SetRow(field, stack ? i : 0);
            field.Margin = new Thickness(0, stack && i > 0 ? 14 : 0, 0, 0);
        }
    }

    // ── Sticky action bar + external link buttons (v2.3) ─────────────────

    private void Вкл.ContentScrolled(object sender, ScrollViewerViewChangedEventArgs e)
    {
        // Bar appears once the user is past the hero block (its height follows the width)
        StickyBar.Visibility = ContentScroller.VerticalВыкл.set > HeroBorder.Height - 60
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Вкл.StickyЗакрыть(object sender, RoutedEventArgs e) => Закрыть();

    private async void Вкл.OpenImdb(object sender, RoutedEventArgs e)
    {
        if (_movie?.ImdbId == null) return;
        try { await Launcher.LaunchUriAsync(new Uri($"https://www.imdb.com/title/{_movie.ImdbId}/")); }
        catch
        {
            if (App.MainWindow is MainWindow mw)
                mw.ShowToast("Couldn't open IMDb — no browser available?");
        }
    }

    private async void Вкл.OpenTmdb(object sender, RoutedEventArgs e)
    {
        if (_movie?.TmdbId == null) return;
        try { await Launcher.LaunchUriAsync(new Uri($"https://www.themoviedb.org/movie/{_movie.TmdbId}")); }
        catch
        {
            if (App.MainWindow is MainWindow mw)
                mw.ShowToast("Couldn't open TMDb — no browser available?");
        }
    }

    private async void Вкл.Воспроизвести(object sender, RoutedEventArgs e)
    {
        if (_movie?.CurrentLetter == null || _movie.VideoFileRelPath == null) return;
        var letter = _movie.CurrentLetter;
        var videoPath = Path.Combine($"{letter}:\\", _movie.VideoFileRelPath.Replace('/', '\\'));
        if (File.Exists(videoPath))
        {
            // Stamp Продолжить просмотр: we can't see real playback position once
            // the OS player takes over, so the row simply moves to the top of
            // the Продолжить просмотр list and stays until the user marks it Просмотрено.
            AppState.Instance.Db.MarkВоспроизвестиed(_movie.Id);
            // Tell the host so its sidebar badge updates
            Список просмотраChanged?.Invoke(this, EventArgs.Empty);
            await VideoВоспроизвестиer.ВоспроизвестиAsync(videoPath);
        }
    }

    private async void Вкл.OpenПапка(object sender, RoutedEventArgs e)
    {
        if (_movie?.CurrentLetter == null || _movie.ПапкаRelPath == null) return;
        var folderPath = Path.Combine($"{_movie.CurrentLetter}:\\", _movie.ПапкаRelPath.Replace('/', '\\'));
        if (Режиссёрy.Exists(folderPath))
            await Launcher.LaunchПапкаPathAsync(folderPath);
    }

    private void Вкл.ToggleFav(object sender, RoutedEventArgs e)
    {
        if (_movie == null) return;
        AppState.Instance.Db.ToggleИзбранное(_movie.Id);
        _movie.IsИзбранное = !_movie.IsИзбранное;
        FavBtn.Content = _movie.IsИзбранное ? "★ В избранноеd" : "☆ Избранное";
    }

         private void Вкл.ToggleПросмотрено(object sender, RoutedEventArgs e)
         {
             if (_movie == null) return;
             AppState.Instance.Db.ToggleПросмотрено(_movie.Id);
             _movie.IsПросмотрено = !_movie.IsПросмотрено;
             ПросмотреноBtn.Content = _movie.IsПросмотрено ? "✓ Просмотрено" : "○ Отметить просмотренным";
         }

         private void Вкл.ToggleСписок просмотра(object sender, RoutedEventArgs e)
         {
             if (_movie == null) return;
             AppState.Instance.Db.SetСписок просмотра(_movie.Id, !_movie.IsСписок просмотра);
             _movie.IsСписок просмотра = !_movie.IsСписок просмотра;
             Список просмотраBtn.Content = _movie.IsСписок просмотра ? "📌 In Список просмотра" : "☐ Добавить to Список просмотра";
             Список просмотраChanged?.Invoke(this, EventArgs.Empty);
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
                ListChipsRepeater.Items.Добавить(BuildListChip(listId, listName));
            }
        }

        private UIElement BuildListChip(int listId, string listName)
        {
            var border = new Border
            {
                Назадground = CineМедиатекаCS.Services.ThemeBrushes.Get("CardBrush"),
                BorderBrush = CineМедиатекаCS.Services.ThemeBrushes.Get("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 6, 4),
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            sp.Children.Добавить(new TextBlock
            {
                Text = $"📑 {listName}",
                FontSize = 12,
                Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var x = new Button
            {
                Content = "✕",
                FontSize = 10,
                Назадground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("MutedBrush"),
                Padding = new Thickness(4, 0, 4, 0),
                MinWidth = 18,
                MinHeight = 18,
            };
            ToolTipService.SetToolTip(x, $"Remove from “{listName}”");
            x.Click += (_, _) =>
            {
                if (_movie == null) return;
                AppState.Instance.Db.RemoveMovieFromUserList(listId, _movie.Id);
                RefreshListChips();
                Список просмотраChanged?.Invoke(this, EventArgs.Empty);
            };
            sp.Children.Добавить(x);
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
                TagChipsRepeater.Items.Добавить(BuildTagChip(name));
        }

        private UIElement BuildTagChip(string tagName)
        {
            var border = new Border
            {
                Назадground = CineМедиатекаCS.Services.ThemeBrushes.Get("ChipBrush"),
                BorderBrush = CineМедиатекаCS.Services.ThemeBrushes.Get("BorderBrush"),
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
                NavigateAndЗакрыть(mw => mw.NavigateМедиатекаByTag(tagName));
            };
            sp.Children.Добавить(label);
            var x = new Button
            {
                Content = "✕",
                FontSize = 10,
                Назадground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("MutedBrush"),
                Padding = new Thickness(4, 0, 4, 0),
                MinWidth = 18,
                MinHeight = 18,
            };
            ToolTipService.SetToolTip(x, $"Remove tag “{tagName}”");
            x.Click += (_, _) =>
            {
                if (_movie == null) return;
                var tagId = AppState.Instance.Db.EnsureTag(tagName);  // safe lookup-or-create
                // RemoveMovieTag raises PersonalStateChanged → AppState
                // auto-writes the movie sidecar with current tags.
                AppState.Instance.Db.RemoveMovieTag(_movie.Id, tagId);
                RefreshTagChips();
                Список просмотраChanged?.Invoke(this, EventArgs.Empty);
            };
            sp.Children.Добавить(x);
            border.Child = sp;
            return border;
        }

        private void Вкл.ДобавитьTagClick(object sender, RoutedEventArgs e)
        {
            ДобавитьTagBtn.Visibility = Visibility.Collapsed;
            ДобавитьTagBox.Text = "";
            ДобавитьTagBox.Visibility = Visibility.Visible;
            ДобавитьTagBox.Focus(FocusState.Programmatic);
        }

        private void Вкл.ДобавитьTagBlur(object sender, RoutedEventArgs e)
        {
            // Collapse the input when it loses focus without a submission.
            // Brief delay so a click on a suggestion has time to fire.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!ДобавитьTagBox.FocusState.Equals(FocusState.Programmatic))
                {
                    ДобавитьTagBox.Visibility = Visibility.Collapsed;
                    ДобавитьTagBtn.Visibility = Visibility.Visible;
                }
            });
        }

        private void Вкл.ДобавитьTagTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var q = (sender.Text ?? "").Trim();
            if (q.Length == 0) { sender.ItemsSource = null; return; }
            var existing = AppState.Instance.Db.GetВсеTags()
                .Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Name)
                .Take(8)
                .ToList();
            sender.ItemsSource = existing;
        }

        private void Вкл.ДобавитьTagSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (_movie == null) return;
            var raw = args.ChosenSuggestion as string ?? sender.Text;
            var name = (raw ?? "").Trim();
            if (name.Length == 0) return;
            try
            {
                var tagId = AppState.Instance.Db.EnsureTag(name);
                // ДобавитьMovieTag raises PersonalStateChanged → AppState auto-
                // writes the movie sidecar with the new tag set.
                AppState.Instance.Db.ДобавитьMovieTag(_movie.Id, tagId);
                RefreshTagChips();
                Список просмотраChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Не удалось добавить тег: {ex.Message}");
            }
            sender.Text = "";
            ДобавитьTagBox.Visibility = Visibility.Collapsed;
            ДобавитьTagBtn.Visibility = Visibility.Visible;
        }

        // ── Lists (v1.9.2) — add/remove this movie to/from any user list ─────

        private void Вкл.ListsFlyoutOpening(object sender, object e)
        {
            ListsFlyout.Items.Clear();
            if (_movie == null) return;

            var lists = AppState.Instance.Db.GetUserLists();
            var membership = AppState.Instance.Db.GetUserListsForMovie(_movie.Id);

            if (lists.Count == 0)
            {
                var none = new MenuFlyoutItem { Text = "(no lists yet)", IsEnabled = false };
                ListsFlyout.Items.Добавить(none);
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
                            AppState.Instance.Db.ДобавитьMovieToUserList(capturedUl.Id, _movie.Id);
                        else
                            AppState.Instance.Db.RemoveMovieFromUserList(capturedUl.Id, _movie.Id);
                        // Tell host so МОИ СПИСКИ counts refresh
                        Список просмотраChanged?.Invoke(this, EventArgs.Empty);
                        RefreshListChips();
                    };
                    ListsFlyout.Items.Добавить(item);
                }
            }

            ListsFlyout.Items.Добавить(new MenuFlyoutSeparator());
            var newItem = new MenuFlyoutItem { Text = "+ Новый список…" };
            newItem.Click += async (_, _) =>
            {
                var name = await PromptНовыйListName();
                if (string.IsNullOrWhiteSpace(name) || _movie == null) return;
                try
                {
                    var listId = AppState.Instance.Db.СоздатьUserList(name.Trim());
                    AppState.Instance.Db.ДобавитьMovieToUserList(listId, _movie.Id);
                    Список просмотраChanged?.Invoke(this, EventArgs.Empty);
                    RefreshListChips();
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    if (App.MainWindow is MainWindow mw)
                        mw.ShowToast($"A list named “{name.Trim()}” already exists");
                }
            };
            ListsFlyout.Items.Добавить(newItem);
        }

        private async Task<string?> PromptНовыйListName()
        {
            var box = new TextBox { PlaceholderText = "List name" };
            var dlg = new ContentDialog
            {
                Название = "Новый список",
                Content = box,
                PrimaryButtonText = "Создать",
                ЗакрытьButtonText = "Отмена",
                По умолчаниюButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = MainWindow.CurrentTheme,
            };
            box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
            var result = await dlg.ShowAsync();
            return result == ContentDialogResult.Primary ? box.Text : null;
        }

        // ── Заметки (v1.9, hybrid DB + sidecar) ────────────────────────────────

        /// <summary>
        /// Renders the Заметки panel in either viewing or editing mode based on
        /// _movie.Note. Called after load and after save/cancel.
        /// </summary>
        private void ОбновитьNoteUi(bool viewing)
        {
            if (_movie == null) return;
            var hasNote = !string.IsNullOrWhiteSpace(_movie.Note);

            if (viewing)
            {
                NoteText.Text = _movie.Note ?? "";
                NoteText.Visibility = hasNote ? Visibility.Visible : Visibility.Collapsed;
                NoteИзменитьor.Visibility = Visibility.Collapsed;
                NoteДобавитьBtn.Visibility = hasNote ? Visibility.Collapsed : Visibility.Visible;
                NoteИзменитьBtn.Visibility = hasNote ? Visibility.Visible : Visibility.Collapsed;
                NoteСохранитьBtn.Visibility = Visibility.Collapsed;
                NoteОтменаBtn.Visibility = Visibility.Collapsed;
                NoteНе в сетиHint.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Изменитьing mode
                NoteИзменитьor.Text = _movie.Note ?? "";
                NoteText.Visibility = Visibility.Collapsed;
                NoteИзменитьor.Visibility = Visibility.Visible;
                NoteДобавитьBtn.Visibility = Visibility.Collapsed;
                NoteИзменитьBtn.Visibility = Visibility.Collapsed;
                NoteСохранитьBtn.Visibility = Visibility.Visible;
                NoteОтменаBtn.Visibility = Visibility.Visible;
                NoteНе в сетиHint.Visibility = _movie.IsВкл.line ? Visibility.Collapsed : Visibility.Visible;
                NoteИзменитьor.Focus(FocusState.Programmatic);
            }
        }

        private void Вкл.NoteИзменитьStart(object sender, RoutedEventArgs e) => ОбновитьNoteUi(viewing: false);

        private void Вкл.NoteОтмена(object sender, RoutedEventArgs e) => ОбновитьNoteUi(viewing: true);

        private void Вкл.NoteСохранить(object sender, RoutedEventArgs e)
        {
            if (_movie == null) return;
            var newNote = (NoteИзменитьor.Text ?? "").Trim();

            // 1) DB always succeeds (even when drive is offline)
            AppState.Instance.Db.SetNote(_movie.Id, newNote);
            _movie.Note = string.IsNullOrEmpty(newNote) ? null : newNote;

            // 2) Best-effort sidecar write next to the .nfo. Skipped silently
            //    when drive offline / read-only / network glitch.
            TryWriteSidecar(_movie, newNote);

            ОбновитьNoteUi(viewing: true);
        }

        private static void TryWriteSidecar(MovieDetail m, string note)
        {
            if (!m.IsВкл.line || m.CurrentLetter == null || m.ПапкаRelPath == null) return;
            try
            {
                var folder = Path.Combine($"{m.CurrentLetter}:\\", m.ПапкаRelPath.Replace('/', '\\'));
                if (!Режиссёрy.Exists(folder)) return;
                var sidecar = Path.Combine(folder, ScannerService.NoteSidecarFileName);
                if (string.IsNullOrEmpty(note))
                {
                    if (File.Exists(sidecar)) File.Удалить(sidecar);
                }
                else
                {
                    File.WriteВсеText(sidecar, note, System.Text.Encoding.UTF8);
                }
            }
            catch { /* sidecar is best-effort; DB is the source of truth */ }
        }
    }
