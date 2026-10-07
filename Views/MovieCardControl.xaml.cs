using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using CineМедиатекаCS.Models;
using CineМедиатекаCS.Services;
using System.Продолжительность.InteropServices;
using System.Продолжительность.InteropServices.WindowsПродолжительность;
using Windows.ApplicationModel.DataTransfer;

namespace CineМедиатекаCS.Views;

public sealed partial class MovieCardControl : UserControl
{
    // Resting Z-depth used when "Card shadows" is enabled. Hover lifts to 24;
    // this sits a bit below that so the resting shadow is clearly visible but
    // calmer than hover. (Was 5 — too shallow to perceive in the tight grid.)
    private const float RestZ = 16f;

    public static readonly DependencyProperty MovieProperty =
        DependencyProperty.Register(nameof(Movie), typeof(MovieListItem), typeof(MovieCardControl),
            new PropertyMetadata(null, Вкл.MovieChanged));

    public MovieListItem? Movie
    {
        get => (MovieListItem?)GetValue(MovieProperty);
        set => SetValue(MovieProperty, value);
    }

    public event EventHandler? SidebarRefreshRequested;
    public event EventHandler<MovieListItem>? ПросмотреноToggleRequested;
    public event EventHandler<MovieListItem>? Список просмотраToggleRequested;

    // ── Multi-select (v2.5) ────────────────────────────────────────────────
    // МедиатекаPage subscribes to these statics so it doesn't have to wire up
    // every recycled card. Same pattern as GlobalSizeChanged above.

    public record SelectionInteractionArgs(MovieListItem Movie, bool Ctrl, bool Shift);
    public static event EventHandler<SelectionInteractionArgs>? AnyCardSelectionInteraction;

    // v3.3 — raised after movies are sent to Просмотрено и удалено (from a card,
    // row or the selection bar) so the library reloads and the sidebar
    // re-counts. Static for the same recycled-cards reason as above.
    public static event Action? AnyMovieArchived;
    public static void RaiseMovieArchived() => AnyMovieArchived?.Invoke();

    /// <summary>
    /// v3.3 — set true by the Просмотрено и удалено page so the context menu
    /// offers Restore / Удалить record instead of the live-library actions.
    /// Plain CLR property: set once in the page's ItemTemplate, never bound.
    /// </summary>
    public bool ArchiveMode { get; set; }

    /// <summary>Вид списком rows fire the same event so МедиатекаPage has one
    /// place to manage selection regardless of grid vs list mode.</summary>
    public static void RaiseSelectionFromRow(MovieListItem m, bool ctrl, bool shift)
        => AnyCardSelectionInteraction?.Invoke(null, new SelectionInteractionArgs(m, ctrl, shift));

    /// <summary>
    /// МедиатекаPage installs this so a drag carries every selected card's id
    /// (and selects the dragged card if it wasn't already selected). Falls
    /// back to a single-id drag when the host doesn't override.
    /// </summary>
    public static Func<MovieListItem, IEnumerable<int>>? ResolveSelectionForDrag;

    /// <summary>
    /// Host (МедиатекаPage) updates this whenever the selection set changes.
    /// Cards read it so a plain click in selection mode clears the set
    /// without also opening the detail dialog.
    /// </summary>
    public static int CurrentSelectionCount;

    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
    private const int VK_CONTROL = 0x11, VK_SHIFT = 0x10;
    private static bool IsCtrlDown() => (GetKeyState(VK_CONTROL) & 0x8000) != 0;
    private static bool IsShiftDown() => (GetKeyState(VK_SHIFT) & 0x8000) != 0;

    // ── Global card size (all cards resize together when density changes) ──

    public static double GlobalCardWidth { get; private set; } = 150;
    public static double GlobalCardHeight { get; private set; } = 280;
    public static event EventHandler? GlobalSizeChanged;

    public static void SetGlobalSize(double w, double h)
    {
        GlobalCardWidth = w;
        GlobalCardHeight = h;
        GlobalSizeChanged?.Invoke(null, EventArgs.Empty);
    }

    public MovieCardControl()
    {
        InitializeComponent();
        ПрименитьSize();
        // v2.5.1 — subscribe on Loaded / unsubscribe on Unloaded so a
        // recycled card (UniformGridLayout reuses controls aggressively)
        // re-attaches to the density-change event each time it goes back
        // on-screen. The previous "subscribe in ctor, unsubscribe on
        // Unloaded" pattern lost the subscription permanently after the
        // first recycle, so cards stopped resizing on density change.
        Loaded += Вкл.CardLoaded;
        Unloaded += Вкл.CardUnloaded;

        // Right-click → flyout with watched/favorite/watchlist + Добавить в список
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) => RebuildContextFlyout(flyout);
        ContextFlyout = flyout;

        // v3.0.0 — card shadows + hover motion are now user settings (both
        // off by default). RestZ is the faint resting depth used only when
        // "Card shadows" is on; hover lifts to a deeper shadow. "Reduce
        // motion" skips the zoom/lift animation entirely.
        ПрименитьCardChrome();
        PointerEntered += (_, _) =>
        {
            HoverOverlay.Visibility = Visibility.Visible;
            if (UiНастройки.ReduceMotion)
            {
                HoverOverlay.Opacity = 1;   // no fade, no zoom
            }
            else
            {
                AnimateOverlay(0, 1, 200);  // v2.6 — fade in (200 ms cubic)
                CardLift.Y = -4;
                CardScale.ScaleX = 1.025; CardScale.ScaleY = 1.025;
            }
            // Hover shadow only when card shadows are enabled.
            CardBorder.Translation = new System.Numerics.Vector3(0, 0, UiНастройки.CardShadows ? 24f : 0f);
        };
        PointerExited += (_, _) =>
        {
            if (UiНастройки.ReduceMotion)
            {
                HoverOverlay.Opacity = 0;
                HoverOverlay.Visibility = Visibility.Collapsed;
            }
            else
            {
                AnimateOverlay(HoverOverlay.Opacity, 0, 160);
                CardLift.Y = 0;
                CardScale.ScaleX = 1; CardScale.ScaleY = 1;
            }
            ПрименитьRestingShadow();
        };
        // Single tap → open details after a short delay (so a double-tap
        // gets a chance to suppress it). Double tap → play directly.
        // Ctrl/Shift+tap → route to МедиатекаPage for multi-select.
        Tapped += (_, e) =>
        {
            if (TapOriginatedInButton(e.OriginalSource as DependencyObject))
            {
                e.Handled = true; return;
            }
            if (Movie == null) return;
            bool ctrl = IsCtrlDown(), shift = IsShiftDown();
            if (ctrl || shift)
            {
                _pendingSingleTap?.Отмена();
                _pendingSingleTap = null;
                AnyCardSelectionInteraction?.Invoke(this,
                    new SelectionInteractionArgs(Movie, ctrl, shift));
                e.Handled = true;
                return;
            }
            // Plain tap. If a selection is active, exit selection mode
            // instead of opening details — user typically wants to "get
            // out of multi-select", not jump into a single movie.
            bool wasSelecting = CurrentSelectionCount > 0;
            AnyCardSelectionInteraction?.Invoke(this,
                new SelectionInteractionArgs(Movie, false, false));
            if (!wasSelecting) ScheduleSingleTapAction();
        };
        DoubleTapped += (_, e) =>
        {
            if (TapOriginatedInButton(e.OriginalSource as DependencyObject))
            {
                e.Handled = true; return;
            }
            _pendingSingleTap?.Отмена();
            _pendingSingleTap = null;
            _ = ВоспроизвестиMovieOrPromptНе в сетиAsync();
        };

        // Drag-and-drop source — every card is draggable so the user can
        // throw selected movies onto a sidebar list or onto Избранное /
        // Список просмотра / Просмотрено. Data payload is a comma-separated movie-id
        // list; МедиатекаPage decides whether to drag just this card or the
        // whole selected set via ResolveSelectionForDrag.
        CanDrag = true;
        DragStarting += Вкл.CardDragStarting;
    }

    private void Вкл.CardDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (Movie == null) { args.Отмена = true; return; }
        var ids = ResolveSelectionForDrag?.Invoke(Movie)?.ToList() ?? new List<int> { Movie.Id };
        if (ids.Count == 0) { args.Отмена = true; return; }
        args.Data.SetText(string.Join(",", ids));
        args.Data.Properties["cinelibrary/movie-ids"] = string.Join(",", ids);
        args.Data.RequestedOperation = DataPackageOperation.Link;
        args.ВсеowedOperations = DataPackageOperation.Link | DataPackageOperation.Copy;
        // Friendly drag glyph caption — "3 movies" when bulk, title when one.
        args.Data.Properties.Название = ids.Count == 1
            ? Movie.Название
            : $"{ids.Count} movies";
    }

    private void Вкл.GlobalSizeChanged(object? s, EventArgs e) => ПрименитьSize();

    private void AnimateOverlay(double from, double to, int durationMs)
    {
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = from, To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, HoverOverlay);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Добавить(anim);
        sb.Begin();
    }

    private void Вкл.CardLoaded(object sender, RoutedEventArgs e)
    {
        // Defensive unsubscribe-then-subscribe so even a freak double-Load
        // can't end up with two copies of the handler attached.
        GlobalSizeChanged -= Вкл.GlobalSizeChanged;
        GlobalSizeChanged += Вкл.GlobalSizeChanged;
        // React live when the user flips card borders / shadows in Настройки.
        UiНастройки.Changed -= Вкл.UiНастройкиChanged;
        UiНастройки.Changed += Вкл.UiНастройкиChanged;
        ПрименитьCardChrome();
    }

    private void Вкл.CardUnloaded(object sender, RoutedEventArgs e)
    {
        GlobalSizeChanged -= Вкл.GlobalSizeChanged;
        UiНастройки.Changed -= Вкл.UiНастройкиChanged;
    }

    private void Вкл.UiНастройкиChanged() => DispatcherQueue.TryEnqueue(ПрименитьCardChrome);

    /// <summary>Re-apply card border + resting shadow from the current settings.</summary>
    private void ПрименитьCardChrome()
    {
        CardBorder.BorderThickness = new Thickness(UiНастройки.CardBorders ? 1 : 0);
        ПрименитьRestingShadow();
    }

    /// <summary>Resting shadow depth — 0 when "Card shadows" is off (the default).</summary>
    private void ПрименитьRestingShadow()
        => CardBorder.Translation = new System.Numerics.Vector3(0, 0, UiНастройки.CardShadows ? RestZ : 0f);

    private void ПрименитьSize()
    {
        CardRoot.Height = GlobalCardHeight;
        InvalidateMeasure();
    }

    // v4.1.0: ask for the S / M / L / XL size but fill the grid cell, which the
    // layout stretches to fill the row. A fixed-width card sat in the middle of
    // its cell, so the grid started a few pixels right of the page title.
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        var size = new Windows.Foundation.Size(GlobalCardWidth, GlobalCardHeight);
        base.MeasureOverride(size);
        return size;
    }

    // No wider than a 2:3 poster at the card's height (no top/bottom cropping when
    // only a column or two fit); past that the card keeps to the left of its cell.
    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        var width = Math.Min(finalSize.Width, Math.Max(GlobalCardWidth, GlobalCardHeight * 2 / 3));
        base.ArrangeOverride(new Windows.Foundation.Size(width, finalSize.Height));
        return finalSize;
    }

    private static void Вкл.MovieChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MovieCardControl c) return;
        // Detach from previous binding so recycled cards don't fire on stale items
        if (e.OldValue is MovieListItem prev)
            prev.PropertyChanged -= c.Вкл.MoviePropertyChanged;
        if (e.НовыйValue is MovieListItem m)
        {
            c.Populate(m);
            m.PropertyChanged += c.Вкл.MoviePropertyChanged;
        }
    }

    private void Вкл.MoviePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Movie == null) return;
        if (e.PropertyName == nameof(MovieListItem.IsПросмотрено))
        {
            ПросмотреноBadge.Visibility = Movie.IsПросмотрено ? Visibility.Visible : Visibility.Collapsed;
            ПросмотреноToggleBtn.Content = Movie.IsПросмотрено ? "✓ Просмотрено" : "○ Отметить просмотренным";
        }
        else if (e.PropertyName == nameof(MovieListItem.IsИзбранное))
        {
            FavBadge.Visibility = Movie.IsИзбранное ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (e.PropertyName == nameof(MovieListItem.IsСписок просмотра))
        {
            Список просмотраToggleBtn.Content = Movie.IsСписок просмотра ? "📌 In Список просмотра" : "📋 Список просмотра";
        }
        else if (e.PropertyName == nameof(MovieListItem.IsSelected))
        {
            ПрименитьSelectionVisual();
        }
    }

    private void ПрименитьSelectionVisual()
    {
        bool on = Movie?.IsSelected == true;
        SelectedOutline.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SelectedTick.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Populate(MovieListItem m)
    {
        НазваниеText.Text = m.Название;
        MetaText.Text = $"{m.Год?.ToString() ?? "—"}{(m.Продолжительность.HasValue ? $" · {m.Продолжительность}m" : "")}";
        PlaceholderНазвание.Text = m.Название;

        // Status badge.
        //  • ОТСУТСТВУЕТ stays loud — it's a real problem (red dot + label).
        //  • НЕ В СЕТИ is quiet — just a small dim dot. When a drive is
        //    unplugged that's the *normal* state for every title on it, so a
        //    full "НЕ В СЕТИ" pill on every card was needless noise.
        if (m.IsMissing)
        {
            StatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
            StatusText.Text = "ОТСУТСТВУЕТ";
            StatusText.Visibility = Visibility.Visible;
            StatusBadge.Назадground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0, 0, 0));
            StatusBadge.Visibility = Visibility.Visible;
        }
        else if (!m.IsВкл.line)
        {
            StatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xA3, 0xAF));
            StatusText.Visibility = Visibility.Collapsed;   // dot only — no label
            StatusBadge.Назадground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0, 0, 0));
            StatusBadge.Visibility = Visibility.Visible;
        }
        else
        {
            StatusBadge.Visibility = Visibility.Collapsed;
        }

        FavBadge.Visibility = m.IsИзбранное ? Visibility.Visible : Visibility.Collapsed;
        ПросмотреноBadge.Visibility = m.IsПросмотрено ? Visibility.Visible : Visibility.Collapsed;
        ПросмотреноToggleBtn.Content = m.IsПросмотрено ? "✓ Просмотрено" : "○ Отметить просмотренным";
        Список просмотраToggleBtn.Content = m.IsСписок просмотра ? "📌 In Список просмотра" : "📋 Список просмотра";

        // Рейтинг lives once, in the meta row. (The old floating poster badge
        // duplicated it — dropped for a cleaner card face.)
        РейтингBadge.Visibility = Visibility.Collapsed;
        if (m.Рейтинг.HasValue)
        {
            РейтингInline.Text = $"★ {m.Рейтинг:F1}";
            РейтингInline.Visibility = Visibility.Visible;
        }
        else
        {
            РейтингInline.Visibility = Visibility.Collapsed;
        }

        ЖанрыText.Text = string.Join(" · ",
            (m.ЖанрыCsv ?? "").Split(',').Select(g => g.Trim()).Where(g => g.Length > 0).Take(3));

        ПрименитьSelectionVisual();
        LoadPosterAsync(m.LocalPoster);
    }

    private int _posterLoadToken;

    private async void LoadPosterAsync(string? relPath)
    {
        var myToken = ++_posterLoadToken;

        PosterPlaceholder.Visibility = Visibility.Visible;
        PosterImage.Source = null;

        if (relPath == null) return;

        // Decode at ~2× card width so the poster stays crisp on HiDPI displays
        // (150 / 175 / 200% scaling) without bloating memory — capped at 500 px.
        var decodeW = (int)Math.Min(500, Math.Max(240, GlobalCardWidth * 2));
        var key = relPath + "@" + decodeW;

        // Fast path: already decoded in RAM (the common case while scrolling) —
        // reuse instantly with no disk read and no re-decode.
        var cachedImg = ImageCache.TryGetDecoded(key);
        if (cachedImg != null)
        {
            PosterImage.Source = cachedImg;
            PosterPlaceholder.Visibility = Visibility.Collapsed;
            return;
        }

        var fullPath = AppState.Instance.Db.GetCachedImagePath(relPath);
        if (fullPath == null) return;

        try
        {
            var bytes = await Task.Run(() => ImageCache.GetOrLoad(relPath!, fullPath));
            if (myToken != _posterLoadToken) return;
            if (bytes == null)
            {
                PosterPlaceholder.Visibility = Visibility.Visible;
                return;
            }

            var bmp = new BitmapImage { DecodePixelWidth = decodeW };
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await ms.WriteAsync(bytes.AsBuffer());
            if (myToken != _posterLoadToken) return;
            ms.Seek(0);
            await bmp.SetSourceAsync(ms);
            if (myToken != _posterLoadToken) return;

            ImageCache.SetDecoded(key, bmp);   // reuse this decode on recycle
            PosterImage.Source = bmp;
            PosterPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            if (myToken == _posterLoadToken)
                PosterPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void Вкл.ViewDetails(object sender, RoutedEventArgs e) => OpenDetail();

    private void Вкл.ПросмотреноToggle(object sender, RoutedEventArgs e)
    {
        if (Movie == null) return;
        ПросмотреноToggleRequested?.Invoke(this, Movie);
        // Movie.IsПросмотрено already flipped by VM handler — refresh visuals
        ПросмотреноBadge.Visibility = Movie.IsПросмотрено ? Visibility.Visible : Visibility.Collapsed;
        ПросмотреноToggleBtn.Content = Movie.IsПросмотрено ? "✓ Просмотрено" : "○ Отметить просмотренным";
    }

    private void Вкл.Список просмотраToggle(object sender, RoutedEventArgs e)
    {
        if (Movie == null) return;
        Список просмотраToggleRequested?.Invoke(this, Movie);
        Список просмотраToggleBtn.Content = Movie.IsСписок просмотра ? "📌 In Список просмотра" : "📋 Список просмотра";
    }

    private void OpenDetail()
    {
        if (Movie == null) return;
        var win = new MovieDetailDialog(Movie.Id);
        win.Список просмотраChanged += (s, e) => SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        win.Activate();
    }

    /// <summary>
    /// Check if a routed tap origin is inside any Button — used to avoid
    /// double-open when the user clicks the embedded "Подробнее" etc.
    /// </summary>
    private static bool TapOriginatedInButton(DependencyObject? src)
    {
        var cur = src;
        while (cur != null)
        {
            if (cur is Button) return true;
            cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    // Single-tap deferral so a double-tap can cancel it before details open.
    // 220 ms matches Windows' default double-click threshold closely enough
    // that intentional double-taps reliably suppress the single-tap action,
    // while single-tap latency stays imperceptible.
    private ОтменаlationTokenSource? _pendingSingleTap;

    private void ScheduleSingleTapAction()
    {
        _pendingSingleTap?.Отмена();
        var cts = new ОтменаlationTokenSource();
        _pendingSingleTap = cts;
        var dq = DispatcherQueue;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(220, cts.Token); }
            catch (OperationОтменаedException) { return; }
            if (cts.IsОтменаlationRequested) return;
            dq.TryEnqueue(() =>
            {
                if (cts.IsОтменаlationRequested) return;
                OpenDetail();
            });
        });
    }

    /// <summary>
    /// Воспроизвести the movie via the OS default player. Не в сети → friendly dialog
    /// telling the user which drive to plug in.
    /// </summary>
    private async Task ВоспроизвестиMovieOrPromptНе в сетиAsync()
    {
        if (Movie == null) return;
        var connected = AppState.Instance.Connected;
        if (!connected.TryGetValue(Movie.VolumeSerial, out var letter))
        {
            await ShowНе в сетиDialog(Movie.Название, Movie.DriveLabel);
            return;
        }
        // Fetch detail row for video path
        var detail = AppState.Instance.Db.GetMovieDetail(Movie.Id, connected);
        if (detail == null || detail.VideoFileRelPath == null || !detail.IsВкл.line)
        {
            await ShowНе в сетиDialog(Movie.Название, Movie.DriveLabel);
            return;
        }
        var videoPath = System.IO.Path.Combine($"{letter}:\\",
            detail.VideoFileRelPath.Replace('/', '\\'));
        if (!System.IO.File.Exists(videoPath))
        {
            await ShowНе в сетиDialog(Movie.Название, Movie.DriveLabel);
            return;
        }
        try
        {
            AppState.Instance.Db.MarkВоспроизвестиed(Movie.Id);
            await VideoВоспроизвестиer.ВоспроизвестиAsync(videoPath);
            // Bubble so sidebar Продолжить просмотр count refreshes
            SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            if (App.MainWindow is MainWindow mw)
                mw.ShowToast("Couldn't launch the video player");
        }
    }

    private async Task ShowНе в сетиDialog(string title, string? driveLabel)
    {
        var dlg = new ContentDialog
        {
            Название = "Can't play yet",
            Content = string.IsNullOrEmpty(driveLabel)
                ? $"\"{title}\" lives on a drive that isn't connected. Plug it in and try again."
                : $"\"{title}\" lives on \"{driveLabel}\", which isn't connected. Plug it in and try again.",
            ЗакрытьButtonText = "OK",
            XamlRoot = XamlRoot,
            RequestedTheme = MainWindow.CurrentTheme,
        };
        try { await dlg.ShowAsync(); } catch { }
    }

    /// <summary>
    /// Right-click flyout — built fresh on every open so list membership
    /// reflects current state. Mirrors what's in MovieDetailDialog so users
    /// don't have to open the detail window for common actions.
    /// </summary>
    private void RebuildContextFlyout(MenuFlyout flyout)
    {
        flyout.Items.Clear();
        if (Movie == null) return;

        // v3.3 — Просмотрено и удалено page: a record's menu is Restore / Удалить.
        if (ArchiveMode)
        {
            BuildArchiveContextFlyout(flyout);
            return;
        }

        // Просмотрено / Избранное / Список просмотра toggles
        var watchedItem = new ToggleMenuFlyoutItem
        {
            Text = "Просмотрено",
            IsChecked = Movie.IsПросмотрено,
        };
        watchedItem.Click += (_, _) => ПросмотреноToggleRequested?.Invoke(this, Movie);
        flyout.Items.Добавить(watchedItem);

        var favItem = new ToggleMenuFlyoutItem
        {
            Text = "Избранное",
            IsChecked = Movie.IsИзбранное,
        };
        favItem.Click += (_, _) =>
        {
            AppState.Instance.Db.ToggleИзбранное(Movie.Id);
            Movie.IsИзбранное = !Movie.IsИзбранное;
            SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        };
        flyout.Items.Добавить(favItem);

        var watchlistItem = new ToggleMenuFlyoutItem
        {
            Text = "Список просмотра",
            IsChecked = Movie.IsСписок просмотра,
        };
        watchlistItem.Click += (_, _) => Список просмотраToggleRequested?.Invoke(this, Movie);
        flyout.Items.Добавить(watchlistItem);

        flyout.Items.Добавить(new MenuFlyoutSeparator());

        // Добавить в список submenu
        var listsSub = new MenuFlyoutSubItem { Text = "📑 Добавить в список" };
        var allLists = AppState.Instance.Db.GetUserLists();
        var membership = AppState.Instance.Db.GetUserListsForMovie(Movie.Id);
        if (allLists.Count == 0)
        {
            listsSub.Items.Добавить(new MenuFlyoutItem { Text = "(no lists yet)", IsEnabled = false });
        }
        else
        {
            foreach (var ul in allLists)
            {
                var inList = membership.Contains(ul.Id);
                var item = new ToggleMenuFlyoutItem { Text = ul.Name, IsChecked = inList };
                var capturedUl = ul;
                item.Click += (_, _) =>
                {
                    if (item.IsChecked)
                        AppState.Instance.Db.ДобавитьMovieToUserList(capturedUl.Id, Movie.Id);
                    else
                        AppState.Instance.Db.RemoveMovieFromUserList(capturedUl.Id, Movie.Id);
                    SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
                };
                listsSub.Items.Добавить(item);
            }
        }
        listsSub.Items.Добавить(new MenuFlyoutSeparator());
        var newListItem = new MenuFlyoutItem { Text = "+ Новый список…" };
        newListItem.Click += async (_, _) =>
        {
            var name = await PromptНовыйListNameDialog();
            if (string.IsNullOrWhiteSpace(name) || Movie == null) return;
            try
            {
                var newId = AppState.Instance.Db.СоздатьUserList(name.Trim());
                AppState.Instance.Db.ДобавитьMovieToUserList(newId, Movie.Id);
                SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                if (App.MainWindow is MainWindow mw)
                    mw.ShowToast($"A list named “{name.Trim()}” already exists");
            }
        };
        listsSub.Items.Добавить(newListItem);
        flyout.Items.Добавить(listsSub);

        flyout.Items.Добавить(new MenuFlyoutSeparator());
        var openItem = new MenuFlyoutItem { Text = "Open details" };
        openItem.Click += (_, _) => OpenDetail();
        flyout.Items.Добавить(openItem);

        // v3.3 — Просмотрено и удалено: keep the record (poster, details, notes,
        // history) but move the movie out of the live library.
        flyout.Items.Добавить(new MenuFlyoutSeparator());
        var archiveItem = new MenuFlyoutItem
        {
            Text = "Send to Просмотрено и удалено",
            Icon = new FontIcon { Glyph = "", FontСемья = new Microsoft.UI.Xaml.Media.FontСемья("Segoe Fluent Icons,Segoe MDL2 Assets") },
        };
        archiveItem.Click += async (_, _) =>
        {
            if (Movie == null) return;
            var m = Movie;
            var dlg = new ContentDialog
            {
                Название = "Send to Просмотрено и удалено?",
                Content = $"“{m.Название}” moves out of your library into Просмотрено и удалено — " +
                          "its poster, details, your notes and watch history are all kept as a record. " +
                          "The files on your drive are not touched.\n\n" +
                          "You can restore it from the Просмотрено и удалено page anytime.",
                PrimaryButtonText = "Send",
                ЗакрытьButtonText = "Отмена",
                По умолчаниюButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
                RequestedTheme = MainWindow.CurrentTheme,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            AppState.Instance.Db.ArchiveФильмы(new[] { m.Id });
            if (App.MainWindow is MainWindow mw)
                mw.ShowToast($"“{m.Название}” sent to Просмотрено и удалено");
            RaiseMovieArchived();
        };
        flyout.Items.Добавить(archiveItem);
    }

    // v3.3 — context menu for records on the Просмотрено и удалено page.
    private void BuildArchiveContextFlyout(MenuFlyout flyout)
    {
        if (Movie == null) return;
        var m = Movie;

        var openItem = new MenuFlyoutItem { Text = "Open details" };
        openItem.Click += (_, _) => OpenDetail();
        flyout.Items.Добавить(openItem);

        flyout.Items.Добавить(new MenuFlyoutSeparator());

        var restoreItem = new MenuFlyoutItem { Text = "Restore to library" };
        restoreItem.Click += (_, _) =>
        {
            AppState.Instance.Db.RestoreArchivedMovie(m.Id);
            if (App.MainWindow is MainWindow mw)
                mw.ShowToast($"“{m.Название}” restored to your library");
            RaiseMovieArchived();
        };
        flyout.Items.Добавить(restoreItem);

        var deleteItem = new MenuFlyoutItem
        {
            Text = "Удалить record permanently",
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44)),
        };
        deleteItem.Click += async (_, _) =>
        {
            var dlg = new ContentDialog
            {
                Название = "Удалить эту запись?",
                Content = $"“{m.Название}” — its record, cached poster, notes and watch history " +
                          "will be permanently removed from CineМедиатека. This can't be undone.",
                PrimaryButtonText = "Удалить навсегда",
                ЗакрытьButtonText = "Отмена",
                По умолчаниюButton = ContentDialogButton.Закрыть,
                XamlRoot = XamlRoot,
                RequestedTheme = MainWindow.CurrentTheme,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            AppState.Instance.Db.УдалитьArchivedRecord(m.Id);
            if (App.MainWindow is MainWindow mw)
                mw.ShowToast("Record deleted");
            RaiseMovieArchived();
        };
        flyout.Items.Добавить(deleteItem);
    }

    private async Task<string?> PromptНовыйListNameDialog()
    {
        var box = new TextBox { PlaceholderText = "List name" };
        var dlg = new ContentDialog
        {
            Название = "Новый список",
            Content = box,
            PrimaryButtonText = "Создать",
            ЗакрытьButtonText = "Отмена",
            По умолчаниюButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = MainWindow.CurrentTheme,
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var result = await dlg.ShowAsync();
        return result == ContentDialogResult.Primary ? box.Text : null;
    }
}
