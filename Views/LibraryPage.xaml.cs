using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using CineМедиатекаCS.Models;
using CineМедиатекаCS.Services;
using CineМедиатекаCS.ViewModels;
using CineМедиатекаCS.Views;

namespace CineМедиатекаCS.Views;

public sealed partial class МедиатекаPage : Page
{
    private readonly МедиатекаViewModel _vm;
    public МедиатекаViewModel ViewModel => _vm;
    public event EventHandler? SidebarRefreshRequested;
    // Raised whenever the search text changes (incl. internal clears) so the
    // global title-bar search box can stay in sync.
    public event EventHandler<string>? ПоискTextChanged;

    // ── Multi-select state (v2.5) ─────────────────────────────────────────
    private readonly HashSet<int> _selectedIds = new();
    private MovieListItem? _selectionAnchor;

    // Set to true only after construction finishes. XAML-driven events
    // (ComboBox.SelectionChanged on IsSelected="True", etc.) can fire during
    // InitializeComponent() — we must not touch _vm from those before it's wired up.
    private bool _ready;

    public МедиатекаPage()
    {
        // _vm MUST be created before InitializeComponent() — the SortCombo's
        // initial SelectionChanged fires inside InitializeComponent and touches _vm.
        _vm = new МедиатекаViewModel();

        InitializeComponent();

        // Assign ItemsSource once — ObservableCollection handles all future updates
        GridRepeater.ItemsSource = _vm.Фильмы;
        ListRepeater.ItemsSource = _vm.Фильмы;

        // Reflect saved prefs in the UI (sort dropdown, grid/list toggle)
        SyncUiFromVm();

        // Load density pref (S/M/L/XL)
        ПрименитьDensity(AppState.Instance.GetPref("gridDensity", "M"));

        // Горячие клавиши (Ctrl+F is handled globally in MainWindow,
        // which focuses the title-bar search box).
        ДобавитьAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, (_, a) =>
        {
            // Esc clears search first, then selection — never both at once.
            if (!string.IsNullOrEmpty(_vm.ПоискText))
            {
                _vm.ПоискText = "";   // sync event clears the title-bar box
                a.Handled = true;
                return;
            }
            if (_selectedIds.Count > 0)
            {
                ClearSelection();
                a.Handled = true;
            }
        });
        ДобавитьAccelerator(VirtualKey.A, VirtualKeyModifiers.Control, (_, a) =>
        {
            if (IsTextИзменитьFocused()) return;
            SelectВсеVisible();
            a.Handled = true;
        });

        // Navigation shortcuts (v2.0.1) — PgDn/PgUp scroll one viewport,
        // Home/End jump to top/bottom, ↑/↓ scroll by a card-row. Все gated
        // on focus: when the search box is editing text, these keys do
        // their default text-cursor thing and we leave them alone.
        ДобавитьAccelerator(VirtualKey.PageDown, VirtualKeyModifiers.None,
            (_, a) => { if (TryScrollByViewport(+1)) a.Handled = true; });
        ДобавитьAccelerator(VirtualKey.PageUp, VirtualKeyModifiers.None,
            (_, a) => { if (TryScrollByViewport(-1)) a.Handled = true; });
        ДобавитьAccelerator(VirtualKey.Home, VirtualKeyModifiers.None,
            (_, a) => { if (TryScrollTo(0)) a.Handled = true; });
        ДобавитьAccelerator(VirtualKey.End, VirtualKeyModifiers.None,
            (_, a) => { if (TryScrollToEnd()) a.Handled = true; });
        ДобавитьAccelerator(VirtualKey.Down, VirtualKeyModifiers.None,
            (_, a) => { if (TryScrollByRow(+1)) a.Handled = true; });
        ДобавитьAccelerator(VirtualKey.Up, VirtualKeyModifiers.None,
            (_, a) => { if (TryScrollByRow(-1)) a.Handled = true; });

        // v2.9 — Card / selection shortcuts. Gated on text-edit focus so
        // typing F or W into the search box doesn't toggle anything.
        //   /        focuses the search box (streaming-style search shortcut)
        //   F        toggle favorite on the current selection
        //   W        toggle watchlist on the current selection
        //   Удалить   remove from current list (when viewing a list)
        ДобавитьAccelerator((VirtualKey)0xBF /* Oem2 = / */, VirtualKeyModifiers.None, (_, a) =>
        {
            if (IsTextИзменитьFocused()) return;
            (App.MainWindow as CineМедиатекаCS.MainWindow)?.FocusНазваниеПоиск();
            a.Handled = true;
        });
        ДобавитьAccelerator(VirtualKey.F, VirtualKeyModifiers.None, (_, a) =>
        {
            if (IsTextИзменитьFocused() || _selectedIds.Count == 0) return;
            Вкл.SelToggleFav(this, new RoutedEventArgs());
            a.Handled = true;
        });
        ДобавитьAccelerator(VirtualKey.W, VirtualKeyModifiers.None, (_, a) =>
        {
            if (IsTextИзменитьFocused() || _selectedIds.Count == 0) return;
            Вкл.SelToggleСписок просмотра(this, new RoutedEventArgs());
            a.Handled = true;
        });
        ДобавитьAccelerator(VirtualKey.Удалить, VirtualKeyModifiers.None, (_, a) =>
        {
            if (IsTextИзменитьFocused() || _selectedIds.Count == 0) return;
            if (_vm.UserListId == null) return;  // only meaningful in a list view
            Вкл.SelRemoveFromCurrentList(this, new RoutedEventArgs());
            a.Handled = true;
        });

        _vm.PropertyChanged += Вкл.VmPropertyChanged;
        _vm.Фильмы.CollectionChanged += (_, _) => ОбновитьEmptyState();

        // v4.0.0: the page is cached and put back on screen as it is, so
        // re-query the shows row each time; a show may have been made a
        // favorite (or finished) on its own page meanwhile.
        Loaded += (_, _) => { _shownRowKey = ""; RefreshShowsInList(); };
        // v4.1.0: a show card's hover panel changed a show (un-favorited it on
        // Избранное, finished it on Продолжить просмотр): re-list the row.
        TvShowCard.ShowChanged += _ => { _shownRowKey = ""; RefreshShowsInList(); };

        // v2.8.2 — tap a show card in the "Сериалы in this list" row to
        // open that show on the TV page.
        ShowsInListRepeater.Tapped += (s, e) =>
        {
            var d = e.OriginalSource as DependencyObject;
            while (d != null && d is not TvShowCard)
                d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d);
            if (d is TvShowCard card && card.Show != null && App.MainWindow is MainWindow mw)
                mw.OpenTvShow(card.Show.Id);
        };

        // v2.9 — same tap behaviour for the "Сериалы matching" search row.
        ShowsInПоискRepeater.Tapped += (s, e) =>
        {
            var d = e.OriginalSource as DependencyObject;
            while (d != null && d is not TvShowCard)
                d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d);
            if (d is TvShowCard card && card.Show != null && App.MainWindow is MainWindow mw)
                mw.OpenTvShow(card.Show.Id);
        };

        // Wire up sidebar refresh from movie cards (watchlist / favorite / watched toggles
        // made inside the detail dialog need to bubble back up so the sidebar counts refresh)
        GridRepeater.ElementPrepared += Вкл.GridRepeaterElementPrepared;
        GridRepeater.ElementClearing += Вкл.GridRepeaterElementClearing;
        ListRepeater.ElementPrepared += Вкл.ListRepeaterElementPrepared;
        ListRepeater.ElementClearing += Вкл.ListRepeaterElementClearing;

        // Multi-select wiring (v2.5) — cards/rows raise this static event;
        // we manage the actual selection set and visual.
        MovieCardControl.AnyCardSelectionInteraction += Вкл.CardSelectionInteraction;
        MovieCardControl.ResolveSelectionForDrag = ResolveSelectionForDrag;
        // v3.3 — a movie was sent to Просмотрено и удалено (card context menu or
        // selection bar): drop it from the current view + re-count sidebar.
        // Subscribed on Loaded (not just ctor) because this page instance is
        // cached and re-shown — a ctor-only subscription would die for good
        // at the first Unloaded.
        MovieCardControl.AnyMovieArchived += Вкл.AnyMovieArchived;
        Loaded += (_, _) =>
        {
            MovieCardControl.AnyMovieArchived -= Вкл.AnyMovieArchived;
            MovieCardControl.AnyMovieArchived += Вкл.AnyMovieArchived;
        };
        Unloaded += (_, _) =>
        {
            MovieCardControl.AnyCardSelectionInteraction -= Вкл.CardSelectionInteraction;
            MovieCardControl.AnyMovieArchived -= Вкл.AnyMovieArchived;
            if (MovieCardControl.ResolveSelectionForDrag == (Func<MovieListItem, IEnumerable<int>>)ResolveSelectionForDrag)
                MovieCardControl.ResolveSelectionForDrag = null;
        };
        // v2.5.2 — preserve selection across reloads. When the filter or
        // search changes, movies that are still visible should stay
        // selected; movies that scrolled out of the view get pruned
        // from _selectedIds. Each newly-added item has its IsSelected
        // flag rehydrated from _selectedIds so its visual matches.
        _vm.Фильмы.CollectionChanged += (_, e) =>
        {
            switch (e.Action)
            {
                case System.Коллекции.Specialized.NotifyCollectionChangedAction.Добавить:
                    if (e.НовыйItems != null)
                        foreach (MovieListItem m in e.НовыйItems)
                            if (_selectedIds.Contains(m.Id) && !m.IsSelected)
                                m.IsSelected = true;
                    break;
                case System.Коллекции.Specialized.NotifyCollectionChangedAction.Reset:
                    // Фильмы just cleared — the next batch of Добавитьs will repopulate.
                    // Schedule a prune after they land so _selectedIds drops any
                    // ids that no longer have a card on screen, and the "X
                    // selected" counter stays honest.
                    if (_selectedIds.Count > 0)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            var visible = new HashSet<int>(_vm.Фильмы.Select(m => m.Id));
                            var before = _selectedIds.Count;
                            _selectedIds.IntersectWith(visible);
                            if (_selectedIds.Count != before) AfterSelectionChanged();
                        });
                    }
                    break;
            }
            RefreshRemoveFromListVisibility();
        };

        _ready = true;

        _ = _vm.LoadAsync();
    }

    // ── Multi-select handlers (v2.5) ──────────────────────────────────────

    private void Вкл.CardSelectionInteraction(object? sender, MovieCardControl.SelectionInteractionArgs e)
    {
        if (e.Ctrl)
        {
            ToggleSelect(e.Movie);
            _selectionAnchor = e.Movie;
        }
        else if (e.Shift)
        {
            SelectRangeTo(e.Movie);
            _selectionAnchor = e.Movie;
        }
        else
        {
            // Plain tap: if a selection exists, exit selection mode.
            if (_selectedIds.Count > 0) ClearSelection();
            _selectionAnchor = e.Movie;
        }
    }

    private void ToggleSelect(MovieListItem m)
    {
        if (_selectedIds.Добавить(m.Id)) m.IsSelected = true;
        else { _selectedIds.Remove(m.Id); m.IsSelected = false; }
        AfterSelectionChanged();
    }

    private void SelectRangeTo(MovieListItem target)
    {
        if (_selectionAnchor == null) { ToggleSelect(target); return; }
        int a = _vm.Фильмы.IndexOf(_selectionAnchor);
        int b = _vm.Фильмы.IndexOf(target);
        if (a < 0 || b < 0) { ToggleSelect(target); return; }
        if (a > b) (a, b) = (b, a);
        for (int i = a; i <= b; i++)
        {
            var m = _vm.Фильмы[i];
            if (_selectedIds.Добавить(m.Id)) m.IsSelected = true;
        }
        AfterSelectionChanged();
    }

    private void SelectВсеVisible()
    {
        foreach (var m in _vm.Фильмы)
            if (_selectedIds.Добавить(m.Id)) m.IsSelected = true;
        AfterSelectionChanged();
    }

    private void ClearSelection()
    {
        foreach (var m in _vm.Фильмы)
            if (m.IsSelected) m.IsSelected = false;
        _selectedIds.Clear();
        AfterSelectionChanged();
    }

    private bool _selectionBarShown;

    private void AfterSelectionChanged()
    {
        MovieCardControl.CurrentSelectionCount = _selectedIds.Count;
        if (_selectedIds.Count == 0)
        {
            SelectionBar.Visibility = Visibility.Collapsed;
            _selectionBarShown = false;
            return;
        }
        SelectionCountText.Text = $"{_selectedIds.Count} selected";
        // v2.6 — animate the bar up the first time it appears in this
        // selection session; subsequent count changes just update the text.
        if (!_selectionBarShown)
        {
            SelectionBar.Visibility = Visibility.Visible;
            AnimateSelectionBarIn();
            _selectionBarShown = true;
        }
        RefreshRemoveFromListVisibility();
    }

    private void AnimateSelectionBarIn()
    {
        SelectionBarSlide.Y = 40;
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 40, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, SelectionBarSlide);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Y");
        sb.Children.Добавить(anim);
        sb.Begin();
    }

    private void RefreshRemoveFromListVisibility()
    {
        SelRemoveFromListBtn.Visibility = (_vm.UserListId != null && _selectedIds.Count > 0)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// MovieCardControl asks for the id-set that a drag should carry. If
    /// the dragged card isn't in the current selection, treat the drag as
    /// single-card AND clear/replace the selection so what the user sees
    /// matches what they're dragging.
    /// </summary>
    private IEnumerable<int> ResolveSelectionForDrag(MovieListItem dragged)
    {
        if (_selectedIds.Contains(dragged.Id) && _selectedIds.Count > 1)
            return _selectedIds.ToList();
        // Drag of a single (possibly unselected) card.
        return new[] { dragged.Id };
    }

    /// <summary>
    /// Snapshot of selected MovieListItems for batch ops. Order follows
    /// the current Фильмы list so operations feel predictable.
    /// </summary>
    private List<MovieListItem> SelectedФильмы() =>
        _vm.Фильмы.Where(m => _selectedIds.Contains(m.Id)).ToList();

    private void Вкл.SelClear(object sender, RoutedEventArgs e) => ClearSelection();

    private void Вкл.SelListsFlyoutOpening(object sender, object e)
    {
        SelListsFlyout.Items.Clear();
        var ids = SelectedФильмы().Select(m => m.Id).ToList();
        if (ids.Count == 0) return;

        var lists = AppState.Instance.Db.GetUserLists();
        if (lists.Count == 0)
        {
            SelListsFlyout.Items.Добавить(new MenuFlyoutItem
            {
                Text = "(no lists yet — use + below)", IsEnabled = false
            });
        }
        foreach (var ul in lists)
        {
            var item = new MenuFlyoutItem { Text = $"📑 {ul.Name}" };
            var capturedUl = ul;
            item.Click += (_, _) => ДобавитьSelectedToList(capturedUl.Id, capturedUl.Name);
            SelListsFlyout.Items.Добавить(item);
        }
        SelListsFlyout.Items.Добавить(new MenuFlyoutSeparator());
        var newItem = new MenuFlyoutItem { Text = "+ Новый список…" };
        newItem.Click += async (_, _) =>
        {
            var name = await PromptНовыйListName();
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                var listId = AppState.Instance.Db.СоздатьUserList(name.Trim());
                ДобавитьSelectedToList(listId, name.Trim());
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                if (App.MainWindow is MainWindow mw)
                    mw.ShowToast($"A list named “{name.Trim()}” already exists");
            }
        };
        SelListsFlyout.Items.Добавить(newItem);
    }

    private void ДобавитьSelectedToList(int listId, string listName)
    {
        var ids = SelectedФильмы().Select(m => m.Id).ToList();
        if (ids.Count == 0) return;
        foreach (var mid in ids)
            AppState.Instance.Db.ДобавитьMovieToUserList(listId, mid);
        SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        ShowSelectionToast($"Добавитьed {ids.Count} to “{listName}”", () =>
        {
            foreach (var mid in ids)
                AppState.Instance.Db.RemoveMovieFromUserList(listId, mid);
            SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Вкл.SelToggleПросмотрено(object sender, RoutedEventArgs e)
    {
        var picked = SelectedФильмы();
        if (picked.Count == 0) return;
        bool anyНе просмотрено = picked.Any(m => !m.IsПросмотрено);
        var changed = new List<MovieListItem>();
        foreach (var m in picked)
        {
            if (m.IsПросмотрено == anyНе просмотрено) continue;
            AppState.Instance.Db.ToggleПросмотрено(m.Id);
            m.IsПросмотрено = anyНе просмотрено;
            changed.Добавить(m);
        }
        SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        ShowSelectionToast(
            $"{(anyНе просмотрено ? "Marked" : "Unmarked")} {changed.Count} watched",
            () => {
                foreach (var m in changed)
                {
                    AppState.Instance.Db.ToggleПросмотрено(m.Id);
                    m.IsПросмотрено = !anyНе просмотрено;
                }
                SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
            });
    }

    private void Вкл.SelToggleСписок просмотра(object sender, RoutedEventArgs e)
    {
        var picked = SelectedФильмы();
        if (picked.Count == 0) return;
        bool anyВыкл. = picked.Any(m => !m.IsСписок просмотра);
        var changed = new List<MovieListItem>();
        foreach (var m in picked)
        {
            if (m.IsСписок просмотра == anyВыкл.) continue;
            AppState.Instance.Db.SetСписок просмотра(m.Id, anyВыкл.);
            m.IsСписок просмотра = anyВыкл.;
            changed.Добавить(m);
        }
        SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        ShowSelectionToast(
            $"{(anyВыкл. ? "Добавитьed" : "Removed")} {changed.Count} {(anyВыкл. ? "to" : "from")} watchlist",
            () => {
                foreach (var m in changed)
                {
                    AppState.Instance.Db.SetСписок просмотра(m.Id, !anyВыкл.);
                    m.IsСписок просмотра = !anyВыкл.;
                }
                SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
            });
    }

    private void Вкл.AnyMovieArchived()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _ = _vm.LoadAsync();
            SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    // v3.3 — selection bar: send every selected movie to Просмотрено и удалено.
    private async void Вкл.SelArchive(object sender, RoutedEventArgs e)
    {
        var picked = SelectedФильмы();
        if (picked.Count == 0) return;
        var dlg = new ContentDialog
        {
            Название = $"Send {picked.Count} movie(s) to Просмотрено и удалено?",
            Content = "They move out of your library into Просмотрено и удалено — posters, details, " +
                      "your notes and watch history are all kept as records. " +
                      "The files on your drives are not touched.",
            PrimaryButtonText = "Send",
            ЗакрытьButtonText = "Отмена",
            По умолчаниюButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = CineМедиатекаCS.MainWindow.CurrentTheme,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var n = AppState.Instance.Db.ArchiveФильмы(picked.Select(m => m.Id).ToList());
        ClearSelection();
        if (App.MainWindow is CineМедиатекаCS.MainWindow mw)
            mw.ShowToast($"Sent {n} movie(s) to Просмотрено и удалено");
        MovieCardControl.RaiseMovieArchived();
    }

    private void Вкл.SelToggleFav(object sender, RoutedEventArgs e)
    {
        var picked = SelectedФильмы();
        if (picked.Count == 0) return;
        bool anyВыкл. = picked.Any(m => !m.IsИзбранное);
        var changed = new List<MovieListItem>();
        foreach (var m in picked)
        {
            if (m.IsИзбранное == anyВыкл.) continue;
            AppState.Instance.Db.ToggleИзбранное(m.Id);
            m.IsИзбранное = anyВыкл.;
            changed.Добавить(m);
        }
        SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        ShowSelectionToast(
            $"{(anyВыкл. ? "В избранном" : "Unfavorited")} {changed.Count}",
            () => {
                foreach (var m in changed)
                {
                    AppState.Instance.Db.ToggleИзбранное(m.Id);
                    m.IsИзбранное = !anyВыкл.;
                }
                SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
            });
    }

    private void Вкл.SelRemoveFromCurrentList(object sender, RoutedEventArgs e)
    {
        if (_vm.UserListId == null) return;
        var listId = _vm.UserListId.Value;
        var picked = SelectedФильмы();
        if (picked.Count == 0) return;
        var ids = picked.Select(m => m.Id).ToList();
        // Snapshot of (movie, original index) so Undo can put each item
        // back exactly where the user removed it from. Reloading the page
        // would lose anything past page 1 of the paged list.
        var snapshots = picked
            .Select(m => (Movie: m, Index: _vm.Фильмы.IndexOf(m)))
            .OrderBy(s => s.Index)
            .ToList();
        foreach (var mid in ids)
            AppState.Instance.Db.RemoveMovieFromUserList(listId, mid);
        // Visually remove from the current view (we're viewing this list).
        foreach (var m in picked) _vm.Фильмы.Remove(m);
        _selectedIds.Clear();
        AfterSelectionChanged();
        SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        ShowSelectionToast($"Removed {ids.Count} from list", () =>
        {
            foreach (var mid in ids)
                AppState.Instance.Db.ДобавитьMovieToUserList(listId, mid);
            // Re-insert in place. Iterate from the lowest-index outwards
            // so later items don't have to be re-indexed as we go.
            foreach (var snap in snapshots)
            {
                var idx = Math.Min(snap.Index, _vm.Фильмы.Count);
                _vm.Фильмы.Insert(idx, snap.Movie);
            }
            SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        });
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
            XamlRoot = this.XamlRoot,
            RequestedTheme = MainWindow.CurrentTheme,
        };
        var result = await dlg.ShowAsync();
        return result == ContentDialogResult.Primary ? box.Text : null;
    }

    private void ShowSelectionToast(string message, Action undo)
    {
        // Route through MainWindow's toast — keeps a single visual style.
        if (App.MainWindow is MainWindow mw)
            mw.ShowToastWithUndo(message, undo);
    }

    // Named handlers so we can unsubscribe on ElementClearing — anonymous
    // lambdas would accumulate every time ItemsRepeater recycles a card,
    // and a click would fire the handler N times → even count = no net
    // change → "Отметить просмотренным looks like it stops working" bug.
    private void Вкл.CardSidebarRefresh(object? s, EventArgs e)
        => SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
    private void Вкл.CardПросмотреноToggle(object? s, MovieListItem movie)
        => _vm.ToggleПросмотрено(movie);
    private void Вкл.CardСписок просмотраToggle(object? s, MovieListItem movie)
    {
        _vm.ToggleСписок просмотраВкл.Card(movie);
        SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
    }
    private void Вкл.RowSidebarRefresh(object? s, EventArgs e)
        => SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);

    private void Вкл.GridRepeaterElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is MovieCardControl card)
        {
            // Defensive unsubscribe — if a card was prepared without a paired
            // clearing (rare, but keeps the count honest).
            card.SidebarRefreshRequested -= Вкл.CardSidebarRefresh;
            card.ПросмотреноToggleRequested  -= Вкл.CardПросмотреноToggle;
            card.Список просмотраToggleRequested -= Вкл.CardСписок просмотраToggle;

            card.SidebarRefreshRequested += Вкл.CardSidebarRefresh;
            card.ПросмотреноToggleRequested  += Вкл.CardПросмотреноToggle;
            card.Список просмотраToggleRequested += Вкл.CardСписок просмотраToggle;
        }
    }

    private void Вкл.GridRepeaterElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is MovieCardControl card)
        {
            card.SidebarRefreshRequested -= Вкл.CardSidebarRefresh;
            card.ПросмотреноToggleRequested  -= Вкл.CardПросмотреноToggle;
            card.Список просмотраToggleRequested -= Вкл.CardСписок просмотраToggle;
        }
    }

    private void Вкл.ListRepeaterElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is MovieRowControl row)
        {
            row.SidebarRefreshRequested -= Вкл.RowSidebarRefresh;
            row.SidebarRefreshRequested += Вкл.RowSidebarRefresh;
        }
    }

    private void Вкл.ListRepeaterElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is MovieRowControl row)
            row.SidebarRefreshRequested -= Вкл.RowSidebarRefresh;
    }

    private void ДобавитьAccelerator(VirtualKey key, VirtualKeyModifiers mods,
        Windows.Foundation.TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var acc = new KeyboardAccelerator { Key = key, Modifiers = mods };
        acc.Invoked += handler;
        KeyboardAccelerators.Добавить(acc);
    }

    public void FocusПоискBox()
    {
        (App.MainWindow as CineМедиатекаCS.MainWindow)?.FocusНазваниеПоиск();
    }

    // ── Keyboard navigation (v2.0.1) ──────────────────────────────────────
    // Все return true when they handled the keypress so the accelerator
    // can swallow it; false when focus is in a text input (let the user
    // navigate the text cursor instead) or scroll can't happen.

    /// <summary>
    /// True when the focused element is a text-editing surface (search box,
    /// notes editor, etc.). In that case we don't steal PgDn / arrow keys.
    /// </summary>
    private bool IsTextИзменитьFocused()
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot);
        return focused is TextBox or AutoSuggestBox || (
            focused is FrameworkElement fe &&
            (fe.Parent is AutoSuggestBox || fe.Parent is TextBox));
    }

    private bool TryScrollByViewport(int direction)
    {
        if (IsTextИзменитьFocused()) return false;
        var target = MainScroller.VerticalВыкл.set + direction * MainScroller.ViewportHeight;
        MainScroller.ChangeView(null, target, null);
        return true;
    }

    /// <summary>
    /// Scroll by roughly one row of cards/list-rows. Вид сеткой: a row is
    /// the current card height + spacing. Вид списком: a fixed ~44 px row.
    /// </summary>
    private bool TryScrollByRow(int direction)
    {
        if (IsTextИзменитьFocused()) return false;
        double rowPx = _vm.ViewMode == ViewMode.Grid
            ? MovieCardControl.GlobalCardHeight + 12
            : 44;
        MainScroller.ChangeView(null, MainScroller.VerticalВыкл.set + direction * rowPx, null);
        return true;
    }

    private bool TryScrollTo(double offset)
    {
        if (IsTextИзменитьFocused()) return false;
        MainScroller.ChangeView(null, offset, null);
        return true;
    }

    /// <summary>
    /// End-of-list jump. Медиатека uses 60-per-page lazy loading so we
    /// first drain remaining pages until everything's loaded, then scroll
    /// to the bottom. Маленький libraries finish instantly.
    /// </summary>
    private bool TryScrollToEnd()
    {
        if (IsTextИзменитьFocused()) return false;
        _ = ScrollToEndAsync();
        return true;
    }

    private async Task ScrollToEndAsync()
    {
        // Drain remaining pages first so ScrollableHeight reflects the real end.
        int guard = 0;
        while (_vm.HasMore && !_vm.IsLoading && guard < 200)
        {
            await _vm.LoadMoreAsync();
            guard++;
        }
        // Layout pass needed before ScrollableHeight is final
        MainScroller.ОбновитьLayout();
        MainScroller.ChangeView(null, MainScroller.ScrollableHeight, null);
    }

    public void ОбновитьPageНазвание(string title) => PageНазваниеText.Text = title;

    private void ОбновитьEmptyState()
    {
        // v4.0.0: a page whose only items are Сериалы (the row above) isn't empty.
        var empty = _vm.Фильмы.Count == 0 && !_vm.IsLoading
                    && ShowsInListSection.Visibility != Visibility.Visible;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        GridBorder.Opacity = empty ? 0 : 1;
        ListBorder.Opacity = empty ? 0 : 1;

        if (!empty) return;

        // First-launch: zero drives in DB → big CTA pointing to Диски → Добавить папку.
        // Empty user list → list-specific message (v2.5.2).
        // Otherwise: filter-empty hint (existing behaviour).
        var hasAnyDrive = AppState.Instance.Db.GetДиски().Count > 0;
        if (!hasAnyDrive)
        {
            EmptyНазвание.Text = "Welcome to CineМедиатека";
            EmptyStateHint.Text =
                "Point CineМедиатека at the folder where MediaElch saved your scraped " +
                "movies (each movie in its own folder with a .nfo + poster). " +
                "Диски → Добавить папку.";
            EmptyCtaBtn.Content = "📂 Добавить my movies folder";
            EmptyCtaBtn.Visibility = Visibility.Visible;
        }
        else if (_vm.UserListId != null && string.IsNullOrEmpty(_vm.ПоискText))
        {
            EmptyНазвание.Text = "This list is empty";
            EmptyStateHint.Text =
                "Добавить movies by right-clicking any poster and choosing " +
                "“📑 Добавить в список”, or from a movie's detail dialog. " +
                "You can also Ctrl+click multiple cards in the library and " +
                "drag them onto this list in the sidebar.";
            EmptyCtaBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            EmptyНазвание.Text = "Нет фильмов, соответствующих фильтрам";
            EmptyStateHint.Text = "Попробуйте очистить поиск или выбрать фильтр «Все».";
            EmptyCtaBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void Вкл.EmptyCtaClick(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow mw)
            mw.NavigateToДискиAndДобавить();
    }

    private void Вкл.ClearFilters(object sender, RoutedEventArgs e)
    {
        ПоискBox.Text = "";
        _vm.ПоискText = "";
        // Reset watched-pill to Все
        var pill = (Style)Application.Current.Resources["PillButtonStyle"];
        var pillActive = (Style)Application.Current.Resources["PillButtonActiveStyle"];
        FilterВсе.Style = pillActive;
        FilterНе просмотрено.Style = pill;
        FilterПросмотрено.Style = pill;
        _vm.ПросмотреноFilter = ПросмотреноFilter.Все;
        _vm.ClearFilters();
        PageНазваниеText.Text = "Все фильмы";
        ОбновитьClearFiltersButton();
    }

    private void ОбновитьClearFiltersButton()
    {
        // v2.6 — covers the new Decade / Рейтинг / ContinueWatching filters too.
        ClearFiltersBtn.Visibility = AnyFilterActive()
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Density (S/M/L/XL) ────────────────────────────────────────────────

    private void Вкл.DensityClick(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is not FrameworkElement { Tag: string tag }) return;
        ПрименитьDensity(tag);
        AppState.Instance.SetPref("gridDensity", tag);
    }

    private void ПрименитьDensity(string tag)
    {
        var (w, h) = tag switch
        {
            "S"  => (120.0, 220.0),
            "L"  => (190.0, 340.0),
            "XL" => (240.0, 420.0),
            _    => (150.0, 280.0),   // M
        };

        MovieCardControl.SetGlobalSize(w, h);
        GridLayout.MinItemWidth = w;
        GridLayout.MinItemHeight = h;

        DensityS.IsChecked  = tag == "S";
        DensityM.IsChecked  = tag == "M";
        DensityL.IsChecked  = tag == "L";
        DensityXL.IsChecked = tag == "XL";
        DensityLabel.Text = $"Size: {(tag is "S" or "L" or "XL" ? tag : "M")}";
    }

    private void SyncUiFromVm()
    {
        SyncSortCombo();

        // View mode toggle
        if (_vm.ViewMode == ViewMode.List)
        {
            GridViewToggle.IsChecked = false;
            ListViewToggle.IsChecked = true;
            GridBorder.Visibility = Visibility.Collapsed;
            ListBorder.Visibility = Visibility.Visible;
        }
        else
        {
            GridViewToggle.IsChecked = true;
            ListViewToggle.IsChecked = false;
            GridBorder.Visibility = Visibility.Visible;
            ListBorder.Visibility = Visibility.Collapsed;
        }
    }

    // Sort combo — match the item whose Tag corresponds to current SortKey+SortDir.
    // v4.1.0: runs whenever the sort changes (it ran only at start, so Continue
    // Watching's own order never showed), and knows Last Просмотрено (it read "Название").
    private void SyncSortCombo()
    {
        var keyStr = _vm.SortKey switch
        {
            SortKey.Год       => "year",
            SortKey.Рейтинг     => "rating",
            SortKey.Продолжительность    => "runtime",
            SortKey.DateДобавитьed  => "date_added",
            SortKey.LastВоспроизвестиed => "last_played",
            _                  => "title"
        };
        var dirStr = _vm.SortDir == SortDir.Asc ? "asc" : "desc";
        var wantTag = $"{keyStr}:{dirStr}";
        for (int i = 0; i < SortCombo.Items.Count; i++)
        {
            if (SortCombo.Items[i] is ComboBoxItem ci && (ci.Tag as string) == wantTag)
            {
                SortCombo.SelectedIndex = i;
                break;
            }
        }
        if (SortCombo.SelectedIndex < 0) SortCombo.SelectedIndex = 0;
    }

    public void ПрименитьNavParam(МедиатекаNavParam p)
    {
        ПоискBox.Text = "";
        _vm.ПоискText = "";
        _shownRowKey = "";   // re-query the shows row: favorites may have changed elsewhere

        if (p.ИзбранноеВкл.ly)
            _vm.SetИзбранное();
        else if (p.DriveSerial != null)
            _vm.SetDriveFilter(p.DriveSerial, p.Label);
        else if (p.Genre != null)
            _vm.SetGenreFilter(p.Genre);
        else if (p.CollectionId != null)
            _vm.SetCollectionFilter(p.CollectionId, p.Label);
        else
            _vm.ClearFilters();

        // Breadcrumb-style title: "Все фильмы › Драма"
        PageНазваниеText.Text = p.Label == null
            ? "Все фильмы"
            : $"Все фильмы › {p.Label}";
    }

    private void Вкл.VmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (e.PropertyName == nameof(МедиатекаViewModel.TotalCount) ||
                e.PropertyName == nameof(МедиатекаViewModel.HasMore) ||
                e.PropertyName == nameof(МедиатекаViewModel.FilterTotal))
            {
                // v3.9.0: how many movies the view holds. (It used to read
                // "60 of 1,200" while pages were still loading, which looked
                // like a filter.)
                ОбновитьCountText();
            }
            if (e.PropertyName == nameof(МедиатекаViewModel.IsLoading))
            {
                LoadingRing.IsActive = _vm.IsLoading;
                LoadingRing.Visibility = _vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                ОбновитьEmptyState();
            }
            if (e.PropertyName == nameof(МедиатекаViewModel.ПоискText))
                ПоискTextChanged?.Invoke(this, _vm.ПоискText ?? "");
            if (e.PropertyName is nameof(МедиатекаViewModel.SortKey) or nameof(МедиатекаViewModel.SortDir))
                SyncSortCombo();
            // Any filter-related VM change should reflect in the Clear button
            ОбновитьClearFiltersButton();
            ОбновитьFilterChips();
            RefreshShowsInList();
            RefreshShowsInПоиск();
            // v2.9 — Недавно добавленные / Недавно просмотренные / В этот день are
            // accessed via sidebar entries instead of home rows now; the
            // horizontal rows interfered with vertical scrolling through
            // the main grid.
        });
    }

    // v2.9 — Недавно добавленные / Недавно просмотренные / В этот день rows used to
    // live at the top of the Медиатека home, but a horizontal scroll row
    // inside a vertical ScrollViewer steals the mouse wheel and makes the
    // main grid feel unscrollable. They're now sidebar entries only.

    // ── v2.9 TV-in-search row ────────────────────────────────────────────────

    private string? _lastShowsПоискTerm;

    /// <summary>
    /// When the search box has a term, also surface matching Сериалы above
    /// the movie grid. Re-queried only when the search term actually changes.
    /// </summary>
    private void RefreshShowsInПоиск()
    {
        var q = (_vm.ПоискText ?? "").Trim();
        if (q.Length < 2)
        {
            ShowsInПоискSection.Visibility = Visibility.Collapsed;
            ShowsInПоискRepeater.ItemsSource = null;
            _lastShowsПоискTerm = null;
            return;
        }
        if (q == _lastShowsПоискTerm) return;
        _lastShowsПоискTerm = q;

        var shows = AppState.Instance.Db.ПоискTvShows(q, AppState.Instance.Connected, limit: 24);
        if (shows.Count == 0)
        {
            ShowsInПоискSection.Visibility = Visibility.Collapsed;
            ShowsInПоискRepeater.ItemsSource = null;
            return;
        }
        ShowsInПоискHeader.Text = shows.Count == 1
            ? "TV SHOW MATCHING"
            : $"ПОДХОДЯЩИЕ СЕРИАЛЫ ({shows.Count})";
        ShowsInПоискRepeater.ItemsSource = shows;
        ShowsInПоискSection.Visibility = Visibility.Visible;
    }

    // ── v2.9 Surprise Me 🎲 ──────────────────────────────────────────────────

    /// <summary>
    /// Filter-aware random pick. Uses the current МедиатекаViewModel filters so
    /// the dice are rolled across whatever the user is looking at — random
    /// 90s comedy, random movie in this list, random favorite, etc. Falls
    /// back to an unfiltered random unwatched if the filter set is empty.
    /// </summary>
    private void Вкл.SurpriseMeClick(object sender, RoutedEventArgs e)
    {
        var opts = _vm.BuildOptsForPick();
        var id = AppState.Instance.Db.GetRandomMovieIdMatching(opts, AppState.Instance.Connected);
        if (id == null)
        {
            if (App.MainWindow is MainWindow mw)
                mw.ShowToast("Фильмов нет match the current filter — try clearing some filters");
            return;
        }
        var dialog = new MovieDetailDialog(id.Value);
        dialog.Список просмотраChanged += (_, _) => SidebarRefreshRequested?.Invoke(this, EventArgs.Empty);
        dialog.Activate();
    }

    private string? _shownRowKey = "";   // "" = not run yet, so the first call always queries
    private int _rowShowCount;

    /// <summary>
    /// v2.8.2 — when the current view is a user list, show that list's TV
    /// shows in a row above the movie grid. v4.0.0: Избранное, К просмотру and
    /// Продолжить просмотр get the same row; v4.3.0: Заметки too. Re-queried only
    /// when the view changes (or the page is opened again), so routine VM
    /// updates stay cheap.
    /// </summary>
    private void RefreshShowsInList()
    {
        DatabaseService.TvShowPage? page =
            _vm.ИзбранноеВкл.ly      ? DatabaseService.TvShowPage.Избранное :
            _vm.IsСписок просмотраВкл.ly    ? DatabaseService.TvShowPage.Список просмотра :
            _vm.IsContinueWatching ? DatabaseService.TvShowPage.ContinueWatching :
            _vm.HasNoteВкл.ly        ? DatabaseService.TvShowPage.Заметки : null;
        var key = _vm.UserListId is int listId ? $"list:{listId}" : page?.ToString();
        if (key == _shownRowKey) return;
        _shownRowKey = key;

        var db = AppState.Instance.Db;
        var shows = _vm.UserListId is int id ? db.GetTvShowsInList(id, AppState.Instance.Connected)
                  : page is { } p ? db.GetTvShowsForPage(p, AppState.Instance.Connected)
                  : new List<TvShowListItem>();
        ShowsInListHeader.Text = _vm.UserListId != null ? "СЕРИАЛЫ В ЭТОМ СПИСКЕ" : "TV SHOWS";
        ShowsInListRepeater.ItemsSource = shows.Count > 0 ? shows : null;
        ShowsInListSection.Visibility = shows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _rowShowCount = shows.Count;
        ОбновитьCountText();
        ОбновитьEmptyState();
    }

    /// <summary>"128 movies", plus " · 3 shows" when the shows row is showing (v4.0.0).</summary>
    private void ОбновитьCountText()
    {
        var movies = _vm.FilterTotal == 1 ? "1 movie" : $"{_vm.FilterTotal:N0} movies";
        MovieCountText.Text = _rowShowCount == 0 ? movies
            : $"{movies} · {(_rowShowCount == 1 ? "1 show" : $"{_rowShowCount} shows")}";
    }

    /// <summary>
    /// v2.6 — render a chip per active VM filter so the user can see what's
    /// narrowing the view and drop any single one with one click.
    ///
    /// Each chip's onClear action also reloads — VM filter setters have no
    /// partial Вкл.XxxChanged handlers, so nulling the field alone wouldn't
    /// re-query. We do a reload + re-render the chip strip to keep
    /// everything in sync.
    /// </summary>
    private void ОбновитьFilterChips()
    {
        ActiveFilterChips.Items.Clear();
        ДобавитьChip("Genre",    _vm.Genre,           () => DropFilter(() => _vm.Genre = null));
        if (_vm.FilterDecadeStart.HasValue)
            ДобавитьChip("Decade", $"{_vm.FilterDecadeStart.Value}s",
                () => DropFilter(() => _vm.FilterDecadeStart = null));
        ДобавитьChip("Рейтинг",   _vm.FilterРейтингBand,() => DropFilter(() => _vm.FilterРейтингBand = null));
        ДобавитьChip("Actor",    _vm.FilterActor,     () => DropFilter(() => _vm.FilterActor = null));
        ДобавитьChip("Режиссёр", _vm.FilterРежиссёр,  () => DropFilter(() => _vm.FilterРежиссёр = null));
        ДобавитьChip("Студия",   _vm.FilterСтудия,    () => DropFilter(() => _vm.FilterСтудия = null));
        if (_vm.ИзбранноеВкл.ly)
            ДобавитьChip("Избранное", "★",   () => DropFilter(() => _vm.ИзбранноеВкл.ly = false));
        if (_vm.IsСписок просмотраВкл.ly)
            ДобавитьChip("Список просмотра", "📌",  () => DropFilter(() => _vm.IsСписок просмотраВкл.ly = false));
        if (_vm.IsContinueWatching)
            ДобавитьChip("Continue", "▶",    () => DropFilter(() => _vm.IsContinueWatching = false));
        if (_vm.IsRecentlyПросмотрено)
            ДобавитьChip("Recent", "🕓",     () => DropFilter(() => _vm.IsRecentlyПросмотрено = false));
        if (_vm.IsRecentlyДобавитьed)
            ДобавитьChip("Новый", "🆕",        () => DropFilter(() => _vm.IsRecentlyДобавитьed = false));
        if (_vm.HasNoteВкл.ly)
            ДобавитьChip("Заметки", "📝",      () => DropFilter(() => _vm.HasNoteВкл.ly = false));
        if (_vm.TagId != null && !string.IsNullOrEmpty(_vm.TagName))
            ДобавитьChip("Tag",   _vm.TagName, () => DropFilter(() => { _vm.TagId = null; _vm.TagName = null; }));
        ActiveFilterChips.Visibility = ActiveFilterChips.Items.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Применить a single filter mutation, then reload. If the result is "no
    /// filters left", also reset the page title so the breadcrumb doesn't
    /// stick around ("ВСЕ ФИЛЬМЫ › 2010S" should revert to "ВСЕ ФИЛЬМЫ").
    /// </summary>
    private void DropFilter(Action mutate)
    {
        mutate();
        if (!AnyFilterActive())
        {
            _vm.PageНазвание = "Все фильмы";
            PageНазваниеText.Text = "Все фильмы";
        }
        _ = _vm.LoadAsync();
        ОбновитьClearFiltersButton();
    }

    private bool AnyFilterActive() =>
        !string.IsNullOrEmpty(_vm.ПоискText) ||
        _vm.ПросмотреноFilter != ПросмотреноFilter.Все ||
        _vm.ИзбранноеВкл.ly || _vm.IsСписок просмотраВкл.ly || _vm.IsContinueWatching ||
        _vm.IsRecentlyПросмотрено || _vm.IsRecentlyДобавитьed || _vm.HasNoteВкл.ly ||
        _vm.DriveSerial != null || _vm.Genre != null || _vm.CollectionId != null ||
        _vm.FilterActor != null || _vm.FilterРежиссёр != null || _vm.FilterСтудия != null ||
        _vm.FilterDecadeStart != null || _vm.FilterРейтингBand != null ||
        _vm.UserListId != null || _vm.TagId != null;

    private void ДобавитьChip(string label, string? value, Action onClear)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var border = new Border
        {
            Назадground = CineМедиатекаCS.Services.ThemeBrushes.Get("ChipBrush"),
            BorderBrush = CineМедиатекаCS.Services.ThemeBrushes.Get("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 3, 6, 3),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sp.Children.Добавить(new TextBlock
        {
            FontSize = 11,
            Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("MutedBrush"),
            Text = label + ":",
            VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Добавить(new TextBlock
        {
            FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("TextBrush"),
            Text = value,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var x = new Button
        {
            Content = "✕", FontSize = 10,
            Назадground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Foreground = CineМедиатекаCS.Services.ThemeBrushes.Get("MutedBrush"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0, 4, 0),
            MinWidth = 18, MinHeight = 18,
        };
        ToolTipService.SetToolTip(x, $"Clear {label.ToLower()} filter");
        x.Click += (_, _) => onClear();
        sp.Children.Добавить(x);
        border.Child = sp;
        ActiveFilterChips.Items.Добавить(border);
    }

    // ── Поиск ────────────────────────────────────────────────────────────

    private void Вкл.ПоискChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_ready) return;
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            _vm.ПоискText = sender.Text;
        // v2.6 — Esc hint visibility tracks "has text" (Esc only does
        // something when there's a search to clear).
        ОбновитьПоискEscHint();
    }

    // v3.1 — search scope (Все / Название / Актёры и съёмочная группа).
    private void Вкл.ScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (ScopeCombo.SelectedItem is ComboBoxItem item && item.Tag is string scope)
            _vm.ПоискScope = scope;
    }

    private void Вкл.ПоискBoxFocus(object sender, RoutedEventArgs e)
    {
        ПоискBox.BorderBrush = CineМедиатекаCS.Services.ThemeBrushes.Get("BrandPurpleBrush");
        ОбновитьПоискEscHint();
    }

    private void Вкл.ПоискBoxBlur(object sender, RoutedEventArgs e)
    {
        ПоискBox.BorderBrush = CineМедиатекаCS.Services.ThemeBrushes.Get("InputBorderBrush");
        ОбновитьПоискEscHint();
    }

    private void ОбновитьПоискEscHint()
    {
        // Show only when the box is focused AND has text to clear.
        var hasText = !string.IsNullOrEmpty(ПоискBox.Text);
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot)
            is FrameworkElement fe && (fe == ПоискBox || fe.Parent == ПоискBox);
        ПоискEscHint.Visibility = (hasText && focused) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Sort ──────────────────────────────────────────────────────────────

    private void Вкл.SortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (SortCombo.SelectedItem is not ComboBoxItem item) return;
        var parts = (item.Tag as string ?? "title:asc").Split(':');
        _vm.SortKey = parts[0] switch
        {
            "year"        => SortKey.Год,
            "rating"      => SortKey.Рейтинг,
            "runtime"     => SortKey.Продолжительность,
            "date_added"  => SortKey.DateДобавитьed,
            "last_played" => SortKey.LastВоспроизвестиed,
            _             => SortKey.Название
        };
        _vm.SortDir = parts.Length > 1 && parts[1] == "desc" ? SortDir.Desc : SortDir.Asc;
    }

    // ── View mode ─────────────────────────────────────────────────────────

    private void Вкл.ViewGrid(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        GridViewToggle.IsChecked = true;
        ListViewToggle.IsChecked  = false;
        GridBorder.Visibility = Visibility.Visible;
        ListBorder.Visibility = Visibility.Collapsed;
        _vm.ViewMode = ViewMode.Grid;
    }

    private void Вкл.ViewList(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ListViewToggle.IsChecked  = true;
        GridViewToggle.IsChecked = false;
        GridBorder.Visibility = Visibility.Collapsed;
        ListBorder.Visibility = Visibility.Visible;
        _vm.ViewMode = ViewMode.List;
    }

    // ── Просмотрено filter ────────────────────────────────────────────────────

    private void Вкл.ПросмотреноFilter(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is not Button btn) return;
        var pill = (Style)Application.Current.Resources["PillButtonStyle"];
        var pillActive = (Style)Application.Current.Resources["PillButtonActiveStyle"];
        FilterВсе.Style       = pill;
        FilterПросмотрено.Style   = pill;
        FilterНе просмотрено.Style = pill;
        btn.Style = pillActive;

        _vm.ПросмотреноFilter = (btn.Tag as string) switch
        {
            "watched"   => ПросмотреноFilter.Просмотрено,
            "unwatched" => ПросмотреноFilter.Не просмотрено,
            _           => ПросмотреноFilter.Все
        };
    }

    // ── Infinite scroll ───────────────────────────────────────────────────

    private void Вкл.ScrollChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (sv.VerticalВыкл.set >= sv.ScrollableHeight - 300 && _vm.HasMore && !_vm.IsLoading)
            _ = _vm.LoadMoreAsync();
    }

    // ── Экспорт ────────────────────────────────────────────────────────────

    // v3.9.0 — Экспорт moved to Tools in the sidebar. These let it offer
    // "this view" and write every matching movie, not just the loaded pages.
    public int ViewCount => _vm.FilterTotal;
    public Task<List<MovieListItem>> GetViewФильмыAsync() => _vm.GetВсеMatchingAsync();

    // ── Назад/Toggle sidebar button ────────────────────────────────────────

    public void ShowНазадButton(bool show)
    {
        // Button is already defined in XAML with Visibility="Collapsed"
        // This will be called by MainWindow to show/hide it
    }

    // v2.7 — "back to Обзор" button. MainWindow drives visibility via
    // ShowОбзорНазад / HideОбзорНазад and handles the actual navigation.
    private void Вкл.ОбзорНазадClick(object sender, RoutedEventArgs e)
        => (App.MainWindow as MainWindow)?.Вкл.МедиатекаНазадRequested();

    public void ShowОбзорНазад(string label)
    {
        НазадLabel.Text = label;
        НазадToggleBtn.Visibility = Visibility.Visible;
    }

    public void HideОбзорНазад() => НазадToggleBtn.Visibility = Visibility.Collapsed;
}


