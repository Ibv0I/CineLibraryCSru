using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using CineМедиатекаCS.Models;
using CineМедиатекаCS.Services;
using CineМедиатекаCS.ViewModels;
using CineМедиатекаCS.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace CineМедиатекаCS;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private МедиатекаPage? _libraryPage;
    private ДискиPage? _drivesPage;
    private СтатистикаPage? _statisticsPage;
    private ДубликатыPage? _dupesPage;
    private ОбзорPage? _browsePage;
    private КоллекцииОбзорPage? _collectionsPage;
    private TvShowsPage? _tvShowsPage;
    private Вкл.ThisDayPage? _onThisDayPage;
    private ПросмотреноGonePage? _watchedGonePage;
    private DeviceChangeWatcher? _deviceWatcher;

    public MainWindow()
    {
        // Initialize the SQLite-backed AppState SYNCHRONOUSLY first.
        // Click handlers on the sidebar (e.g. Вкл.NavВсеФильмы) construct
        // МедиатекаViewModel which calls AppState.GetPref → Db.GetPref. If
        // the user can click before the async InitAsync completes, that
        // path NREs because Db is still null. Init is a few ms of SQLite
        // work — fine to run on the UI thread before XAML loads.
        try { AppState.Instance.Initialize(); }
        catch (Exception ex)
        {
            // If schema/migration explodes, surface it instead of leaving
            // a half-constructed app behind.
            App.LogStartupCrashStatic(ex, "AppState.Initialize");
            throw;
        }

        // v3.0.0 — load UI preferences (card shadows, reduce motion) before any
        // card is built so they paint with the right look on first render.
        UiНастройки.Load();

        InitializeComponent();

        // Extend content into titlebar for Mica effect + use our custom drag region
        ExtendsContentIntoНазваниеBar = true;
        SetНазваниеBar(AppНазваниеBar);

        // Применить Mica material (user-toggleable in Настройки) and keep it in sync.
        ПрименитьMica();
        UiНастройки.Changed += () => DispatcherQueue.TryEnqueue(ПрименитьMica);

        // Window size
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(1400, 900));   // restore-size used when the user un-maximizes
        appWindow.Название = "CineМедиатека";

        // v2.9 — start maximized. The 1400×900 above becomes the size the
        // window restores to when the user clicks the restore button.
        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Maximize();

        // Set custom titlebar icon
        try
        {
            var iconPath = Path.Combine(AppContext.BaseРежиссёрy, "Assets", "icon.ico");
            if (File.Exists(iconPath))
                appWindow.SetIcon(iconPath);
        }
        catch { }

        _vm = new MainViewModel();

        // Suppress the auto-displayed accelerator-key tooltip (e.g. "Ctrl+F"
        // floating over whichever element the focus visual lands on).
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        // Global Ctrl+B to toggle sidebar
        var acc = new KeyboardAccelerator { Key = VirtualKey.B, Modifiers = VirtualKeyModifiers.Control };
        acc.Invoked += (_, a) => { ПрименитьSidebarCollapsed(!_sidebarCollapsed); a.Handled = true; };
        RootGrid.KeyboardAccelerators.Добавить(acc);

        // Global Ctrl+Q to quit
        var quitAcc = new KeyboardAccelerator { Key = VirtualKey.Q, Modifiers = VirtualKeyModifiers.Control };
        quitAcc.Invoked += (_, a) => { Закрыть(); a.Handled = true; };
        RootGrid.KeyboardAccelerators.Добавить(quitAcc);

        // Global Ctrl+F to focus search
        var searchAcc = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        searchAcc.Invoked += (_, a) => {
            НазваниеПоискBox.Focus(FocusState.Programmatic);
            a.Handled = true;
        };
        RootGrid.KeyboardAccelerators.Добавить(searchAcc);

        // Esc inside the title-bar search clears the text and exits the box.
        НазваниеПоискBox.ДобавитьHandler(UIElement.KeyDownEvent,
            new Microsoft.UI.Xaml.Input.KeyEventHandler(Вкл.НазваниеПоискKeyDown), handledEventsToo: true);

        // v2.5 — drag-and-drop targets in the sidebar. Drag selected cards
        // onto Избранное / Список просмотра to flip those flags in bulk; drop on
        // a user-list row to add. ВсеowDrop must be set per-target.
        WireSidebarDropTarget(BtnИзбранное, ids =>
        {
            foreach (var mid in ids) AppState.Instance.Db.ToggleИзбранное(mid);
            return $"В избранном {ids.Count}";
        });
        WireSidebarDropTarget(BtnСписок просмотра, ids =>
        {
            foreach (var mid in ids) AppState.Instance.Db.SetСписок просмотра(mid, true);
            return $"Добавитьed {ids.Count} to watchlist";
        });

        // v1.4.1 — Ctrl+? (Ctrl+Shift+/) shows keyboard shortcuts dialog
        var helpAcc = new KeyboardAccelerator
        {
            Key = (VirtualKey)191, // '/' on US layout
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
        };
        helpAcc.Invoked += async (_, a) => { a.Handled = true; await ShowShortcutsDialogAsync(); };
        RootGrid.KeyboardAccelerators.Добавить(helpAcc);

        // Event-driven drive detection — replaces the 10-second poll timer.
        // The watcher subclasses our HWND and marshals WM_DEVICECHANGE to the
        // UI thread (the subclass proc runs on the UI thread by definition).
        _deviceWatcher = new DeviceChangeWatcher(hwnd, () =>
        {
            // Don't await — we're inside WndProc and must return promptly.
            _ = _vm.Вкл.DeviceChangeAsync();
        });

        // Stop background timers BEFORE the XAML/Sqlite teardown begins.
        // The poll timer is gone in v1.5 (see above) but the toast timer
        // still exists, and the SqliteConnection still needs clean disposal.
        this.Закрытьd += (_, _) =>
        {
            try { _deviceWatcher?.Dispose(); } catch { }
            _deviceWatcher = null;
            try { _vm.Shutdown(); } catch { }
            try { AppState.Instance.Db.Dispose(); } catch { }
        };

        _ = InitAsync();
    }

    // ── Theme ─────────────────────────────────────────────────────────────

    public static ElementTheme CurrentTheme { get; private set; } = ElementTheme.По умолчанию;

    private void ПрименитьTheme(ElementTheme theme)
    {
        CurrentTheme = theme;
        if (Content is FrameworkElement root)
            root.RequestedTheme = theme;
        // v3.0.0 — monochrome FontIcon glyph instead of a colour emoji so it
        // sits cleanly next to the other footer icons.
        //   Светлая  → Brightness (sun)   E706
        //   Тёмная   → QuietHours (moon)  E708
        //   System → TVMonitor          E7F4
        ThemeIcon.Glyph = theme switch
        {
            ElementTheme.Светлая => "",
            ElementTheme.Тёмная  => "",
            _                  => "",
        };
        AppState.Instance.SetPref("theme", theme.ToString());
        // Re-tint the (code-set) sidebar brush for the new theme.
        ПрименитьMica();
    }

    private void Вкл.ToggleTheme(object sender, RoutedEventArgs e)
    {
        var next = CurrentTheme switch
        {
            ElementTheme.По умолчанию => ElementTheme.Тёмная,
            ElementTheme.Тёмная    => ElementTheme.Светлая,
            _                    => ElementTheme.По умолчанию,
        };
        ПрименитьTheme(next);
    }

    // ── v3.0.0 Настройки dialog ────────────────────────────────────────────

    /// <summary>
    /// Настройки dialog: theme (mirrors the quick toggle), card shadows, and
    /// reduce motion. Both visual extras default off, persisted via prefs,
    /// and applied live (cards listen to UiНастройки.Changed).
    /// </summary>
    private async void Вкл.НастройкиClick(object sender, RoutedEventArgs e)
    {
        var muted = CineМедиатекаCS.Services.ThemeBrushes.Get("MutedBrush");

        TextBlock Header(string t) => new()
        {
            Text = t, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 120, Opacity = 0.7, Foreground = muted,
            Margin = new Thickness(0, 12, 0, 4),
        };

        var panel = new StackPanel { Spacing = 6, MinWidth = 360 };

        // ── Внешний вид ──
        panel.Children.Добавить(Header("ВНЕШНИЙ ВИД"));

        // Theme — three-way segmented choice that mirrors the quick toggle.
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        RadioButton ThemeChip(string label, ElementTheme value)
        {
            var rb = new RadioButton
            {
                Content = label,
                GroupName = "theme",
                IsChecked = CurrentTheme == value,
                MinWidth = 0,
                Margin = new Thickness(0, 0, 14, 0),
            };
            rb.Checked += (_, _) => ПрименитьTheme(value);
            return rb;
        }
        themeRow.Children.Добавить(ThemeChip("Светлая",  ElementTheme.Светлая));
        themeRow.Children.Добавить(ThemeChip("Тёмная",   ElementTheme.Тёмная));
        themeRow.Children.Добавить(ThemeChip("System", ElementTheme.По умолчанию));
        panel.Children.Добавить(new TextBlock { Text = "Theme", FontSize = 13 });
        panel.Children.Добавить(themeRow);

        // Назадground material (Mica) — Выкл. / Subtle / Strong
        var micaChoice = new RadioButtons
        {
            Header = "Назадground material (Mica)",
            MaxColumns = 3,
            Margin = new Thickness(0, 12, 0, 0),
        };
        micaChoice.Items.Добавить("Выкл.");
        micaChoice.Items.Добавить("Subtle");
        micaChoice.Items.Добавить("Strong");
        micaChoice.SelectedIndex = (int)UiНастройки.Mica;   // Выкл.=0, Subtle=1, Strong=2
        micaChoice.SelectionChanged += (_, _) =>
        {
            if (micaChoice.SelectedIndex >= 0)
                UiНастройки.SetMica((UiНастройки.MicaLevel)micaChoice.SelectedIndex);
        };
        panel.Children.Добавить(micaChoice);
        panel.Children.Добавить(new TextBlock
        {
            Text = "How much of the Windows Mica material — your wallpaper, softly tinted — shows behind the sidebar. Выкл. is a flat, solid look, and the lightest on the GPU. (The scrolling poster area always stays solid for speed.)",
            FontSize = 12, Opacity = 0.7, Foreground = muted, TextWrapping = TextWrapping.Wrap,
        });

        // Границы карточек
        var borderToggle = new ToggleSwitch
        {
            Header = "Границы карточек",
            IsВкл. = UiНастройки.CardBorders,
            Выкл.Content = "Выкл.", Вкл.Content = "Вкл.",
            Margin = new Thickness(0, 10, 0, 0),
        };
        borderToggle.Toggled += (_, _) => UiНастройки.SetCardBorders(borderToggle.IsВкл.);
        panel.Children.Добавить(borderToggle);
        panel.Children.Добавить(new TextBlock
        {
            Text = "A thin outline around each movie card. Helps the cards stand out, especially in light theme.",
            FontSize = 12, Opacity = 0.7, Foreground = muted, TextWrapping = TextWrapping.Wrap,
        });

        // Card shadows
        var shadowToggle = new ToggleSwitch
        {
            Header = "Тени карточек",
            IsВкл. = UiНастройки.CardShadows,
            Выкл.Content = "Выкл.", Вкл.Content = "Вкл.",
            Margin = new Thickness(0, 10, 0, 0),
        };
        shadowToggle.Toggled += (_, _) => UiНастройки.SetCardShadows(shadowToggle.IsВкл.);
        panel.Children.Добавить(shadowToggle);
        panel.Children.Добавить(new TextBlock
        {
            Text = "A subtle shadow that lifts each movie card. Выкл. by default for the smoothest scrolling.",
            FontSize = 12, Opacity = 0.7, Foreground = muted, TextWrapping = TextWrapping.Wrap,
        });

        // Уменьшить анимацию
        var motionToggle = new ToggleSwitch
        {
            Header = "Уменьшить анимацию",
            IsВкл. = UiНастройки.ReduceMotion,
            Выкл.Content = "Выкл.", Вкл.Content = "Вкл.",
            Margin = new Thickness(0, 10, 0, 0),
        };
        motionToggle.Toggled += (_, _) => UiНастройки.SetReduceMotion(motionToggle.IsВкл.);
        panel.Children.Добавить(motionToggle);
        panel.Children.Добавить(new TextBlock
        {
            Text = "Turns off the zoom/lift animation when you hover a card. Easier on the eyes and on low-end GPUs.",
            FontSize = 12, Opacity = 0.7, Foreground = muted, TextWrapping = TextWrapping.Wrap,
        });

        // ── Воспроизвестиback (v3.6.0) ──
        panel.Children.Добавить(Header("PLAYBACK"));
        panel.Children.Добавить(new TextBlock { Text = "Видеоплеер", FontSize = 13 });
        var playerBox = new TextBox
        {
            IsReadВкл.ly = true,
            Text = UiНастройки.ВоспроизвестиerPath,
            PlaceholderText = "Стандартный проигрыватель Windows",
        };
        var chooseВоспроизвестиer = new Button { Content = "Выбрать…" };
        var defaultВоспроизвестиer = new Button
        {
            Content = "Use Windows default",
            IsEnabled = UiНастройки.ВоспроизвестиerPath.Length > 0,
            Margin = new Thickness(0, 4, 0, 0),
        };
        chooseВоспроизвестиer.Click += async (_, _) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerПапка,
                };
                picker.FileTypeFilter.Добавить(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                UiНастройки.SetВоспроизвестиerPath(file.Path);
                playerBox.Text = file.Path;
                defaultВоспроизвестиer.IsEnabled = true;
            }
            catch { }
        };
        defaultВоспроизвестиer.Click += (_, _) =>
        {
            UiНастройки.SetВоспроизвестиerPath("");
            playerBox.Text = "";
            defaultВоспроизвестиer.IsEnabled = false;
        };
        var playerRow = new Grid { ColumnSpacing = 8 };
        playerRow.ColumnDefinitions.Добавить(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        playerRow.ColumnDefinitions.Добавить(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(chooseВоспроизвестиer, 1);
        playerRow.Children.Добавить(playerBox);
        playerRow.Children.Добавить(chooseВоспроизвестиer);
        panel.Children.Добавить(playerRow);
        panel.Children.Добавить(defaultВоспроизвестиer);
        panel.Children.Добавить(new TextBlock
        {
            Text = "Воспроизвести opens videos in this program, for example VLC, MPC-HC, PotВоспроизвестиer or mpv. With none chosen, Воспроизвести uses whatever Windows opens video files with.",
            FontSize = 12, Opacity = 0.7, Foreground = muted, TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            Название = "Настройки",
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 520,
                // Оставить text and buttons clear of the scrollbar (v3.7.2).
                Padding = new Thickness(0, 0, 16, 0),
            },
            ЗакрытьButtonText = "Done",
            XamlRoot = Content.XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        try { await dialog.ShowAsync(); } catch { }
    }

    private async Task InitAsync()
    {
        // AppState.Initialize() already ran synchronously in the ctor —
        // DB is ready by the time any UI handler can fire. InitAsync now
        // only does the slower bits: theme apply, sidebar populate, update check.

        // Restore saved theme (default = System, not forced dark)
        var saved = AppState.Instance.GetPref("theme", "По умолчанию");
        var theme = Enum.TryParse<ElementTheme>(saved, out var t) ? t : ElementTheme.По умолчанию;
        ПрименитьTheme(theme);

        // Restore sidebar collapsed state
        if (AppState.Instance.GetPref("sidebarCollapsed", "false") == "true")
            ПрименитьSidebarCollapsed(true);

        // v2.9 — faster perceived startup. Previously we awaited the full
        // sidebar data fetch (drives + collections + stats) BEFORE showing
        // the library, so the main content waited on queries the user isn't
        // even looking at yet. Now:
        //   1. Prime the connected-drive set (fast — just enumerates drive
        //      letters) so the grid's first paint shows correct В СЕТИ/НЕ В СЕТИ.
        //   2. Show the library immediately; its grid load runs in the
        //      background, in parallel with the sidebar fetch below.
        //   3. Fill the sidebar, rendering it immediately (skip the 150 ms
        //      debounce that exists to coalesce mid-session bursts).
        await Task.Run(() => AppState.Instance.RefreshConnected());

        NavigateTo("library");
        SetActiveNav(BtnВсеФильмы);   // initial active highlight on Все фильмы

        await _vm.InitializeAsync();
        RefreshSidebarImmediate();

        // Fire-and-forget update check. Silent on no-network. Skipped versions
        // are remembered via the prefs table so the user isn't nagged.
        _ = CheckForОбновитьsAsync();
    }

    // ── Обновить check ──────────────────────────────────────────────────────

    private string? _pendingОбновитьUrl;
    private string? _pendingОбновитьVersion;

    private async Task CheckForОбновитьsAsync()
    {
        try
        {
            var skipped = AppState.Instance.GetPref("skippedОбновить", "");
            var info = await ОбновитьChecker.CheckAsync(string.IsNullOrEmpty(skipped) ? null : skipped);
            if (info == null) return;

            _pendingОбновитьVersion = info.LatestVersion;
            _pendingОбновитьUrl = info.ReleaseUrl;
            DispatcherQueue.TryEnqueue(() => ShowОбновитьToast(info.LatestVersion));
        }
        catch { /* never let an update check break the app */ }
    }

    private void ShowОбновитьToast(string version)
    {
        ToastText.Text = $"CineМедиатека v{version} is available";
        ToastActionBtn.Content = "Download";
        ToastActionBtn.Visibility = Visibility.Visible;
        ToastBorder.Visibility = Visibility.Visible;
        // Обновить toast stays until dismissed — no auto-hide.
    }

    // Pending action for the toast's primary button. The update-notifier
    // path sets _pendingОбновитьUrl; selection bulk-ops set _pendingUndo.
    // Whichever is set when the button is clicked wins.
    //
    // v2.5.1 — every show-toast call bumps _toastGeneration. Auto-hide
    // timers capture the generation at show-time and bail out if a newer
    // toast has been shown since. Prevents a stale 6 s timer from
    // collapsing a freshly-shown toast (or worse, clearing the wrong
    // _pendingUndo).
    private Action? _pendingUndo;
    private int _toastGeneration;

    private async void Вкл.ToastActionClick(object sender, RoutedEventArgs e)
    {
        // Undo first (selection bulk-ops). Обновить download is a fallback.
        if (_pendingUndo != null)
        {
            try { _pendingUndo(); } catch { }
            _pendingUndo = null;
            ToastBorder.Visibility = Visibility.Collapsed;
            return;
        }
        if (string.IsNullOrEmpty(_pendingОбновитьUrl)) return;
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(_pendingОбновитьUrl)); }
        catch { }
        ToastBorder.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Show a toast with an "Undo" button that calls back into the caller
    /// to revert the action. Auto-hides after 6s. Used by the multi-select
    /// bulk operations (add to list / mark watched / favorite / etc.) so a
    /// fat-fingered batch is one click away from being put back.
    /// </summary>
    public void ShowToastWithUndo(string message, Action undo)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var myGen = ++_toastGeneration;
            ToastText.Text = message;
            // Set the visual label BEFORE installing the action so a rapid
            // click can never trigger the wrong handler.
            ToastActionBtn.Content = "Undo";
            ToastActionBtn.Visibility = Visibility.Visible;
            _pendingUndo = undo;
            ToastBorder.Visibility = Visibility.Visible;
            AnimateToastIn();
            ScheduleToastHide(myGen);
        });
    }

    /// <summary>
    /// Hide the toast 6 s from now, but only if it's still showing the
    /// generation we captured. Новыйer ShowToast* calls bump the counter
    /// and effectively cancel us.
    /// </summary>
    private void ScheduleToastHide(int generation)
    {
        Task.Delay(6000).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
        {
            if (_toastGeneration != generation) return;
            _pendingUndo = null;
            ToastBorder.Visibility = Visibility.Collapsed;
        }));
    }

    // ── Sidebar refresh ───────────────────────────────────────────────────

    // v2.9 — Debounce a flurry of sidebar refresh requests into one render.
    // Multiple DB writes (e.g. a bulk multi-select toggle, or the scanner
    // wrapping up) can fire RefreshSidebar 5+ times in rapid succession;
    // coalescing them within ~150 ms keeps the tree from re-rendering N×.
    private ОтменаlationTokenSource? _sidebarDebounce;

    public void RefreshSidebar()
    {
        _sidebarDebounce?.Отмена();
        _sidebarDebounce = new ОтменаlationTokenSource();
        var token = _sidebarDebounce.Token;
        _ = Task.Delay(150, token).ContinueWith(t =>
        {
            if (t.IsОтменаed) return;
            DispatcherQueue.TryEnqueue(RefreshSidebarImmediate);
        }, TaskScheduler.По умолчанию);
    }

    private void RefreshSidebarImmediate()
    {
        // Body runs on UI thread (caller dispatches). The original inline
        // DispatcherQueue.TryEnqueue is gone — we already arrived here via
        // the dispatch in the debouncer above.
        {
            var stats = _vm.Stats;
            TotalBadge.Text = stats?.TotalФильмы.ToString() ?? "0";
            // v3.9.0: Диски is an icon in the bottom bar now; the count lives in its tooltip.
            ToolTipService.SetToolTip(BtnДиски, $"Диски ({_vm.Диски.Count})");
            try { TvShowsBadge.Text = AppState.Instance.Db.GetTvShowCount().ToString(); } catch { }
            // v4.3.0: shows with a note count too, like К просмотру since 4.0.0
            try
            {
                ЗаметкиBadge.Text = (AppState.Instance.Db.GetЗаметкиCount()
                    + AppState.Instance.Db.GetTvShowPageCount(DatabaseService.TvShowPage.Заметки)).ToString();
            }
            catch { }
            // v3.3 — Просмотрено и удалено entry appears once the first record exists.
            try
            {
                var wg = AppState.Instance.Db.GetArchivedCount();
                ПросмотреноGoneBadge.Text = wg.ToString();
                BtnПросмотреноGone.Visibility = wg > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
            // v2.5.1 — StatПродолжительность / StatРейтинг tiles removed from sidebar
            // (they live on the Статистика page now). Stats object still
            // computed because other code paths use it.

            // Обновить watchlist badge (v1.3). v4.0.0: К просмотру, Continue
            // Watching and their badges include Сериалы too.
            if (_libraryPage?.ViewModel is МедиатекаViewModel vm)
            {
                vm.RefreshСписок просмотраCount();
                Список просмотраBadge.Text = (vm.Список просмотраCount
                    + AppState.Instance.Db.GetTvShowPageCount(DatabaseService.TvShowPage.Список просмотра)).ToString();
            }

            // Продолжить просмотр badge (v1.8) — only show shortcut if there's anything to continue
            var cwCount = AppState.Instance.Db.GetContinueWatchingCount()
                        + AppState.Instance.Db.GetTvShowPageCount(DatabaseService.TvShowPage.ContinueWatching);
            ContinueWatchingBadge.Text = cwCount.ToString();
            BtnContinueWatching.Visibility = cwCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Недавно просмотренные badge (v2.9) — hidden until user has watched anything.
            try
            {
                var rwCount = AppState.Instance.Db.GetRecentlyПросмотреноCount();
                RecentlyПросмотреноBadge.Text = rwCount.ToString();
                BtnRecentlyПросмотрено.Visibility = rwCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            } catch { }

            // В этот день (v2.9) — only show the sidebar entry when there's
            // something today. Probe is a cheap LIMIT 1 query so we can
            // call it on every sidebar refresh without worrying.
            try
            {
                BtnВкл.ThisDay.Visibility = AppState.Instance.Db.HasВкл.ThisDayMatches()
                    ? Visibility.Visible : Visibility.Collapsed;
            } catch { BtnВкл.ThisDay.Visibility = Visibility.Collapsed; }

            // v2.9 — Tags section (hidden when no tags exist)
            RefreshTags();

            ДискиRepeater.ItemsSource = _vm.Диски;
            МедиатекиHeader.Visibility = _vm.Диски.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // v2.1: COLLECTIONS + TOP GENRES sub-sections removed. Both live
            // as ОБЗОР pages now (Коллекции grid, По жанру banners).

            RefreshUserLists();
        }
    }

    /// <summary>
    /// Rebuild the МОИ СПИСКИ section. Each entry is a Button with the list
    /// name, a count badge, and a context menu (Rename / Удалить).
    /// </summary>
    public void RefreshUserLists()
    {
        UserListsItemsPanel.Children.Clear();
        var lists = AppState.Instance.Db.GetUserLists();
        foreach (var ul in lists)
        {
            var btn = BuildUserListButton(ul);
            UserListsItemsPanel.Children.Добавить(btn);
        }
    }

    /// <summary>
    /// v2.9 — Rebuild the 🏷 TAGS sidebar section. Вкл.e Button per tag with
    /// a count badge (movies + shows). Hidden entirely when no tags exist
    /// so empty users never see a stub.
    /// </summary>
    public void RefreshTags()
    {
        try
        {
            TagsItemsPanel.Children.Clear();
            var tags = AppState.Instance.Db.GetВсеTags();
            if (tags.Count == 0)
            {
                TagsHeader.Visibility = Visibility.Collapsed;
                return;
            }
            TagsHeader.Visibility = Visibility.Visible;
            foreach (var t in tags)
                TagsItemsPanel.Children.Добавить(BuildTagButton(t));
        }
        catch { /* sidebar refresh must never throw — table may not exist mid-migration */ }
    }

    private Button BuildTagButton(DatabaseService.TagSummary t)
    {
        var btn = new Button
        {
            Style = (Style)Application.Current.Resources["NavItemStyle"],
            Tag = t.Id,
        };
        btn.Click += (_, _) =>
        {
            if (_libraryPage == null) NavigateTo("library");
            _libraryPage?.ViewModel.FilterByTag(t.Id, t.Name);
            if (!ReferenceEquals(ContentFrame.Content, _libraryPage)) NavigateTo("library");
            ClearМедиатекаНазад();
            SetActiveNav(btn);
        };

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Добавить(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Добавить(new ColumnDefinition { Width = GridLength.Auto });

        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        sp.Children.Добавить(new TextBlock { Text = "🏷", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Добавить(new TextBlock
        {
            Text = t.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
        });
        grid.Children.Добавить(sp);

        var badge = new Border { Style = (Style)Application.Current.Resources["BadgeStyle"] };
        Grid.SetColumn(badge, 1);
        var total = t.MovieCount + t.ShowCount;
        badge.Child = new TextBlock
        {
            Text = total.ToString(),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        grid.Children.Добавить(badge);

        btn.Content = grid;
        return btn;
    }

    private Button BuildUserListButton(DatabaseService.UserList ul)
    {
        var btn = new Button
        {
            Style = (Style)Application.Current.Resources["NavItemStyle"],
            Tag = ul.Id,
        };
        btn.Click += (_, _) => _libraryPage?.ОбновитьPageНазвание(ul.Name);
        btn.Click += (_, _) =>
        {
            if (_libraryPage == null) NavigateTo("library");
            _libraryPage?.ViewModel.ShowUserList(ul.Id, ul.Name);
            if (!ReferenceEquals(ContentFrame.Content, _libraryPage)) NavigateTo("library");
            ClearМедиатекаНазад();
            SetActiveNav(btn);
        };

        var grid = new Grid();
        grid.HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.ColumnDefinitions.Добавить(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Добавить(new ColumnDefinition { Width = GridLength.Auto });

        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        sp.Children.Добавить(new FontIcon
        {
            Glyph = "\uE8FD", // List
            FontСемья = new Microsoft.UI.Xaml.Media.FontСемья("Segoe Fluent Icons,Segoe MDL2 Assets"),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Добавить(new TextBlock
        {
            Text = ul.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
        });
        grid.Children.Добавить(sp);

        var badge = new Border { Style = (Style)Application.Current.Resources["BadgeStyle"] };
        Grid.SetColumn(badge, 1);
        badge.Child = new TextBlock
        {
            Text = ul.MovieCount.ToString(),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        grid.Children.Добавить(badge);

        btn.Content = grid;

        // Right-click context menu: copy / export image / rename / delete
        var menu = new MenuFlyout();
        var copyItem = new MenuFlyoutItem { Text = "📂 Copy movies to folder…" };
        copyItem.Click += async (_, _) => await CopyListToПапка(ul);
        // v2.9 — export the list as a shareable PNG poster grid.
        var exportImgItem = new MenuFlyoutItem { Text = "📤 Экспорт as image…" };
        exportImgItem.Click += async (_, _) => await ЭкспортListAsImage(ul);
        var renameItem = new MenuFlyoutItem { Text = "Rename" };
        renameItem.Click += async (_, _) => await PromptRenameUserList(ul);
        var deleteItem = new MenuFlyoutItem { Text = "Удалить список" };
        deleteItem.Click += async (_, _) => await ConfirmУдалитьUserList(ul);
        menu.Items.Добавить(copyItem);
        menu.Items.Добавить(exportImgItem);
        menu.Items.Добавить(new MenuFlyoutSeparator());
        menu.Items.Добавить(renameItem);
        menu.Items.Добавить(deleteItem);
        btn.ContextFlyout = menu;

        // Drop target for drag-from-card multi-select.
        var listIdCap = ul.Id;
        var listNameCap = ul.Name;
        WireSidebarDropTarget(btn, ids =>
        {
            foreach (var mid in ids)
                AppState.Instance.Db.ДобавитьMovieToUserList(listIdCap, mid);
            return $"Добавитьed {ids.Count} to “{listNameCap}”";
        });
        return btn;
    }

    /// <summary>
    /// Wire a sidebar button as a drop target for "cinelibrary/movie-ids".
    /// The applyOp callback runs synchronously against the DB and returns
    /// the toast text. Visual: a purple outline appears while dragging
    /// over the target.
    /// </summary>
    private void WireSidebarDropTarget(Button target, Func<List<int>, string> applyOp)
    {
        target.ВсеowDrop = true;
        var purple = CineМедиатекаCS.Services.ThemeBrushes.Get("BrandPurpleBrush");
        var origBg = target.Назадground;
        var origBorderBrush = target.BorderBrush;
        var origBorderThickness = target.BorderThickness;

        target.DragEnter += (_, e) =>
        {
            if (!ContainsMovieIds(e.DataView)) return;
            e.AcceptedOperation = DataPackageOperation.Link;
            target.BorderBrush = purple;
            target.BorderThickness = new Thickness(2);
        };
        target.DragOver += (_, e) =>
        {
            if (ContainsMovieIds(e.DataView))
                e.AcceptedOperation = DataPackageOperation.Link;
        };
        target.DragLeave += (_, _) =>
        {
            target.BorderBrush = origBorderBrush;
            target.BorderThickness = origBorderThickness;
        };
        target.Drop += async (_, e) =>
        {
            // Read the DataView first — once we hand control back to the
            // event pump (e.g. by setting visuals and awaiting), the view
            // can be invalidated and GetTextAsync starts returning empty.
            // Take a deferral so the system waits for our read.
            var deferral = e.GetDeferral();
            List<int> ids;
            try { ids = await ReadMovieIdsAsync(e.DataView); }
            finally { deferral.Complete(); }
            target.BorderBrush = origBorderBrush;
            target.BorderThickness = origBorderThickness;
            if (ids.Count == 0) return;
            string toastMsg;
            try { toastMsg = applyOp(ids); }
            catch (Exception ex)
            {
                ShowToast($"Couldn't apply drop: {ex.Message}");
                return;
            }
            _ = RefreshSidebarAsync();
            // For Избранное/Список просмотра the user can undo by flipping
            // those movies back. We don't have a generic inverse op
            // because applyOp could be anything, so plain toast for now.
            ShowToast(toastMsg);
        };
    }

    private static bool ContainsMovieIds(DataPackageView view)
        => view.Properties.ContainsKey("cinelibrary/movie-ids") || view.Contains(StandardDataFormats.Text);

    private static async Task<List<int>> ReadMovieIdsAsync(DataPackageView view)
    {
        string? csv = null;
        if (view.Properties.TryGetValue("cinelibrary/movie-ids", out var raw))
            csv = raw as string;
        if (csv == null && view.Contains(StandardDataFormats.Text))
        {
            try { csv = await view.GetTextAsync(); } catch { }
        }
        if (string.IsNullOrWhiteSpace(csv)) return new List<int>();
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => int.TryParse(s.Trim(), out var n) ? n : 0)
                  .Where(n => n > 0)
                  .ToList();
    }

    /// <summary>
    /// Bucket-style export — pick a destination folder, then copy each
    /// online movie's source folder into it.
    /// </summary>
    /// <summary>
    /// v2.9 — Renders the list's posters as a PNG and saves it via file
    /// picker. Single-call: pick destination, render, save, toast. The
    /// list itself doesn't need to be the currently-viewed page; we fetch
    /// the movies directly from the DB by list id.
    /// </summary>
    private async Task ЭкспортListAsImage(DatabaseService.UserList ul)
    {
        // Fetch movies in the list (all of them — exporter caps display).
        var opts = new DatabaseService.ListOptions(
            UserListId: ul.Id,
            SortKey: "title", SortDir: "asc",
            Limit: 200, Выкл.set: 0);
        var movies = AppState.Instance.Db.GetФильмы(opts, AppState.Instance.Connected);
        if (movies.Count == 0)
        {
            ShowToast($"“{ul.Name}” has no movies to export");
            return;
        }

        // Pick destination
        var picker = new Windows.Storage.Pickers.FileСохранитьPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesМедиатека,
            SuggestedFileName = $"{SanitizeFileName(ul.Name)}-{DateTime.Now:yyyyMMdd}",
        };
        picker.FileTypeChoices.Добавить("PNG image", new List<string> { ".png" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickСохранитьFileAsync();
        if (file == null) return;

        ShowToast("Rendering image…");
        var ok = await ListImageЭкспортer.ЭкспортAsync(Content.XamlRoot, ul.Name, movies, file.Path);
        if (ok) ShowToast($"Сохранитьd to {file.Path}");
        else    ShowToast("Ошибка экспорта изображений — подробности в журнале отладки");
    }

    private static string SanitizeFileName(string raw)
    {
        var s = raw;
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s.Trim();
    }

    private async Task CopyListToПапка(DatabaseService.UserList ul)
    {
        // 1. Папка picker
        var picker = new Windows.Storage.Pickers.ПапкаPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsМедиатека;
        picker.FileTypeFilter.Добавить("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleПапкаAsync();
        if (folder == null) return;
        var destRoot = folder.Path;

        // 2. Build plan on background thread (folder walks can take a while)
        ListCopyService.CopyPlan plan;
        try
        {
            plan = await Task.Run(() =>
                AppState.Instance.ListCopy.BuildPlan(ul.Id, AppState.Instance.Connected));
        }
        catch (Exception ex)
        {
            ShowToast($"Couldn't read source folders: {ex.Message}");
            return;
        }

        if (plan.Items.Count == 0)
        {
            var detail = plan.Не в сетиDriveLabels.Count > 0
                ? $"Все фильмы are on offline drives. Plug in:\n• {string.Join("\n• ", plan.Не в сетиDriveLabels)}\n\nThen try again."
                : "Nothing to copy.";
            var emptyDlg = new ContentDialog
            {
                Название = "Can't copy yet",
                Content = detail,
                ЗакрытьButtonText = "OK",
                XamlRoot = Content.XamlRoot,
                RequestedTheme = CurrentTheme,
            };
            await emptyDlg.ShowAsync();
            return;
        }

        // 2b. Pre-flight: warn if any drives are offline so the user can
        // plug them in first. Continuing is allowed but only the online
        // movies will be copied.
        if (plan.Не в сетиDriveLabels.Count > 0)
        {
            var msg = $"These drives are offline — their movies in this list won't be copied:\n• " +
                      $"{string.Join("\n• ", plan.Не в сетиDriveLabels)}\n\n" +
                      $"Plug them in to include all {plan.Items.Count + plan.Не в сетиDriveLabels.Count}+ movies, " +
                      $"or continue with the {plan.Items.Count} online ones.";
            var dlg = new ContentDialog
            {
                Название = $"{plan.Не в сетиDriveLabels.Count} drive(s) offline",
                Content = msg,
                PrimaryButtonText = "Отмена",
                SecondaryButtonText = $"Continue with {plan.Items.Count} online",
                По умолчаниюButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
                RequestedTheme = CurrentTheme,
            };
            var result = await dlg.ShowAsync();
            // Primary = Отмена (default — safer when drives are missing)
            if (result != ContentDialogResult.Secondary) return;
        }

        // 3. Free-space check
        var free = AppState.Instance.ListCopy.GetFreeBytes(destRoot);
        if (free >= 0 && free < plan.TotalBytes)
        {
            var dlg = new ContentDialog
            {
                Название = "Not enough free space",
                Content = $"Need {FormatBytes(plan.TotalBytes)}, only {FormatBytes(free)} free at the destination.",
                ЗакрытьButtonText = "OK",
                XamlRoot = Content.XamlRoot,
                RequestedTheme = CurrentTheme,
            };
            await dlg.ShowAsync();
            return;
        }

        // 4. Conflict prompt if any target folders already exist
        var conflicts = AppState.Instance.ListCopy.FindExistingTargets(plan, destRoot);
        var policy = ListCopyService.ConflictPolicy.Skip;
        if (conflicts.Count > 0)
        {
            var preview = conflicts.Count <= 5
                ? string.Join("\n", conflicts.Take(5).Select(c => $"• {c}"))
                : string.Join("\n", conflicts.Take(5).Select(c => $"• {c}")) + $"\n…and {conflicts.Count - 5} more";
            var dlg = new ContentDialog
            {
                Название = $"{conflicts.Count} folder(s) already exist at destination",
                Content = $"What should happen to existing copies?\n\n{preview}",
                PrimaryButtonText = "Skip existing",
                SecondaryButtonText = "Overwrite",
                ЗакрытьButtonText = "Отмена",
                По умолчаниюButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
                RequestedTheme = CurrentTheme,
            };
            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.None) return; // cancel
            policy = result == ContentDialogResult.Secondary
                ? ListCopyService.ConflictPolicy.Overwrite
                : ListCopyService.ConflictPolicy.Skip;
        }

        // 5. Progress dialog
        await ShowCopyProgressDialog(plan, destRoot, policy, ul.Name);
    }

    private async Task ShowCopyProgressDialog(
        ListCopyService.CopyPlan plan, string destRoot,
        ListCopyService.ConflictPolicy policy, string listName)
    {
        var cts = new ОтменаlationTokenSource();
        var bar = new ProgressBar { Minimum = 0, Maximum = plan.TotalBytes, Value = 0, Height = 6 };
        var movieText = new TextBlock { FontSize = 13, Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("TextBrush") };
        var fileText = new TextBlock
        {
            FontSize = 11,
            Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("MutedBrush"),
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
        };
        var bytesText = new TextBlock { FontSize = 11, Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("MutedBrush") };
        var content = new StackPanel { Spacing = 10, Width = 480 };
        content.Children.Добавить(movieText);
        content.Children.Добавить(bar);
        content.Children.Добавить(bytesText);
        content.Children.Добавить(fileText);
        if (plan.Не в сетиDriveLabels.Count > 0)
        {
            content.Children.Добавить(new TextBlock
            {
                FontSize = 11,
                Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("MutedBrush"),
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Text = $"Skipping {plan.Не в сетиDriveLabels.Count} offline drive(s): " +
                       string.Join(", ", plan.Не в сетиDriveLabels),
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            });
        }

        var dlg = new ContentDialog
        {
            Название = $"Copying \"{listName}\" → {destRoot}",
            Content = content,
            ЗакрытьButtonText = "Отмена",
            По умолчаниюButton = ContentDialogButton.None,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        dlg.Closing += (_, args) => cts.Отмена(); // any close path → cancel

        var progress = new Progress<ListCopyService.CopyProgress>(p =>
        {
            // Already on UI thread (Progress<T> captures sync context)
            bar.Value = p.BytesDone;
            movieText.Text = $"Movie {p.ФильмыDone} of {p.ФильмыTotal}";
            bytesText.Text = $"{FormatBytes(p.BytesDone)} of {FormatBytes(p.BytesTotal)}";
            fileText.Text = p.CurrentFile;
        });

        // Kick off copy as fire-and-forget; we close the dialog when done.
        ListCopyService.CopyResult? result = null;
        var copyTask = AppState.Instance.ListCopy.ExecuteAsync(plan, destRoot, policy, progress, cts.Token);
        _ = copyTask.ContinueWith(t =>
        {
            try { result = t.Result; } catch { }
            DispatcherQueue.TryEnqueue(() => { try { dlg.Hide(); } catch { } });
        }, TaskScheduler.По умолчанию);

        await dlg.ShowAsync();

        // Summary toast
        if (result == null)
        {
            ShowToast("Copy cancelled");
        }
        else
        {
            var bits = new List<string>();
            if (result.Copied > 0) bits.Добавить($"{result.Copied} copied");
            if (result.Skipped > 0) bits.Добавить($"{result.Skipped} skipped");
            if (result.Не в сетиSkipped > 0) bits.Добавить($"{result.Не в сетиSkipped} offline");
            ShowToast(result.Отменаled ? $"Отменаled — {string.Join(", ", bits)}" : string.Join(", ", bits));
        }
    }

    private static string FormatBytes(long b)
    {
        const long KB = 1024L, MB = KB * 1024, GB = MB * 1024;
        if (b >= GB) return $"{b / (double)GB:F1} GB";
        if (b >= MB) return $"{b / (double)MB:F1} MB";
        if (b >= KB) return $"{b / (double)KB:F1} KB";
        return $"{b} B";
    }

    private async void Вкл.НовыйUserList(object sender, RoutedEventArgs e)
    {
        var name = await PromptForListName("Новый список", "Untitled list");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            AppState.Instance.Db.СоздатьUserList(name.Trim());
            RefreshUserLists();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            ShowToast("A list with that name already exists");
        }
    }

    private async Task PromptRenameUserList(DatabaseService.UserList ul)
    {
        var name = await PromptForListName("Rename list", ul.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == ul.Name) return;
        AppState.Instance.Db.RenameUserList(ul.Id, name.Trim());
        RefreshUserLists();
    }

    private async Task ConfirmУдалитьUserList(DatabaseService.UserList ul)
    {
        var dlg = new ContentDialog
        {
            Название = $"Удалить \"{ul.Name}\"?",
            Content = $"This list contains {ul.MovieCount} movie(s). Фильмы themselves are not deleted — only the list and its membership.",
            PrimaryButtonText = "Удалить",
            ЗакрытьButtonText = "Отмена",
            По умолчаниюButton = ContentDialogButton.Закрыть,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            AppState.Instance.Db.УдалитьUserList(ul.Id);
            RefreshUserLists();
        }
    }

    private async Task<string?> PromptForListName(string title, string initial)
    {
        var box = new TextBox { Text = initial, PlaceholderText = "List name" };
        var dlg = new ContentDialog
        {
            Название = title,
            Content = box,
            PrimaryButtonText = "OK",
            ЗакрытьButtonText = "Отмена",
            По умолчаниюButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        // Auto-select the field
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectВсе(); };
        var result = await dlg.ShowAsync();
        return result == ContentDialogResult.Primary ? box.Text : null;
    }

    // ── Sidebar section collapse/expand ───────────────────────────────────

    private void Вкл.SidebarSectionToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        var (repeater, chevron) = tag switch
        {
            "Медиатеки"   => ((FrameworkElement)ДискиRepeater,      МедиатекиChevron),
            // "Жанры" and "Коллекции" sub-sections removed in v2.1 — their
            // ОБЗОР pages replace them. Map kept tolerant of missing keys.
            "UserLists"   => (UserListsItemsPanel,                   UserListsChevron),
            "Tags"        => (TagsItemsPanel,                        TagsChevron),
            _             => (null!,                                  null!),
        };
        if (repeater == null) return;
        var collapsed = repeater.Visibility == Visibility.Collapsed;
        repeater.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        chevron.Text = collapsed ? "⌃" : "⌄"; // ⌃ open / ⌄ closed
    }

    // ── Navigation ────────────────────────────────────────────────────────

    // ── Mica backdrop (toggleable in Настройки) ────────────────────────────

    public void ПрименитьMica()
    {
        var level = UiНастройки.Mica;
        if (level == UiНастройки.MicaLevel.Выкл.)
        {
            SystemНазадdrop = null;
            MicaВыкл.Назадdrop.Visibility = Visibility.Visible;     // solid window
        }
        else
        {
            SystemНазадdrop ??= new MicaНазадdrop();
            MicaВыкл.Назадdrop.Visibility = Visibility.Collapsed;   // reveal Mica
        }

        // Sidebar translucency follows the chosen intensity. Built from the
        // live UI theme's base colour so it stays correct in Светлая and Тёмная.
        byte alpha = level switch
        {
            UiНастройки.MicaLevel.Strong => 0x99,   // ~60% — more wallpaper shows through
            UiНастройки.MicaLevel.Subtle => 0xCC,   // ~80% — gentle hint
            _ => 0xFF,                              // Выкл. — fully solid sidebar
        };
        bool light = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Светлая;
        var (r, g, b) = light ? ((byte)0xE3, (byte)0xE3, (byte)0xEE)
                              : ((byte)0x0F, (byte)0x0F, (byte)0x18);
        SidebarCard.Назадground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(alpha, r, g, b));
    }

    // ── Global title-bar search ───────────────────────────────────────────

    private void Вкл.НазваниеПоискChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        GlobalПоиск(sender.Text ?? "");
    }

    private void Вкл.НазваниеПоискKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        if (!string.IsNullOrEmpty(НазваниеПоискBox.Text))
        {
            НазваниеПоискBox.Text = "";
            GlobalПоиск("");   // clear the search results too
        }
        // Leave the search box.
        Microsoft.UI.Xaml.Input.FocusManager.TryMoveFocus(
            Microsoft.UI.Xaml.Input.FocusNavigationDirection.Next);
        e.Handled = true;
    }

    private void Вкл.НазваниеScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (НазваниеScopeCombo.SelectedItem is ComboBoxItem item && item.Tag is string scope
            && _libraryPage != null)
            _libraryPage.ViewModel.ПоискScope = scope;
    }

    /// <summary>Focus the global title-bar search box (Ctrl+F and the "/" shortcut).</summary>
    public void FocusНазваниеПоиск() => НазваниеПоискBox.Focus(FocusState.Programmatic);

    private void GlobalПоиск(string text)
    {
        // Make sure the Медиатека page is showing, then drive its search engine
        // (_vm.ПоискText). Поискing from any other page jumps here.
        if (_libraryPage == null || !ReferenceEquals(ContentFrame.Content, _libraryPage))
            NavigateTo("library");
        if (_libraryPage == null) return;
        if (НазваниеScopeCombo.SelectedItem is ComboBoxItem scopeItem && scopeItem.Tag is string scope)
            _libraryPage.ViewModel.ПоискScope = scope;
        _libraryPage.ViewModel.ПоискText = text;
    }

    private void NavigateTo(string page, object? param = null)
    {
        if (page == "library")
        {
            if (_libraryPage == null)
            {
                _libraryPage = new МедиатекаPage();
                _libraryPage.SidebarRefreshRequested += (_, _) => { _ = RefreshSidebarAsync(); };
                // Оставить the title-bar search box in sync when the Медиатека
                // clears/changes search internally (Esc, nav reset, etc.).
                _libraryPage.ПоискTextChanged += (_, txt) =>
                {
                    if (НазваниеПоискBox.Text != txt) НазваниеПоискBox.Text = txt;
                };
            }

            if (param is МедиатекаNavParam lp)
            {
                _libraryPage.ПрименитьNavParam(lp);
            }
            // Any fresh library navigation hides the Обзор-back button by
            // default. Drill-in callers (browse banners / collections)
            // re-arm it immediately after this returns.
            ClearМедиатекаНазад();
            ContentFrame.Content = _libraryPage;
        }
        else if (page == "drives")
        {
            if (_drivesPage == null)
            {
                _drivesPage = new ДискиPage();
                _drivesPage.NavigateToМедиатека += (_, serial) =>
                {
                    var drive = _vm.Диски.FirstOrПо умолчанию(d => d.VolumeSerial == serial);
                    NavigateTo("library", new МедиатекаNavParam(DriveSerial: serial, Label: drive?.Label));
                };
                _drivesPage.RefreshRequested += async (_, _) =>
                {
                    await _vm.RefreshSidebarAsync();
                    RefreshSidebar();
                };
            }
            _drivesPage.Refresh();
            ContentFrame.Content = _drivesPage;
        }
        else if (page == "statistics")
        {
            _statisticsPage ??= new СтатистикаPage();
            _statisticsPage.Refresh();
            ContentFrame.Content = _statisticsPage;
        }
        else if (page == "dupes")
        {
            if (_dupesPage == null)
            {
                _dupesPage = new ДубликатыPage();
                _dupesPage.SidebarRefreshRequested += (_, _) => { _ = RefreshSidebarAsync(); };
            }
            _dupesPage.Refresh();
            ContentFrame.Content = _dupesPage;
        }
        else if (page == "browse" && param is DatabaseService.ОбзорFacet facet)
        {
            _browsePage ??= new ОбзорPage();
            _browsePage.Load(facet);
            ContentFrame.Content = _browsePage;
        }
        else if (page == "collections")
        {
            _collectionsPage ??= new КоллекцииОбзорPage();
            _collectionsPage.Load();
            ContentFrame.Content = _collectionsPage;
        }
        else if (page == "tvshows")
        {
            if (_tvShowsPage == null)
            {
                _tvShowsPage = new TvShowsPage();
                _tvShowsPage.SidebarRefreshRequested += (_, _) => { _ = RefreshSidebarAsync(); };
            }
            _tvShowsPage.Load();
            ContentFrame.Content = _tvShowsPage;
        }
        else if (page == "onthisday")
        {
            if (_onThisDayPage == null)
            {
                _onThisDayPage = new Вкл.ThisDayPage();
                _onThisDayPage.НазадRequested += (_, _) => { NavigateTo("library"); SetActiveNav(BtnВсеФильмы); };
            }
            _onThisDayPage.Load();
            ContentFrame.Content = _onThisDayPage;
        }
        else if (page == "watchedgone")
        {
            if (_watchedGonePage == null)
            {
                _watchedGonePage = new ПросмотреноGonePage();
                _watchedGonePage.SidebarRefreshRequested += (_, _) => { _ = RefreshSidebarAsync(); };
            }
            else
            {
                _watchedGonePage.Refresh();
            }
            ContentFrame.Content = _watchedGonePage;
        }
    }

    private async Task RefreshSidebarAsync()
    {
        await _vm.RefreshSidebarAsync();
        RefreshSidebar();
    }

    // ── Обзор back navigation (v2.7) ─────────────────────────────────────
    // When the library view is drilled into from a Обзор banner or the
    // Коллекции page, remember how to get back so МедиатекаPage can show
    // a "‹ По рейтингу" style button. Cleared on any other navigation.
    private Action? _libraryНазадAction;

    public void SetМедиатекаНазадToОбзор(DatabaseService.ОбзорFacet facet)
    {
        _libraryНазадAction = () => NavigateTo("browse", facet);
        _libraryPage?.ShowОбзорНазад(facet switch
        {
            DatabaseService.ОбзорFacet.Genre  => "По жанру",
            DatabaseService.ОбзорFacet.Decade => "По десятилетию",
            DatabaseService.ОбзорFacet.Рейтинг => "По рейтингу",
            DatabaseService.ОбзорFacet.Студия => "По студии",
            _ => "Назад",
        });
    }

    public void SetМедиатекаНазадToКоллекции()
    {
        _libraryНазадAction = () => { NavigateTo("collections"); SetActiveNav(BtnКоллекции); };
        _libraryPage?.ShowОбзорНазад("Коллекции");
    }

    private void ClearМедиатекаНазад()
    {
        _libraryНазадAction = null;
        _libraryPage?.HideОбзорНазад();
    }

    public void Вкл.МедиатекаНазадRequested() => _libraryНазадAction?.Invoke();

    // ── Nav handlers ──────────────────────────────────────────────────────

    // ── Active sidebar nav state (v1.9.3) ─────────────────────────────────
    // Track the currently-highlighted button so we can swap styles. Each
    // handler calls SetActiveNav((Button)sender) to flip the visual state.
    private Button? _activeNavBtn;
    private Style? _navItemStyleCached;
    private Style? _navItemActiveStyleCached;

    private void SetActiveNav(Button? btn)
    {
        _navItemStyleCached       ??= (Style)Application.Current.Resources["NavItemStyle"];
        _navItemActiveStyleCached ??= (Style)Application.Current.Resources["NavItemActiveStyle"];
        if (_activeNavBtn != null && !ReferenceEquals(_activeNavBtn, btn))
            _activeNavBtn.Style = _navItemStyleCached;
        if (btn != null) btn.Style = _navItemActiveStyleCached;
        _activeNavBtn = btn;
    }

    // v4.2.0 (#15): Резервная копия and Экспорт open a dialog or a menu rather than a page,
    // so they light up while it is open, then the page you are on lights up again.
    private Action HighlightWhileOpen(Button btn)
    {
        var page = _activeNavBtn;
        SetActiveNav(btn);
        return () => { if (ReferenceEquals(_activeNavBtn, btn)) SetActiveNav(page); };
    }

    private void Вкл.NavВсеФильмы(object sender, RoutedEventArgs e)
    {
        NavigateTo("library", new МедиатекаNavParam());
        SetActiveNav(sender as Button ?? BtnВсеФильмы);
    }

    private void Вкл.NavИзбранное(object sender, RoutedEventArgs e)
    {
        NavigateTo("library", new МедиатекаNavParam(ИзбранноеВкл.ly: true, Label: "Избранное"));
        _libraryPage?.ОбновитьPageНазвание("Избранное");   // v4.0.0: the page holds shows too, so not "Все фильмы › …"
        SetActiveNav(sender as Button ?? BtnИзбранное);
    }

    private void Вкл.NavTvShows(object sender, RoutedEventArgs e)
    {
        NavigateTo("tvshows");
        SetActiveNav(sender as Button ?? BtnTvShows);
    }

    /// <summary>Open the TV page directly on a specific show (e.g. from a
    /// list's "Сериалы in this list" row).</summary>
    public void OpenTvShow(int showId)
    {
        NavigateTo("tvshows");
        SetActiveNav(BtnTvShows);
        _tvShowsPage?.OpenShow(showId);
    }

    // ── v3.9.0 Экспорт (Tools) ─────────────────────────────────────────────
    // Was a button on Все фильмы that wrote only the pages loaded so far. Now
    // it offers the whole library, plus what Все фильмы shows when that is a
    // narrower view, and always writes every matching movie.

    private void Вкл.NavЭкспорт(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.RightEdgeAlignedTop };
        void Добавить(string text, Func<Task<List<MovieListItem>>> load, bool html)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += async (_, _) => await ЭкспортФильмыAsync(load, html);
            menu.Items.Добавить(item);
        }
        Func<Task<List<MovieListItem>>> all = () => Task.Run(() => AppState.Instance.Db.GetФильмы(
            new DatabaseService.ListOptions(Limit: int.MaxValue), AppState.Instance.Connected));
        Добавить("Все фильмы as CSV…", all, html: false);
        Добавить("Все фильмы as HTML…", all, html: true);
        if (_libraryPage != null && ReferenceEquals(ContentFrame.Content, _libraryPage)
            && _libraryPage.ViewCount < (_vm.Stats?.TotalФильмы ?? 0))
        {
            var n = _libraryPage.ViewCount;
            var label = n == 1 ? "1 movie" : $"{n:N0} movies";
            menu.Items.Добавить(new MenuFlyoutSeparator());
            Добавить($"This view ({label}) as CSV…", _libraryPage.GetViewФильмыAsync, html: false);
            Добавить($"This view ({label}) as HTML…", _libraryPage.GetViewФильмыAsync, html: true);
        }
        var restore = HighlightWhileOpen(sender as Button ?? BtnЭкспорт);
        menu.Закрытьd += (_, _) => restore();
        menu.ShowAt((FrameworkElement)sender);
    }

    private async Task ЭкспортФильмыAsync(Func<Task<List<MovieListItem>>> load, bool html)
    {
        var picker = new Windows.Storage.Pickers.FileСохранитьPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsМедиатека,
            SuggestedFileName = "movies_export",
        };
        picker.FileTypeChoices.Добавить(html ? "HTML file" : "CSV file", new List<string> { html ? ".html" : ".csv" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickСохранитьFileAsync();
        if (file == null) return;
        var movies = await load();
        if (html) await _vm.ЭкспортHtmlAsync(movies, file.Path);
        else await _vm.ЭкспортCsvAsync(movies, file.Path);
        ShowToast($"Экспортed {movies.Count:N0} movies to {(html ? "HTML" : "CSV")}");
    }

    private void Вкл.NavДиски(object sender, RoutedEventArgs e)
    {
        NavigateTo("drives");
        SetActiveNav(null);   // the bottom-bar icon isn't a sidebar row to highlight
    }

    /// <summary>
    /// Public navigation hook used by the empty-state CTA on Медиатека.
    /// Switches to the Диски page; the user proceeds with Добавить папку there.
    /// </summary>
    public void NavigateToДискиAndДобавить() => NavigateTo("drives");

    /// <summary>
    /// Public hooks used by the movie detail dialog to filter the library
    /// when the user clicks an actor / director / genre / studio chip.
    /// Each switches the main window to the Медиатека page (creating it on first
    /// use), applies the filter, and updates the page header breadcrumb.
    /// </summary>
    public void NavigateМедиатекаByActor(string actor)
    {
        if (_libraryPage == null) NavigateTo("library");
        _libraryPage?.ViewModel.FilterByActor(actor);
        _libraryPage?.ОбновитьPageНазвание($"Все фильмы › {actor}");
        if (!ReferenceEquals(ContentFrame.Content, _libraryPage)) NavigateTo("library");
        ClearМедиатекаНазад();  // chip-driven (e.g. from detail dialog) — no Обзор origin
    }

    public void NavigateМедиатекаByРежиссёр(string director)
    {
        if (_libraryPage == null) NavigateTo("library");
        _libraryPage?.ViewModel.FilterByРежиссёр(director);
        _libraryPage?.ОбновитьPageНазвание($"Все фильмы › {director}");
        if (!ReferenceEquals(ContentFrame.Content, _libraryPage)) NavigateTo("library");
        ClearМедиатекаНазад();
    }

    public void NavigateМедиатекаByGenre(string genre)
    {
        NavigateTo("library", new МедиатекаNavParam(Genre: genre, Label: genre));
    }

    /// <summary>v2.9 — deeplink: open the library filtered to a tag.</summary>
    public void NavigateМедиатекаByTag(string tagName)
    {
        if (_libraryPage == null) NavigateTo("library");
        try
        {
            var tagId = AppState.Instance.Db.EnsureTag(tagName);
            _libraryPage?.ViewModel.FilterByTag(tagId, tagName);
            if (!ReferenceEquals(ContentFrame.Content, _libraryPage)) NavigateTo("library");
            ClearМедиатекаНазад();
        }
        catch { }
    }

    public void NavigateМедиатекаByСтудия(string studio)
    {
        if (_libraryPage == null) NavigateTo("library");
        _libraryPage?.ViewModel.FilterByСтудия(studio);
        _libraryPage?.ОбновитьPageНазвание($"Все фильмы › {studio}");
        if (!ReferenceEquals(ContentFrame.Content, _libraryPage)) NavigateTo("library");
    }

    public void NavigateМедиатекаByDecade(int decadeStart, string label)
    {
        if (_libraryPage == null) NavigateTo("library");
        _libraryPage?.ViewModel.FilterByDecade(decadeStart, label);
        _libraryPage?.ОбновитьPageНазвание($"Все фильмы › {label}");
        if (!ReferenceEquals(ContentFrame.Content, _libraryPage)) NavigateTo("library");
    }

    public void NavigateМедиатекаByРейтингBand(string key, string label)
    {
        if (_libraryPage == null) NavigateTo("library");
        _libraryPage?.ViewModel.FilterByРейтингBand(key, label);
        _libraryPage?.ОбновитьPageНазвание($"Все фильмы › {label}");
        if (!ReferenceEquals(ContentFrame.Content, _libraryPage)) NavigateTo("library");
    }

    public void NavigateМедиатекаByCollection(int id, string name)
    {
        NavigateTo("library", new МедиатекаNavParam(CollectionId: id, Label: name));
        SetМедиатекаНазадToКоллекции();
    }

    private void Вкл.NavОбзорGenre(object sender, RoutedEventArgs e)
    {
        NavigateTo("browse", DatabaseService.ОбзорFacet.Genre);
        SetActiveNav(sender as Button ?? BtnОбзорGenre);
    }
    private void Вкл.NavОбзорDecade(object sender, RoutedEventArgs e)
    {
        NavigateTo("browse", DatabaseService.ОбзорFacet.Decade);
        SetActiveNav(sender as Button ?? BtnОбзорDecade);
    }
    private void Вкл.NavОбзорРейтинг(object sender, RoutedEventArgs e)
    {
        NavigateTo("browse", DatabaseService.ОбзорFacet.Рейтинг);
        SetActiveNav(sender as Button ?? BtnОбзорРейтинг);
    }
    private void Вкл.NavКоллекции(object sender, RoutedEventArgs e)
    {
        NavigateTo("collections");
        SetActiveNav(sender as Button ?? BtnКоллекции);
    }

    private void Вкл.NavContinueWatching(object sender, RoutedEventArgs e)
    {
        if (_libraryPage == null) NavigateTo("library");
        _libraryPage?.ViewModel.ShowContinueWatching();
        _libraryPage?.ОбновитьPageНазвание("Continue watching");   // v4.0.0: it said "Все фильмы"
        if (!ReferenceEquals(ContentFrame.Content, _libraryPage)) NavigateTo("library");
        ClearМедиатекаНазад();
        SetActiveNav(sender as Button ?? BtnContinueWatching);
    }

    private void Вкл.NavRecentlyДобавитьed(object sender, RoutedEventArgs e)
    {
        if (_libraryPage == null) NavigateTo("library");
        _libraryPage?.ViewModel.ShowRecentlyДобавитьed();
        _libraryPage?.ОбновитьPageНазвание("Recently added");
        if (!ReferenceEquals(ContentFrame.Content, _libraryPage)) NavigateTo("library");
        ClearМедиатекаНазад();
        SetActiveNav(sender as Button ?? BtnRecentlyДобавитьed);
    }

    private void Вкл.NavRecentlyПросмотрено(object sender, RoutedEventArgs e)
    {
        if (_libraryPage == null) NavigateTo("library");
        _libraryPage?.ViewModel.ShowRecentlyПросмотрено();
        _libraryPage?.ОбновитьPageНазвание("Recently watched");
        if (!ReferenceEquals(ContentFrame.Content, _libraryPage)) NavigateTo("library");
        ClearМедиатекаНазад();
        SetActiveNav(sender as Button ?? BtnRecentlyПросмотрено);
    }

    /// <summary>
    /// v2.9 — Opens the Резервная копия dialog: Экспорт or Import personal state.
    /// </summary>
    private async void Вкл.NavРезервная копия(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Добавить(new TextBlock
        {
            Text = "Сохранить or restore everything personal — favorites, watchlist, notes, " +
                   "lists, tags, watched flags, and watch history. The exported JSON file " +
                   "is portable: import it on another PC to merge your state in.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("TextBrush"),
        });
        var status = new TextBlock
        {
            FontSize = 12,
            Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var exportBtn = new Button
        {
            Content = "📤  Экспорт backup…",
            Назадground = CineМедиатекаCS.Services.ThemeBrushes.Get("BrandPurpleBrush"),
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var importBtn = new Button
        {
            Content = "📥  Import backup…",
            Назадground = CineМедиатекаCS.Services.ThemeBrushes.Get("CardBrush"),
            Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("TextBrush"),
            BorderBrush = CineМедиатекаCS.Services.ThemeBrushes.Get("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Добавить(exportBtn);
        row.Children.Добавить(importBtn);
        panel.Children.Добавить(row);
        panel.Children.Добавить(status);

        exportBtn.Click += async (_, _) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileСохранитьPicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsМедиатека,
                    SuggestedFileName = $"cinelibrary-backup-{DateTime.Now:yyyyMMdd-HHmmss}",
                };
                picker.FileTypeChoices.Добавить("CineМедиатека backup", new List<string> { ".json" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickСохранитьFileAsync();
                if (file == null) return;
                status.Visibility = Visibility.Visible;
                status.Text = "Building backup…";
                exportBtn.IsEnabled = false; importBtn.IsEnabled = false;
                await Task.Run(() =>
                {
                    var snapshot = Резервная копияService.BuildSnapshot(AppState.Instance.Db, AppVersionString());
                    Резервная копияService.WriteToFile(snapshot, file.Path);
                });
                status.Text = $"Сохранитьd to {file.Path}";
            }
            catch (Exception ex)
            {
                status.Visibility = Visibility.Visible;
                status.Text = $"Ошибка экспорта: {ex.Message}";
            }
            finally { exportBtn.IsEnabled = true; importBtn.IsEnabled = true; }
        };

        importBtn.Click += async (_, _) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsМедиатека,
                };
                picker.FileTypeFilter.Добавить(".json");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                status.Visibility = Visibility.Visible;
                status.Text = "Reading backup…";
                exportBtn.IsEnabled = false; importBtn.IsEnabled = false;
                Резервная копияService.ImportResult? result = null;
                await Task.Run(() =>
                {
                    var b = Резервная копияService.ReadFromFile(file.Path);
                    if (b == null) throw new InvalidOperationException("File isn't a CineМедиатека backup");
                    result = Резервная копияService.Import(AppState.Instance.Db, b);
                });
                if (result != null)
                {
                    status.Text =
                        $"Imported: {result.ФильмыMerged} movies, {result.ShowsMerged} shows, " +
                        $"{result.ЭпизодыMerged} episodes, {result.ListsСоздатьd} new lists, " +
                        $"{result.TagsСоздатьd} new tags, {result.EventsAppended} history events. " +
                        $"Skipped: {result.ФильмыSkipped + result.ShowsSkipped} (drive not mounted).";
                    _ = RefreshSidebarAsync();
                }
            }
            catch (Exception ex)
            {
                status.Visibility = Visibility.Visible;
                status.Text = $"Ошибка импорта: {ex.Message}";
            }
            finally { exportBtn.IsEnabled = true; importBtn.IsEnabled = true; }
        };

        var dlg = new ContentDialog
        {
            Название = "🔒  Резервная копия",
            Content = panel,
            ЗакрытьButtonText = "Закрыть",
            XamlRoot = Content.XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        var restore = HighlightWhileOpen(sender as Button ?? BtnРезервная копия);
        try { await dlg.ShowAsync(); } catch { }
        restore();
    }

    /// <summary>Best-effort assembly version string for backup metadata.</summary>
    private static string AppVersionString()
    {
        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v?.ToString(3) ?? "";
        }
        catch { return ""; }
    }

    private async void Вкл.NavRandomPick(object sender, RoutedEventArgs e)
    {
        var id = AppState.Instance.Db.GetRandomНе просмотреноId(AppState.Instance.Connected);
        if (id == null)
        {
            ShowToast("Nothing unwatched left — fully caught up 🎉");
            return;
        }
        var dialog = new MovieDetailDialog(id.Value);
        dialog.Список просмотраChanged += (_, _) => { _ = RefreshSidebarAsync(); };
        dialog.Activate();
    }

    /// <summary>
    /// v2.9 — Navigates to the dedicated В этот день page. The sidebar
    /// entry is hidden when there are no matches today, so this handler
    /// can assume there's content to show.
    /// </summary>
    private void Вкл.NavВкл.ThisDay(object sender, RoutedEventArgs e)
    {
        NavigateTo("onthisday");
        ClearМедиатекаНазад();
        SetActiveNav(sender as Button ?? BtnВкл.ThisDay);
    }

    private void Вкл.NavDriveItem(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string serial)
        {
            var drive = _vm.Диски.FirstOrПо умолчанию(d => d.VolumeSerial == serial);
            NavigateTo("library", new МедиатекаNavParam(DriveSerial: serial, Label: drive?.Label));
        }
    }

    private void Вкл.NavCollection(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        // WinRT can box an int as Int64 when it goes through {Binding} — accept both.
        int id;
        if (btn.Tag is int i)       id = i;
        else if (btn.Tag is long l) id = (int)l;
        else return;

        var col = _vm.Коллекции.FirstOrПо умолчанию(c => c.Id == id);
        NavigateTo("library", new МедиатекаNavParam(CollectionId: id, Label: col?.Name));
    }

    private void Вкл.NavGenre(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string genre)
            NavigateTo("library", new МедиатекаNavParam(Genre: genre, Label: genre));
    }

    // ── v1.3 Новый Navigation ────────────────────────────────────────────────

    private void Вкл.NavСписок просмотра(object sender, RoutedEventArgs e)
    {
        if (_libraryPage?.ViewModel is МедиатекаViewModel vm)
        {
            vm.ShowСписок просмотра();
            NavigateTo("library");
            _libraryPage?.ОбновитьPageНазвание("To watch");
        }
        SetActiveNav(sender as Button ?? BtnСписок просмотра);
    }

    private void Вкл.NavЗаметки(object sender, RoutedEventArgs e)
    {
        if (_libraryPage == null) NavigateTo("library");
        _libraryPage?.ViewModel.ShowЗаметки();
        _libraryPage?.ОбновитьPageНазвание("Заметки");
        if (!ReferenceEquals(ContentFrame.Content, _libraryPage)) NavigateTo("library");
        ClearМедиатекаНазад();
        SetActiveNav(sender as Button ?? BtnЗаметки);
    }

    private void Вкл.NavПросмотреноGone(object sender, RoutedEventArgs e)
    {
        NavigateTo("watchedgone");
        SetActiveNav(sender as Button ?? BtnПросмотреноGone);
    }

    // ── v1.4.1 Статистика dashboard ────────────────────────────────────────

    private void Вкл.NavСтатистика(object sender, RoutedEventArgs e)
    {
        NavigateTo("statistics");
        SetActiveNav(sender as Button ?? BtnСтатистика);
    }

    private void Вкл.NavДубликаты(object sender, RoutedEventArgs e)
    {
        NavigateTo("dupes");
        SetActiveNav(sender as Button ?? BtnДубликаты);
    }

    // ── v1.4.1 Горячие клавиши dialog ──────────────────────────────────

    private async void Вкл.HelpClick(object sender, RoutedEventArgs e)
        => await ShowShortcutsDialogAsync();

    private async Task ShowShortcutsDialogAsync()
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 360 };

        void ДобавитьRow(string keys, string what)
        {
            var row = new Grid();
            row.ColumnDefinitions.Добавить(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Добавить(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Theme-neutral key chip. (Resolving CardBrush/BorderBrush via
            // Application.Resources returns the wrong theme variant — that made
            // the chip white in dark mode, hiding the key text.) A translucent
            // grey reads correctly in both Светлая and Тёмная.
            var kb = new Border
            {
                Назадground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0x80, 0x80, 0x80)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0x80, 0x80, 0x80)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            kb.Child = new TextBlock { Text = keys, FontSize = 12, FontСемья = new Microsoft.UI.Xaml.Media.FontСемья("Consolas") };
            kb.SetValue(Grid.ColumnProperty, 0);
            row.Children.Добавить(kb);

            var desc = new TextBlock
            {
                Text = what,
                FontSize = 13,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            desc.SetValue(Grid.ColumnProperty, 1);
            row.Children.Добавить(desc);

            panel.Children.Добавить(row);
        }

        void ДобавитьHeader(string text)
        {
            panel.Children.Добавить(new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 120,
                Opacity = 0.7,
                Margin = new Thickness(0, 10, 0, 2),
                Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("MutedBrush"),
            });
        }

        ДобавитьHeader("SEARCH & GENERAL");
        ДобавитьRow("Ctrl + F   ·   /", "Перейти к полю поиска");
        ДобавитьRow("Esc",              "Clear search, then clear selection");
        ДобавитьRow("Ctrl + B",         "Toggle the sidebar");
        ДобавитьRow("Ctrl + Shift + /", "Show this shortcuts dialog");
        ДобавитьRow("Ctrl + Q",         "Quit CineМедиатека");

        ДобавитьHeader("SELECTION & ACTIONS");
        ДобавитьRow("Ctrl + A",     "Select every card on screen");
        ДобавитьRow("Ctrl + щелчок", "Добавить / remove a single card");
        ДобавитьRow("Shift + click","Range-select from the last card");
        ДобавитьRow("F",            "Toggle favorite on the selection");
        ДобавитьRow("W",            "Toggle watchlist on the selection");
        ДобавитьRow("Удалить",       "Remove selection from the current list");

        ДобавитьHeader("НАВИГАЦИЯ");
        ДобавитьRow("PgDn / PgUp",  "Scroll one viewport");
        ДобавитьRow("Home / End",   "Jump to top / bottom");
        ДобавитьRow("↑ / ↓", "Scroll by one row of cards");

        var note = new TextBlock
        {
            Text = "Card shortcuts (F, W, Удалить) act on the current selection — "
                 + "click one or more cards first. They pause while you're typing in the search box.",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
            Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("MutedBrush"),
        };

        var dialog = new ContentDialog
        {
            Название = "Горячие клавиши",
            Content = new ScrollViewer
            {
                Content = new StackPanel { Children = { panel, note } },
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 520,
                Padding = new Thickness(0, 0, 16, 0),
            },
            ЗакрытьButtonText = "Закрыть",
            XamlRoot = Content.XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        await dialog.ShowAsync();
    }

    // ── About ─────────────────────────────────────────────────────────────

    private async void Вкл.AboutClick(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Добавить(new TextBlock
        {
            Text = "A fast, native movie catalog for MediaElch-scraped collections.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Добавить(new TextBlock
        {
            Text = "Обзор, search and play your movies across multiple external drives.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Добавить(new TextBlock
        {
            Text = "Built with C# + WinUI 3.",
            TextWrapping = TextWrapping.Wrap,
        });
        var link = new HyperlinkButton
        {
            Content = "github.com/aungkokomm/CineМедиатекаCS",
            NavigateUri = new Uri("https://github.com/aungkokomm/CineМедиатекаCS"),
            Padding = new Thickness(0),
        };
        panel.Children.Добавить(link);

        // Read the version straight from the assembly so the About box can
        // never drift out of sync with the build again.
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

        var dialog = new ContentDialog
        {
            Название = $"CineМедиатека v{versionText}",
            Content = panel,
            ЗакрытьButtonText = "OK",
            XamlRoot = Content.XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        await dialog.ShowAsync();
    }

    // ── Toast ─────────────────────────────────────────────────────────────

    public void ShowToast(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var myGen = ++_toastGeneration;
            ToastText.Text = message;
            ToastActionBtn.Visibility = Visibility.Collapsed;
            // Plain toast — no undo, so clear any pending action so the
            // user clicking the ✕ doesn't accidentally fire a stale undo.
            _pendingUndo = null;
            ToastBorder.Visibility = Visibility.Visible;
            AnimateToastIn();
            ScheduleToastHide(myGen);
        });
    }

    /// <summary>
    /// v2.6 — slide the toast up from 20 px below to 0 over ~180 ms. Run
    /// every time the toast becomes visible so each new toast animates,
    /// even if the previous one was still on screen.
    /// </summary>
    private void AnimateToastIn()
    {
        ToastSlide.Y = 20;
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 20, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, ToastSlide);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Y");
        sb.Children.Добавить(anim);
        sb.Begin();
    }

    private void Вкл.ToastDismiss(object sender, RoutedEventArgs e)
    {
        // Dismissing an update toast = "skip this version, don't nag again".
        if (!string.IsNullOrEmpty(_pendingОбновитьVersion))
        {
            try { AppState.Instance.SetPref("skippedОбновить", _pendingОбновитьVersion); } catch { }
            _pendingОбновитьVersion = null;
            _pendingОбновитьUrl = null;
        }
        ToastBorder.Visibility = Visibility.Collapsed;
    }

    // ── Sidebar collapse ──────────────────────────────────────────────────

    private bool _sidebarCollapsed;

    private void Вкл.ToggleSidebar(object sender, RoutedEventArgs e)
        => ПрименитьSidebarCollapsed(!_sidebarCollapsed);

    private void ПрименитьSidebarCollapsed(bool collapsed)
    {
        _sidebarCollapsed = collapsed;
        if (collapsed)
        {
            SidebarCol.Width = new GridLength(0);
            // Hide the whole floating card (not just the inner grid) so no
            // rounded sliver / margin is left behind when collapsed.
            SidebarCard.Visibility = Visibility.Collapsed;
            ContentCard.Margin = new Thickness(8, 4, 8, 8);   // the same 8 at the left edge
            SidebarReopenBtn.Visibility = Visibility.Visible;
        }
        else
        {
            SidebarCol.Width = new GridLength(248);   // keep in sync with SidebarCol default (236 inner + 12 margin)
            SidebarCard.Visibility = Visibility.Visible;
            ContentCard.Margin = new Thickness(4, 4, 8, 8);
            SidebarReopenBtn.Visibility = Visibility.Collapsed;
        }
        AppState.Instance.SetPref("sidebarCollapsed", collapsed ? "true" : "false");
    }
}

public record МедиатекаNavParam(
    string? DriveSerial = null,
    string? Genre = null,
    int? CollectionId = null,
    bool ИзбранноеВкл.ly = false,
    bool RecentlyДобавитьed = false,
    string? Label = null
);
