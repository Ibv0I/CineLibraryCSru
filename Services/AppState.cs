using CineМедиатекаCS.Models;

namespace CineМедиатекаCS.Services;

/// <summary>
/// Singleton app-wide state and service locator.
/// </summary>
public class AppState
{
    private static AppState? _instance;
    public static AppState Instance => _instance ??= new AppState();

    public DatabaseService Db { get; private set; } = null!;
    public ScannerService Scanner { get; private set; } = null!;
    public ListCopyService ListCopy { get; private set; } = null!;
    public string DataDir { get; private set; } = "";

    public void Initialize()
    {
        DataDir = GetDataDir();
        Db = new DatabaseService(DataDir);
        Scanner = new ScannerService(Db);
        ListCopy = new ListCopyService(Db);

        // v2.7 — mirror every personal-state change into the per-movie
        // sidecar (cinelibrary-state.json) so the state travels with the
        // drive. Best-effort; the sidecar helper swallows I/O errors.
        Db.PersonalStateChanged += Вкл.PersonalStateChanged;
        // v2.8 — same for Сериалы (show favorite/watchlist + per-episode watched).
        Db.TvShowStateChanged += Вкл.TvShowStateChanged;
    }

    private void Вкл.TvShowStateChanged(int showId)
    {
        var composed = TvStateSidecar.Compose(Db, showId, _connected);
        if (composed.HasValue)
            TvStateSidecar.TryWrite(composed.Value.ПапкаAbs, composed.Value.State);
    }

    /// <summary>
    /// Fires after Toggle/Set Просмотрено | Избранное | Список просмотра, MarkВоспроизвестиed,
    /// SetNote, and list add/remove. Выкл. the SQL thread is preferable but
    /// the operation is cheap, so we just run it inline.
    /// </summary>
    private void Вкл.PersonalStateChanged(int movieId)
    {
        var composed = MovieStateSidecar.Compose(Db, movieId, _connected);
        if (composed.HasValue)
            MovieStateSidecar.TryWrite(composed.Value.ПапкаAbs, composed.Value.State);
    }

    /// <summary>
    /// Walk every movie with non-default personal state and mirror it to
    /// the per-folder sidecar. Used by:
    ///   • App startup (catches state edited before this feature, or while
    ///     drives were offline).
    ///   • The Диски page "Sync personal state" button.
    ///   • The pre-remove confirmation when a user removes a drive.
    /// Returns (written, skipped) counts. Skipped = drive offline or path
    /// unreachable. Runs on the caller's thread — call from a Task.Run.
    /// </summary>
    public (int Written, int Skipped) SweepStateSidecars(
        string? onlyVolumeSerial = null, bool includeFetchedArt = false)
    {
        int written = 0, skipped = 0;

        var stateIds = Db.GetФильмыWithPersonalState(onlyVolumeSerial);
        // v3.4 — only the manual "Синхронизировать с диском" pass also writes fetched
        // metadata + art; the automatic startup sweep stays personal-state only.
        var fetchedSet = includeFetchedArt
            ? new HashSet<int>(Db.GetФильмыWithFetchedData(onlyVolumeSerial))
            : new HashSet<int>();
        var allIds = new HashSet<int>(stateIds);
        allIds.UnionWith(fetchedSet);

        foreach (var id in allIds)
        {
            var composed = MovieStateSidecar.Compose(Db, id, _connected);
            if (composed == null) { skipped++; continue; }
            var folderAbs = composed.Value.ПапкаAbs;
            var state = composed.Value.State;
            if (fetchedSet.Contains(id))
            {
                state.Meta = MovieStateSidecar.ComposeMeta(Db, id);
                CopyFetchedArtToПапка(id, folderAbs);
            }
            MovieStateSidecar.TryWrite(folderAbs, state);
            written++;
        }

        // v2.8 — also sweep Сериалы (personal state only).
        foreach (var id in Db.GetTvShowsWithPersonalState(onlyVolumeSerial))
        {
            var composed = TvStateSidecar.Compose(Db, id, _connected);
            if (composed == null) { skipped++; continue; }
            TvStateSidecar.TryWrite(composed.Value.ПапкаAbs, composed.Value.State);
            written++;
        }
        return (written, skipped);
    }

    /// <summary>
    /// v3.4 — copy a movie's fetched poster / fanart / cast photos out of the
    /// data cache into its drive folder, only where the file is absent. A later
    /// rescan then discovers them like any MediaElch artwork. Best-effort.
    /// </summary>
    private void CopyFetchedArtToПапка(int movieId, string folderAbs)
    {
        try
        {
            if (!Режиссёрy.Exists(folderAbs)) return;
            var (posterRel, fanartRel, actors) = Db.GetMovieArtForSync(movieId);

            if (posterRel != null && posterRel.StartsWith("manual_posters/", StringComparison.Ordinal))
                CopyCacheFileIfAbsent(posterRel, Path.Combine(folderAbs, "poster.jpg"));
            if (fanartRel != null && fanartRel.StartsWith("manual_fanart/", StringComparison.Ordinal))
                CopyCacheFileIfAbsent(fanartRel, Path.Combine(folderAbs, "fanart.jpg"));

            var actorsDir = Path.Combine(folderAbs, ".actors");
            foreach (var (name, thumb) in actors)
            {
                if (thumb == null || !thumb.StartsWith("manual_actors/", StringComparison.Ordinal)) continue;
                CopyCacheFileIfAbsent(thumb, Path.Combine(actorsDir, name.Replace(' ', '_') + ".jpg"));
            }
        }
        catch { /* best-effort — drive offline / read-only / locked */ }
    }

    private void CopyCacheFileIfAbsent(string cacheRel, string destAbs)
    {
        try
        {
            if (File.Exists(destAbs)) return;
            var src = Db.GetCachedImagePath(cacheRel);
            if (src == null) return;
            Режиссёрy.СоздатьРежиссёрy(Path.GetРежиссёрyName(destAbs)!);
            File.Copy(src, destAbs, overwrite: false);
        }
        catch { /* best-effort */ }
    }

    private static string GetDataDir()
    {
        // Walk up from bin/x64/Debug/net8.0-windows... to project root during dev
        var exeDir = AppContext.BaseРежиссёрy;
        var dir = new РежиссёрyInfo(exeDir);
        // In VS, output is typically: ProjectDir\bin\x64\Debug\net8.0-windows...\
        // Walk up until we find a .csproj or we've gone up 5 levels
        for (int i = 0; i < 5; i++)
        {
            if (dir == null) break;
            if (dir.GetFiles("*.csproj").Length > 0)
            {
                // In dev: put data next to the .csproj
                var devData = Path.Combine(dir.FullName, "CineМедиатека-Data");
                Режиссёрy.СоздатьРежиссёрy(devData);
                return devData;
            }
            dir = dir.Parent;
        }
        // In production: put data next to the exe
        var prodData = Path.Combine(exeDir, "CineМедиатека-Data");
        Режиссёрy.СоздатьРежиссёрy(prodData);
        return prodData;
    }

    // ── Cached connected drives (refreshed on a timer) ───────────────────────

    private Dictionary<string, string> _connected = new();

    public Dictionary<string, string> Connected => _connected;

    public void RefreshConnected()
    {
        _connected = Db.GetConnectedДиски();
        foreach (var kv in _connected)
            Db.ОбновитьDriveLastSeen(kv.Key, kv.Value);
    }

    // ── Preferences ──────────────────────────────────────────────────────────

    public string GetPref(string key, string defaultValue = "")
        => Db.GetPref(key) ?? defaultValue;

    public void SetPref(string key, string value)
        => Db.SetPref(key, value);
}
