using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CineМедиатекаCS.Models;
using CineМедиатекаCS.Services;
using System.Коллекции.ObjectModel;

namespace CineМедиатекаCS.ViewModels;

public enum SortKey { Название, Год, Рейтинг, Продолжительность, DateДобавитьed, LastВоспроизвестиed }
public enum SortDir { Asc, Desc }
public enum ПросмотреноFilter { Все, Не просмотрено, Просмотрено }
public enum ViewMode { Grid, List }
public enum МедиатекаViewType { ВсеФильмы, Просмотрено, Не просмотрено, Избранное, Список просмотра }

public partial class МедиатекаViewModel : ObservableObject
{
    private const int PageSize = 60;
    private readonly AppState _state;

    [ObservableProperty] private string _searchText = "";
    // v3.1 — search scope: "all" | "title" | "cast".
    [ObservableProperty] private string _searchScope = "all";
    [ObservableProperty] private SortKey _sortKey = SortKey.Название;
    [ObservableProperty] private SortDir _sortDir = SortDir.Asc;
    [ObservableProperty] private ПросмотреноFilter _watchedFilter = ПросмотреноFilter.Все;
    [ObservableProperty] private ViewMode _viewMode = ViewMode.Grid;
    [ObservableProperty] private bool _favoritesВкл.ly = false;
    [ObservableProperty] private string? _driveSerial = null;
    [ObservableProperty] private string? _genre = null;
    [ObservableProperty] private string? _filterActor = null;
    [ObservableProperty] private string? _filterРежиссёр = null;
    [ObservableProperty] private string? _filterСтудия = null;
    [ObservableProperty] private int? _filterDecadeStart = null;
    [ObservableProperty] private string? _filterРейтингBand = null;
    [ObservableProperty] private bool _isСписок просмотраВкл.ly = false;
    [ObservableProperty] private bool _isContinueWatching = false;
    [ObservableProperty] private bool _isRecentlyПросмотрено = false;
    [ObservableProperty] private bool _isRecentlyДобавитьed = false;
    [ObservableProperty] private bool _hasNoteВкл.ly = false;
    [ObservableProperty] private int? _userListId = null;
    [ObservableProperty] private int? _tagId = null;
    [ObservableProperty] private string? _tagName = null;
    /// <summary>Total rows that match the current filter, before paging.</summary>
    [ObservableProperty] private int _filterTotal = 0;
    [ObservableProperty] private int? _collectionId = null;
    [ObservableProperty] private bool _isLoading = false;
    [ObservableProperty] private bool _hasMore = false;
    [ObservableProperty] private int _totalCount = 0;
    [ObservableProperty] private string _pageНазвание = "Все фильмы";
    [ObservableProperty] private int _watchlistCount = 0;

    public ObservableCollection<MovieListItem> Фильмы { get; } = new();

    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

    public МедиатекаViewModel()
    {
        _state = AppState.Instance;
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        LoadPrefs();
    }

    private void LoadPrefs()
    {
        // Direct field assignment is intentional here: writing through the
        // public properties would fire Вкл.SortKeyChanged / Вкл.SortDirChanged,
        // each of which calls LoadAsync(). At construction time we want to
        // hydrate the prefs first and let the page's explicit LoadAsync()
        // do exactly one DB hop — not race two parallel loads.
#pragma warning disable MVVMTK0034
        var vm = _state.GetPref("viewMode", "Grid");
        _viewMode = Enum.TryParse<ViewMode>(vm, out var vmp) ? vmp : ViewMode.Grid;

        var sk = _state.GetPref("sortKey", "Название");
        _sortKey = Enum.TryParse<SortKey>(sk, out var skp) ? skp : SortKey.Название;

        var sd = _state.GetPref("sortDir", "Asc");
        _sortDir = Enum.TryParse<SortDir>(sd, out var sdp) ? sdp : SortDir.Asc;
#pragma warning restore MVVMTK0034
    }

    // ── Load movies ──────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        IsLoading = true;
        var opts = BuildOpts(0);
        // Real total for the filter — separate cheap COUNT(*) query so the
        // header reads "60 of 1,200 movies" instead of pretending the whole
        // library is whatever the first page returned.
        var (movies, total) = await Task.Run(() => (
            _state.Db.GetФильмы(opts, _state.Connected),
            _state.Db.GetФильмыCount(opts)
        ));

        Фильмы.Clear();
        foreach (var m in movies) Фильмы.Добавить(m);
        FilterTotal = total;
        HasMore = movies.Count == PageSize && Фильмы.Count < total;
        TotalCount = Фильмы.Count;
        IsLoading = false;
    }

    public async Task LoadMoreAsync()
    {
        if (!HasMore || IsLoading) return;
        IsLoading = true;
        var opts = BuildOpts(Фильмы.Count);
        var movies = await Task.Run(() => _state.Db.GetФильмы(opts, _state.Connected));
        foreach (var m in movies) Фильмы.Добавить(m);
        HasMore = movies.Count == PageSize && Фильмы.Count < FilterTotal;
        TotalCount = Фильмы.Count;
        IsLoading = false;
    }

    /// <summary>
    /// v2.9 — Public snapshot of the current filter set for use by the
    /// 🎲 Surprise Me button (which needs the same WHERE clause without paging).
    /// </summary>
    public DatabaseService.ListOptions BuildOptsForPick() => BuildOpts(0);

    /// <summary>v3.9.0 — every movie matching the current view, not just the
    /// loaded pages (for Tools › Экспорт).</summary>
    public Task<List<MovieListItem>> GetВсеMatchingAsync() =>
        Task.Run(() => _state.Db.GetФильмы(BuildOpts(0) with { Limit = int.MaxValue }, _state.Connected));

    private DatabaseService.ListOptions BuildOpts(int offset) => new(
        Поиск: string.IsNullOrWhiteSpace(ПоискText) ? null : ПоискText,
        ПоискScope: ПоискScope,
        SortKey: SortKey.ToString().ToLower() switch
        {
            "dateadded" => "date_added",
            "lastplayed" => "last_played",
            var s => s
        },
        SortDir: SortDir == SortDir.Asc ? "asc" : "desc",
        DriveSerial: DriveSerial,
        Genre: Genre,
        Actor: FilterActor,
        Режиссёр: FilterРежиссёр,
        Студия: FilterСтудия,
        CollectionId: CollectionId,
        ПросмотреноFilter: ПросмотреноFilter switch
        {
            ПросмотреноFilter.Просмотрено => "watched",
            ПросмотреноFilter.Не просмотрено => "unwatched",
            _ => "all"
        },
        ИзбранноеВкл.ly: ИзбранноеВкл.ly,
        IsСписок просмотраВкл.ly: IsСписок просмотраВкл.ly,
        ContinueWatching: IsContinueWatching,
        RecentlyПросмотреноВкл.ly: IsRecentlyПросмотрено,
        RecentlyДобавитьedВкл.ly: IsRecentlyДобавитьed,
        HasNoteВкл.ly: HasNoteВкл.ly,
        UserListId: UserListId,
        TagId: TagId,
        DecadeStart: FilterDecadeStart,
        РейтингBand: FilterРейтингBand,
        Limit: PageSize,
        Выкл.set: offset
    );

    // ── Поиск ───────────────────────────────────────────────────────────────
    // Debounced reload — cancels in-flight delay on every keystroke.
    // Replaces the previous Timer-per-keystroke pattern (each new keystroke
    // disposed the old Timer, but a callback could still fire on a disposed
    // VM during shutdown, and creating a fresh Timer per keystroke was
    // wasteful on busy typing).
    private ОтменаlationTokenSource? _searchCts;

    partial void Вкл.ПоискTextChanged(string value)
    {
        _searchCts?.Отмена();
        _searchCts?.Dispose();
        _searchCts = new ОтменаlationTokenSource();
        var token = _searchCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(300, token); }
            catch (OperationОтменаedException) { return; }
            if (token.IsОтменаlationRequested) return;
            _dispatcherQueue?.TryEnqueue(async () =>
            {
                if (token.IsОтменаlationRequested) return;
                await LoadAsync();
            });
        }, token);
    }

    // v3.1 — changing the scope while a query is present re-runs the search.
    partial void Вкл.ПоискScopeChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(ПоискText)) _ = LoadAsync();
    }

    // ── Sort / View ──────────────────────────────────────────────────────────

    // v4.1.0: Недавно добавленные, Продолжить просмотр and Недавно просмотренные sort by
    // their own date. That order used to be saved as your sort, so Все фильмы
    // came back sorted by last watched. Now a sort on those pages is never
    // saved, and leaving them brings your own sort back.
    private bool HasOwnSort => IsRecentlyДобавитьed || IsContinueWatching || IsRecentlyПросмотрено;
    private bool _restoringSort;

    partial void Вкл.SortKeyChanged(SortKey value)
    {
        if (_restoringSort) return;   // whoever left the page reloads next
        if (!HasOwnSort) _state.SetPref("sortKey", value.ToString());
        _ = LoadAsync();
    }

    partial void Вкл.SortDirChanged(SortDir value)
    {
        if (_restoringSort) return;
        if (!HasOwnSort) _state.SetPref("sortDir", value.ToString());
        _ = LoadAsync();
    }

    partial void Вкл.IsRecentlyДобавитьedChanged(bool value) { if (!value) RestoreСохранитьdSort(); }
    partial void Вкл.IsContinueWatchingChanged(bool value) { if (!value) RestoreСохранитьdSort(); }
    partial void Вкл.IsRecentlyПросмотреноChanged(bool value) { if (!value) RestoreСохранитьdSort(); }

    private void RestoreСохранитьdSort()
    {
        if (HasOwnSort) return;
        _restoringSort = true;
        try
        {
            SortKey = Enum.TryParse<SortKey>(_state.GetPref("sortKey", "Название"), out var k) ? k : SortKey.Название;
            SortDir = Enum.TryParse<SortDir>(_state.GetPref("sortDir", "Asc"), out var d) ? d : SortDir.Asc;
        }
        finally { _restoringSort = false; }
    }

    partial void Вкл.ViewModeChanged(ViewMode value)
    {
        _state.SetPref("viewMode", value.ToString());
    }

    partial void Вкл.ПросмотреноFilterChanged(ПросмотреноFilter value) => _ = LoadAsync();
    partial void Вкл.ИзбранноеВкл.lyChanged(bool value) => _ = LoadAsync();

    // ── Navigation filters ───────────────────────────────────────────────────

    public void SetDriveFilter(string? serial, string? driveLabel = null)
    {
        ResetВсеFilters();
        DriveSerial = serial;
        PageНазвание = driveLabel ?? (serial == null ? "Все фильмы" : "Drive");
        _ = LoadAsync();
    }

    public void SetGenreFilter(string? genre)
    {
        ResetВсеFilters();
        Genre = genre;
        PageНазвание = genre ?? "Все фильмы";
        _ = LoadAsync();
    }

    public void SetCollectionFilter(int? id, string? name)
    {
        ResetВсеFilters();
        CollectionId = id;
        PageНазвание = name ?? "Collection";
        _ = LoadAsync();
    }

    public void SetИзбранное()
    {
        ResetВсеFilters();
        ИзбранноеВкл.ly = true;
        PageНазвание = "Избранное";
        _ = LoadAsync();
    }

    /// <summary>
    /// "Недавно добавленные" view — clears filters and forces sort by date_added DESC.
    /// Doesn't persist this sort to prefs (it's a transient nav choice).
    /// </summary>
    public void ShowRecentlyДобавитьed()
    {
        ResetВсеFilters();
        // v2.9.1 — caps the view to the top-50 newest, so "Недавно добавленные"
        // actually means *recent*. Without this, the page just sorted the
        // entire library by date_added and the header count showed the
        // total library, which made it indistinguishable from Все фильмы.
        IsRecentlyДобавитьed = true;
        ПросмотреноFilter = ПросмотреноFilter.Все;
        SortKey = SortKey.DateДобавитьed;
        SortDir = SortDir.Desc;
        PageНазвание = "🆕 Недавно добавленные";
        _ = LoadAsync();
    }

    /// <summary>
    /// "Продолжить просмотр" — movies the user has hit Воспроизвести on at least once
    /// but hasn't marked watched. Sorted by last_played_at DESC so the most
    /// recently started one is on top. is_watched=0 is enforced in the SQL.
    /// </summary>
    public void ShowContinueWatching()
    {
        ResetВсеFilters();
        IsContinueWatching = true;
        ПросмотреноFilter = ПросмотреноFilter.Все;
        SortKey = SortKey.LastВоспроизвестиed;
        SortDir = SortDir.Desc;
        PageНазвание = "▶ Продолжить просмотр";
        _ = LoadAsync();
    }

    /// <summary>
    /// v2.9 — "Недавно просмотренные": everything you've touched at least once,
    /// ordered by most recent activity. Differs from Продолжить просмотр in
    /// that it includes movies you've marked watched, not just ones you
    /// stopped halfway through.
    /// </summary>
    public void ShowRecentlyПросмотрено()
    {
        ResetВсеFilters();
        IsRecentlyПросмотрено = true;
        ПросмотреноFilter = ПросмотреноFilter.Все;
        SortKey = SortKey.LastВоспроизвестиed;
        SortDir = SortDir.Desc;
        PageНазвание = "🕓 Недавно просмотренные";
        _ = LoadAsync();
    }

    public void ClearFilters()
    {
        ResetВсеFilters();
        PageНазвание = "Все фильмы";
        _ = LoadAsync();
    }

    public void ShowUserList(int listId, string listName)
    {
        ResetВсеFilters();
        UserListId = listId;
        ПросмотреноFilter = ПросмотреноFilter.Все;
        PageНазвание = $"📑 {listName}";
        _ = LoadAsync();
    }

    /// <summary>v2.9 — filter the library to movies carrying a given tag.</summary>
    public void FilterByTag(int tagId, string tagName)
    {
        ResetВсеFilters();
        TagId = tagId;
        TagName = tagName;
        PageНазвание = $"🏷 {tagName}";
        _ = LoadAsync();
    }

    // ── Новый filters (v1.3) ───────────────────────────────────────────────────

    public void FilterByActor(string actorName)
    {
        ResetВсеFilters();
        FilterActor = actorName;
        PageНазвание = $"Фильмы with {actorName}";
        _ = LoadAsync();
    }

    public void FilterByРежиссёр(string directorName)
    {
        ResetВсеFilters();
        FilterРежиссёр = directorName;
        PageНазвание = $"Режиссёр: {directorName}";
        _ = LoadAsync();
    }

    public void FilterByСтудия(string studio)
    {
        ResetВсеFilters();
        FilterСтудия = studio;
        PageНазвание = $"Студия: {studio}";
        _ = LoadAsync();
    }

    public void FilterByDecade(int decadeStart, string label)
    {
        ResetВсеFilters();
        FilterDecadeStart = decadeStart;
        PageНазвание = label;
        _ = LoadAsync();
    }

    public void FilterByРейтингBand(string key, string label)
    {
        ResetВсеFilters();
        FilterРейтингBand = key;
        PageНазвание = label;
        _ = LoadAsync();
    }

    private void ResetВсеFilters()
    {
        FilterActor = null;
        FilterРежиссёр = null;
        FilterСтудия = null;
        Genre = null;
        DriveSerial = null;
        CollectionId = null;
        ИзбранноеВкл.ly = false;
        IsСписок просмотраВкл.ly = false;
        IsContinueWatching = false;
        IsRecentlyПросмотрено = false;
        IsRecentlyДобавитьed = false;
        HasNoteВкл.ly = false;
        UserListId = null;
        TagId = null;
        TagName = null;
        FilterDecadeStart = null;
        FilterРейтингBand = null;
        ПоискText = "";
    }

    public void ShowСписок просмотра()
    {
        ResetВсеFilters();
        IsСписок просмотраВкл.ly = true;
        PageНазвание = "📋 К просмотру";
        RefreshСписок просмотраCount();
        _ = LoadAsync();
    }

    public void ShowЗаметки()
    {
        ResetВсеFilters();
        HasNoteВкл.ly = true;
        PageНазвание = "📝 Заметки";
        _ = LoadAsync();
    }

    public void RefreshСписок просмотраCount()
    {
        Список просмотраCount = _state.Db.GetСписок просмотраCount();
    }

    public void ToggleСписок просмотра(int movieId, bool isСписок просмотра)
    {
        _state.Db.SetСписок просмотра(movieId, isСписок просмотра);
        RefreshСписок просмотраCount();
    }

    // ── Mutations ────────────────────────────────────────────────────────────

    public void ToggleИзбранное(MovieListItem movie)
    {
        _state.Db.ToggleИзбранное(movie.Id);
        movie.IsИзбранное = !movie.IsИзбранное;
        if (ИзбранноеВкл.ly && !movie.IsИзбранное)
            Фильмы.Remove(movie);
    }

    public void ToggleПросмотрено(MovieListItem movie)
    {
        _state.Db.ToggleПросмотрено(movie.Id);
        movie.IsПросмотрено = !movie.IsПросмотрено;
        if (ПросмотреноFilter == ПросмотреноFilter.Не просмотрено && movie.IsПросмотрено)
            Фильмы.Remove(movie);
        else if (ПросмотреноFilter == ПросмотреноFilter.Просмотрено && !movie.IsПросмотрено)
            Фильмы.Remove(movie);
    }

    public void ToggleСписок просмотраВкл.Card(MovieListItem movie)
    {
        var newVal = !movie.IsСписок просмотра;
        _state.Db.SetСписок просмотра(movie.Id, newVal);
        movie.IsСписок просмотра = newVal;
        // If currently filtered to watchlist-only, removing should drop the card
        if (IsСписок просмотраВкл.ly && !newVal)
            Фильмы.Remove(movie);
        RefreshСписок просмотраCount();
    }
}
