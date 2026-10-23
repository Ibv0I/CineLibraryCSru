using CineLibraryCS.Models;

namespace CineLibraryCS.Services;

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
        Db.PersonalStateChanged += OnPersonalStateChanged;
        // v2.8 — same for TV shows (show favorite/watchlist + per-episode watched).
        Db.TvShowStateChanged += OnTvShowStateChanged;
    }

    private void OnTvShowStateChanged(int showId)
    {
        var composed = TvStateSidecar.Compose(Db, showId, _connected);
        if (composed.HasValue)
            TvStateSidecar.TryWrite(composed.Value.FolderAbs, composed.Value.State);
    }

    /// <summary>
    /// Fires after Toggle/Set Watched | Favorite | Watchlist, MarkPlayed,
    /// SetNote, and list add/remove. Off the SQL thread is preferable but
    /// the operation is cheap, so we just run it inline.
    /// </summary>
    private void OnPersonalStateChanged(int movieId)
    {
        var composed = MovieStateSidecar.Compose(Db, movieId, _connected);
        if (composed.HasValue)
            MovieStateSidecar.TryWrite(composed.Value.FolderAbs, composed.Value.State);
    }

    /// <summary>
    /// Walk every movie with non-default personal state and mirror it to
    /// the per-folder sidecar. Used by:
    ///   • App startup (catches state edited before this feature, or while
    ///     drives were offline).
    ///   • The Drives page "Sync personal state" button.
    ///   • The pre-remove confirmation when a user removes a drive.
    /// Returns (written, skipped) counts. Skipped = drive offline or path
    /// unreachable. Runs on the caller's thread — call from a Task.Run.
    /// </summary>
    public (int Written, int Skipped) SweepStateSidecars(
        string? onlyVolumeSerial = null, bool includeFetchedArt = false)
    {
        int written = 0, skipped = 0;

        var stateIds = Db.GetMoviesWithPersonalState(onlyVolumeSerial);
        // v3.4 — only the manual "Sync to drive" pass also writes fetched
        // metadata + art; the automatic startup sweep stays personal-state only.
        var fetchedSet = includeFetchedArt
            ? new HashSet<int>(Db.GetMoviesWithFetchedData(onlyVolumeSerial))
            : new HashSet<int>();
        var allIds = new HashSet<int>(stateIds);
        allIds.UnionWith(fetchedSet);

        foreach (var id in allIds)
        {
            var composed = MovieStateSidecar.Compose(Db, id, _connected);
            if (composed == null) { skipped++; continue; }
            var folderAbs = composed.Value.FolderAbs;
            var state = composed.Value.State;
            if (fetchedSet.Contains(id))
            {
                state.Meta = MovieStateSidecar.ComposeMeta(Db, id);
                CopyFetchedArtToFolder(id, folderAbs);
            }
            MovieStateSidecar.TryWrite(folderAbs, state);
            written++;
        }

        // v2.8 — also sweep TV shows (personal state only).
        foreach (var id in Db.GetTvShowsWithPersonalState(onlyVolumeSerial))
        {
            var composed = TvStateSidecar.Compose(Db, id, _connected);
            if (composed == null) { skipped++; continue; }
            TvStateSidecar.TryWrite(composed.Value.FolderAbs, composed.Value.State);
            written++;
        }
        return (written, skipped);
    }

    /// <summary>
    /// v3.4 — copy a movie's fetched poster / fanart / cast photos out of the
    /// data cache into its drive folder, only where the file is absent. A later
    /// rescan then discovers them like any MediaElch artwork. Best-effort.
    /// </summary>
    private void CopyFetchedArtToFolder(int movieId, string folderAbs)
    {
        try
        {
            if (!Directory.Exists(folderAbs)) return;
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
            Directory.CreateDirectory(Path.GetDirectoryName(destAbs)!);
            File.Copy(src, destAbs, overwrite: false);
        }
        catch { /* best-effort */ }
    }

    private static string GetDataDir()
    {
        // Use one portable data folder beside the project during development,
        // and beside the executable in published builds.
        var exeDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(exeDir);
        string? root = null;
        for (int i = 0; i < 5; i++)
        {
            if (dir == null) break;
            if (dir.GetFiles("*.csproj").Length > 0)
            {
                root = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        root ??= exeDir;
        var dataDir = Path.Combine(root, "movie library_data");
        var legacyDir = Path.Combine(root, "CineLibrary-Data");
        MigrateLegacyDataIfNeeded(legacyDir, dataDir);
        Directory.CreateDirectory(dataDir);
        return dataDir;
    }

    private static void MigrateLegacyDataIfNeeded(string legacyDir, string newDir)
    {
        if (!Directory.Exists(legacyDir)) return;
        try
        {
            // Prefer a same-volume directory rename: it preserves the entire cache
            // and avoids copying a potentially large image cache during startup.
            if (!Directory.Exists(newDir))
            {
                try
                {
                    Directory.Move(legacyDir, newDir);
                    return;
                }
                catch
                {
                    // Fall through to a conservative copy if rename is unavailable.
                }
            }

            // If the new database already exists, it is authoritative. Otherwise
            // copy missing files from the old folder so users keep their catalog/cache.
            var newDb = Path.Combine(newDir, "cinelibrary.db");
            var oldDb = Path.Combine(legacyDir, "cinelibrary.db");
            if (File.Exists(newDb) || !File.Exists(oldDb)) return;
            Directory.CreateDirectory(newDir);
            foreach (var source in Directory.EnumerateFiles(legacyDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(legacyDir, source);
                var destination = Path.Combine(newDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!File.Exists(destination)) File.Copy(source, destination, overwrite: false);
            }
        }
        catch
        {
            // Keep startup resilient; the old folder remains untouched on failure.
        }
    }

    // ── Cached connected drives (refreshed on a timer) ───────────────────────

    private Dictionary<string, string> _connected = new();

    public Dictionary<string, string> Connected => _connected;

    public void RefreshConnected()
    {
        _connected = Db.GetConnectedDrives();
        foreach (var kv in _connected)
            Db.UpdateDriveLastSeen(kv.Key, kv.Value);
    }

    // ── Preferences ──────────────────────────────────────────────────────────

    public string GetPref(string key, string defaultValue = "")
        => Db.GetPref(key) ?? defaultValue;

    public void SetPref(string key, string value)
        => Db.SetPref(key, value);
}
