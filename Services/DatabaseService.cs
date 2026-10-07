using Microsoft.Data.Sqlite;
using CineМедиатекаCS.Models;
using System.Продолжительность.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace CineМедиатекаCS.Services;

public class DatabaseService : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _dataDir;

    // v3.1 — true when the FTS5 full-text search index is available. If the
    // SQLite build somehow lacks FTS5, this stays false and every search path
    // falls back to the older LIKE matching, so search still works.
    private bool _ftsAvailable;

    public DatabaseService(string dataDir)
    {
        _dataDir = dataDir;
        Режиссёрy.СоздатьРежиссёрy(dataDir);
        var dbPath = Path.Combine(dataDir, "cinelibrary.db");
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        ExecutePragmas();
        СоздатьSchema();
        RunMigrations();
        SetupFullTextПоиск();
    }

    // ── v3.1 Full-text search (FTS5) ──────────────────────────────────────────

    /// <summary>
    /// Создать the FTS5 index (if the SQLite build supports it) and populate it
    /// once for existing libraries. Columns are weighted at query time so a
    /// title hit always outranks a plot hit.
    /// </summary>
    private void SetupFullTextПоиск()
    {
        // v3.1.2 — FTS5 search is DISABLED, and any leftover index is removed.
        //
        // Earlier 3.1.0 builds created a `movies_fts` virtual table and did
        // partial / failed writes into it, which could leave its backing
        // shadow tables corrupt. SQLite then reports "database disk image is
        // malformed" on later writes (e.g. a drive scan). Dropping the table
        // clears that corruption and leaves the rest of the catalog intact.
        //
        // Поиск itself uses the proven LIKE path (title + cast, no plot
        // noise) plus the scope selector — FtsMatchFor() always returns null
        // while this flag is false.
        _ftsAvailable = false;

        // Remove EVERY database object that references the old movies_fts
        // index — not just ones named "movies_fts*". A leftover trigger that
        // mentions movies_fts in its body will fire on the next movie write
        // (a scan) and fail with either "malformed" (if the table is still
        // there and corrupt) or "no such table: movies_fts" (if it's gone).
        // Dropping the referencing objects — by reading their definitions
        // from sqlite_master — fixes both, and leaves the rest of the
        // catalog (drives, movies, lists, tags, watched state) untouched.
        try
        {
            var drops = new List<(string Type, string Name)>();
            using (var q = _conn.СоздатьCommand())
            {
                q.CommandText =
                    @"SELECT type, name FROM sqlite_master
                       WHERE (sql LIKE '%movies_fts%' OR name LIKE 'movies_fts%')
                         AND name NOT LIKE 'sqlite_%'";
                using var r = q.ExecuteReader();
                while (r.Read())
                    drops.Добавить((r.IsDBNull(0) ? "" : r.GetString(0),
                               r.IsDBNull(1) ? "" : r.GetString(1)));
            }
            // Drop triggers/views first (they reference the table), then the
            // table itself.
            foreach (var (type, name) in drops.OrderBy(d => d.Type == "table" ? 1 : 0))
            {
                if (string.IsNullOrEmpty(name)) continue;
                var kw = type switch
                {
                    "trigger" => "TRIGGER",
                    "view"    => "VIEW",
                    "index"   => "INDEX",
                    _          => "TABLE",
                };
                try { Exec($"DROP {kw} IF EXISTS \"{name}\";"); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"FTS cleanup: drop {type} {name} failed: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FTS cleanup failed: {ex.Message}");
        }
    }

    private long ScalarLong(string sql)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? 0 : Convert.ToInt64(v);
    }

    /// <summary>
    /// Rebuild the whole movie search index from the catalog. Cheap even for
    /// thousands of rows; called at the end of every scan and on first launch
    /// after upgrading. Best-effort — never throws into the caller. The DELETE
    /// and INSERT run as separate statements so there's no dependence on
    /// multi-statement batching.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RebuildMovieПоискIndex()
    {
        if (!_ftsAvailable) return;
        try
        {
            Exec("DELETE FROM movies_fts;");
            Exec(@"
                INSERT INTO movies_fts(rowid, title, original_title, people, collection, plot)
                SELECT m.id,
                       COALESCE(m.title, ''),
                       COALESCE(m.original_title, ''),
                       TRIM(
                         COALESCE((SELECT GROUP_CONCAT(a.name, ' ') FROM movie_actors ma
                                    JOIN actors a ON a.id = ma.actor_id WHERE ma.movie_id = m.id), '')
                         || ' ' ||
                         COALESCE((SELECT GROUP_CONCAT(d.name, ' ') FROM movie_directors md
                                    JOIN directors d ON d.id = md.director_id WHERE md.movie_id = m.id), '')
                       ),
                       COALESCE((SELECT GROUP_CONCAT(s.name, ' ') FROM movie_sets ms
                                  JOIN sets s ON s.id = ms.set_id WHERE ms.movie_id = m.id), ''),
                       COALESCE(m.plot, '')
                  FROM movies m;");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RebuildMovieПоискIndex failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Build an FTS5 MATCH expression from the user's query + scope, or null
    /// when FTS shouldn't/can't be used (then callers fall back to LIKE).
    /// Each whitespace word becomes a prefix term; words are AND-ed; the scope
    /// restricts which column the terms must match.
    /// </summary>
    private string? FtsMatchFor(ListOptions opts)
    {
        if (!_ftsAvailable || string.IsNullOrWhiteSpace(opts.Поиск)) return null;

        // Pull out runs of letters/digits as words (so "spider-man" → spider,
        // man and nothing in the query can be read as an FTS operator). Each
        // word becomes a prefix term; words are AND-ed.
        var words = System.Text.RegularExpressions.Regex.Matches(opts.Поиск, @"[\p{L}\p{N}]+");
        string? column = opts.ПоискScope switch
        {
            "title" => "{title original_title}",
            "cast"  => "{people}",
            _        => null,
        };
        var terms = new List<string>();
        foreach (System.Text.RegularExpressions.Match w in words)
            terms.Добавить(column != null ? $"{column} : {w.Value}*" : $"{w.Value}*");
        return terms.Count == 0 ? null : string.Join(" AND ", terms);
    }

    // ── Schema ──────────────────────────────────────────────────────────────

    private void ExecutePragmas()
    {
        // v2.6 — busy_timeout makes a writer wait up to 5 s for the lock
        // instead of failing immediately with SQLITE_BUSY. Critical when
        // the scanner is mid-transaction and the UI thread tries to UPDATE
        // (e.g. drive-last-seen, watched/fav toggles). WAL gives concurrent
        // readers; busy_timeout gives serialized writers a chance to land.
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
    }

    private void СоздатьSchema()
    {
        Exec(@"
CREATE TABLE IF NOT EXISTS drives (
    volume_serial TEXT PRIMARY KEY,
    label TEXT NOT NULL,
    last_seen_letter TEXT,
    last_connected_at INTEGER,
    movie_root_relative TEXT NOT NULL DEFAULT 'Фильмы'
);

CREATE TABLE IF NOT EXISTS drive_roots (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    volume_serial TEXT NOT NULL REFERENCES drives(volume_serial) ON DELETE CASCADE,
    root_path TEXT NOT NULL,
    UNIQUE(volume_serial, root_path)
);

CREATE TABLE IF NOT EXISTS movies (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    volume_serial TEXT NOT NULL REFERENCES drives(volume_serial) ON DELETE CASCADE,
    folder_rel_path TEXT NOT NULL,
    video_file_rel_path TEXT,
    title TEXT NOT NULL,
    original_title TEXT,
    sort_title TEXT,
    year INTEGER,
    rating REAL,
    votes INTEGER,
    runtime INTEGER,
    plot TEXT,
    outline TEXT,
    tagline TEXT,
    mpaa TEXT,
    imdb_id TEXT,
    tmdb_id TEXT,
    premiered TEXT,
    studio TEXT,
    country TEXT,
    trailer TEXT,
    local_poster TEXT,
    local_fanart TEXT,
    local_nfo TEXT,
    is_missing INTEGER DEFAULT 0,
    is_favorite INTEGER DEFAULT 0,
    is_watched INTEGER DEFAULT 0,
    date_added INTEGER DEFAULT (strftime('%s','now')),
    date_modified INTEGER DEFAULT (strftime('%s','now')),
    UNIQUE(volume_serial, folder_rel_path)
);

CREATE TABLE IF NOT EXISTS genres (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT UNIQUE NOT NULL
);
CREATE TABLE IF NOT EXISTS movie_genres (
    movie_id INTEGER REFERENCES movies(id) ON DELETE CASCADE,
    genre_id INTEGER REFERENCES genres(id) ON DELETE CASCADE,
    PRIMARY KEY(movie_id, genre_id)
);

CREATE TABLE IF NOT EXISTS directors (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT UNIQUE NOT NULL
);
CREATE TABLE IF NOT EXISTS movie_directors (
    movie_id INTEGER REFERENCES movies(id) ON DELETE CASCADE,
    director_id INTEGER REFERENCES directors(id) ON DELETE CASCADE,
    PRIMARY KEY(movie_id, director_id)
);

CREATE TABLE IF NOT EXISTS actors (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT UNIQUE NOT NULL,
    thumb TEXT
);
CREATE TABLE IF NOT EXISTS movie_actors (
    movie_id INTEGER REFERENCES movies(id) ON DELETE CASCADE,
    actor_id INTEGER REFERENCES actors(id) ON DELETE CASCADE,
    role TEXT,
    sort_order INTEGER DEFAULT 0,
    PRIMARY KEY(movie_id, actor_id)
);

CREATE TABLE IF NOT EXISTS sets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT UNIQUE NOT NULL
);
CREATE TABLE IF NOT EXISTS movie_sets (
    movie_id INTEGER REFERENCES movies(id) ON DELETE CASCADE,
    set_id INTEGER REFERENCES sets(id) ON DELETE CASCADE,
    PRIMARY KEY(movie_id, set_id)
);

CREATE TABLE IF NOT EXISTS preferences (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

-- v1.9.2 — user-defined custom lists (e.g. ""Date night"", ""80s sci-fi"").
-- Фильмы can be in many lists; deleting a list cascades the join rows.
CREATE TABLE IF NOT EXISTS user_lists (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT UNIQUE NOT NULL,
    created_at INTEGER DEFAULT (strftime('%s','now')),
    sort_order INTEGER DEFAULT 0
);
CREATE TABLE IF NOT EXISTS user_list_movies (
    list_id INTEGER REFERENCES user_lists(id) ON DELETE CASCADE,
    movie_id INTEGER REFERENCES movies(id) ON DELETE CASCADE,
    added_at INTEGER DEFAULT (strftime('%s','now')),
    PRIMARY KEY (list_id, movie_id)
);

-- v2.8 — TV Shows. A show folder (contains tvshow.nfo) maps to one
-- tv_shows row; each episode file maps to one tv_episodes row. Seasons
-- are derived from tv_episodes.season (no separate table). Просмотрено is
-- per-episode; favorite/watchlist/note are show-level.
CREATE TABLE IF NOT EXISTS tv_shows (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    volume_serial TEXT NOT NULL REFERENCES drives(volume_serial) ON DELETE CASCADE,
    folder_rel_path TEXT NOT NULL,
    title TEXT NOT NULL,
    original_title TEXT,
    sort_title TEXT,
    year INTEGER,
    rating REAL,
    votes INTEGER,
    plot TEXT,
    mpaa TEXT,
    premiered TEXT,
    studio TEXT,
    status TEXT,
    imdb_id TEXT,
    tmdb_id TEXT,
    tvdb_id TEXT,
    local_poster TEXT,
    local_fanart TEXT,
    local_nfo TEXT,
    is_missing INTEGER DEFAULT 0,
    is_favorite INTEGER DEFAULT 0,
    is_watchlist INTEGER DEFAULT 0,
    note TEXT,
    date_added INTEGER DEFAULT (strftime('%s','now')),
    date_modified INTEGER DEFAULT (strftime('%s','now')),
    UNIQUE(volume_serial, folder_rel_path)
);

CREATE TABLE IF NOT EXISTS tv_episodes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    show_id INTEGER NOT NULL REFERENCES tv_shows(id) ON DELETE CASCADE,
    season INTEGER NOT NULL,
    episode INTEGER NOT NULL,
    title TEXT,
    plot TEXT,
    aired TEXT,
    rating REAL,
    runtime INTEGER,
    video_file_rel_path TEXT,
    local_thumb TEXT,
    subtitle_languages TEXT,
    video_width INTEGER,
    video_height INTEGER,
    video_codec TEXT,
    hdr_type TEXT,
    audio_codec TEXT,
    audio_channels TEXT,
    audio_languages TEXT,
    duration_seconds INTEGER,
    container_ext TEXT,
    file_size_bytes INTEGER,
    is_watched INTEGER DEFAULT 0,
    last_played_at INTEGER DEFAULT 0,
    UNIQUE(show_id, season, episode)
);

CREATE TABLE IF NOT EXISTS tv_show_genres (
    show_id INTEGER REFERENCES tv_shows(id) ON DELETE CASCADE,
    genre_id INTEGER REFERENCES genres(id) ON DELETE CASCADE,
    PRIMARY KEY(show_id, genre_id)
);
CREATE TABLE IF NOT EXISTS tv_show_actors (
    show_id INTEGER REFERENCES tv_shows(id) ON DELETE CASCADE,
    actor_id INTEGER REFERENCES actors(id) ON DELETE CASCADE,
    role TEXT,
    sort_order INTEGER DEFAULT 0,
    PRIMARY KEY(show_id, actor_id)
);

CREATE INDEX IF NOT EXISTS idx_tv_shows_volume ON tv_shows(volume_serial);
CREATE INDEX IF NOT EXISTS idx_tv_episodes_show ON tv_episodes(show_id);
CREATE INDEX IF NOT EXISTS idx_tv_episodes_watched ON tv_episodes(is_watched);

-- v2.8.2 — lists are independent buckets: a list can hold movies AND
-- shows. user_list_movies already exists; this is its TV twin.
CREATE TABLE IF NOT EXISTS user_list_shows (
    list_id INTEGER REFERENCES user_lists(id) ON DELETE CASCADE,
    show_id INTEGER REFERENCES tv_shows(id) ON DELETE CASCADE,
    added_at INTEGER DEFAULT (strftime('%s','now')),
    PRIMARY KEY (list_id, show_id)
);
");
    }

    private void RunMigrations()
    {
        var cols = new HashSet<string>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "PRAGMA table_info(movies)";
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Добавить(r.GetString(1));

        if (!cols.Contains("is_watched"))
            Exec("ALTER TABLE movies ADD COLUMN is_watched INTEGER DEFAULT 0");
        if (!cols.Contains("is_favorite"))
            Exec("ALTER TABLE movies ADD COLUMN is_favorite INTEGER DEFAULT 0");
        if (!cols.Contains("is_watchlist"))
            Exec("ALTER TABLE movies ADD COLUMN is_watchlist INTEGER DEFAULT 0");
        if (!cols.Contains("last_played_at"))
            Exec("ALTER TABLE movies ADD COLUMN last_played_at INTEGER DEFAULT 0");
        if (!cols.Contains("note"))
            Exec("ALTER TABLE movies ADD COLUMN note TEXT");
        // v2.2 — stream details + file info + writers fields
        if (!cols.Contains("video_width"))      Exec("ALTER TABLE movies ADD COLUMN video_width INTEGER");
        if (!cols.Contains("video_height"))     Exec("ALTER TABLE movies ADD COLUMN video_height INTEGER");
        if (!cols.Contains("video_codec"))      Exec("ALTER TABLE movies ADD COLUMN video_codec TEXT");
        if (!cols.Contains("video_aspect"))     Exec("ALTER TABLE movies ADD COLUMN video_aspect TEXT");
        if (!cols.Contains("hdr_type"))         Exec("ALTER TABLE movies ADD COLUMN hdr_type TEXT");
        if (!cols.Contains("audio_codec"))      Exec("ALTER TABLE movies ADD COLUMN audio_codec TEXT");
        if (!cols.Contains("audio_channels"))   Exec("ALTER TABLE movies ADD COLUMN audio_channels TEXT");
        if (!cols.Contains("audio_languages"))  Exec("ALTER TABLE movies ADD COLUMN audio_languages TEXT");
        if (!cols.Contains("subtitle_languages"))Exec("ALTER TABLE movies ADD COLUMN subtitle_languages TEXT");
        if (!cols.Contains("duration_seconds")) Exec("ALTER TABLE movies ADD COLUMN duration_seconds INTEGER");
        if (!cols.Contains("container_ext"))    Exec("ALTER TABLE movies ADD COLUMN container_ext TEXT");
        if (!cols.Contains("file_size_bytes"))  Exec("ALTER TABLE movies ADD COLUMN file_size_bytes INTEGER");
        // v3.3 — Просмотрено и удалено. NULL = normal library movie; a unix
        // timestamp = the movie was archived (watched then deleted from
        // disk, kept as a record with poster/notes/history). Archived rows
        // are excluded from every live-library query and survive drive
        // removal and missing-cleanup.
        if (!cols.Contains("archived_at"))      Exec("ALTER TABLE movies ADD COLUMN archived_at INTEGER");
        // Hidden sentinel drive that archived records are re-pointed to when
        // their original drive is removed (movies cascade-delete with their
        // drive, and a record must outlive the drive it came from). Never
        // shown in the Диски UI.
        Exec("INSERT OR IGNORE INTO drives (volume_serial, label) VALUES ('__archive__', 'Просмотрено и удалено')");

        // Multi-source ratings table (TMDb / IMDb / Rotten Tomatoes / …)
        Exec(@"
CREATE TABLE IF NOT EXISTS movie_ratings (
    movie_id INTEGER REFERENCES movies(id) ON DELETE CASCADE,
    source   TEXT NOT NULL,
    value    REAL NOT NULL,
    votes    INTEGER,
    PRIMARY KEY (movie_id, source)
);
");

        // Writers (separate table to mirror directors)
        Exec(@"
CREATE TABLE IF NOT EXISTS writers (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT UNIQUE NOT NULL
);
CREATE TABLE IF NOT EXISTS movie_writers (
    movie_id  INTEGER REFERENCES movies(id) ON DELETE CASCADE,
    writer_id INTEGER REFERENCES writers(id) ON DELETE CASCADE,
    PRIMARY KEY (movie_id, writer_id)
);
");

        // v1.9.2 — ensure user_lists tables exist on databases that pre-date them
        Exec(@"
CREATE TABLE IF NOT EXISTS user_lists (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT UNIQUE NOT NULL,
    created_at INTEGER DEFAULT (strftime('%s','now')),
    sort_order INTEGER DEFAULT 0
);
CREATE TABLE IF NOT EXISTS user_list_movies (
    list_id INTEGER REFERENCES user_lists(id) ON DELETE CASCADE,
    movie_id INTEGER REFERENCES movies(id) ON DELETE CASCADE,
    added_at INTEGER DEFAULT (strftime('%s','now')),
    PRIMARY KEY (list_id, movie_id)
);
");

        // v1.9.2 — one-shot normalization of pre-existing actor/set rows
        // (whitespace drift made "Tom Hanks" and "Tom Hanks " distinct rows,
        // which split actor/collection counts in the UI). Migration failures
        // are logged but never block app launch — user can still use the
        // catalog even if normalization couldn't complete on their data.
        var normPrefKey = "_migration_normalize_v192";
        if (GetPref(normPrefKey) != "done")
        {
            try
            {
                NormalizeAndMergeNames("actors",   "movie_actors",   "actor_id");
                NormalizeAndMergeNames("directors","movie_directors","director_id");
                NormalizeAndMergeNames("genres",   "movie_genres",   "genre_id");
                NormalizeAndMergeNames("sets",     "movie_sets",     "set_id");
                SetPref(normPrefKey, "done");
            }
            catch (Exception ex)
            {
                // Don't mark done — we'll retry next launch with fixed code.
                System.Diagnostics.Debug.WriteLine($"v1.9.2 normalization skipped: {ex.Message}");
            }
        }

        // v2.1.x — split multi-genre rows and fold aliases into canonical
        // English names. Migration key tracks the alias map version, so
        // expanding the alias dictionary (e.g. v2.1.1 adds Arabic, Hindi…)
        // triggers a re-run automatically — no manual user action needed.
        var genreSplitKey = $"_migration_split_genres_{GenreAliases.Version}";
        if (GetPref(genreSplitKey) != "done")
        {
            try { SplitAndAliasЖанрыMigration(); SetPref(genreSplitKey, "done"); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"genre split {GenreAliases.Version} skipped: {ex.Message}");
            }
        }

        // v2.1.1 — re-extract sets from cached .nfo files using the broader
        // parser (covers <collection>, <sets><set>…, <setname>, etc.).
        // Cheap targeted re-parse: doesn't re-copy artwork or re-process the
        // rest of the metadata — just rebuilds movie_sets linkage.
        var setПересканироватьKey = "_migration_recompute_sets_v211";
        if (GetPref(setПересканироватьKey) != "done")
        {
            try { RecomputeВсеSetsFromNfos(); SetPref(setПересканироватьKey, "done"); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"sets recompute v2.1.1 skipped: {ex.Message}");
            }
        }

        // v2.2 — re-extract stream details, multiple ratings, writers, trailer
        // from cached .nfo files. Same approach as the v2.1.1 sets migration —
        // user doesn't need to do a full rescan to gain the new fields.
        var richNfoKey = "_migration_rich_nfo_v220";
        if (GetPref(richNfoKey) != "done")
        {
            try { RecomputeRichNfoFields(); SetPref(richNfoKey, "done"); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"rich nfo recompute v2.2 skipped: {ex.Message}");
            }
        }

        // ── v2.9 Phase 2 (Memory) ───────────────────────────────────────────

        // Watch events — append-only log of every play / mark-watched /
        // mark-unwatched interaction, for both movies and TV episodes.
        // Powers "Недавно просмотренные" and "В этот день" features. item_kind
        // is 'movie' or 'episode'; item_id refers to the matching table's
        // primary key. No FK so deletions don't cascade — history survives
        // even if a drive is unplugged and a row is later pruned.
        Exec(@"
CREATE TABLE IF NOT EXISTS watch_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    item_kind  TEXT NOT NULL,
    item_id    INTEGER NOT NULL,
    watched_at INTEGER NOT NULL,
    action     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_watch_events_lookup
    ON watch_events(item_kind, item_id, watched_at DESC);
CREATE INDEX IF NOT EXISTS idx_watch_events_when
    ON watch_events(watched_at DESC);
");

        // v3.4.4 — Дубликаты: group keys the user has chosen to ignore (kept on
        // purpose, or a case the classifier got wrong). Survives rescans.
        Exec(@"
CREATE TABLE IF NOT EXISTS dupe_ignored (
    group_key  TEXT PRIMARY KEY,
    created_at INTEGER DEFAULT (strftime('%s','now'))
);");

        // Free-form tags — independent of lists. Фильмы and shows reference
        // the same `tags` rows so a tag like "rewatched" can carry across.
        Exec(@"
CREATE TABLE IF NOT EXISTS tags (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT UNIQUE NOT NULL COLLATE NOCASE,
    created_at INTEGER DEFAULT (strftime('%s','now'))
);
CREATE TABLE IF NOT EXISTS movie_tags (
    movie_id INTEGER REFERENCES movies(id) ON DELETE CASCADE,
    tag_id   INTEGER REFERENCES tags(id) ON DELETE CASCADE,
    added_at INTEGER DEFAULT (strftime('%s','now')),
    PRIMARY KEY (movie_id, tag_id)
);
CREATE TABLE IF NOT EXISTS tv_show_tags (
    show_id INTEGER REFERENCES tv_shows(id) ON DELETE CASCADE,
    tag_id  INTEGER REFERENCES tags(id) ON DELETE CASCADE,
    added_at INTEGER DEFAULT (strftime('%s','now')),
    PRIMARY KEY (show_id, tag_id)
);
");

        // Per-episode favorite + note columns. Defensive ALTER so re-runs are no-ops.
        var epCols = new HashSet<string>();
        using (var ec = _conn.СоздатьCommand())
        {
            ec.CommandText = "PRAGMA table_info(tv_episodes)";
            using var er = ec.ExecuteReader();
            while (er.Read()) epCols.Добавить(er.GetString(1));
        }
        if (!epCols.Contains("is_favorite"))
            Exec("ALTER TABLE tv_episodes ADD COLUMN is_favorite INTEGER DEFAULT 0");
        if (!epCols.Contains("note"))
            Exec("ALTER TABLE tv_episodes ADD COLUMN note TEXT");

        // Создать indexes for performance
        СоздатьIndexes();
    }

    /// <summary>
    /// Dedupe + normalize a name table. Order matters:
    ///   1. Read every row, normalize its name in memory.
    ///   2. Group by normalized name; pick MIN(id) as keeper per group.
    ///   3. Merge join rows from other ids → keeper, then delete the other rows.
    ///   4. UPDATE keepers to the normalized form (now safe — no collisions left).
    ///
    /// Doing it in this order matters because the name column has a UNIQUE
    /// constraint. If we tried to TRIM in place first, "Tom Hanks " collapsing
    /// to "Tom Hanks" would collide with an existing "Tom Hanks" row and throw,
    /// killing DB construction and bricking the app at launch (this was the
    /// v1.9.2 first-cut bug).
    /// </summary>

    /// <summary>
    /// Вкл.e-shot v2.1.0 fix for genre rows that contain multiple genres in
    /// one string (e.g. "Action / Adventure / Sci-Fi") plus alias drift
    /// (Science Fiction → Sci-Fi). For every offender, splits into separate
    /// canonical names, repoints movie_genres links to the canonical rows,
    /// then deletes the original multi-genre row.
    /// </summary>
    /// <summary>
    /// Walks every movie's cached .nfo file, re-parses it for sets only via
    /// the broadened NfoParser, and rebuilds movie_sets linkage. Used as a
    /// one-shot v2.1.1 migration so users don't need to do a full rescan to
    /// pick up collections their nfos always had (in formats we didn't read).
    /// </summary>
    private void RecomputeВсеSetsFromNfos()
    {
        // Snapshot (movie_id, nfo_path) pairs
        var movies = new List<(int id, string nfoRel)>();
        using (var sel = _conn.СоздатьCommand())
        {
            sel.CommandText = "SELECT id, local_nfo FROM movies WHERE local_nfo IS NOT NULL AND is_missing=0";
            using var r = sel.ExecuteReader();
            while (r.Read())
                movies.Добавить((r.GetInt32(0), r.GetString(1)));
        }
        if (movies.Count == 0) return;

        // Cache existing set names → ids (case-insensitive)
        var setCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var sel = _conn.СоздатьCommand())
        {
            sel.CommandText = "SELECT id, name FROM sets";
            using var r = sel.ExecuteReader();
            while (r.Read())
            {
                var nm = r.GetString(1).Trim();
                if (nm.Length > 0) setCache[nm] = r.GetInt32(0);
            }
        }

        using var tx = _conn.BeginTransaction();
        try
        {
            foreach (var (movieId, nfoRel) in movies)
            {
                var nfoPath = Path.Combine(_dataDir, nfoRel);
                if (!File.Exists(nfoPath)) continue;

                ParsedMovie? parsed;
                try { parsed = NfoParser.Parse(nfoPath); }
                catch { continue; }
                if (parsed == null) continue;

                // Drop existing links, re-insert from the re-parsed list
                using (var del = _conn.СоздатьCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM movie_sets WHERE movie_id=@m";
                    del.Parameters.ДобавитьWithValue("@m", movieId);
                    del.ExecuteNonQuery();
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in parsed.Sets)
                {
                    var name = (raw ?? "").Trim();
                    if (string.IsNullOrEmpty(name) || !seen.Добавить(name)) continue;

                    if (!setCache.TryGetValue(name, out var setId))
                    {
                        using var ins = _conn.СоздатьCommand();
                        ins.Transaction = tx;
                        ins.CommandText = "INSERT INTO sets (name) VALUES (@n); SELECT last_insert_rowid();";
                        ins.Parameters.ДобавитьWithValue("@n", name);
                        setId = Convert.ToInt32(ins.ExecuteScalar());
                        setCache[name] = setId;
                    }

                    using var link = _conn.СоздатьCommand();
                    link.Transaction = tx;
                    link.CommandText = "INSERT OR IGNORE INTO movie_sets VALUES (@m, @s)";
                    link.Parameters.ДобавитьWithValue("@m", movieId);
                    link.Parameters.ДобавитьWithValue("@s", setId);
                    link.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// v2.2 one-shot: walks every cached .nfo and refreshes the new fields
    /// (stream details, multi-source ratings, writers, trailer) without
    /// touching artwork or other already-correct metadata.
    /// </summary>
    private void RecomputeRichNfoFields()
    {
        var rows = new List<(int id, string nfoRel, string? videoRel, string letter, string serial)>();
        var connected = GetConnectedДиски();
        using (var sel = _conn.СоздатьCommand())
        {
            sel.CommandText = @"SELECT id, local_nfo, video_file_rel_path, volume_serial
                                FROM movies WHERE local_nfo IS NOT NULL AND is_missing=0";
            using var r = sel.ExecuteReader();
            while (r.Read())
            {
                var serial = r.GetString(3);
                connected.TryGetValue(serial, out var letter);
                rows.Добавить((r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), letter ?? "", serial));
            }
        }
        if (rows.Count == 0) return;

        var writerCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var preload = _conn.СоздатьCommand();
            preload.CommandText = "SELECT id, name FROM writers";
            using var r = preload.ExecuteReader();
            while (r.Read()) writerCache[r.GetString(1)] = r.GetInt32(0);
        }
        catch { }

        using var tx = _conn.BeginTransaction();
        try
        {
            foreach (var row in rows)
            {
                var nfoPath = Path.Combine(_dataDir, row.nfoRel);
                if (!File.Exists(nfoPath)) continue;

                ParsedMovie? p;
                try { p = NfoParser.Parse(nfoPath); }
                catch { continue; }
                if (p == null) continue;

                var sd = p.Stream;

                // Обновить stream + trailer columns
                using (var upd = _conn.СоздатьCommand())
                {
                    upd.Transaction = tx;
                    upd.CommandText = @"UPDATE movies SET
                        trailer=@tr,
                        video_width=@vw, video_height=@vh, video_codec=@vc, video_aspect=@va,
                        hdr_type=@hdr, audio_codec=@ac, audio_channels=@ach,
                        audio_languages=@al, subtitle_languages=@sl, duration_seconds=@dur
                        WHERE id=@id";
                    upd.Parameters.ДобавитьWithValue("@id", row.id);
                    upd.Parameters.ДобавитьWithValue("@tr",  (object?)p.Trailer ?? DBNull.Value);
                    upd.Parameters.ДобавитьWithValue("@vw",  (object?)sd?.VideoWidth        ?? DBNull.Value);
                    upd.Parameters.ДобавитьWithValue("@vh",  (object?)sd?.VideoHeight       ?? DBNull.Value);
                    upd.Parameters.ДобавитьWithValue("@vc",  (object?)sd?.VideoCodec        ?? DBNull.Value);
                    upd.Parameters.ДобавитьWithValue("@va",  (object?)sd?.VideoAspect       ?? DBNull.Value);
                    upd.Parameters.ДобавитьWithValue("@hdr", (object?)sd?.HdrType           ?? DBNull.Value);
                    upd.Parameters.ДобавитьWithValue("@ac",  (object?)sd?.АудиоCodec        ?? DBNull.Value);
                    upd.Parameters.ДобавитьWithValue("@ach", (object?)sd?.АудиоChannels     ?? DBNull.Value);
                    upd.Parameters.ДобавитьWithValue("@al",  (object?)sd?.АудиоLanguages    ?? DBNull.Value);
                    upd.Parameters.ДобавитьWithValue("@sl",  (object?)sd?.SubtitleLanguages ?? DBNull.Value);
                    upd.Parameters.ДобавитьWithValue("@dur", (object?)sd?.DurationSeconds   ?? DBNull.Value);
                    upd.ExecuteNonQuery();
                }

                // Размер файла + container — only if drive online + file present
                if (!string.IsNullOrEmpty(row.letter) && row.videoRel != null)
                {
                    var full = Path.Combine($"{row.letter}:\\", row.videoRel.Replace('/', '\\'));
                    if (File.Exists(full))
                    {
                        long size = 0;
                        try { size = new FileInfo(full).Length; } catch { }
                        var ext = Path.GetExtension(full).TrimStart('.').ToLowerInvariant();
                        using var upd = _conn.СоздатьCommand();
                        upd.Transaction = tx;
                        upd.CommandText = "UPDATE movies SET file_size_bytes=@s, container_ext=@x WHERE id=@id";
                        upd.Parameters.ДобавитьWithValue("@s", size);
                        upd.Parameters.ДобавитьWithValue("@x", ext);
                        upd.Parameters.ДобавитьWithValue("@id", row.id);
                        upd.ExecuteNonQuery();
                    }
                }

                // Рейтингs — rebuild
                using (var del = _conn.СоздатьCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM movie_ratings WHERE movie_id=@id";
                    del.Parameters.ДобавитьWithValue("@id", row.id);
                    del.ExecuteNonQuery();
                }
                foreach (var rt in p.Рейтингs)
                {
                    using var ins = _conn.СоздатьCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT OR REPLACE INTO movie_ratings VALUES (@m,@s,@v,@vt)";
                    ins.Parameters.ДобавитьWithValue("@m", row.id);
                    ins.Parameters.ДобавитьWithValue("@s", rt.Source);
                    ins.Parameters.ДобавитьWithValue("@v", rt.Value);
                    ins.Parameters.ДобавитьWithValue("@vt", (object?)rt.Votes ?? DBNull.Value);
                    ins.ExecuteNonQuery();
                }

                // Writers — rebuild
                using (var del = _conn.СоздатьCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM movie_writers WHERE movie_id=@id";
                    del.Parameters.ДобавитьWithValue("@id", row.id);
                    del.ExecuteNonQuery();
                }
                foreach (var name in p.Writers)
                {
                    var clean = name.Trim();
                    if (clean.Length == 0) continue;
                    if (!writerCache.TryGetValue(clean, out var wid))
                    {
                        using var ins = _conn.СоздатьCommand();
                        ins.Transaction = tx;
                        ins.CommandText = "INSERT INTO writers (name) VALUES (@n); SELECT last_insert_rowid();";
                        ins.Parameters.ДобавитьWithValue("@n", clean);
                        wid = Convert.ToInt32(ins.ExecuteScalar());
                        writerCache[clean] = wid;
                    }
                    using var link = _conn.СоздатьCommand();
                    link.Transaction = tx;
                    link.CommandText = "INSERT OR IGNORE INTO movie_writers VALUES (@m,@w)";
                    link.Parameters.ДобавитьWithValue("@m", row.id);
                    link.Parameters.ДобавитьWithValue("@w", wid);
                    link.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    private void SplitAndAliasЖанрыMigration()
    {
        // Shared map — single source of truth for alias folding.
        var aliases = GenreAliases.Map;
        static string CanonicalSeg(string s)
        {
            var n = s.Trim();
            n = System.Text.RegularExpressions.Regex.Replace(n, @"\s+", " ");
            return n;
        }

        var rows = new List<(int id, string name)>();
        using (var sel = _conn.СоздатьCommand())
        {
            sel.CommandText = "SELECT id, name FROM genres";
            using var r = sel.ExecuteReader();
            while (r.Read()) rows.Добавить((r.GetInt32(0), r.GetString(1)));
        }

        var canon = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in rows) canon[name] = id;

        using var tx = _conn.BeginTransaction();
        try
        {
            foreach (var (id, name) in rows)
            {
                var parts = name.Split(new[] { '/', ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(CanonicalSeg)
                                .Where(s => s.Length > 0)
                                .Select(s => aliases.TryGetValue(s, out var c) ? c : s)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();

                if (parts.Count == 1 && string.Equals(parts[0], name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (parts.Count == 0) continue;

                var partIds = new List<int>();
                foreach (var part in parts)
                {
                    if (!canon.TryGetValue(part, out var pid))
                    {
                        using var ins = _conn.СоздатьCommand();
                        ins.Transaction = tx;
                        ins.CommandText = "INSERT INTO genres (name) VALUES (@n); SELECT last_insert_rowid();";
                        ins.Parameters.ДобавитьWithValue("@n", part);
                        pid = Convert.ToInt32(ins.ExecuteScalar());
                        canon[part] = pid;
                    }
                    if (pid != id) partIds.Добавить(pid);
                }
                if (partIds.Count == 0) continue;

                var linkedФильмы = new List<int>();
                using (var s2 = _conn.СоздатьCommand())
                {
                    s2.Transaction = tx;
                    s2.CommandText = "SELECT movie_id FROM movie_genres WHERE genre_id=@id";
                    s2.Parameters.ДобавитьWithValue("@id", id);
                    using var rr = s2.ExecuteReader();
                    while (rr.Read()) linkedФильмы.Добавить(rr.GetInt32(0));
                }
                foreach (var mid in linkedФильмы)
                    foreach (var pid in partIds)
                    {
                        using var link = _conn.СоздатьCommand();
                        link.Transaction = tx;
                        link.CommandText = "INSERT OR IGNORE INTO movie_genres VALUES (@m, @g)";
                        link.Parameters.ДобавитьWithValue("@m", mid);
                        link.Parameters.ДобавитьWithValue("@g", pid);
                        link.ExecuteNonQuery();
                    }

                using (var del1 = _conn.СоздатьCommand())
                {
                    del1.Transaction = tx;
                    del1.CommandText = "DELETE FROM movie_genres WHERE genre_id=@id";
                    del1.Parameters.ДобавитьWithValue("@id", id);
                    del1.ExecuteNonQuery();
                }
                using (var del2 = _conn.СоздатьCommand())
                {
                    del2.Transaction = tx;
                    del2.CommandText = "DELETE FROM genres WHERE id=@id";
                    del2.Parameters.ДобавитьWithValue("@id", id);
                    del2.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    private void NormalizeAndMergeNames(string nameTable, string joinTable, string fkCol)
    {
        // 1. Snapshot all rows
        var rows = new List<(int id, string raw)>();
        using (var sel = _conn.СоздатьCommand())
        {
            sel.CommandText = $"SELECT id, name FROM {nameTable}";
            using var r = sel.ExecuteReader();
            while (r.Read()) rows.Добавить((r.GetInt32(0), r.GetString(1)));
        }

        // 2. Group by normalized lowercase name
        static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool prevWhite = false;
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!prevWhite && sb.Length > 0) sb.Append(' ');
                    prevWhite = true;
                }
                else { sb.Append(ch); prevWhite = false; }
            }
            while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
            return sb.ToString();
        }

        var groups = new Dictionary<string, List<(int id, string raw)>>();
        foreach (var row in rows)
        {
            var key = Normalize(row.raw).ToLowerInvariant();
            if (string.IsNullOrEmpty(key)) continue;
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = new();
            list.Добавить(row);
        }

        // 3 + 4. Вкл.e transaction for the whole table — large libraries can
        // produce thousands of UPDATEs, doing it in one tx is much faster
        // than per-row commits and either succeeds whole or rolls back.
        using var tx = _conn.BeginTransaction();
        try
        {
            foreach (var (key, list) in groups)
            {
                if (list.Count == 0) continue;
                // Оставитьer = lowest id; rest are duplicates
                list.Sort((a, b) => a.id.CompareTo(b.id));
                var keepId = list[0].id;
                var normalizedDisplay = Normalize(list[0].raw);

                for (int i = 1; i < list.Count; i++)
                {
                    var dupId = list[i].id;
                    // Repoint join rows to the keeper, ignoring those that
                    // would collide on (movie_id, fk) primary key — those
                    // are the same association via different ids and one
                    // row is enough.
                    using (var upd = _conn.СоздатьCommand())
                    {
                        upd.Transaction = tx;
                        upd.CommandText = $"UPDATE OR IGNORE {joinTable} SET {fkCol}=@k WHERE {fkCol}=@d";
                        upd.Parameters.ДобавитьWithValue("@k", keepId);
                        upd.Parameters.ДобавитьWithValue("@d", dupId);
                        upd.ExecuteNonQuery();
                    }
                    using (var del = _conn.СоздатьCommand())
                    {
                        del.Transaction = tx;
                        del.CommandText = $"DELETE FROM {joinTable} WHERE {fkCol}=@d";
                        del.Parameters.ДобавитьWithValue("@d", dupId);
                        del.ExecuteNonQuery();
                    }
                    using (var delName = _conn.СоздатьCommand())
                    {
                        delName.Transaction = tx;
                        delName.CommandText = $"DELETE FROM {nameTable} WHERE id=@d";
                        delName.Parameters.ДобавитьWithValue("@d", dupId);
                        delName.ExecuteNonQuery();
                    }
                }

                // Now safely update the keeper to its normalized display form
                // (no more collisions possible — duplicates are gone).
                if (normalizedDisplay != list[0].raw)
                {
                    using var updName = _conn.СоздатьCommand();
                    updName.Transaction = tx;
                    updName.CommandText = $"UPDATE {nameTable} SET name=@n WHERE id=@id";
                    updName.Parameters.ДобавитьWithValue("@n", normalizedDisplay);
                    updName.Parameters.ДобавитьWithValue("@id", keepId);
                    updName.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    private void СоздатьIndexes()
    {
        Exec(@"
CREATE INDEX IF NOT EXISTS idx_movies_title ON movies(title);
CREATE INDEX IF NOT EXISTS idx_movies_sort_title ON movies(sort_title);
CREATE INDEX IF NOT EXISTS idx_movies_year ON movies(year);
CREATE INDEX IF NOT EXISTS idx_movies_volume ON movies(volume_serial);
CREATE INDEX IF NOT EXISTS idx_movies_date_added ON movies(date_added DESC);
CREATE INDEX IF NOT EXISTS idx_movie_genres_genre_id ON movie_genres(genre_id);
CREATE INDEX IF NOT EXISTS idx_movie_genres_movie_id ON movie_genres(movie_id);
CREATE INDEX IF NOT EXISTS idx_movie_directors_director_id ON movie_directors(director_id);
CREATE INDEX IF NOT EXISTS idx_watchlist ON movies(is_watchlist) WHERE is_watchlist = 1;
CREATE INDEX IF NOT EXISTS idx_favorite ON movies(is_favorite) WHERE is_favorite = 1;
CREATE INDEX IF NOT EXISTS idx_watched ON movies(is_watched) WHERE is_watched = 1;
CREATE INDEX IF NOT EXISTS idx_last_played ON movies(last_played_at) WHERE last_played_at > 0;
CREATE INDEX IF NOT EXISTS idx_user_list_movies_movie ON user_list_movies(movie_id);
CREATE INDEX IF NOT EXISTS idx_user_list_movies_list ON user_list_movies(list_id);
-- v2.9 — TV ordering, tag reverse-lookup, episode last-played
CREATE INDEX IF NOT EXISTS idx_tv_shows_sort_title ON tv_shows(sort_title);
CREATE INDEX IF NOT EXISTS idx_tv_episodes_last_played ON tv_episodes(last_played_at) WHERE last_played_at > 0;
CREATE INDEX IF NOT EXISTS idx_movie_tags_tag ON movie_tags(tag_id);
CREATE INDEX IF NOT EXISTS idx_tv_show_tags_tag ON tv_show_tags(tag_id);
        ");
    }

    // ── Диски ───────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<Models.DriveInfo> GetДиски()
    {
        var list = new List<Models.DriveInfo>();
        var connected = GetConnectedДиски();

        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT d.volume_serial, d.label, d.last_seen_letter, d.movie_root_relative,
                   COUNT(m.id) as movie_count,
                   SUM(CASE WHEN m.is_missing=1 THEN 1 ELSE 0 END) as missing_count
            FROM drives d
            LEFT JOIN movies m ON m.volume_serial = d.volume_serial AND m.archived_at IS NULL
            WHERE d.volume_serial != '__archive__'
            GROUP BY d.volume_serial";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var serial = r.GetString(0);
            var letter = connected.TryGetValue(serial, out var l) ? l : null;
            list.Добавить(new Models.DriveInfo
            {
                VolumeSerial = serial,
                Label = r.GetString(1),
                LastSeenLetter = r.IsDBNull(2) ? null : r.GetString(2),
                MovieRootRelative = r.IsDBNull(3) ? "Фильмы" : r.GetString(3),
                IsConnected = letter != null,
                CurrentLetter = letter,
                MovieCount = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                MissingCount = r.IsDBNull(5) ? 0 : r.GetInt32(5),
            });
        }
        r.Закрыть();
        // v2.8 — TV show count per drive (separate query; cheap).
        var tvCounts = new Dictionary<string, int>();
        using (var tc = _conn.СоздатьCommand())
        {
            tc.CommandText = "SELECT volume_serial, COUNT(*) FROM tv_shows GROUP BY volume_serial";
            using var tr = tc.ExecuteReader();
            while (tr.Read()) tvCounts[tr.GetString(0)] = tr.GetInt32(1);
        }
        foreach (var d in list)
        {
            if (tvCounts.TryGetValue(d.VolumeSerial, out var n)) d.TvShowCount = n;
            d.Папкаs = GetDriveRoots(d.VolumeSerial);
        }
        return list;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<Models.DriveRoot> GetDriveRoots(string serial)
    {
        var list = new List<Models.DriveRoot>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT id, volume_serial, root_path FROM drive_roots WHERE volume_serial=@s ORDER BY root_path";
        cmd.Parameters.ДобавитьWithValue("@s", serial);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Добавить(new Models.DriveRoot
            {
                Id = r.GetInt32(0),
                VolumeSerial = r.GetString(1),
                RootPath = r.GetString(2),
            });
        }
        return list;
    }

    /// <summary>
    /// Удалить movies whose folder_rel_path isn't under any of the drive's
    /// configured drive_roots. Used to undo the v1.9.2 "Обновить изменения"
    /// bug that walked the whole drive and inserted episode .nfo files as
    /// fake movies. Returns count deleted. Also clears their cached artwork.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int RemoveФильмыOutsideDriveRoots(string serial)
    {
        var roots = GetDriveRoots(serial);
        if (roots.Count == 0) return 0;

        // Build the "keep" predicate
        var keepParts = new List<string>();
        var pars = new List<(string name, string val)>();
        for (int i = 0; i < roots.Count; i++)
        {
            var r = roots[i].RootPath ?? "";
            // Empty root_path means "whole drive" — match everything, nothing to remove
            if (string.IsNullOrEmpty(r)) return 0;
            keepParts.Добавить($"(folder_rel_path = @r{i} OR folder_rel_path LIKE @p{i})");
            pars.Добавить(($"@r{i}", r));
            pars.Добавить(($"@p{i}", r + "/%"));
        }
        var keepSql = string.Join(" OR ", keepParts);

        // First, collect cached artwork paths so we can clean them up
        using (var sel = _conn.СоздатьCommand())
        {
            sel.CommandText = $"SELECT local_poster, local_fanart, local_nfo FROM movies WHERE volume_serial=@s AND archived_at IS NULL AND NOT ({keepSql})";
            sel.Parameters.ДобавитьWithValue("@s", serial);
            foreach (var (n, v) in pars) sel.Parameters.ДобавитьWithValue(n, v);
            using var r = sel.ExecuteReader();
            while (r.Read())
            {
                for (int i = 0; i < 3; i++)
                {
                    if (!r.IsDBNull(i))
                    {
                        var p = Path.Combine(_dataDir, r.GetString(i));
                        if (File.Exists(p)) try { File.Удалить(p); } catch { }
                    }
                }
            }
        }

        using var del = _conn.СоздатьCommand();
        del.CommandText = $"DELETE FROM movies WHERE volume_serial=@s AND archived_at IS NULL AND NOT ({keepSql})";
        del.Parameters.ДобавитьWithValue("@s", serial);
        foreach (var (n, v) in pars) del.Parameters.ДобавитьWithValue(n, v);
        return del.ExecuteNonQuery();
    }

    /// <summary>Purge movies flagged is_missing=1 for this drive (plus their cached artwork). Returns count deleted.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int CleanupMissingФильмы(string serial)
    {
        // First, collect + delete cached artwork for missing rows
        using (var get = _conn.СоздатьCommand())
        {
            get.CommandText = "SELECT local_poster, local_fanart, local_nfo FROM movies WHERE volume_serial=@s AND is_missing=1 AND archived_at IS NULL";
            get.Parameters.ДобавитьWithValue("@s", serial);
            using var rr = get.ExecuteReader();
            while (rr.Read())
            {
                for (int i = 0; i < 3; i++)
                {
                    if (!rr.IsDBNull(i))
                    {
                        var p = Path.Combine(_dataDir, rr.GetString(i));
                        if (File.Exists(p)) try { File.Удалить(p); } catch { }
                    }
                }
            }
        }

        using var del = _conn.СоздатьCommand();
        del.CommandText = "DELETE FROM movies WHERE volume_serial=@s AND is_missing=1 AND archived_at IS NULL";
        del.Parameters.ДобавитьWithValue("@s", serial);
        return del.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ДобавитьDriveRoot(string serial, string relPath)
    {
        var norm = (relPath ?? "").Replace('\\', '/').Trim().TrimEnd('/');
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO drive_roots (volume_serial, root_path) VALUES (@s, @p)";
        cmd.Parameters.ДобавитьWithValue("@s", serial);
        cmd.Parameters.ДобавитьWithValue("@p", norm);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Removes a tracked folder and purges its movies (+ cached artwork) from the DB.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RemoveDriveRoot(string serial, string relPath)
    {
        var norm = (relPath ?? "").Replace('\\', '/').Trim().TrimEnd('/');

        // Удалить cached artwork for movies under this root
        using (var get = _conn.СоздатьCommand())
        {
            get.CommandText = @"SELECT local_poster, local_fanart, local_nfo FROM movies
                                WHERE volume_serial=@s AND archived_at IS NULL AND (folder_rel_path=@r OR folder_rel_path LIKE @p)";
            get.Parameters.ДобавитьWithValue("@s", serial);
            get.Parameters.ДобавитьWithValue("@r", norm);
            get.Parameters.ДобавитьWithValue("@p", norm + "/%");
            using var rr = get.ExecuteReader();
            while (rr.Read())
            {
                for (int i = 0; i < 3; i++)
                {
                    if (!rr.IsDBNull(i))
                    {
                        var p = Path.Combine(_dataDir, rr.GetString(i));
                        if (File.Exists(p)) try { File.Удалить(p); } catch { }
                    }
                }
            }
        }

        using var tx = _conn.BeginTransaction();
        using (var del1 = _conn.СоздатьCommand())
        {
            del1.Transaction = tx;
            del1.CommandText = @"DELETE FROM movies
                                 WHERE volume_serial=@s AND archived_at IS NULL AND (folder_rel_path=@r OR folder_rel_path LIKE @p)";
            del1.Parameters.ДобавитьWithValue("@s", serial);
            del1.Parameters.ДобавитьWithValue("@r", norm);
            del1.Parameters.ДобавитьWithValue("@p", norm + "/%");
            del1.ExecuteNonQuery();
        }
        using (var del2 = _conn.СоздатьCommand())
        {
            del2.Transaction = tx;
            del2.CommandText = "DELETE FROM drive_roots WHERE volume_serial=@s AND root_path=@p";
            del2.Parameters.ДобавитьWithValue("@s", serial);
            del2.Parameters.ДобавитьWithValue("@p", norm);
            del2.ExecuteNonQuery();
        }
        tx.Commit();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public Dictionary<string, string> GetConnectedДиски()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in global::System.IO.DriveInfo.GetДиски())
        {
            if (!drive.IsReady) continue;
            try
            {
                var serial = GetVolumeSerial(drive.Name);
                if (serial != null)
                    result[serial] = drive.Name.TrimEnd('\\').TrimEnd(':');
            }
            catch { /* ignore */ }
        }
        return result;
    }

    public static string? GetVolumeSerial(string drivePath)
    {
        try
        {
            uint serial = 0, maxLen = 0, flags = 0;
            var sb = new StringBuilder(256);
            if (GetVolumeInformation(drivePath, sb, 256, out serial, out maxLen, out flags, null, 0))
                return serial.ToString("X8");
        }
        catch { }
        return null;
    }

    [System.Продолжительность.InteropServices.DllImport("kernel32.dll", CharSet = System.Продолжительность.InteropServices.CharSet.Auto, SetLastОшибка = true)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName, 
        [System.Продолжительность.InteropServices.Out] StringBuilder lpVolumeNameBuffer, 
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber, 
        out uint lpMaximumComponentLength, 
        out uint lpFileSystemFlags,
        [System.Продолжительность.InteropServices.Out] StringBuilder? lpFileSystemNameBuffer, 
        int nFileSystemNameSize);

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ДобавитьDrive(string volumeSerial, string label, string lastSeenLetter)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"INSERT OR IGNORE INTO drives (volume_serial, label, last_seen_letter, movie_root_relative)
                            VALUES (@s, @l, @let, 'Фильмы')";
        cmd.Parameters.ДобавитьWithValue("@s", volumeSerial);
        cmd.Parameters.ДобавитьWithValue("@l", label);
        cmd.Parameters.ДобавитьWithValue("@let", lastSeenLetter);
        cmd.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ОбновитьDriveLastSeen(string serial, string letter)
    {
        // v2.6 — fire-and-forget cosmetic bookkeeping. busy_timeout already
        // makes us wait up to 5 s for the write lock; if the scanner is
        // doing a long transaction and we still can't squeeze in, just skip
        // the update — the next RefreshConnected (next drive event or
        // sidebar nav) will pick it up. Better than crashing the UI.
        try
        {
            using var cmd = _conn.СоздатьCommand();
            cmd.CommandText = "UPDATE drives SET last_seen_letter=@l, last_connected_at=strftime('%s','now') WHERE volume_serial=@s";
            cmd.Parameters.ДобавитьWithValue("@l", letter);
            cmd.Parameters.ДобавитьWithValue("@s", serial);
            cmd.ExecuteNonQuery();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // SQLITE_BUSY after the 5 s wait — scan still running. Skip.
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RenameDrive(string serial, string newLabel)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "UPDATE drives SET label=@l WHERE volume_serial=@s";
        cmd.Parameters.ДобавитьWithValue("@l", newLabel);
        cmd.Parameters.ДобавитьWithValue("@s", serial);
        cmd.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RemoveDrive(string serial)
    {
        // Clean up cached image files (archived records keep theirs — the
        // poster IS the record).
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT local_poster, local_fanart, local_nfo FROM movies WHERE volume_serial=@s AND archived_at IS NULL";
        cmd.Parameters.ДобавитьWithValue("@s", serial);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            foreach (var idx in new[] { 0, 1, 2 })
            {
                if (!r.IsDBNull(idx))
                {
                    var p = Path.Combine(_dataDir, r.GetString(idx));
                    if (File.Exists(p)) try { File.Удалить(p); } catch { }
                }
            }
        }
        r.Закрыть();

        // v3.3 — archived records must outlive the drive (movies cascade
        // with their drive row). Re-point them to the hidden sentinel.
        using (var move = _conn.СоздатьCommand())
        {
            move.CommandText = "UPDATE movies SET volume_serial='__archive__' WHERE volume_serial=@s AND archived_at IS NOT NULL";
            move.Parameters.ДобавитьWithValue("@s", serial);
            move.ExecuteNonQuery();
        }

        using var del = _conn.СоздатьCommand();
        del.CommandText = "DELETE FROM drives WHERE volume_serial=@s";
        del.Parameters.ДобавитьWithValue("@s", serial);
        del.ExecuteNonQuery();
    }

    // ── Фильмы List ──────────────────────────────────────────────────────────

    public record ListOptions(
        string? Поиск = null,
        string ПоискScope = "all",     // v3.1 — all | title | cast
        string SortKey = "title",
        string SortDir = "asc",
        string? DriveSerial = null,
        string? Genre = null,
        string? Actor = null,
        string? Режиссёр = null,
        string? Студия = null,
        int? CollectionId = null,
        string ПросмотреноFilter = "all",   // all | watched | unwatched
        bool ИзбранноеВкл.ly = false,
        bool IsСписок просмотраВкл.ly = false,
        bool ContinueWatching = false,  // true = last_played_at > 0 AND is_watched = 0
        bool RecentlyПросмотреноВкл.ly = false, // v2.9 — true = last_played_at > 0 (any watched state)
        bool RecentlyДобавитьedВкл.ly = false,   // v2.9.0 fix — true = restrict to top N most-recently-added
        int RecentlyДобавитьedTopN = 50,
        bool HasNoteВкл.ly = false,       // v2.8.2 — only movies carrying a note
        int? UserListId = null,
        int? TagId = null,              // v2.9 — filter movies by tag id
        int? DecadeStart = null,        // e.g. 1980 → year in [1980..1989]
        string? РейтингBand = null,      // bucket key: "9","8","7","6","5","0"
        bool ArchivedВкл.ly = false,      // v3.3 — Просмотрено и удалено page
        int Limit = 60,
        int Выкл.set = 0
    );

    /// <summary>
    /// Total rows that would match these ListOptions ignoring Limit/Выкл.set.
    /// Used for the "X of Y movies" header so we don't mislead the user
    /// with the per-page Фильмы.Count.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int GetФильмыCount(ListOptions opts)
    {
        var (whereStr, _) = BuildMovieListWhere(opts);
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM movies m {whereStr}";
        BindMovieListParams(cmd, opts, includePaging: false);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Split the user's search query into whitespace tokens, dropping empties.
    /// Each token is then AND'ed in the SQL so multi-word queries like
    /// "iron man 2008" match titles in any order with punctuation between
    /// them — e.g. "Iron Man (2008)" or "The Iron Man — 2008 Remaster".
    /// </summary>
    private static string[] TokenizeПоиск(string s) =>
        s.Trim().Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Escape SQL LIKE wildcards in a user-provided token. Without this,
    /// typing "100%" or "snake_case" would behave as wildcards instead of
    /// literals. Combined with `ESCAPE '\'` in the LIKE clause.
    /// </summary>
    private static string EscapeLike(string s) => s
        .Replace(@"\", @"\\")
        .Replace("%", @"\%")
        .Replace("_", @"\_");

    private (string whereStr, List<string> clauses) BuildMovieListWhere(ListOptions opts)
    {
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(opts.Поиск))
        {
            var ftsMatch = FtsMatchFor(opts);
            if (ftsMatch != null)
            {
                // v3.1 — FTS5 path: word-aware, ranked, fast. The scope is
                // baked into the MATCH expression (see FtsMatchFor).
                where.Добавить("m.id IN (SELECT rowid FROM movies_fts WHERE movies_fts MATCH @ftsq)");
            }
            else
            {
                // Fallback LIKE path (used only if FTS5 isn't available). Plot
                // and tagline are intentionally excluded — a short word like
                // "hit" appears in hundreds of plot summaries and floods the
                // results. Scope is honoured at a basic level.
                var tokens = TokenizeПоиск(opts.Поиск);
                for (int i = 0; i < tokens.Length; i++)
                {
                    var p = $"@q{i}";
                    var titleClause = $"m.title LIKE {p} ESCAPE '\\' OR m.original_title LIKE {p} ESCAPE '\\'";
                    var castClause =
                        $"EXISTS (SELECT 1 FROM movie_actors ma JOIN actors a ON a.id=ma.actor_id WHERE ma.movie_id=m.id AND a.name LIKE {p} ESCAPE '\\')"
                      + $" OR EXISTS (SELECT 1 FROM movie_directors md JOIN directors d ON d.id=md.director_id WHERE md.movie_id=m.id AND d.name LIKE {p} ESCAPE '\\')";
                    string clause = opts.ПоискScope switch
                    {
                        "title" => titleClause,
                        "cast"  => castClause,
                        _        => $"{titleClause} OR АКТЁРЫ(m.year AS TEXT) LIKE {p} ESCAPE '\\' OR {castClause}"
                                  + $" OR EXISTS (SELECT 1 FROM movie_sets ms JOIN sets s ON s.id=ms.set_id WHERE ms.movie_id=m.id AND s.name LIKE {p} ESCAPE '\\')",
                    };
                    where.Добавить($"({clause})");
                }
            }
        }
        if (opts.DriveSerial != null) where.Добавить("m.volume_serial=@serial");
        if (opts.Genre != null) where.Добавить("EXISTS (SELECT 1 FROM movie_genres mg JOIN genres g ON g.id=mg.genre_id WHERE mg.movie_id=m.id AND g.name=@genre)");
        if (opts.Actor != null) where.Добавить("EXISTS (SELECT 1 FROM movie_actors ma JOIN actors a ON a.id=ma.actor_id WHERE ma.movie_id=m.id AND LOWER(a.name)=LOWER(@actor))");
        if (opts.Режиссёр != null) where.Добавить("EXISTS (SELECT 1 FROM movie_directors md JOIN directors d ON d.id=md.director_id WHERE md.movie_id=m.id AND LOWER(d.name)=LOWER(@director))");
        if (opts.Студия != null) where.Добавить("LOWER(m.studio)=LOWER(@studio)");
        if (opts.CollectionId != null) where.Добавить("EXISTS (SELECT 1 FROM movie_sets ms WHERE ms.movie_id=m.id AND ms.set_id=@colId)");
        if (opts.ПросмотреноFilter == "watched") where.Добавить("m.is_watched=1");
        else if (opts.ПросмотреноFilter == "unwatched") where.Добавить("m.is_watched=0");
        if (opts.ИзбранноеВкл.ly) where.Добавить("m.is_favorite=1");
        if (opts.IsСписок просмотраВкл.ly) where.Добавить("m.is_watchlist=1");
        if (opts.HasNoteВкл.ly) where.Добавить("m.note IS NOT NULL AND TRIM(m.note) != ''");
        if (opts.ContinueWatching) where.Добавить("m.last_played_at > 0 AND m.is_watched = 0");
        if (opts.RecentlyПросмотреноВкл.ly) where.Добавить("m.last_played_at > 0");
        if (opts.RecentlyДобавитьedВкл.ly)
            where.Добавить($"m.id IN (SELECT id FROM movies WHERE is_missing=0 AND archived_at IS NULL ORDER BY date_added DESC, id DESC LIMIT {Math.Max(1, opts.RecentlyДобавитьedTopN)})");
        if (opts.UserListId != null) where.Добавить("EXISTS (SELECT 1 FROM user_list_movies ulm WHERE ulm.movie_id=m.id AND ulm.list_id=@listId)");
        if (opts.TagId != null) where.Добавить("EXISTS (SELECT 1 FROM movie_tags mt WHERE mt.movie_id=m.id AND mt.tag_id=@tagId)");
        if (opts.DecadeStart != null)
            where.Добавить("m.year >= @decLo AND m.year <= @decHi");
        if (opts.РейтингBand != null)
            where.Добавить("m.rating >= @ratLo AND m.rating < @ratHi");
        // v3.3 — Просмотрено и удалено isolation. Every live-library query excludes
        // archived rows; the W&G page flips the flag to see ONLY them. Always
        // present, so a missed call site can't leak archived movies.
        where.Добавить(opts.ArchivedВкл.ly ? "m.archived_at IS NOT NULL" : "m.archived_at IS NULL");
        return ("WHERE " + string.Join(" AND ", where), where);
    }

    private void BindMovieListParams(SqliteCommand cmd, ListOptions opts, bool includePaging)
    {
        if (!string.IsNullOrWhiteSpace(opts.Поиск))
        {
            var ftsMatch = FtsMatchFor(opts);
            if (ftsMatch != null)
            {
                cmd.Parameters.ДобавитьWithValue("@ftsq", ftsMatch);
            }
            else
            {
                var tokens = TokenizeПоиск(opts.Поиск);
                for (int i = 0; i < tokens.Length; i++)
                    cmd.Parameters.ДобавитьWithValue($"@q{i}", $"%{EscapeLike(tokens[i])}%");
            }
        }
        if (opts.DriveSerial != null) cmd.Parameters.ДобавитьWithValue("@serial", opts.DriveSerial);
        if (opts.Genre != null) cmd.Parameters.ДобавитьWithValue("@genre", opts.Genre);
        if (opts.Actor != null) cmd.Parameters.ДобавитьWithValue("@actor", opts.Actor);
        if (opts.Режиссёр != null) cmd.Parameters.ДобавитьWithValue("@director", opts.Режиссёр);
        if (opts.Студия != null) cmd.Parameters.ДобавитьWithValue("@studio", opts.Студия);
        if (opts.CollectionId != null) cmd.Parameters.ДобавитьWithValue("@colId", opts.CollectionId);
        if (opts.UserListId != null) cmd.Parameters.ДобавитьWithValue("@listId", opts.UserListId);
        if (opts.TagId != null) cmd.Parameters.ДобавитьWithValue("@tagId", opts.TagId);
        if (opts.DecadeStart != null)
        {
            cmd.Parameters.ДобавитьWithValue("@decLo", opts.DecadeStart.Value);
            cmd.Parameters.ДобавитьWithValue("@decHi", opts.DecadeStart.Value + 9);
        }
        if (opts.РейтингBand != null)
        {
            var (lo, hi) = opts.РейтингBand switch
            {
                "9" => (9.0, 10.1),
                "8" => (8.0, 9.0),
                "7" => (7.0, 8.0),
                "6" => (6.0, 7.0),
                "5" => (5.0, 6.0),
                _   => (0.0, 5.0),
            };
            cmd.Parameters.ДобавитьWithValue("@ratLo", lo);
            cmd.Parameters.ДобавитьWithValue("@ratHi", hi);
        }
        if (includePaging)
        {
            cmd.Parameters.ДобавитьWithValue("@lim", opts.Limit);
            cmd.Parameters.ДобавитьWithValue("@off", opts.Выкл.set);
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<MovieListItem> GetФильмы(ListOptions opts, Dictionary<string, string> connected)
    {
        var (whereStr, _) = BuildMovieListWhere(opts);
        var sortCol = opts.SortKey switch
        {
            "year" => "m.year",
            "rating" => "m.rating",
            "runtime" => "m.runtime",
            "date_added" => "m.date_added",
            "last_played" => "m.last_played_at",
            "archived" => "m.archived_at",   // v3.3 — Просмотрено и удалено "recently gone"
            _ => "m.sort_title"
        };
        var sortDir = opts.SortDir == "desc" ? "DESC" : "ASC";
        var nullsLast = sortDir == "ASC" ? "NULLS LAST" : "NULLS FIRST";

        // Поиск relevance ordering.
        //  • FTS path: rank by bm25 with title weighted highest, plot lowest,
        //    so the best textual match floats to the top.
        //  • LIKE fallback: a simple CASE — title prefix, then title contains,
        //    then everything else.
        bool hasПоиск = !string.IsNullOrWhiteSpace(opts.Поиск);
        var ftsMatch = FtsMatchFor(opts);
        bool useFts = ftsMatch != null;

        var searchJoin = useFts
            ? @"LEFT JOIN (
                   SELECT rowid AS fid, bm25(movies_fts, 12.0, 8.0, 4.0, 2.0, 1.0) AS frank
                     FROM movies_fts WHERE movies_fts MATCH @ftsq
                ) fr ON fr.fid = m.id"
            : "";

        var relevanceOrder =
            useFts ? "fr.frank ASC, "
          : hasПоиск
                ? @"CASE
                        WHEN m.title LIKE @relPrefix   ESCAPE '\' THEN 0
                        WHEN m.title LIKE @relContains ESCAPE '\' THEN 1
                        ELSE 2
                      END, "
          : "";

        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = $@"
            SELECT m.id, m.title, m.year, m.rating, m.runtime, m.local_poster,
                   m.is_missing, m.is_favorite, m.is_watched, m.volume_serial, d.label,
                   (SELECT GROUP_CONCAT(g.name, ', ')
                    FROM movie_genres mg JOIN genres g ON g.id=mg.genre_id
                    WHERE mg.movie_id=m.id) as genres_csv,
                   m.is_watchlist
            FROM movies m
            LEFT JOIN drives d ON d.volume_serial=m.volume_serial
            {searchJoin}
            {whereStr}
            ORDER BY {relevanceOrder}{sortCol} {sortDir} {nullsLast}, m.sort_title ASC
            LIMIT @lim OFFSET @off";

        BindMovieListParams(cmd, opts, includePaging: true);
        if (hasПоиск && !useFts)
        {
            var clean = EscapeLike((opts.Поиск ?? "").Trim());
            cmd.Parameters.ДобавитьWithValue("@relPrefix", clean + "%");
            cmd.Parameters.ДобавитьWithValue("@relContains", "%" + clean + "%");
        }

        var list = new List<MovieListItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var serial = r.GetString(9);
            list.Добавить(new MovieListItem
            {
                Id = r.GetInt32(0),
                Название = r.GetString(1),
                Год = r.IsDBNull(2) ? null : r.GetInt32(2),
                Рейтинг = r.IsDBNull(3) ? null : r.GetDouble(3),
                Продолжительность = r.IsDBNull(4) ? null : r.GetInt32(4),
                LocalPoster = r.IsDBNull(5) ? null : r.GetString(5),
                IsMissing = r.GetInt32(6) == 1,
                IsИзбранное = r.GetInt32(7) == 1,
                IsПросмотрено = r.GetInt32(8) == 1,
                VolumeSerial = serial,
                DriveLabel = r.IsDBNull(10) ? null : r.GetString(10),
                ЖанрыCsv = r.IsDBNull(11) ? null : r.GetString(11),
                IsСписок просмотра = !r.IsDBNull(12) && r.GetInt32(12) == 1,
                IsВкл.line = connected.ContainsKey(serial),
            });
        }
        return list;
    }

    // ── Movie Detail ─────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.Synchronized)]
    public MovieDetail? GetMovieDetail(int id, Dictionary<string, string> connected)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT m.id, m.title, m.original_title, m.year, m.rating, m.runtime,
                   m.plot, m.tagline, m.mpaa, m.imdb_id, m.premiered, m.studio, m.country,
                   m.local_poster, m.local_fanart, m.is_missing, m.is_favorite, m.is_watched,
                   m.is_watchlist, m.volume_serial, d.label, m.folder_rel_path, m.video_file_rel_path,
                   m.outline, m.note, m.trailer,
                   m.video_width, m.video_height, m.video_codec, m.video_aspect, m.hdr_type,
                   m.audio_codec, m.audio_channels, m.audio_languages, m.subtitle_languages,
                   m.duration_seconds, m.container_ext, m.file_size_bytes,
                   m.tmdb_id
            FROM movies m LEFT JOIN drives d ON d.volume_serial=m.volume_serial
            WHERE m.id=@id";
        cmd.Parameters.ДобавитьWithValue("@id", id);

        MovieDetail? movie = null;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            var serial = r.GetString(19);
            var letter = connected.TryGetValue(serial, out var l) ? l : null;
            var folderRel = r.IsDBNull(21) ? null : r.GetString(21);
            var videoRel = r.IsDBNull(22) ? null : r.GetString(22);
            var playable = letter != null && videoRel != null && !r.GetBoolean(15);

            movie = new MovieDetail
            {
                Id = r.GetInt32(0),
                Название = r.GetString(1),
                OriginalНазвание = r.IsDBNull(2) ? null : r.GetString(2),
                Год = r.IsDBNull(3) ? null : r.GetInt32(3),
                Рейтинг = r.IsDBNull(4) ? null : r.GetDouble(4),
                Продолжительность = r.IsDBNull(5) ? null : r.GetInt32(5),
                Plot = r.IsDBNull(6) ? null : r.GetString(6),
                Tagline = r.IsDBNull(7) ? null : r.GetString(7),
                Mpaa = r.IsDBNull(8) ? null : r.GetString(8),
                ImdbId = r.IsDBNull(9) ? null : r.GetString(9),
                Premiered = r.IsDBNull(10) ? null : r.GetString(10),
                Студия = r.IsDBNull(11) ? null : r.GetString(11),
                Страна = r.IsDBNull(12) ? null : r.GetString(12),
                LocalPoster = r.IsDBNull(13) ? null : r.GetString(13),
                LocalFanart = r.IsDBNull(14) ? null : r.GetString(14),
                IsMissing = r.GetInt32(15) == 1,
                IsИзбранное = r.GetInt32(16) == 1,
                IsПросмотрено = r.GetInt32(17) == 1,
                IsСписок просмотра = r.GetInt32(18) == 1,
                VolumeSerial = serial,
                DriveLabel = r.IsDBNull(20) ? null : r.GetString(20),
                CurrentLetter = letter,
                IsВкл.line = letter != null,
                Воспроизвестиable = playable,
                ПапкаRelPath = folderRel,
                VideoFileRelPath = videoRel,
                Outline = r.IsDBNull(23) ? null : r.GetString(23),
                Note = r.IsDBNull(24) ? null : r.GetString(24),
                Trailer = r.IsDBNull(25) ? null : r.GetString(25),
                VideoWidth        = r.IsDBNull(26) ? null : r.GetInt32(26),
                VideoHeight       = r.IsDBNull(27) ? null : r.GetInt32(27),
                VideoCodec        = r.IsDBNull(28) ? null : r.GetString(28),
                VideoAspect       = r.IsDBNull(29) ? null : r.GetString(29),
                HdrType           = r.IsDBNull(30) ? null : r.GetString(30),
                АудиоCodec        = r.IsDBNull(31) ? null : r.GetString(31),
                АудиоChannels     = r.IsDBNull(32) ? null : r.GetString(32),
                АудиоLanguages    = r.IsDBNull(33) ? null : r.GetString(33),
                SubtitleLanguages = r.IsDBNull(34) ? null : r.GetString(34),
                DurationSeconds   = r.IsDBNull(35) ? null : r.GetInt32(35),
                ContainerExt      = r.IsDBNull(36) ? null : r.GetString(36),
                FileSizeBytes     = r.IsDBNull(37) ? null : r.GetInt64(37),
                TmdbId            = r.IsDBNull(38) ? null : r.GetString(38),
            };
        }

        // Load related data
        movie.Жанры = GetMovieЖанры(id);
        movie.Режиссёрs = GetMovieРежиссёрs(id);
        movie.Writers = GetMovieWriters(id);
        movie.Actors = GetMovieActors(id);
        movie.Sets = GetMovieSets(id);
        movie.ВсеРейтингs = GetMovieРейтингs(id);
        movie.Tags = GetTagNamesForMovie(id);
        return movie;
    }

    private List<string> GetMovieWriters(int id)
    {
        var list = new List<string>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT w.name FROM movie_writers mw JOIN writers w ON w.id=mw.writer_id WHERE mw.movie_id=@id";
        cmd.Parameters.ДобавитьWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Добавить(r.GetString(0));
        return list;
    }

    private List<(string Source, double Value, int? Votes)> GetMovieРейтингs(int id)
    {
        var list = new List<(string, double, int?)>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT source, value, votes FROM movie_ratings WHERE movie_id=@id ORDER BY value DESC";
        cmd.Parameters.ДобавитьWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Добавить((r.GetString(0), r.GetDouble(1), r.IsDBNull(2) ? (int?)null : r.GetInt32(2)));
        return list;
    }

    private List<string> GetMovieЖанры(int id)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT g.name FROM movie_genres mg JOIN genres g ON g.id=mg.genre_id WHERE mg.movie_id=@id";
        cmd.Parameters.ДобавитьWithValue("@id", id);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Добавить(r.GetString(0));
        return list;
    }

    private List<string> GetMovieРежиссёрs(int id)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT d.name FROM movie_directors md JOIN directors d ON d.id=md.director_id WHERE md.movie_id=@id";
        cmd.Parameters.ДобавитьWithValue("@id", id);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Добавить(r.GetString(0));
        return list;
    }

    private List<Actor> GetMovieActors(int id)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT a.name, ma.role, a.thumb, ma.sort_order FROM movie_actors ma JOIN actors a ON a.id=ma.actor_id WHERE ma.movie_id=@id ORDER BY ma.sort_order LIMIT 12";
        cmd.Parameters.ДобавитьWithValue("@id", id);
        var list = new List<Actor>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Добавить(new Actor
            {
                Name = r.GetString(0),
                Role = r.IsDBNull(1) ? null : r.GetString(1),
                Thumb = r.IsDBNull(2) ? null : r.GetString(2),
                SortOrder = r.GetInt32(3),
            });
        }
        return list;
    }

    private List<string> GetMovieSets(int id)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT s.name FROM movie_sets ms JOIN sets s ON s.id=ms.set_id WHERE ms.movie_id=@id";
        cmd.Parameters.ДобавитьWithValue("@id", id);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Добавить(r.GetString(0));
        return list;
    }

    // ── Mutations ────────────────────────────────────────────────────────────
    //
    // v2.7 — every personal-state mutation fires PersonalStateChanged(id)
    // so AppState can mirror the new state into the per-movie sidecar file
    // (cinelibrary-state.json) for portability. Subscribers run on the
    // calling thread — keep them cheap or hand off to a background queue.

    public event Action<int>? PersonalStateChanged;
    private void RaisePersonalStateChanged(int id)
    {
        try { PersonalStateChanged?.Invoke(id); } catch { /* sidecar is best-effort */ }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ToggleИзбранное(int id)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "UPDATE movies SET is_favorite = 1 - is_favorite WHERE id=@id";
        cmd.Parameters.ДобавитьWithValue("@id", id);
        cmd.ExecuteNonQuery();
        RaisePersonalStateChanged(id);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ToggleПросмотрено(int id)
    {
        int newState = 0;
        using (var cmd = _conn.СоздатьCommand())
        {
            // RETURNING gives us the post-toggle value so we can log the
            // right event (and bump last_played_at only on 0→1).
            cmd.CommandText = @"UPDATE movies SET
                                    is_watched = 1 - is_watched,
                                    last_played_at = CASE
                                        WHEN is_watched = 0 THEN strftime('%s','now')
                                        ELSE last_played_at END
                                WHERE id=@id
                                RETURNING is_watched";
            cmd.Parameters.ДобавитьWithValue("@id", id);
            var o = cmd.ExecuteScalar();
            if (o != null && o != DBNull.Value) newState = Convert.ToInt32(o);
        }
        LogWatchEvent("movie", id, newState == 1 ? "marked_watched" : "marked_unwatched");
        RaisePersonalStateChanged(id);
    }

    // ── Просмотрено и удалено (v3.3) ────────────────────────────────────────────────
    // A movie the user watched and then deleted from disk can be kept as a
    // permanent record — poster, metadata, notes, tags and watch history all
    // stay on the row; only archived_at flips. Records live outside the main
    // library (see BuildMovieListWhere) and survive drive removal.

    /// <summary>Send movies to Просмотрено и удалено. No-op for already-archived ids.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int ArchiveФильмы(IReadВкл.lyCollection<int> ids)
    {
        if (ids.Count == 0) return 0;
        var inList = string.Join(",", ids);
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = $"UPDATE movies SET archived_at=strftime('%s','now') WHERE id IN ({inList}) AND archived_at IS NULL";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Создатьs a Просмотрено и удалено record from scratch — a movie the user
    /// watched but never had in the library (no file on any drive). Lives on the
    /// '__archive__' sentinel drive with a synthetic, collision-proof
    /// folder_rel_path so a real scan can never match or disturb it. Marked
    /// watched, archived now, and (optionally) given a note, tags, and a dated
    /// watch event. Returns the new movie id.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int InsertПросмотреноGoneRecord(
        string title, int? year, double? rating, int? votes, int? runtime,
        string? plot, string? tagline, string? mpaa, string? imdbId, string? tmdbId,
        string? premiered, string? studio, string? country,
        string? posterRelPath, string? note, IEnumerable<string>? tags, long watchedAtUnix)
    {
        // Synthetic path: unique per record, namespaced so it's obvious in the
        // DB and can never equal a real "<folder>" produced by the scanner.
        var folderRel = "__manual__/" + Guid.НовыйGuid().ToString("N");

        int newId;
        using (var cmd = _conn.СоздатьCommand())
        {
            cmd.CommandText = @"
                INSERT INTO movies
                    (volume_serial, folder_rel_path, title, year, rating, votes, runtime,
                     plot, tagline, mpaa, imdb_id, tmdb_id, premiered, studio, country,
                     local_poster, note, is_missing, is_watched, archived_at)
                VALUES
                    ('__archive__', @fr, @t, @y, @ra, @vo, @ru,
                     @pl, @tg, @mp, @im, @tm, @pr, @su, @co,
                     @lp, @nt, 0, 1, strftime('%s','now'))
                RETURNING id";
            cmd.Parameters.ДобавитьWithValue("@fr", folderRel);
            cmd.Parameters.ДобавитьWithValue("@t", title);
            cmd.Parameters.ДобавитьWithValue("@y", (object?)year ?? DBNull.Value);
            cmd.Parameters.ДобавитьWithValue("@ra", (object?)rating ?? DBNull.Value);
            cmd.Parameters.ДобавитьWithValue("@vo", (object?)votes ?? DBNull.Value);
            cmd.Parameters.ДобавитьWithValue("@ru", (object?)runtime ?? DBNull.Value);
            cmd.Parameters.ДобавитьWithValue("@pl", (object?)plot ?? DBNull.Value);
            cmd.Parameters.ДобавитьWithValue("@tg", (object?)tagline ?? DBNull.Value);
            cmd.Parameters.ДобавитьWithValue("@mp", (object?)mpaa ?? DBNull.Value);
            cmd.Parameters.ДобавитьWithValue("@im", (object?)imdbId ?? DBNull.Value);
            cmd.Parameters.ДобавитьWithValue("@tm", (object?)tmdbId ?? DBNull.Value);
            cmd.Parameters.ДобавитьWithValue("@pr", (object?)premiered ?? DBNull.Value);
            cmd.Parameters.ДобавитьWithValue("@su", (object?)studio ?? DBNull.Value);
            cmd.Parameters.ДобавитьWithValue("@co", (object?)country ?? DBNull.Value);
            cmd.Parameters.ДобавитьWithValue("@lp", (object?)posterRelPath ?? DBNull.Value);
            cmd.Parameters.ДобавитьWithValue("@nt",
                string.IsNullOrWhiteSpace(note) ? (object)DBNull.Value : note.Trim());
            newId = Convert.ToInt32(cmd.ExecuteScalar());
        }

        // Tags — reuse the shared tag rows so manual records mingle with the rest.
        if (tags != null)
            foreach (var raw in tags)
            {
                var clean = (raw ?? "").Trim();
                if (clean.Length == 0) continue;
                ДобавитьMovieTag(newId, EnsureTag(clean));
            }

        // A dated watch event so the record reads like any other watched movie.
        try
        {
            using var ev = _conn.СоздатьCommand();
            ev.CommandText = @"INSERT INTO watch_events(item_kind, item_id, watched_at, action)
                               VALUES('movie', @id, @when, 'watched')";
            ev.Parameters.ДобавитьWithValue("@id", newId);
            ev.Parameters.ДобавитьWithValue("@when", watchedAtUnix);
            ev.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"InsertПросмотреноGoneRecord watch event failed: {ex.Message}");
        }

        return newId;
    }

    /// <summary>
    /// v3.4 — fill ONLY the blank fields of an existing movie from a TMDB fetch.
    /// Every column is written with a fill-only guard (numeric COALESCE / text
    /// emptiness check), so anything the user or MediaElch already set is never
    /// overwritten. Актёры is handled separately (see <see cref="ДобавитьManualActors"/>),
    /// only when the movie currently has none. Returns true if any row changed.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool FillMovieGaps(
        int id, int? year, double? rating, int? votes, int? runtime,
        string? plot, string? tagline, string? mpaa, string? imdbId, string? tmdbId,
        string? premiered, string? studio, string? country,
        string? posterRel, string? fanartRel)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            UPDATE movies SET
                year         = COALESCE(year, @y),
                rating       = COALESCE(rating, @ra),
                votes        = COALESCE(votes, @vo),
                runtime      = COALESCE(runtime, @ru),
                plot         = CASE WHEN plot      IS NULL OR plot=''      THEN @pl ELSE plot      END,
                tagline      = CASE WHEN tagline   IS NULL OR tagline=''   THEN @tg ELSE tagline   END,
                mpaa         = CASE WHEN mpaa      IS NULL OR mpaa=''      THEN @mp ELSE mpaa      END,
                imdb_id      = CASE WHEN imdb_id   IS NULL OR imdb_id=''   THEN @im ELSE imdb_id   END,
                tmdb_id      = CASE WHEN tmdb_id   IS NULL OR tmdb_id=''   THEN @tm ELSE tmdb_id   END,
                premiered    = CASE WHEN premiered IS NULL OR premiered='' THEN @pr ELSE premiered END,
                studio       = CASE WHEN studio    IS NULL OR studio=''    THEN @su ELSE studio    END,
                country      = CASE WHEN country   IS NULL OR country=''   THEN @co ELSE country   END,
                local_poster = CASE WHEN local_poster IS NULL OR local_poster='' THEN @lp ELSE local_poster END,
                local_fanart = CASE WHEN local_fanart IS NULL OR local_fanart='' THEN @lf ELSE local_fanart END,
                date_modified = strftime('%s','now')
            WHERE id=@id";
        cmd.Parameters.ДобавитьWithValue("@id", id);
        cmd.Parameters.ДобавитьWithValue("@y", (object?)year ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ra", (object?)rating ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@vo", (object?)votes ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ru", (object?)runtime ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@pl", (object?)plot ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@tg", (object?)tagline ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@mp", (object?)mpaa ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@im", (object?)imdbId ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@tm", (object?)tmdbId ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@pr", (object?)premiered ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@su", (object?)studio ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@co", (object?)country ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@lp", (object?)posterRel ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@lf", (object?)fanartRel ?? DBNull.Value);
        var changed = cmd.ExecuteNonQuery() > 0;
        if (changed) RaisePersonalStateChanged(id);
        return changed;
    }

    /// <summary>Does this movie currently have any cast rows? Used to decide
    /// whether a TMDB fetch should fill in the cast.</summary>
    public bool MovieHasActors(int id)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM movie_actors WHERE movie_id=@id)";
        cmd.Parameters.ДобавитьWithValue("@id", id);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    /// <summary>
    /// v3.4 — fill genres / directors / writers from a TMDB fetch, each
    /// independently and fill-only: a relation is added only when the movie
    /// currently has none of that kind, so existing MediaElch data is untouched.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void FillMovieGenreРежиссёрWriter(
        int movieId, IReadВкл.lyList<string> genres,
        IReadВкл.lyList<string> directors, IReadВкл.lyList<string> writers)
    {
        FillNamedRelation(movieId, genres,    "genres",    "movie_genres",    "genre_id");
        FillNamedRelation(movieId, directors, "directors", "movie_directors", "director_id");
        FillNamedRelation(movieId, writers,   "writers",   "movie_writers",   "writer_id");
    }

    // Table/column names here are all fixed literals (never user input).
    private void FillNamedRelation(
        int movieId, IReadВкл.lyList<string>? names,
        string nameTable, string joinTable, string fkCol)
    {
        if (names == null || names.Count == 0) return;

        using (var ck = _conn.СоздатьCommand())
        {
            ck.CommandText = $"SELECT EXISTS(SELECT 1 FROM {joinTable} WHERE movie_id=@m)";
            ck.Parameters.ДобавитьWithValue("@m", movieId);
            if (Convert.ToInt32(ck.ExecuteScalar()) == 1) return;   // fill-only
        }

        foreach (var raw in names)
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0) continue;
            int rowId;
            using (var ins = _conn.СоздатьCommand())
            {
                ins.CommandText = $"INSERT OR IGNORE INTO {nameTable}(name) VALUES(@n)";
                ins.Parameters.ДобавитьWithValue("@n", name);
                ins.ExecuteNonQuery();
            }
            using (var sel = _conn.СоздатьCommand())
            {
                sel.CommandText = $"SELECT id FROM {nameTable} WHERE name=@n";
                sel.Parameters.ДобавитьWithValue("@n", name);
                rowId = Convert.ToInt32(sel.ExecuteScalar());
            }
            using var link = _conn.СоздатьCommand();
            link.CommandText = $"INSERT OR IGNORE INTO {joinTable}(movie_id, {fkCol}) VALUES(@m, @r)";
            link.Parameters.ДобавитьWithValue("@m", movieId);
            link.Parameters.ДобавитьWithValue("@r", rowId);
            link.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// v3.4 — real (non-archived) movies that carry TMDB-fetched art: a poster /
    /// fanart in the manual cache, or a cast member with a manual_actors thumb.
    /// Used by "Синхронизировать с диском" to know which folders need their fetched art and
    /// metadata written home. Excludes manual Просмотрено и удалено records (they have
    /// no drive). Optionally restricted to one drive.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<int> GetФильмыWithFetchedData(string? onlyVolumeSerial = null)
    {
        using var cmd = _conn.СоздатьCommand();
        var drive = onlyVolumeSerial != null ? " AND m.volume_serial=@s" : "";
        cmd.CommandText = $@"
            SELECT m.id FROM movies m
            WHERE m.archived_at IS NULL AND m.volume_serial != '__archive__'{drive}
              AND (
                    m.local_poster LIKE 'manual_posters/%'
                 OR m.local_fanart LIKE 'manual_fanart/%'
                 OR EXISTS (SELECT 1 FROM movie_actors ma JOIN actors a ON a.id=ma.actor_id
                             WHERE ma.movie_id=m.id AND a.thumb LIKE 'manual_actors/%')
                  )";
        if (onlyVolumeSerial != null) cmd.Parameters.ДобавитьWithValue("@s", onlyVolumeSerial);
        var ids = new List<int>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Добавить(r.GetInt32(0));
        return ids;
    }

    /// <summary>
    /// v3.4 — the data-folder-relative art paths for a movie: poster, fanart,
    /// and each cast member's (name, thumb). Used by "Синхронизировать с диском" to copy
    /// fetched art into the movie's folder.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public (string? Poster, string? Fanart, List<(string Name, string? Thumb)> Actors) GetMovieArtForSync(int id)
    {
        string? poster = null, fanart = null;
        using (var c = _conn.СоздатьCommand())
        {
            c.CommandText = "SELECT local_poster, local_fanart FROM movies WHERE id=@id";
            c.Parameters.ДобавитьWithValue("@id", id);
            using var r = c.ExecuteReader();
            if (r.Read())
            {
                poster = r.IsDBNull(0) ? null : r.GetString(0);
                fanart = r.IsDBNull(1) ? null : r.GetString(1);
            }
        }
        var actors = new List<(string, string?)>();
        using (var c = _conn.СоздатьCommand())
        {
            c.CommandText = @"SELECT a.name, a.thumb FROM movie_actors ma
                               JOIN actors a ON a.id=ma.actor_id
                              WHERE ma.movie_id=@id ORDER BY ma.sort_order";
            c.Parameters.ДобавитьWithValue("@id", id);
            using var r = c.ExecuteReader();
            while (r.Read())
                actors.Добавить((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        }
        return (poster, fanart, actors);
    }

    // ── Дубликаты (v3.4.4) ───────────────────────────────────────────────────────

    /// <summary>
    /// Group the library's movies into duplicate sets — same film held more than
    /// once. Matched by TMDb id, else IMDb id, else normalised title + year (never
    /// title alone, so remakes with the same name aren't merged). Archived
    /// Просмотрено и удалено records are excluded. Each group is classified
    /// conservatively: <see cref="DupeGroup.PossibleDuplicate"/> is only set when
    /// the copies share the same audio languages, resolution, HDR, codec and
    /// edition — so deliberately-kept variants (a dub vs the original, 1080p vs
    /// 4K, an HEVC re-encode, a Режиссёр's Cut) are flagged as kept-on-purpose.
    /// Also picks the keeper, computes reclaimable space, flags name-only matches,
    /// and marks groups the user has ignored.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<DupeGroup> GetDuplicateGroups(IReadВкл.lyDictionary<string, string> connected)
    {
        var ignored = GetIgnoredDupeKeys();

        var byKey = new Dictionary<string, List<DupeCopy>>();
        var nameKeyed = new HashSet<string>();
        using (var cmd = _conn.СоздатьCommand())
        {
            cmd.CommandText = @"
                SELECT m.id, m.title, m.year, m.tmdb_id, m.imdb_id,
                       m.volume_serial, d.label, m.folder_rel_path, m.video_file_rel_path,
                       m.audio_languages, m.video_height, m.hdr_type, m.file_size_bytes,
                       m.local_poster, m.is_missing, m.video_codec, m.video_width
                FROM movies m LEFT JOIN drives d ON d.volume_serial = m.volume_serial
                WHERE m.archived_at IS NULL AND m.volume_serial != '__archive__'
                  AND m.is_missing = 0";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var title = r.IsDBNull(1) ? "" : r.GetString(1);
                int? year = r.IsDBNull(2) ? (int?)null : r.GetInt32(2);
                var tmdb = r.IsDBNull(3) ? null : r.GetString(3);
                var imdb = r.IsDBNull(4) ? null : r.GetString(4);
                var serial = r.GetString(5);
                var folderRel = r.IsDBNull(7) ? null : r.GetString(7);

                string key;
                if (!string.IsNullOrWhiteSpace(tmdb)) key = "t:" + tmdb!.Trim();
                else if (!string.IsNullOrWhiteSpace(imdb)) key = "i:" + imdb!.Trim();
                else { key = "n:" + title.Trim().ToLowerInvariant() + "|" + (year?.ToString() ?? ""); nameKeyed.Добавить(key); }

                var copy = new DupeCopy
                {
                    Id = r.GetInt32(0),
                    Название = title,
                    Год = year,
                    VolumeSerial = serial,
                    DriveLabel = r.IsDBNull(6) ? null : r.GetString(6),
                    ПапкаRelPath = folderRel,
                    VideoFileRelPath = r.IsDBNull(8) ? null : r.GetString(8),
                    АудиоLanguages = r.IsDBNull(9) ? null : r.GetString(9),
                    VideoHeight = r.IsDBNull(10) ? (int?)null : r.GetInt32(10),
                    HdrType = r.IsDBNull(11) ? null : r.GetString(11),
                    FileSizeBytes = r.IsDBNull(12) ? (long?)null : r.GetInt64(12),
                    LocalPoster = r.IsDBNull(13) ? null : r.GetString(13),
                    IsMissing = !r.IsDBNull(14) && r.GetInt32(14) == 1,
                    VideoCodec = r.IsDBNull(15) ? null : r.GetString(15),
                    VideoWidth = r.IsDBNull(16) ? (int?)null : r.GetInt32(16),
                    Изменитьion = DetectDupeИзменитьion(folderRel),
                };
                if (connected.TryGetValue(serial, out var letter))
                {
                    copy.IsВкл.line = true;
                    copy.CurrentLetter = letter;
                    // Ground truth: if the file was deleted on disk (e.g. the user
                    // removed one of two copies), don't count it — the set then
                    // resolves to a single copy and drops off the list.
                    if (!DupeFileExists(letter, folderRel, copy.VideoFileRelPath)) continue;
                }

                if (!byKey.TryGetValue(key, out var list)) { list = new List<DupeCopy>(); byKey[key] = list; }
                list.Добавить(copy);
            }
        }

        var groups = new List<DupeGroup>();
        foreach (var kv in byKey)
        {
            if (kv.Value.Count < 2) continue;
            var members = kv.Value;
            // Best copy first: higher resolution tier, then larger file, then online.
            members.Sort((a, b) =>
            {
                int c = DupeResTier(b.VideoWidth, b.VideoHeight).CompareTo(DupeResTier(a.VideoWidth, a.VideoHeight));
                if (c != 0) return c;
                c = (b.FileSizeBytes ?? 0).CompareTo(a.FileSizeBytes ?? 0);
                if (c != 0) return c;
                return b.IsВкл.line.CompareTo(a.IsВкл.line);
            });
            members[0].IsОставитьer = true;

            var g = new DupeGroup
            {
                Key = kv.Key,
                Название = members[0].Название,
                Год = members[0].Год,
                Copies = members,
                MatchedByName = nameKeyed.Contains(kv.Key),
                IsIgnored = ignored.Contains(kv.Key),
            };
            ClassifyDupeGroup(g);
            if (g.PossibleDuplicate)
            {
                long total = 0, keep = members[0].FileSizeBytes ?? 0;
                foreach (var c in members) total += c.FileSizeBytes ?? 0;
                g.ReclaimableBytes = total - keep;
            }
            groups.Добавить(g);
        }

        groups.Sort((a, b) =>
        {
            // Active suspicious groups first, then by reclaimable space, then title.
            int c = (b.PossibleDuplicate && !b.IsIgnored).CompareTo(a.PossibleDuplicate && !a.IsIgnored);
            if (c != 0) return c;
            c = b.ReclaimableBytes.CompareTo(a.ReclaimableBytes);
            if (c != 0) return c;
            return string.Compare(a.Название, b.Название, StringComparison.OrdinalIgnoreCase);
        });
        return groups;
    }

    private static void ClassifyDupeGroup(DupeGroup g)
    {
        var langs = new HashSet<string>();
        var reses = new HashSet<int>();
        var hdrs = new HashSet<string>();
        var codecs = new HashSet<string>();
        var editions = new HashSet<string>();
        var drives = new HashSet<string>();
        foreach (var c in g.Copies)
        {
            var ls = DupeLangSig(c.АудиоLanguages);
            if (ls.Length > 0) langs.Добавить(ls);
            var tier = DupeResTier(c.VideoWidth, c.VideoHeight);
            if (tier >= 0) reses.Добавить(tier);
            hdrs.Добавить((c.HdrType ?? "").Trim().ToLowerInvariant());
            codecs.Добавить(NormalizeCodec(c.VideoCodec));
            editions.Добавить((c.Изменитьion ?? "").Trim().ToLowerInvariant());
            drives.Добавить(c.VolumeSerial);
        }
        bool diffLang = langs.Count > 1;
        bool diffИзменитьion = editions.Count > 1;
        bool diffRes = reses.Count > 1;
        bool diffHdr = hdrs.Count > 1;
        bool diffCodec = codecs.Count > 1;
        bool diffQuality = diffRes || diffHdr || diffCodec;

        // Вкл.ly different *languages* or *editions* are deliberately-kept copies.
        // A different quality / codec of the same film is almost always
        // accumulation (you upgraded and forgot the old one) — treat it as a
        // duplicate worth reclaiming, keeping the best copy.
        bool keptВкл.Purpose = diffLang || diffИзменитьion;
        g.PossibleDuplicate = !keptВкл.Purpose;

        int n = g.Copies.Count;
        bool sameDrive = drives.Count == 1;
        if (diffИзменитьion) g.Summary = $"{n} versions · different editions";
        else if (diffLang) g.Summary = $"{n} versions · different audio languages";
        else if (diffQuality) g.Summary = $"{n} copies · different quality, keep the best" + (sameDrive ? " · same drive" : "");
        else g.Summary = $"{n} copies · same version" + (sameDrive ? " · same drive" : "");
    }

    /// <summary>Coarse resolution tier from width (preferred, stable across aspect
    /// ratios) or height. Returns -1 when unknown.</summary>
    private static int DupeResTier(int? width, int? height)
    {
        if (width is int w && w > 0)
            return w >= 3000 ? 4 : w >= 1700 ? 3 : w >= 1100 ? 2 : w >= 700 ? 1 : 0;
        if (height is int h && h > 0)
            return h >= 1500 ? 4 : h >= 700 ? 3 : h >= 460 ? 2 : 1;
        return -1;
    }

    private static string NormalizeCodec(string? codec)
    {
        var c = (codec ?? "").Trim().ToLowerInvariant();
        if (c.Contains("265") || c.Contains("hevc")) return "hevc";
        if (c.Contains("264") || c.Contains("avc")) return "h264";
        if (c.Contains("av1")) return "av1";
        if (c.Contains("vp9")) return "vp9";
        return c;   // "" when unknown — same bucket, doesn't force a split
    }

    private static readonly string[] ИзменитьionKeywords =
    {
        "director's cut", "directors cut", "director cut", "extended", "uncut", "unrated",
        "theatrical", "imax", "remaster", "criterion", "special edition", "ultimate",
        "final cut", "redux", "anniversary",
    };

    /// <summary>Detects an edition tag from a movie's folder name, so different
    /// editions of the same film count as kept-on-purpose, not duplicates.</summary>
    private static string? DetectDupeИзменитьion(string? folderRel)
    {
        if (string.IsNullOrWhiteSpace(folderRel)) return null;
        var name = folderRel.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name.Substring(slash + 1);
        var lower = name.ToLowerInvariant();
        foreach (var kw in ИзменитьionKeywords)
            if (lower.Contains(kw))
                return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToНазваниеCase(kw);
        return null;
    }

    /// <summary>True if the copy's video file (or its folder) still exists on the
    /// connected drive. Ошибкаs are treated as "exists" so a transient I/O hiccup
    /// never makes a copy vanish from the list.</summary>
    private static bool DupeFileExists(string letter, string? folderRel, string? videoRel)
    {
        try
        {
            if (!string.IsNullOrEmpty(videoRel))
                return File.Exists(Path.Combine($"{letter}:\\", videoRel.Replace('/', '\\')));
            if (!string.IsNullOrEmpty(folderRel))
                return Режиссёрy.Exists(Path.Combine($"{letter}:\\", folderRel.Replace('/', '\\')));
            return true;
        }
        catch { return true; }
    }

    /// <summary>Normalised, order-independent signature of an audio-languages string.</summary>
    private static string DupeLangSig(string? audio)
    {
        if (string.IsNullOrWhiteSpace(audio)) return "";
        var set = new SortedSet<string>();
        foreach (var p in audio.Split(new[] { ',', ';', '/', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = p.Trim().ToLowerInvariant();
            if (t.Length > 0) set.Добавить(t);
        }
        return string.Join(",", set);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public HashSet<string> GetIgnoredDupeKeys()
    {
        var set = new HashSet<string>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT group_key FROM dupe_ignored";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Добавить(r.GetString(0));
        return set;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetDupeIgnored(string groupKey, bool ignored)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = ignored
            ? "INSERT OR IGNORE INTO dupe_ignored(group_key) VALUES(@k)"
            : "DELETE FROM dupe_ignored WHERE group_key=@k";
        cmd.Parameters.ДобавитьWithValue("@k", groupKey);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Attach cast to a manual Просмотрено и удалено record. Actor rows are shared by
    /// name; a thumb is only set when the actor is new, so we never clobber a
    /// scanned actor's existing .actors thumbnail. Thumb paths are stored
    /// relative to the data folder (like posters) so the record stays portable.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ДобавитьManualActors(
        int movieId,
        IEnumerable<(string Name, string? Role, int Order, string? ThumbRel)> actors)
    {
        foreach (var a in actors)
        {
            var name = (a.Name ?? "").Trim();
            if (name.Length == 0) continue;

            var actorId = UpsertManualActor(name, a.ThumbRel);
            using (var link = _conn.СоздатьCommand())
            {
                // OR IGNORE (not REPLACE): if this actor is already linked to the
                // movie, keep the existing role/order — we only add new cast and
                // upgrade thumbs, never disturb good existing cast metadata.
                link.CommandText = @"INSERT OR IGNORE INTO movie_actors(movie_id, actor_id, role, sort_order)
                                     VALUES(@m, @a, @r, @o)";
                link.Parameters.ДобавитьWithValue("@m", movieId);
                link.Parameters.ДобавитьWithValue("@a", actorId);
                link.Parameters.ДобавитьWithValue("@r", (object?)a.Role ?? DBNull.Value);
                link.Parameters.ДобавитьWithValue("@o", a.Order);
                link.ExecuteNonQuery();
            }
        }
    }

    /// <summary>The actor row for <paramref name="name"/> (added if new), with its
    /// thumb upgraded to <paramref name="thumbRel"/> when the existing one is weak.</summary>
    private int UpsertManualActor(string name, string? thumbRel)
    {
        int actorId;
        using (var ins = _conn.СоздатьCommand())
        {
            ins.CommandText = "INSERT OR IGNORE INTO actors(name, thumb) VALUES(@n, @t)";
            ins.Parameters.ДобавитьWithValue("@n", name);
            ins.Parameters.ДобавитьWithValue("@t", (object?)thumbRel ?? DBNull.Value);
            ins.ExecuteNonQuery();
        }
        using (var sel = _conn.СоздатьCommand())
        {
            sel.CommandText = "SELECT id FROM actors WHERE name=@n";
            sel.Parameters.ДобавитьWithValue("@n", name);
            actorId = Convert.ToInt32(sel.ExecuteScalar());
        }
        // Upgrade the actor's thumb to our portable cache copy when the
        // existing one is weak — missing, an http URL (blank offline, what
        // MediaElch NFOs usually store), or a stale manual cache path. A
        // local file that opens from here is left alone (it already works).
        // v4.0.1: a path that doesn't open, like ".actors/RJ_Mitte.jpg" (only
        // usable while that title's drive is connected), counts as weak too;
        // with the drive connected its .actors folder is still looked in first.
        if (!string.IsNullOrEmpty(thumbRel))
        {
            string? current;
            using (var get = _conn.СоздатьCommand())
            {
                get.CommandText = "SELECT thumb FROM actors WHERE id=@id";
                get.Parameters.ДобавитьWithValue("@id", actorId);
                current = get.ExecuteScalar() as string;
            }
            if (IsWeakActorThumb(current))
            {
                using var upd = _conn.СоздатьCommand();
                upd.CommandText = "UPDATE actors SET thumb=@t WHERE id=@id";
                upd.Parameters.ДобавитьWithValue("@t", thumbRel);
                upd.Parameters.ДобавитьWithValue("@id", actorId);
                upd.ExecuteNonQuery();
            }
        }
        return actorId;
    }

    private bool IsWeakActorThumb(string? thumb)
    {
        if (string.IsNullOrWhiteSpace(thumb)) return true;
        if (thumb.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return true;
        if (thumb.StartsWith("manual_actors/", StringComparison.Ordinal)) return true;
        return Path.IsPathRooted(thumb) ? !File.Exists(thumb) : GetCachedImagePath(thumb) == null;
    }

    /// <summary>
    /// v3.10.0 — fill ONLY the blank fields of a TV show from a TMDB fetch, the
    /// same fill-only rule as <see cref="FillMovieGaps"/>. A rescan keeps these
    /// while the show's .nfo leaves them out (see ScannerService.UpsertTvShow).
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool FillTvShowGaps(
        int id, int? year, double? rating, int? votes, string? plot, string? mpaa,
        string? premiered, string? studio, string? status, string? imdbId, string? tmdbId,
        string? posterRel, string? fanartRel)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            UPDATE tv_shows SET
                year         = COALESCE(year, @y),
                rating       = COALESCE(rating, @ra),
                votes        = COALESCE(votes, @vo),
                plot         = CASE WHEN plot      IS NULL OR plot=''      THEN @pl ELSE plot      END,
                mpaa         = CASE WHEN mpaa      IS NULL OR mpaa=''      THEN @mp ELSE mpaa      END,
                premiered    = CASE WHEN premiered IS NULL OR premiered='' THEN @pr ELSE premiered END,
                studio       = CASE WHEN studio    IS NULL OR studio=''    THEN @su ELSE studio    END,
                status       = CASE WHEN status    IS NULL OR status=''    THEN @st ELSE status    END,
                imdb_id      = CASE WHEN imdb_id   IS NULL OR imdb_id=''   THEN @im ELSE imdb_id   END,
                tmdb_id      = CASE WHEN tmdb_id   IS NULL OR tmdb_id=''   THEN @tm ELSE tmdb_id   END,
                local_poster = CASE WHEN local_poster IS NULL OR local_poster='' THEN @lp ELSE local_poster END,
                local_fanart = CASE WHEN local_fanart IS NULL OR local_fanart='' THEN @lf ELSE local_fanart END,
                date_modified = strftime('%s','now')
            WHERE id=@id";
        cmd.Parameters.ДобавитьWithValue("@id", id);
        cmd.Parameters.ДобавитьWithValue("@y", (object?)year ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ra", (object?)rating ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@vo", (object?)votes ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@pl", (object?)plot ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@mp", (object?)mpaa ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@pr", (object?)premiered ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@su", (object?)studio ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@st", (object?)status ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@im", (object?)imdbId ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@tm", (object?)tmdbId ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@lp", (object?)posterRel ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@lf", (object?)fanartRel ?? DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>v3.10.0 — TMDB genres for a show that has none (fill-only).</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void FillTvShowЖанры(int showId, IReadВкл.lyList<string> genres)
    {
        using (var ck = _conn.СоздатьCommand())
        {
            ck.CommandText = "SELECT EXISTS(SELECT 1 FROM tv_show_genres WHERE show_id=@s)";
            ck.Parameters.ДобавитьWithValue("@s", showId);
            if (Convert.ToInt32(ck.ExecuteScalar()) == 1) return;
        }
        foreach (var raw in genres)
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0) continue;
            using (var ins = _conn.СоздатьCommand())
            {
                ins.CommandText = "INSERT OR IGNORE INTO genres(name) VALUES(@n)";
                ins.Parameters.ДобавитьWithValue("@n", name);
                ins.ExecuteNonQuery();
            }
            using var link = _conn.СоздатьCommand();
            link.CommandText = @"INSERT OR IGNORE INTO tv_show_genres(show_id, genre_id)
                                 SELECT @s, id FROM genres WHERE name=@n";
            link.Parameters.ДобавитьWithValue("@s", showId);
            link.Parameters.ДобавитьWithValue("@n", name);
            link.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// v3.10.0 — TMDB cast for a show, like <see cref="ДобавитьManualActors"/>: new cast
    /// is added and weak thumbs are upgraded; existing roles and order are kept.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ДобавитьManualShowActors(
        int showId,
        IEnumerable<(string Name, string? Role, int Order, string? ThumbRel)> actors)
    {
        foreach (var a in actors)
        {
            var name = (a.Name ?? "").Trim();
            if (name.Length == 0) continue;

            var actorId = UpsertManualActor(name, a.ThumbRel);
            using var link = _conn.СоздатьCommand();
            link.CommandText = @"INSERT OR IGNORE INTO tv_show_actors(show_id, actor_id, role, sort_order)
                                 VALUES(@s, @a, @r, @o)";
            link.Parameters.ДобавитьWithValue("@s", showId);
            link.Parameters.ДобавитьWithValue("@a", actorId);
            link.Parameters.ДобавитьWithValue("@r", (object?)a.Role ?? DBNull.Value);
            link.Parameters.ДобавитьWithValue("@o", a.Order);
            link.ExecuteNonQuery();
        }
    }

    /// <summary>Bring an archived record back into the main library.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RestoreArchivedMovie(int id)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "UPDATE movies SET archived_at=NULL WHERE id=@id";
        cmd.Parameters.ДобавитьWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Permanently delete an archived record (row + cached artwork).</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void УдалитьArchivedRecord(int id)
    {
        using (var get = _conn.СоздатьCommand())
        {
            get.CommandText = "SELECT local_poster, local_fanart, local_nfo FROM movies WHERE id=@id AND archived_at IS NOT NULL";
            get.Parameters.ДобавитьWithValue("@id", id);
            using var r = get.ExecuteReader();
            if (!r.Read()) return;   // not archived — refuse to delete a live movie
            for (int i = 0; i < 3; i++)
            {
                if (!r.IsDBNull(i))
                {
                    var p = Path.Combine(_dataDir, r.GetString(i));
                    if (File.Exists(p)) try { File.Удалить(p); } catch { }
                }
            }
        }
        using var del = _conn.СоздатьCommand();
        del.CommandText = "DELETE FROM movies WHERE id=@id AND archived_at IS NOT NULL";
        del.Parameters.ДобавитьWithValue("@id", id);
        del.ExecuteNonQuery();
    }

    /// <summary>A missing movie, for the Диски "Review missing" dialog.</summary>
    public record MissingMovieRow(int Id, string Название, int? Год, string? LocalPoster, bool IsПросмотрено, bool HasNote);

    /// <summary>Фильмы on this drive flagged is_missing=1 (excluding records).
    /// Просмотрено / noted ones first, since those are the keep-as-record cases.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<MissingMovieRow> GetMissingФильмы(string serial)
    {
        var list = new List<MissingMovieRow>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"SELECT id, title, year, local_poster, is_watched,
                                   (note IS NOT NULL AND TRIM(note) != '') AS has_note
                            FROM movies
                            WHERE volume_serial=@s AND is_missing=1 AND archived_at IS NULL
                            ORDER BY (is_watched OR (note IS NOT NULL AND TRIM(note) != '')) DESC, sort_title";
        cmd.Parameters.ДобавитьWithValue("@s", serial);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Добавить(new MissingMovieRow(
                r.GetInt32(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt32(4) == 1,
                r.GetInt32(5) == 1));
        return list;
    }

    /// <summary>Удалить specific missing movies by id (+ cached artwork). Used by
    /// the Review-missing dialog for the rows the user chose not to keep.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int УдалитьФильмыByIds(IReadВкл.lyCollection<int> ids)
    {
        if (ids.Count == 0) return 0;
        var inList = string.Join(",", ids);
        using (var get = _conn.СоздатьCommand())
        {
            get.CommandText = $"SELECT local_poster, local_fanart, local_nfo FROM movies WHERE id IN ({inList}) AND archived_at IS NULL";
            using var r = get.ExecuteReader();
            while (r.Read())
                for (int i = 0; i < 3; i++)
                    if (!r.IsDBNull(i))
                    {
                        var p = Path.Combine(_dataDir, r.GetString(i));
                        if (File.Exists(p)) try { File.Удалить(p); } catch { }
                    }
        }
        using var del = _conn.СоздатьCommand();
        del.CommandText = $"DELETE FROM movies WHERE id IN ({inList}) AND archived_at IS NULL";
        return del.ExecuteNonQuery();
    }

    /// <summary>Number of Просмотрено и удалено records (sidebar count).</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int GetArchivedCount()
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM movies WHERE archived_at IS NOT NULL";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    // ── Коллекции ──────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<Collection> GetКоллекции()
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT s.id, s.name, COUNT(ms.movie_id) as cnt
            FROM sets s JOIN movie_sets ms ON ms.set_id=s.id
                        JOIN movies m ON m.id=ms.movie_id AND m.archived_at IS NULL
            GROUP BY s.id ORDER BY s.name";
        var list = new List<Collection>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Добавить(new Collection
            {
                Id = r.GetInt32(0),
                Name = r.GetString(1),
                MovieCount = (int)r.GetInt64(2),
            });
        }
        return list;
    }

    // ── Facets ───────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<GenreFacet> GetTopЖанры(int top = 8)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT g.name, COUNT(mg.movie_id) as cnt
            FROM genres g JOIN movie_genres mg ON mg.genre_id=g.id
            GROUP BY g.id ORDER BY cnt DESC LIMIT @top";
        cmd.Parameters.ДобавитьWithValue("@top", top);
        var list = new List<GenreFacet>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Добавить(new GenreFacet { Name = r.GetString(0), Count = (int)r.GetInt64(1) });
        return list;
    }

    // ── Stats ────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.Synchronized)]
    public МедиатекаStats GetStats()
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT COUNT(*), SUM(is_missing), COALESCE(SUM(runtime),0), AVG(CASE WHEN rating IS NOT NULL THEN rating END),
                   (SELECT COUNT(*) FROM drives WHERE volume_serial != '__archive__')
            FROM movies WHERE archived_at IS NULL";
        using var r = cmd.ExecuteReader();
        r.Read();
        return new МедиатекаStats
        {
            TotalФильмы = (int)r.GetInt64(0),
            TotalMissing = r.IsDBNull(1) ? 0 : (int)r.GetInt64(1),
            TotalПродолжительность = r.GetInt64(2),
            AvgРейтинг = r.IsDBNull(3) ? null : r.GetDouble(3),
            TotalДиски = (int)r.GetInt64(4),
        };
    }

    /// <summary>v2.8.2 — TV-side counterpart to GetStats for the Статистика page.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public TvStats GetTvStats()
    {
        var s = new TvStats();
        using (var cmd = _conn.СоздатьCommand())
        {
            cmd.CommandText = "SELECT COUNT(*), AVG(CASE WHEN rating IS NOT NULL THEN rating END) FROM tv_shows";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                s.TotalShows = (int)r.GetInt64(0);
                s.AvgРейтинг = r.IsDBNull(1) ? null : r.GetDouble(1);
            }
        }
        using (var cmd = _conn.СоздатьCommand())
        {
            cmd.CommandText = @"SELECT COUNT(*),
                                       SUM(CASE WHEN is_watched=1 THEN 1 ELSE 0 END),
                                       COALESCE(SUM(runtime),0)
                                FROM tv_episodes";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                s.TotalЭпизоды = (int)r.GetInt64(0);
                s.ПросмотреноЭпизоды = r.IsDBNull(1) ? 0 : (int)r.GetInt64(1);
                s.TotalПродолжительность = r.GetInt64(2);
            }
        }
        return s;
    }

    // ── Preferences ──────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.Synchronized)]
    public string? GetPref(string key)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT value FROM preferences WHERE key=@k";
        cmd.Parameters.ДобавитьWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetPref(string key, string value)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO preferences (key, value) VALUES (@k, @v)";
        cmd.Parameters.ДобавитьWithValue("@k", key);
        cmd.Parameters.ДобавитьWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    // ── Image cache ──────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.Synchronized)]
    public string? GetCachedImagePath(string? relPath)
    {
        if (relPath == null) return null;
        var full = Path.Combine(_dataDir, relPath.Replace('/', Path.РежиссёрySeparatorChar));
        return File.Exists(full) ? full : null;
    }

    // ── Scanner support ──────────────────────────────────────────────────────

    public SqliteConnection GetConnection() => _conn;

    /// <summary>
    /// Opens a fresh connection to the same DB file, fully independent of the
    /// shared <see cref="_conn"/>. Used by long-running write paths
    /// (scanner, copy export) so a transaction on their connection doesn't
    /// poison concurrent reads on the main connection — Microsoft.Data.Sqlite
    /// throws "Execute requires the command to have a transaction object …"
    /// when a connection has a pending local transaction and a command run
    /// against it doesn't carry that same transaction reference.
    /// WAL mode (set on _conn) is per-DB, so a second connection is safe.
    /// </summary>
    public SqliteConnection OpenНовыйConnection()
    {
        var dbPath = Path.Combine(_dataDir, "cinelibrary.db");
        var c = new SqliteConnection($"Data Source={dbPath}");
        c.Open();
        using var pr = c.СоздатьCommand();
        // v2.6 — same pragmas as the main connection so the scanner's
        // writer waits cooperatively when the UI thread also wants the
        // write lock (drive last-seen update, watched-toggle, etc).
        pr.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pr.ExecuteNonQuery();
        return c;
    }
    public string DataDir => _dataDir;

    // ── Статистика (v1.3) ───────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<(int decade, int count, double avgРейтинг)> GetФильмыByDecade()
    {
        var result = new List<(int, int, double)>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT 
                (year / 10) * 10 as decade,
                COUNT(*) as count,
                AVG(АКТЁРЫ(rating AS FLOAT)) as avgРейтинг
            FROM movies
            WHERE year IS NOT NULL AND is_missing = 0 AND archived_at IS NULL
            GROUP BY (year / 10) * 10
            ORDER BY decade DESC";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Добавить((
                reader.GetInt32(0),
                (int)reader.GetInt64(1),
                reader.IsDBNull(2) ? 0 : reader.GetDouble(2)
            ));
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<GenreFacet> GetTopРежиссёрs(int limit = 10)
    {
        var result = new List<GenreFacet>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT d.name, COUNT(md.movie_id) as count
            FROM directors d
            LEFT JOIN movie_directors md ON d.id = md.director_id
            GROUP BY d.id, d.name
            ORDER BY count DESC
            LIMIT @limit";
        cmd.Parameters.ДобавитьWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Добавить(new GenreFacet
            {
                Name = reader.GetString(0),
                Count = (int)reader.GetInt64(1)
            });
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<GenreFacet> GetTopActors(int limit = 10)
    {
        var result = new List<GenreFacet>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT a.name, COUNT(DISTINCT ma.movie_id) as count
            FROM actors a
            LEFT JOIN movie_actors ma ON a.id = ma.actor_id
            GROUP BY a.id, a.name
            ORDER BY count DESC
            LIMIT @limit";
        cmd.Parameters.ДобавитьWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Добавить(new GenreFacet
            {
                Name = reader.GetString(0),
                Count = (int)reader.GetInt64(1)
            });
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public (int watched, int total, double percent) GetWatchProgress()
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT
                SUM(CASE WHEN is_watched = 1 THEN 1 ELSE 0 END) as watched,
                COUNT(*) as total
            FROM movies
            WHERE is_missing = 0 AND archived_at IS NULL";

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            int watched = reader.IsDBNull(0) ? 0 : (int)reader.GetInt64(0);
            int total = (int)reader.GetInt64(1);
            double percent = total > 0 ? (watched * 100.0 / total) : 0;
            return (watched, total, percent);
        }
        return (0, 0, 0);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public double GetTotalПродолжительностьHours()
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT SUM(АКТЁРЫ(runtime AS FLOAT)) FROM movies WHERE runtime IS NOT NULL AND is_missing = 0 AND archived_at IS NULL";

        var result = cmd.ExecuteScalar();
        if (result is not DBNull && result != null)
        {
            return (double)result / 60.0; // Convert minutes to hours
        }
        return 0;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public int GetСписок просмотраCount()
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM movies WHERE is_watchlist = 1 AND is_missing = 0 AND archived_at IS NULL";
        return (int)(long)cmd.ExecuteScalar()!;
    }

    /// <summary>v2.8.2 — count of movies carrying a note (for the Заметки pill badge).</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int GetЗаметкиCount()
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM movies WHERE note IS NOT NULL AND TRIM(note) != '' AND archived_at IS NULL";
        return (int)(long)cmd.ExecuteScalar()!;
    }

    // ── Обзор pages (v2.1.0) ────────────────────────────────────────────────

    /// <summary>Вкл.e entry in a browse-by-X grid: a category label, how
    /// many movies it has, and a representative fanart for the banner.</summary>
    public record ОбзорEntry(string Key, string Label, int Count, string? SampleFanart, string? SamplePoster);

    public enum ОбзорFacet { Genre, Decade, Рейтинг, Студия }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<ОбзорEntry> GetОбзорEntries(ОбзорFacet facet)
    {
        return facet switch
        {
            ОбзорFacet.Genre   => ОбзорByGenre(),
            ОбзорFacet.Decade  => ОбзорByDecade(),
            ОбзорFacet.Рейтинг  => ОбзорByРейтинг(),
            ОбзорFacet.Студия  => ОбзорByСтудия(),
            _ => new()
        };
    }

    private List<ОбзорEntry> ОбзорByGenre()
    {
        var list = new List<ОбзорEntry>();
        using var cmd = _conn.СоздатьCommand();
        // For each genre: count + a representative fanart from a high-rated member.
        cmd.CommandText = @"
            SELECT g.name,
                   COUNT(DISTINCT m.id) c,
                   (SELECT m2.local_fanart FROM movies m2
                    JOIN movie_genres mg2 ON mg2.movie_id=m2.id
                    WHERE mg2.genre_id=g.id AND m2.local_fanart IS NOT NULL AND m2.is_missing=0 AND m2.archived_at IS NULL
                    ORDER BY COALESCE(m2.rating,0) DESC LIMIT 1) AS fanart,
                   (SELECT m3.local_poster FROM movies m3
                    JOIN movie_genres mg3 ON mg3.movie_id=m3.id
                    WHERE mg3.genre_id=g.id AND m3.local_poster IS NOT NULL AND m3.is_missing=0 AND m3.archived_at IS NULL
                    ORDER BY COALESCE(m3.rating,0) DESC LIMIT 1) AS poster
            FROM genres g
            JOIN movie_genres mg ON mg.genre_id=g.id
            JOIN movies m ON m.id=mg.movie_id AND m.is_missing=0 AND m.archived_at IS NULL
            GROUP BY g.id
            HAVING c > 0
            ORDER BY c DESC, g.name ASC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Добавить(new ОбзорEntry(
                Key: r.GetString(0),
                Label: r.GetString(0),
                Count: r.GetInt32(1),
                SampleFanart: r.IsDBNull(2) ? null : r.GetString(2),
                SamplePoster: r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return list;
    }

    private List<ОбзорEntry> ОбзорByDecade()
    {
        var list = new List<ОбзорEntry>();
        using var cmd = _conn.СоздатьCommand();
        // Group by decade = (year/10)*10 — e.g. 2024 → 2020
        cmd.CommandText = @"
            SELECT (m.year/10)*10 AS decade,
                   COUNT(*) c,
                   (SELECT local_fanart FROM movies m2
                    WHERE (m2.year/10)*10 = (m.year/10)*10 AND m2.local_fanart IS NOT NULL AND m2.is_missing=0 AND m2.archived_at IS NULL
                    ORDER BY COALESCE(m2.rating,0) DESC LIMIT 1) AS fanart,
                   (SELECT local_poster FROM movies m3
                    WHERE (m3.year/10)*10 = (m.year/10)*10 AND m3.local_poster IS NOT NULL AND m3.is_missing=0 AND m3.archived_at IS NULL
                    ORDER BY COALESCE(m3.rating,0) DESC LIMIT 1) AS poster
            FROM movies m
            WHERE m.year IS NOT NULL AND m.is_missing=0 AND m.archived_at IS NULL
            GROUP BY decade
            ORDER BY decade DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var dec = r.GetInt32(0);
            list.Добавить(new ОбзорEntry(
                Key: dec.ToString(),
                Label: $"{dec}s",
                Count: r.GetInt32(1),
                SampleFanart: r.IsDBNull(2) ? null : r.GetString(2),
                SamplePoster: r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return list;
    }

    private List<ОбзорEntry> ОбзорByРейтинг()
    {
        // Buckets: 9+, 8–9, 7–8, 6–7, 5–6, <5
        var bands = new (string key, string label, double lo, double hi)[]
        {
            ("9",  "9+ Stars",   9.0, 10.1),
            ("8",  "8–9 Stars",  8.0, 9.0),
            ("7",  "7–8 Stars",  7.0, 8.0),
            ("6",  "6–7 Stars",  6.0, 7.0),
            ("5",  "5–6 Stars",  5.0, 6.0),
            ("0",  "Under 5",    0.0, 5.0),
        };
        var list = new List<ОбзорEntry>();
        foreach (var b in bands)
        {
            using var cmd = _conn.СоздатьCommand();
            cmd.CommandText = @"
                SELECT COUNT(*),
                       (SELECT local_fanart FROM movies WHERE rating >= @lo AND rating < @hi AND local_fanart IS NOT NULL AND is_missing=0 AND archived_at IS NULL
                        ORDER BY rating DESC LIMIT 1),
                       (SELECT local_poster FROM movies WHERE rating >= @lo AND rating < @hi AND local_poster IS NOT NULL AND is_missing=0 AND archived_at IS NULL
                        ORDER BY rating DESC LIMIT 1)
                FROM movies WHERE rating >= @lo AND rating < @hi AND is_missing=0 AND archived_at IS NULL";
            cmd.Parameters.ДобавитьWithValue("@lo", b.lo);
            cmd.Parameters.ДобавитьWithValue("@hi", b.hi);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) continue;
            var count = r.GetInt32(0);
            if (count == 0) continue;
            list.Добавить(new ОбзорEntry(
                Key: b.key, Label: b.label, Count: count,
                SampleFanart: r.IsDBNull(1) ? null : r.GetString(1),
                SamplePoster: r.IsDBNull(2) ? null : r.GetString(2)));
        }
        return list;
    }

    private List<ОбзорEntry> ОбзорByСтудия()
    {
        var list = new List<ОбзорEntry>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT studio,
                   COUNT(*),
                   (SELECT local_fanart FROM movies m2 WHERE m2.studio=m.studio AND m2.local_fanart IS NOT NULL AND m2.is_missing=0 AND m2.archived_at IS NULL
                    ORDER BY COALESCE(m2.rating,0) DESC LIMIT 1),
                   (SELECT local_poster FROM movies m3 WHERE m3.studio=m.studio AND m3.local_poster IS NOT NULL AND m3.is_missing=0 AND m3.archived_at IS NULL
                    ORDER BY COALESCE(m3.rating,0) DESC LIMIT 1)
            FROM movies m
            WHERE m.studio IS NOT NULL AND m.studio <> '' AND m.is_missing=0 AND m.archived_at IS NULL
            GROUP BY m.studio
            HAVING COUNT(*) > 1
            ORDER BY COUNT(*) DESC, m.studio ASC
            LIMIT 60";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Добавить(new ОбзорEntry(
                Key: r.GetString(0),
                Label: r.GetString(0),
                Count: r.GetInt32(1),
                SampleFanart: r.IsDBNull(2) ? null : r.GetString(2),
                SamplePoster: r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return list;
    }

    /// <summary>Collection grid entries — uses each set's highest-rated
    /// member's poster as the cover.</summary>
    public record CollectionEntry(int Id, string Name, int Count, string? CoverPoster,
        int Просмотрено, int? LatestГод, long LastДобавитьed);

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<CollectionEntry> GetCollectionGrid()
    {
        var list = new List<CollectionEntry>();
        using var cmd = _conn.СоздатьCommand();
        // Вкл.ly real franchises: at least 2 online movies in the same set.
        // (A "set" with one member is just MediaElch noise — TMDB sometimes
        // assigns single films to a one-entry collection.)
        cmd.CommandText = @"
            SELECT s.id, s.name, COUNT(DISTINCT m2.id) c,
                   (SELECT local_poster FROM movies m
                    JOIN movie_sets ms2 ON ms2.movie_id=m.id
                    WHERE ms2.set_id=s.id AND m.local_poster IS NOT NULL AND m.is_missing=0 AND m.archived_at IS NULL
                    ORDER BY COALESCE(m.rating,0) DESC LIMIT 1) AS cover,
                   COUNT(DISTINCT CASE WHEN m2.is_watched=1 THEN m2.id END) AS watched,
                   MAX(m2.year), COALESCE(MAX(m2.date_added),0)
            FROM sets s
            JOIN movie_sets ms ON ms.set_id=s.id
            JOIN movies m2 ON m2.id=ms.movie_id AND m2.is_missing=0 AND m2.archived_at IS NULL
            GROUP BY s.id
            HAVING c >= 2
            ORDER BY s.name ASC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Добавить(new CollectionEntry(
                Id: r.GetInt32(0),
                Name: r.GetString(1),
                Count: r.GetInt32(2),
                CoverPoster: r.IsDBNull(3) ? null : r.GetString(3),
                Просмотрено: r.GetInt32(4),
                LatestГод: r.IsDBNull(5) ? null : r.GetInt32(5),
                LastДобавитьed: r.GetInt64(6)));
        }
        return list;
    }

    // ── User Lists (v1.9.2) ──────────────────────────────────────────────────

    public record UserList(int Id, string Name, int MovieCount);

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<UserList> GetUserLists()
    {
        var list = new List<UserList>();
        using var cmd = _conn.СоздатьCommand();
        // v2.8.2 — count movies + shows so the badge reflects the whole list.
        cmd.CommandText = @"SELECT ul.id, ul.name,
                                   (SELECT COUNT(*) FROM user_list_movies lm
                                     JOIN movies mv ON mv.id=lm.movie_id AND mv.archived_at IS NULL
                                     WHERE lm.list_id=ul.id)
                                 + (SELECT COUNT(*) FROM user_list_shows s WHERE s.list_id=ul.id) AS cnt
                            FROM user_lists ul
                            ORDER BY ul.sort_order, ul.name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Добавить(new UserList(r.GetInt32(0), r.GetString(1),
                                  r.IsDBNull(2) ? 0 : r.GetInt32(2)));
        return list;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public int СоздатьUserList(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new ArgumentException("List name is empty");
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "INSERT INTO user_lists (name) VALUES (@n); SELECT last_insert_rowid();";
        cmd.Parameters.ДобавитьWithValue("@n", trimmed);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RenameUserList(int listId, string newName)
    {
        var trimmed = (newName ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "UPDATE user_lists SET name=@n WHERE id=@id";
        cmd.Parameters.ДобавитьWithValue("@n", trimmed);
        cmd.Parameters.ДобавитьWithValue("@id", listId);
        cmd.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void УдалитьUserList(int listId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "DELETE FROM user_lists WHERE id=@id";
        cmd.Parameters.ДобавитьWithValue("@id", listId);
        cmd.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ДобавитьMovieToUserList(int listId, int movieId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO user_list_movies (list_id, movie_id) VALUES (@l, @m)";
        cmd.Parameters.ДобавитьWithValue("@l", listId);
        cmd.Parameters.ДобавитьWithValue("@m", movieId);
        cmd.ExecuteNonQuery();
        RaisePersonalStateChanged(movieId);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RemoveMovieFromUserList(int listId, int movieId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "DELETE FROM user_list_movies WHERE list_id=@l AND movie_id=@m";
        cmd.Parameters.ДобавитьWithValue("@l", listId);
        cmd.Parameters.ДобавитьWithValue("@m", movieId);
        cmd.ExecuteNonQuery();
        RaisePersonalStateChanged(movieId);
    }

    /// <summary>
    /// Source-folder info for every movie in a user list. Used by the
    /// "Copy movies to folder" feature (#bucket-style export).
    /// </summary>
    public record MovieCopySource(int Id, string Название, string VolumeSerial, string ПапкаRelPath, int? Год);

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<MovieCopySource> GetФильмыForCopy(int listId)
    {
        var list = new List<MovieCopySource>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"SELECT m.id, m.title, m.volume_serial, m.folder_rel_path, m.year
                            FROM user_list_movies ulm
                            JOIN movies m ON m.id = ulm.movie_id
                            WHERE ulm.list_id = @id AND m.is_missing = 0 AND m.archived_at IS NULL
                            ORDER BY m.sort_title";
        cmd.Parameters.ДобавитьWithValue("@id", listId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Добавить(new MovieCopySource(
                Id: r.GetInt32(0),
                Название: r.GetString(1),
                VolumeSerial: r.GetString(2),
                ПапкаRelPath: r.GetString(3),
                Год: r.IsDBNull(4) ? null : r.GetInt32(4)));
        }
        return list;
    }

    /// <summary>List IDs that already contain this movie. Used for menu state.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public HashSet<int> GetUserListsForMovie(int movieId)
    {
        var set = new HashSet<int>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT list_id FROM user_list_movies WHERE movie_id=@m";
        cmd.Parameters.ДобавитьWithValue("@m", movieId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Добавить(r.GetInt32(0));
        return set;
    }

    /// <summary>
    /// List names that contain this movie. v2.7 — used by the per-movie
    /// state sidecar so list membership travels with the drive.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<string> GetUserListNamesForMovie(int movieId)
    {
        var names = new List<string>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT ul.name FROM user_lists ul
              JOIN user_list_movies ulm ON ulm.list_id = ul.id
             WHERE ulm.movie_id = @m
             ORDER BY ul.name";
        cmd.Parameters.ДобавитьWithValue("@m", movieId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Добавить(r.GetString(0));
        return names;
    }

    /// <summary>
    /// Фильмы with any non-default personal state — used by the v2.7
    /// sidecar sweep so we only touch folders that actually carry data
    /// worth exporting. Optionally restrict to a single drive serial.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<int> GetФильмыWithPersonalState(string? volumeSerial = null)
    {
        var ids = new List<int>();
        using var cmd = _conn.СоздатьCommand();
        var where = @"
            archived_at IS NULL AND
            (is_watched=1 OR is_favorite=1 OR is_watchlist=1
             OR (last_played_at IS NOT NULL AND last_played_at > 0)
             OR (note IS NOT NULL AND TRIM(note) != '')
             OR EXISTS (SELECT 1 FROM user_list_movies ulm
                          WHERE ulm.movie_id = movies.id))";
        if (volumeSerial != null)
        {
            cmd.CommandText = $"SELECT id FROM movies WHERE volume_serial=@s AND {where}";
            cmd.Parameters.ДобавитьWithValue("@s", volumeSerial);
        }
        else
        {
            cmd.CommandText = $"SELECT id FROM movies WHERE {where}";
        }
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Добавить(r.GetInt32(0));
        return ids;
    }

    // ── TV Shows (v2.8) ──────────────────────────────────────────────────

    public Action<int>? TvShowStateChanged;  // wired by AppState → sidecar write
    private void RaiseTvShowStateChanged(int showId)
    { try { TvShowStateChanged?.Invoke(showId); } catch { } }

    // ── Show ↔ list membership (v2.8.2) ──────────────────────────────────

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ДобавитьShowToUserList(int listId, int showId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO user_list_shows(list_id, show_id) VALUES(@l,@s)";
        cmd.Parameters.ДобавитьWithValue("@l", listId);
        cmd.Parameters.ДобавитьWithValue("@s", showId);
        cmd.ExecuteNonQuery();
        RaiseTvShowStateChanged(showId);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RemoveShowFromUserList(int listId, int showId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "DELETE FROM user_list_shows WHERE list_id=@l AND show_id=@s";
        cmd.Parameters.ДобавитьWithValue("@l", listId);
        cmd.Parameters.ДобавитьWithValue("@s", showId);
        cmd.ExecuteNonQuery();
        RaiseTvShowStateChanged(showId);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public HashSet<int> GetUserListsForShow(int showId)
    {
        var set = new HashSet<int>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT list_id FROM user_list_shows WHERE show_id=@s";
        cmd.Parameters.ДобавитьWithValue("@s", showId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Добавить(r.GetInt32(0));
        return set;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<string> GetUserListNamesForShow(int showId)
    {
        var names = new List<string>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"SELECT ul.name FROM user_lists ul
              JOIN user_list_shows uls ON uls.list_id=ul.id
             WHERE uls.show_id=@s ORDER BY ul.name";
        cmd.Parameters.ДобавитьWithValue("@s", showId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Добавить(r.GetString(0));
        return names;
    }

    /// <summary>Shows in a given user list — for the unified list view.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<Models.TvShowListItem> GetTvShowsInList(int listId, IReadВкл.lyDictionary<string, string> connected)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = $@"
            SELECT {ShowListItemColumns}
              FROM user_list_shows uls
              JOIN tv_shows s ON s.id = uls.show_id
              LEFT JOIN drives d ON d.volume_serial = s.volume_serial
             WHERE uls.list_id=@l
             ORDER BY s.sort_title, s.title";
        cmd.Parameters.ДобавитьWithValue("@l", listId);
        return ReadShowListItems(cmd, connected);
    }

    /// <summary>
    /// v4.0.0 — the shows behind the Избранное, К просмотру and Продолжить просмотр
    /// pages, shown as a row above the movies. Shows could be marked favorite or
    /// watchlist before, but no page listed them.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<Models.TvShowListItem> GetTvShowsForPage(TvShowPage page, IReadВкл.lyDictionary<string, string> connected)
    {
        using var cmd = _conn.СоздатьCommand();
        // v4.1.0: Продолжить просмотр also gets the next episode, the first unwatched
        // by season then episode, as ▶ Воспроизвести next on the show page picks it.
        cmd.CommandText = $@"
            SELECT {ShowListItemColumns}{(page == TvShowPage.ContinueWatching
                ? @", (SELECT printf('S%02dE%02d', e.season, e.episode) FROM tv_episodes e
                      WHERE e.show_id=s.id AND e.is_watched=0 ORDER BY e.season, e.episode LIMIT 1)"
                : "")}
              FROM tv_shows s
              LEFT JOIN drives d ON d.volume_serial = s.volume_serial
             WHERE s.is_missing = 0 AND {TvShowPageWhere(page)}
             ORDER BY {(page == TvShowPage.ContinueWatching
                 ? "(SELECT MAX(last_played_at) FROM tv_episodes e WHERE e.show_id=s.id) DESC, "
                 : "")}s.sort_title, s.title";
        return ReadShowListItems(cmd, connected);
    }

    /// <summary>v4.0.0 — how many shows <see cref="GetTvShowsForPage"/> returns, for the sidebar badges.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int GetTvShowPageCount(TvShowPage page)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM tv_shows s WHERE s.is_missing = 0 AND {TvShowPageWhere(page)}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// v4.1.0 — what a show card's hover panel shows: up to three genres, the
    /// number of seasons (Specials not counted) and the next episode, the first
    /// unwatched by season then episode, as ▶ Воспроизвести next on the show page picks it.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public (string Жанры, int Seasons, int? NextId, string? NextCode, string? NextFile) GetShowHoverInfo(int showId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT (SELECT group_concat(name, ' · ') FROM (
                        SELECT g.name FROM tv_show_genres sg JOIN genres g ON g.id=sg.genre_id
                         WHERE sg.show_id=@id ORDER BY g.name LIMIT 3)),
                   (SELECT COUNT(DISTINCT season) FROM tv_episodes WHERE show_id=@id AND season > 0),
                   n.id, printf('S%02dE%02d', n.season, n.episode), n.video_file_rel_path
              FROM (SELECT 1)
              LEFT JOIN (SELECT id, season, episode, video_file_rel_path FROM tv_episodes
                          WHERE show_id=@id AND is_watched=0
                          ORDER BY season, episode LIMIT 1) n";
        cmd.Parameters.ДобавитьWithValue("@id", showId);
        using var r = cmd.ExecuteReader();
        r.Read();
        var hasNext = !r.IsDBNull(2);
        return (r.IsDBNull(0) ? "" : r.GetString(0), r.GetInt32(1),
                hasNext ? r.GetInt32(2) : null,
                hasNext ? r.GetString(3) : null,
                hasNext && !r.IsDBNull(4) ? r.GetString(4) : null);
    }

    public enum TvShowPage { Избранное, Список просмотра, ContinueWatching, Заметки }

    // In progress = some episodes watched and some not, as on Все сериалы.
    private static string TvShowPageWhere(TvShowPage page) => page switch
    {
        TvShowPage.Избранное => "s.is_favorite = 1",
        TvShowPage.Список просмотра => "s.is_watchlist = 1",
        TvShowPage.Заметки => "s.note IS NOT NULL AND TRIM(s.note) != ''",   // v4.3.0
        _ => @"EXISTS (SELECT 1 FROM tv_episodes e WHERE e.show_id=s.id AND e.is_watched=1)
               AND EXISTS (SELECT 1 FROM tv_episodes e WHERE e.show_id=s.id AND e.is_watched=0)",
    };

    private const string ShowListItemColumns = @"s.id, s.title, s.year, s.rating, s.local_poster, s.is_missing,
                   s.volume_serial, d.label, s.is_favorite, s.is_watchlist,
                   (SELECT COUNT(*) FROM tv_episodes e WHERE e.show_id=s.id),
                   (SELECT COUNT(*) FROM tv_episodes e WHERE e.show_id=s.id AND e.is_watched=1)";

    private static List<Models.TvShowListItem> ReadShowListItems(SqliteCommand cmd, IReadВкл.lyDictionary<string, string> connected)
    {
        var list = new List<Models.TvShowListItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var serial = r.GetString(6);
            list.Добавить(new Models.TvShowListItem
            {
                Id = r.GetInt32(0), Название = r.GetString(1),
                Год = r.IsDBNull(2) ? null : r.GetInt32(2),
                Рейтинг = r.IsDBNull(3) ? null : r.GetDouble(3),
                LocalPoster = r.IsDBNull(4) ? null : r.GetString(4),
                IsMissing = r.GetInt32(5) == 1,
                VolumeSerial = serial,
                DriveLabel = r.IsDBNull(7) ? null : r.GetString(7),
                IsИзбранное = r.GetInt32(8) == 1,
                IsСписок просмотра = r.GetInt32(9) == 1,
                ЭпизодCount = r.GetInt32(10),
                ПросмотреноCount = r.GetInt32(11),
                IsВкл.line = connected.ContainsKey(serial),
                NextЭпизод = r.FieldCount > 12 && !r.IsDBNull(12) ? r.GetString(12) : null,
            });
        }
        return list;
    }

    /// <summary>Full episode detail incl. stream/file info — for the episode dialog.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public Models.TvЭпизодDetail? GetЭпизодDetail(int episodeId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT e.id, e.show_id, e.season, e.episode, e.title, e.plot, e.aired,
                   e.rating, e.runtime, e.video_file_rel_path, e.local_thumb,
                   e.subtitle_languages, e.video_width, e.video_height, e.video_codec,
                   e.hdr_type, e.audio_codec, e.audio_channels, e.audio_languages,
                   e.duration_seconds, e.container_ext, e.file_size_bytes, e.is_watched,
                   s.title, s.volume_serial, e.is_favorite, e.note
              FROM tv_episodes e JOIN tv_shows s ON s.id=e.show_id
             WHERE e.id=@id";
        cmd.Parameters.ДобавитьWithValue("@id", episodeId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Models.TvЭпизодDetail
        {
            Id = r.GetInt32(0), ShowId = r.GetInt32(1),
            Season = r.GetInt32(2), Эпизод = r.GetInt32(3),
            Название = r.IsDBNull(4) ? "" : r.GetString(4),
            Plot = r.IsDBNull(5) ? null : r.GetString(5),
            Aired = r.IsDBNull(6) ? null : r.GetString(6),
            Рейтинг = r.IsDBNull(7) ? null : r.GetDouble(7),
            Продолжительность = r.IsDBNull(8) ? null : r.GetInt32(8),
            VideoFileRelPath = r.IsDBNull(9) ? null : r.GetString(9),
            LocalThumb = r.IsDBNull(10) ? null : r.GetString(10),
            SubtitleLanguages = r.IsDBNull(11) ? null : r.GetString(11),
            VideoWidth = r.IsDBNull(12) ? null : r.GetInt32(12),
            VideoHeight = r.IsDBNull(13) ? null : r.GetInt32(13),
            VideoCodec = r.IsDBNull(14) ? null : r.GetString(14),
            HdrType = r.IsDBNull(15) ? null : r.GetString(15),
            АудиоCodec = r.IsDBNull(16) ? null : r.GetString(16),
            АудиоChannels = r.IsDBNull(17) ? null : r.GetString(17),
            АудиоLanguages = r.IsDBNull(18) ? null : r.GetString(18),
            DurationSeconds = r.IsDBNull(19) ? null : r.GetInt32(19),
            ContainerExt = r.IsDBNull(20) ? null : r.GetString(20),
            FileSizeBytes = r.IsDBNull(21) ? null : r.GetInt64(21),
            IsПросмотрено = r.GetInt32(22) == 1,
            ShowНазвание = r.IsDBNull(23) ? "" : r.GetString(23),
            VolumeSerial = r.GetString(24),
            IsИзбранное = !r.IsDBNull(25) && r.GetInt32(25) == 1,
            Note = r.IsDBNull(26) ? null : r.GetString(26),
        };
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<Models.TvShowListItem> GetTvShows(IReadВкл.lyDictionary<string, string> connected)
    {
        var list = new List<Models.TvShowListItem>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT s.id, s.title, s.year, s.rating, s.local_poster, s.is_missing,
                   s.volume_serial, d.label, s.is_favorite, s.is_watchlist,
                   (SELECT COUNT(*) FROM tv_episodes e WHERE e.show_id=s.id) AS ep_count,
                   (SELECT COUNT(*) FROM tv_episodes e WHERE e.show_id=s.id AND e.is_watched=1) AS watched_count,
                   (SELECT GROUP_CONCAT(g.name, ', ') FROM tv_show_genres sg JOIN genres g ON g.id=sg.genre_id WHERE sg.show_id=s.id) AS genres,
                   COALESCE(s.date_added, 0),
                   (SELECT COALESCE(MAX(e.last_played_at), 0) FROM tv_episodes e WHERE e.show_id=s.id) AS last_played
              FROM tv_shows s
              LEFT JOIN drives d ON d.volume_serial = s.volume_serial
             ORDER BY s.sort_title, s.title";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var serial = r.GetString(6);
            list.Добавить(new Models.TvShowListItem
            {
                Id = r.GetInt32(0),
                Название = r.GetString(1),
                Год = r.IsDBNull(2) ? null : r.GetInt32(2),
                Рейтинг = r.IsDBNull(3) ? null : r.GetDouble(3),
                LocalPoster = r.IsDBNull(4) ? null : r.GetString(4),
                IsMissing = r.GetInt32(5) == 1,
                VolumeSerial = serial,
                DriveLabel = r.IsDBNull(7) ? null : r.GetString(7),
                IsИзбранное = r.GetInt32(8) == 1,
                IsСписок просмотра = r.GetInt32(9) == 1,
                ЭпизодCount = r.GetInt32(10),
                ПросмотреноCount = r.GetInt32(11),
                ЖанрыCsv = r.IsDBNull(12) ? null : r.GetString(12),
                DateДобавитьed = r.GetInt64(13),
                LastВоспроизвестиed = r.GetInt64(14),
                IsВкл.line = connected.ContainsKey(serial),
            });
        }
        return list;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public int GetTvShowCount()
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tv_shows WHERE is_missing=0";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<Models.TvSeason> GetSeasons(int showId)
    {
        var list = new List<Models.TvSeason>();
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT season, COUNT(*) AS ep, SUM(CASE WHEN is_watched=1 THEN 1 ELSE 0 END) AS watched
              FROM tv_episodes WHERE show_id=@id GROUP BY season ORDER BY season";
        cmd.Parameters.ДобавитьWithValue("@id", showId);
        string? poster = null;
        using (var pc = _conn.СоздатьCommand())
        {
            pc.CommandText = "SELECT local_poster FROM tv_shows WHERE id=@id";
            pc.Parameters.ДобавитьWithValue("@id", showId);
            var o = pc.ExecuteScalar();
            poster = o == null || o == DBNull.Value ? null : (string)o;
        }
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Добавить(new Models.TvSeason
            {
                ShowId = showId,
                Season = r.GetInt32(0),
                ЭпизодCount = r.GetInt32(1),
                ПросмотреноCount = r.IsDBNull(2) ? 0 : r.GetInt32(2),
                PosterPath = poster,
            });
        return list;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<Models.TvЭпизодItem> GetЭпизоды(int showId, int season, IReadВкл.lyDictionary<string, string> connected)
    {
        var list = new List<Models.TvЭпизодItem>();
        string serial = "";
        using (var sc = _conn.СоздатьCommand())
        {
            sc.CommandText = "SELECT volume_serial FROM tv_shows WHERE id=@id";
            sc.Parameters.ДобавитьWithValue("@id", showId);
            var o = sc.ExecuteScalar();
            serial = o == null || o == DBNull.Value ? "" : (string)o;
        }
        bool online = connected.ContainsKey(serial);
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT id, season, episode, title, plot, aired, runtime, rating,
                   local_thumb, video_file_rel_path, is_watched, is_favorite, note
              FROM tv_episodes WHERE show_id=@id AND season=@se ORDER BY episode";
        cmd.Parameters.ДобавитьWithValue("@id", showId);
        cmd.Parameters.ДобавитьWithValue("@se", season);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Добавить(new Models.TvЭпизодItem
            {
                Id = r.GetInt32(0),
                ShowId = showId,
                Season = r.GetInt32(1),
                Эпизод = r.GetInt32(2),
                Название = r.IsDBNull(3) ? "" : r.GetString(3),
                Plot = r.IsDBNull(4) ? null : r.GetString(4),
                Aired = r.IsDBNull(5) ? null : r.GetString(5),
                Продолжительность = r.IsDBNull(6) ? null : r.GetInt32(6),
                Рейтинг = r.IsDBNull(7) ? null : r.GetDouble(7),
                LocalThumb = r.IsDBNull(8) ? null : r.GetString(8),
                VideoFileRelPath = r.IsDBNull(9) ? null : r.GetString(9),
                IsПросмотрено = r.GetInt32(10) == 1,
                IsИзбранное = !r.IsDBNull(11) && r.GetInt32(11) == 1,
                Note = r.IsDBNull(12) ? null : r.GetString(12),
                VolumeSerial = serial,
                IsВкл.line = online,
            });
        }
        return list;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public Models.TvShowDetail? GetTvShowDetail(int showId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT s.id, s.title, s.year, s.rating, s.plot, s.mpaa, s.studio, s.status, s.premiered,
                   s.imdb_id, s.tmdb_id, s.local_poster, s.local_fanart, s.volume_serial,
                   s.folder_rel_path, s.is_favorite, s.is_watchlist, s.note, d.label
              FROM tv_shows s LEFT JOIN drives d ON d.volume_serial=s.volume_serial
             WHERE s.id=@id";
        cmd.Parameters.ДобавитьWithValue("@id", showId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var d = new Models.TvShowDetail
        {
            Id = r.GetInt32(0),
            Название = r.GetString(1),
            Год = r.IsDBNull(2) ? null : r.GetInt32(2),
            Рейтинг = r.IsDBNull(3) ? null : r.GetDouble(3),
            Plot = r.IsDBNull(4) ? null : r.GetString(4),
            Mpaa = r.IsDBNull(5) ? null : r.GetString(5),
            Студия = r.IsDBNull(6) ? null : r.GetString(6),
            Status = r.IsDBNull(7) ? null : r.GetString(7),
            Premiered = r.IsDBNull(8) ? null : r.GetString(8),
            ImdbId = r.IsDBNull(9) ? null : r.GetString(9),
            TmdbId = r.IsDBNull(10) ? null : r.GetString(10),
            LocalPoster = r.IsDBNull(11) ? null : r.GetString(11),
            LocalFanart = r.IsDBNull(12) ? null : r.GetString(12),
            VolumeSerial = r.GetString(13),
            ПапкаRelPath = r.IsDBNull(14) ? null : r.GetString(14),
            IsИзбранное = r.GetInt32(15) == 1,
            IsСписок просмотра = r.GetInt32(16) == 1,
            Note = r.IsDBNull(17) ? null : r.GetString(17),
            DriveLabel = r.IsDBNull(18) ? null : r.GetString(18),
        };
        r.Закрыть();
        // genres
        using (var g = _conn.СоздатьCommand())
        {
            g.CommandText = "SELECT genre.name FROM tv_show_genres sg JOIN genres genre ON genre.id=sg.genre_id WHERE sg.show_id=@id ORDER BY genre.name";
            g.Parameters.ДобавитьWithValue("@id", showId);
            using var gr = g.ExecuteReader();
            while (gr.Read()) d.Жанры.Добавить(gr.GetString(0));
        }
        // actors
        using (var a = _conn.СоздатьCommand())
        {
            a.CommandText = @"SELECT ac.name, sa.role, ac.thumb FROM tv_show_actors sa
                JOIN actors ac ON ac.id=sa.actor_id WHERE sa.show_id=@id ORDER BY sa.sort_order LIMIT 30";
            a.Parameters.ДобавитьWithValue("@id", showId);
            using var ar = a.ExecuteReader();
            while (ar.Read())
                d.Actors.Добавить(new Models.Actor
                {
                    Name = ar.GetString(0),
                    Role = ar.IsDBNull(1) ? null : ar.GetString(1),
                    Thumb = ar.IsDBNull(2) ? null : ar.GetString(2),
                });
        }
        using (var c = _conn.СоздатьCommand())
        {
            c.CommandText = "SELECT COUNT(*), SUM(CASE WHEN is_watched=1 THEN 1 ELSE 0 END) FROM tv_episodes WHERE show_id=@id";
            c.Parameters.ДобавитьWithValue("@id", showId);
            using var cr = c.ExecuteReader();
            if (cr.Read()) { d.ЭпизодCount = cr.GetInt32(0); d.ПросмотреноCount = cr.IsDBNull(1) ? 0 : cr.GetInt32(1); }
        }
        d.Tags = GetTagNamesForShow(showId);
        return d;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetЭпизодПросмотрено(int episodeId, bool watched)
    {
        int showId = 0;
        using (var cmd = _conn.СоздатьCommand())
        {
            cmd.CommandText = @"UPDATE tv_episodes SET is_watched=@w,
                last_played_at = CASE WHEN @w=1 AND (last_played_at IS NULL OR last_played_at=0)
                                       THEN strftime('%s','now') ELSE last_played_at END
                WHERE id=@id RETURNING show_id";
            cmd.Parameters.ДобавитьWithValue("@w", watched ? 1 : 0);
            cmd.Parameters.ДобавитьWithValue("@id", episodeId);
            var o = cmd.ExecuteScalar();
            if (o != null && o != DBNull.Value) showId = Convert.ToInt32(o);
        }
        LogWatchEvent("episode", episodeId, watched ? "marked_watched" : "marked_unwatched");
        if (showId != 0) RaiseTvShowStateChanged(showId);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void MarkЭпизодВоспроизвестиed(int episodeId)
    {
        int showId = 0;
        using (var cmd = _conn.СоздатьCommand())
        {
            cmd.CommandText = "UPDATE tv_episodes SET last_played_at=strftime('%s','now') WHERE id=@id RETURNING show_id";
            cmd.Parameters.ДобавитьWithValue("@id", episodeId);
            var o = cmd.ExecuteScalar();
            if (o != null && o != DBNull.Value) showId = Convert.ToInt32(o);
        }
        LogWatchEvent("episode", episodeId, "played");
        if (showId != 0) RaiseTvShowStateChanged(showId);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetTvShowИзбранное(int showId, bool fav)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "UPDATE tv_shows SET is_favorite=@v WHERE id=@id";
        cmd.Parameters.ДобавитьWithValue("@v", fav ? 1 : 0);
        cmd.Parameters.ДобавитьWithValue("@id", showId);
        cmd.ExecuteNonQuery();
        RaiseTvShowStateChanged(showId);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetTvShowСписок просмотра(int showId, bool wl)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "UPDATE tv_shows SET is_watchlist=@v WHERE id=@id";
        cmd.Parameters.ДобавитьWithValue("@v", wl ? 1 : 0);
        cmd.Parameters.ДобавитьWithValue("@id", showId);
        cmd.ExecuteNonQuery();
        RaiseTvShowStateChanged(showId);
    }

    /// <summary>v4.3.0 (#17): the show's own note. The column, the show folder's
    /// state file and Резервная копия have carried it since 2.8; nothing wrote it until now.
    /// Blank clears it.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetTvShowNote(int showId, string? note)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "UPDATE tv_shows SET note=@n WHERE id=@id";
        cmd.Parameters.ДобавитьWithValue("@n", string.IsNullOrWhiteSpace(note) ? (object)DBNull.Value : note.Trim());
        cmd.Parameters.ДобавитьWithValue("@id", showId);
        cmd.ExecuteNonQuery();
        RaiseTvShowStateChanged(showId);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<int> GetTvShowsWithPersonalState(string? volumeSerial = null)
    {
        var ids = new List<int>();
        using var cmd = _conn.СоздатьCommand();
        var where = @"(is_favorite=1 OR is_watchlist=1 OR (note IS NOT NULL AND TRIM(note)!='')
                       OR EXISTS (SELECT 1 FROM tv_episodes e WHERE e.show_id=tv_shows.id
                                   AND (e.is_watched=1 OR e.last_played_at>0)))";
        if (volumeSerial != null)
        {
            cmd.CommandText = $"SELECT id FROM tv_shows WHERE volume_serial=@s AND {where}";
            cmd.Parameters.ДобавитьWithValue("@s", volumeSerial);
        }
        else cmd.CommandText = $"SELECT id FROM tv_shows WHERE {where}";
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Добавить(r.GetInt32(0));
        return ids;
    }

    /// <summary>
    /// Сохранить user note to the row. Empty/null clears it.
    /// Sidecar file write is handled by the caller (needs the drive letter).
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetNote(int movieId, string? note)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "UPDATE movies SET note=@n WHERE id=@id";
        cmd.Parameters.ДобавитьWithValue("@n", string.IsNullOrWhiteSpace(note) ? (object)DBNull.Value : note);
        cmd.Parameters.ДобавитьWithValue("@id", movieId);
        cmd.ExecuteNonQuery();
        RaisePersonalStateChanged(movieId);
    }

    /// <summary>
    /// Stamps last_played_at = now for "Продолжить просмотр" tracking.
    /// Called when the user hits Воспроизвести in the detail dialog.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void MarkВоспроизвестиed(int movieId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "UPDATE movies SET last_played_at = strftime('%s','now') WHERE id=@id";
        cmd.Parameters.ДобавитьWithValue("@id", movieId);
        cmd.ExecuteNonQuery();
        LogWatchEvent("movie", movieId, "played");
        RaisePersonalStateChanged(movieId);
    }

    // ── v2.9 Watch history ───────────────────────────────────────────────────

    /// <summary>
    /// Append a row to watch_events. Never throws — history logging is a
    /// best-effort side channel; a failure here should never block the
    /// primary action that triggered it.
    /// </summary>
    private void LogWatchEvent(string kind, int itemId, string action)
    {
        try
        {
            using var cmd = _conn.СоздатьCommand();
            cmd.CommandText = @"INSERT INTO watch_events(item_kind, item_id, watched_at, action)
                                VALUES(@k, @id, strftime('%s','now'), @a)";
            cmd.Parameters.ДобавитьWithValue("@k", kind);
            cmd.Parameters.ДобавитьWithValue("@id", itemId);
            cmd.Parameters.ДобавитьWithValue("@a", action);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LogWatchEvent failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Top-N most recently played movies for the "🕓 Недавно просмотренные" row.
    /// Uses movies.last_played_at, which we bump from both Воспроизвести and
    /// mark-watched. Items with last_played_at=0 (never touched) are
    /// excluded.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<MovieListItem> GetRecentlyПросмотреноФильмы(
        Dictionary<string, string> connected, int limit = 12)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT m.id, m.title, m.year, m.rating, m.runtime, m.local_poster,
                   m.is_missing, m.is_favorite, m.is_watched, m.volume_serial, d.label,
                   (SELECT GROUP_CONCAT(g.name, ', ') FROM movie_genres mg
                     JOIN genres g ON g.id=mg.genre_id WHERE mg.movie_id=m.id) AS genres_csv,
                   m.is_watchlist
              FROM movies m
              LEFT JOIN drives d ON d.volume_serial=m.volume_serial
             WHERE m.is_missing = 0 AND m.archived_at IS NULL AND m.last_played_at > 0
             ORDER BY m.last_played_at DESC
             LIMIT @lim";
        cmd.Parameters.ДобавитьWithValue("@lim", limit);
        var list = new List<MovieListItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var serial = r.GetString(9);
            list.Добавить(new MovieListItem
            {
                Id = r.GetInt32(0),
                Название = r.GetString(1),
                Год = r.IsDBNull(2) ? null : r.GetInt32(2),
                Рейтинг = r.IsDBNull(3) ? null : r.GetDouble(3),
                Продолжительность = r.IsDBNull(4) ? null : r.GetInt32(4),
                LocalPoster = r.IsDBNull(5) ? null : r.GetString(5),
                IsMissing = r.GetInt32(6) == 1,
                IsИзбранное = r.GetInt32(7) == 1,
                IsПросмотрено = r.GetInt32(8) == 1,
                VolumeSerial = serial,
                DriveLabel = r.IsDBNull(10) ? null : r.GetString(10),
                ЖанрыCsv = r.IsDBNull(11) ? null : r.GetString(11),
                IsСписок просмотра = !r.IsDBNull(12) && r.GetInt32(12) == 1,
                IsВкл.line = connected.ContainsKey(serial),
            });
        }
        return list;
    }

    public enum Вкл.ThisDayReason { Просмотрено, Дата выхода }
    public record Вкл.ThisDayMatch(MovieListItem Movie, Вкл.ThisDayReason Reason, int ГодsAgo);

    /// <summary>
    /// Фильмы tied to today's calendar day — either you watched them on
    /// this date in a past year (Reason=Просмотрено), or they were released
    /// on this date (Reason=Дата выхода). The Дата выхода branch turns the
    /// banner into a small cinema-history calendar, so new users without
    /// much watch history still get something most days. A movie appearing
    /// in both sources is returned once with Reason=Просмотрено (more personal
    /// wins).
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<Вкл.ThisDayMatch> GetВкл.ThisDayItems(
        Dictionary<string, string> connected, int limit = 12)
    {
        var matches = new List<Вкл.ThisDayMatch>();
        var seen = new HashSet<int>();
        int nowГод = DateTime.Now.Год;

        // ── Source 1: you watched it on this date in a past year ─────────
        // Read events directly so we catch every historical touch, not
        // just the most recent last_played_at on each movie.
        using (var cmd = _conn.СоздатьCommand())
        {
            cmd.CommandText = @"
                SELECT m.id, m.title, m.year, m.rating, m.runtime, m.local_poster,
                       m.is_missing, m.is_favorite, m.is_watched, m.volume_serial, d.label,
                       (SELECT GROUP_CONCAT(g.name, ', ') FROM movie_genres mg
                         JOIN genres g ON g.id=mg.genre_id WHERE mg.movie_id=m.id) AS genres_csv,
                       m.is_watchlist,
                       MAX(e.watched_at) AS most_recent_match,
                       АКТЁРЫ(strftime('%Y', e.watched_at, 'unixepoch', 'localtime') AS INTEGER) AS match_year
                  FROM watch_events e
                  JOIN movies m ON m.id = e.item_id
                  LEFT JOIN drives d ON d.volume_serial = m.volume_serial
                 WHERE e.item_kind = 'movie'
                   AND m.is_missing = 0
                   AND m.archived_at IS NULL
                   AND strftime('%m-%d', e.watched_at, 'unixepoch', 'localtime')
                       = strftime('%m-%d', 'now', 'localtime')
                   AND strftime('%Y', e.watched_at, 'unixepoch', 'localtime')
                       < strftime('%Y', 'now', 'localtime')
                 GROUP BY m.id
                 ORDER BY most_recent_match DESC
                 LIMIT @lim";
            cmd.Parameters.ДобавитьWithValue("@lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var movie = ReadMovieRow(r, connected);
                var matchГод = r.IsDBNull(14) ? nowГод - 1 : r.GetInt32(14);
                matches.Добавить(new Вкл.ThisDayMatch(movie, Вкл.ThisDayReason.Просмотрено, nowГод - matchГод));
                seen.Добавить(movie.Id);
            }
        }

        // ── Source 2: released on this date in a past year ───────────────
        // m.premiered is a TEXT ISO date "YYYY-MM-DD" when available.
        // Some nfos only give "YYYY" — length<10 rows are skipped.
        int releasedBudget = limit - matches.Count;
        if (releasedBudget > 0)
        {
            using var cmd = _conn.СоздатьCommand();
            cmd.CommandText = @"
                SELECT m.id, m.title, m.year, m.rating, m.runtime, m.local_poster,
                       m.is_missing, m.is_favorite, m.is_watched, m.volume_serial, d.label,
                       (SELECT GROUP_CONCAT(g.name, ', ') FROM movie_genres mg
                         JOIN genres g ON g.id=mg.genre_id WHERE mg.movie_id=m.id) AS genres_csv,
                       m.is_watchlist,
                       NULL AS most_recent_match,
                       АКТЁРЫ(substr(m.premiered, 1, 4) AS INTEGER) AS prem_year
                  FROM movies m
                  LEFT JOIN drives d ON d.volume_serial = m.volume_serial
                 WHERE m.is_missing = 0
                   AND m.archived_at IS NULL
                   AND m.premiered IS NOT NULL
                   AND length(m.premiered) >= 10
                   AND substr(m.premiered, 6, 5) = strftime('%m-%d', 'now', 'localtime')
                   AND АКТЁРЫ(substr(m.premiered, 1, 4) AS INTEGER)
                       < АКТЁРЫ(strftime('%Y', 'now', 'localtime') AS INTEGER)
                 ORDER BY prem_year DESC
                 LIMIT @lim";
            cmd.Parameters.ДобавитьWithValue("@lim", releasedBudget + seen.Count);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var movie = ReadMovieRow(r, connected);
                if (seen.Contains(movie.Id)) continue;  // already in Просмотрено
                var premГод = r.IsDBNull(14) ? nowГод - 1 : r.GetInt32(14);
                matches.Добавить(new Вкл.ThisDayMatch(movie, Вкл.ThisDayReason.Дата выхода, nowГод - premГод));
                seen.Добавить(movie.Id);
                if (matches.Count >= limit) break;
            }
        }
        return matches;
    }

    /// <summary>
    /// Cheap "is there anything today?" probe used by the sidebar to decide
    /// whether to show the В этот день entry. Combines the two sources of
    /// GetВкл.ThisDayItems but bails as soon as a match is found.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool HasВкл.ThisDayMatches()
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT 1 FROM watch_events
             WHERE item_kind='movie'
               AND strftime('%m-%d', watched_at, 'unixepoch', 'localtime') = strftime('%m-%d', 'now', 'localtime')
               AND strftime('%Y', watched_at, 'unixepoch', 'localtime') < strftime('%Y', 'now', 'localtime')
             LIMIT 1";
        var v = cmd.ExecuteScalar();
        if (v != null && v != DBNull.Value) return true;

        using var cmd2 = _conn.СоздатьCommand();
        cmd2.CommandText = @"
            SELECT 1 FROM movies
             WHERE is_missing=0
               AND archived_at IS NULL
               AND premiered IS NOT NULL
               AND length(premiered) >= 10
               AND substr(premiered, 6, 5) = strftime('%m-%d', 'now', 'localtime')
               AND АКТЁРЫ(substr(premiered, 1, 4) AS INTEGER)
                   < АКТЁРЫ(strftime('%Y', 'now', 'localtime') AS INTEGER)
             LIMIT 1";
        var v2 = cmd2.ExecuteScalar();
        return v2 != null && v2 != DBNull.Value;
    }

    /// <summary>
    /// Shared MovieListItem mapping. Expects columns 0..12 in the same
    /// shape as GetФильмы. Used by GetВкл.ThisDayItems' two branches so the
    /// SELECT lists stay identical and easy to keep in sync.
    /// </summary>
    private static MovieListItem ReadMovieRow(
        Microsoft.Data.Sqlite.SqliteDataReader r,
        IReadВкл.lyDictionary<string, string> connected)
    {
        var serial = r.GetString(9);
        return new MovieListItem
        {
            Id = r.GetInt32(0),
            Название = r.GetString(1),
            Год = r.IsDBNull(2) ? null : r.GetInt32(2),
            Рейтинг = r.IsDBNull(3) ? null : r.GetDouble(3),
            Продолжительность = r.IsDBNull(4) ? null : r.GetInt32(4),
            LocalPoster = r.IsDBNull(5) ? null : r.GetString(5),
            IsMissing = r.GetInt32(6) == 1,
            IsИзбранное = r.GetInt32(7) == 1,
            IsПросмотрено = r.GetInt32(8) == 1,
            VolumeSerial = serial,
            DriveLabel = r.IsDBNull(10) ? null : r.GetString(10),
            ЖанрыCsv = r.IsDBNull(11) ? null : r.GetString(11),
            IsСписок просмотра = !r.IsDBNull(12) && r.GetInt32(12) == 1,
            IsВкл.line = connected.ContainsKey(serial),
        };
    }

    /// <summary>
    /// Count of distinct movies the user has played at least once. Cheaper
    /// than re-querying the full Недавно просмотренные list; used for the
    /// sidebar entry's badge.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int GetRecentlyПросмотреноCount()
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM movies WHERE last_played_at > 0 AND is_missing=0 AND archived_at IS NULL";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    // ── v2.9 Tags ────────────────────────────────────────────────────────────

    /// <summary>
    /// Find an existing tag (case-insensitive) or create it. Trims and
    /// rejects empty / whitespace-only names. Returns the tag id.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int EnsureTag(string name)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) throw new ArgumentException("Tag name is empty", nameof(name));

        // Lookup (NOCASE collation set on the column).
        using (var sel = _conn.СоздатьCommand())
        {
            sel.CommandText = "SELECT id FROM tags WHERE name = @n";
            sel.Parameters.ДобавитьWithValue("@n", clean);
            var existing = sel.ExecuteScalar();
            if (existing != null && existing != DBNull.Value) return Convert.ToInt32(existing);
        }
        using (var ins = _conn.СоздатьCommand())
        {
            ins.CommandText = "INSERT INTO tags(name) VALUES(@n) RETURNING id";
            ins.Parameters.ДобавитьWithValue("@n", clean);
            return Convert.ToInt32(ins.ExecuteScalar());
        }
    }

    /// <summary>
    /// Permanently remove a tag. Cascades to movie_tags / tv_show_tags via FK.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void УдалитьTag(int tagId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "DELETE FROM tags WHERE id=@id";
        cmd.Parameters.ДобавитьWithValue("@id", tagId);
        cmd.ExecuteNonQuery();
    }

    public record TagSummary(int Id, string Name, int MovieCount, int ShowCount);

    /// <summary>Every tag with its current movie and show counts.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<TagSummary> GetВсеTags()
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT t.id, t.name,
                   (SELECT COUNT(*) FROM movie_tags mt
                     JOIN movies mv ON mv.id=mt.movie_id AND mv.archived_at IS NULL
                     WHERE mt.tag_id = t.id) AS mc,
                   (SELECT COUNT(*) FROM tv_show_tags st WHERE st.tag_id = t.id) AS sc
              FROM tags t
             ORDER BY t.name COLLATE NOCASE";
        var list = new List<TagSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Добавить(new TagSummary(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
        return list;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<string> GetTagNamesForMovie(int movieId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"SELECT t.name FROM movie_tags mt
                            JOIN tags t ON t.id = mt.tag_id
                            WHERE mt.movie_id = @id
                            ORDER BY t.name COLLATE NOCASE";
        cmd.Parameters.ДобавитьWithValue("@id", movieId);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Добавить(r.GetString(0));
        return list;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<string> GetTagNamesForShow(int showId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"SELECT t.name FROM tv_show_tags st
                            JOIN tags t ON t.id = st.tag_id
                            WHERE st.show_id = @id
                            ORDER BY t.name COLLATE NOCASE";
        cmd.Parameters.ДобавитьWithValue("@id", showId);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Добавить(r.GetString(0));
        return list;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ДобавитьMovieTag(int movieId, int tagId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO movie_tags(movie_id, tag_id) VALUES(@m, @t)";
        cmd.Parameters.ДобавитьWithValue("@m", movieId);
        cmd.Parameters.ДобавитьWithValue("@t", tagId);
        cmd.ExecuteNonQuery();
        RaisePersonalStateChanged(movieId);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RemoveMovieTag(int movieId, int tagId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "DELETE FROM movie_tags WHERE movie_id=@m AND tag_id=@t";
        cmd.Parameters.ДобавитьWithValue("@m", movieId);
        cmd.Parameters.ДобавитьWithValue("@t", tagId);
        cmd.ExecuteNonQuery();
        RaisePersonalStateChanged(movieId);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ДобавитьShowTag(int showId, int tagId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO tv_show_tags(show_id, tag_id) VALUES(@s, @t)";
        cmd.Parameters.ДобавитьWithValue("@s", showId);
        cmd.Parameters.ДобавитьWithValue("@t", tagId);
        cmd.ExecuteNonQuery();
        RaiseTvShowStateChanged(showId);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RemoveShowTag(int showId, int tagId)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "DELETE FROM tv_show_tags WHERE show_id=@s AND tag_id=@t";
        cmd.Parameters.ДобавитьWithValue("@s", showId);
        cmd.Parameters.ДобавитьWithValue("@t", tagId);
        cmd.ExecuteNonQuery();
        RaiseTvShowStateChanged(showId);
    }

    // ── v2.9 Per-episode favorite + note ─────────────────────────────────────

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetЭпизодИзбранное(int episodeId, bool fav)
    {
        int showId = 0;
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "UPDATE tv_episodes SET is_favorite=@v WHERE id=@id RETURNING show_id";
        cmd.Parameters.ДобавитьWithValue("@v", fav ? 1 : 0);
        cmd.Parameters.ДобавитьWithValue("@id", episodeId);
        var o = cmd.ExecuteScalar();
        if (o != null && o != DBNull.Value) showId = Convert.ToInt32(o);
        if (showId != 0) RaiseTvShowStateChanged(showId);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetЭпизодNote(int episodeId, string? note)
    {
        int showId = 0;
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "UPDATE tv_episodes SET note=@n WHERE id=@id RETURNING show_id";
        cmd.Parameters.ДобавитьWithValue("@n", string.IsNullOrWhiteSpace(note) ? (object)DBNull.Value : note);
        cmd.Parameters.ДобавитьWithValue("@id", episodeId);
        var o = cmd.ExecuteScalar();
        if (o != null && o != DBNull.Value) showId = Convert.ToInt32(o);
        if (showId != 0) RaiseTvShowStateChanged(showId);
    }

    /// <summary>
    /// Count of "in progress" movies — played at least once and not yet watched.
    /// Used to gate the sidebar Продолжить просмотр shortcut visibility/badge.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int GetContinueWatchingCount()
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"SELECT COUNT(*) FROM movies
                            WHERE last_played_at > 0 AND is_watched = 0 AND is_missing = 0 AND archived_at IS NULL";
        return (int)(long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Picks a random unwatched movie id, preferring online drives.
    /// Returns null if the library is empty or fully watched.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int? GetRandomНе просмотреноId(Dictionary<string, string> connected)
    {
        if (connected.Count == 0)
        {
            using var any = _conn.СоздатьCommand();
            any.CommandText = "SELECT id FROM movies WHERE is_watched=0 AND is_missing=0 AND archived_at IS NULL ORDER BY RANDOM() LIMIT 1";
            var v = any.ExecuteScalar();
            return v == null || v == DBNull.Value ? null : Convert.ToInt32(v);
        }

        // Restrict to drives that are currently online so the user can actually play it.
        var serials = string.Join(",", connected.Keys.Select(s => $"'{s.Replace("'", "''")}'"));
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = $@"SELECT id FROM movies
                              WHERE is_watched=0 AND is_missing=0 AND archived_at IS NULL
                                AND volume_serial IN ({serials})
                              ORDER BY RANDOM() LIMIT 1";
        var val = cmd.ExecuteScalar();
        if (val == null || val == DBNull.Value)
        {
            // Fall back to any unwatched (drive offline) so the user still gets a pick.
            using var fb = _conn.СоздатьCommand();
            fb.CommandText = "SELECT id FROM movies WHERE is_watched=0 AND is_missing=0 AND archived_at IS NULL ORDER BY RANDOM() LIMIT 1";
            val = fb.ExecuteScalar();
            if (val == null || val == DBNull.Value) return null;
        }
        return Convert.ToInt32(val);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void SetСписок просмотра(int movieId, bool isСписок просмотра)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = "UPDATE movies SET is_watchlist = @val WHERE id = @id";
        cmd.Parameters.ДобавитьWithValue("@val", isСписок просмотра ? 1 : 0);
        cmd.Parameters.ДобавитьWithValue("@id", movieId);
        cmd.ExecuteNonQuery();
        RaisePersonalStateChanged(movieId);
    }

    // ── Discovery (v2.9) ─────────────────────────────────────────────────────

    /// <summary>
    /// v2.9 — Top N most recently added movies for the "Недавно добавленные" row
    /// on the Медиатека home. Honours connected-drive availability so offline
    /// rows still surface (badge says НЕ В СЕТИ), but online comes first.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<MovieListItem> GetRecentlyДобавитьedФильмы(
        Dictionary<string, string> connected, int limit = 12)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = @"
            SELECT m.id, m.title, m.year, m.rating, m.runtime, m.local_poster,
                   m.is_missing, m.is_favorite, m.is_watched, m.volume_serial, d.label,
                   (SELECT GROUP_CONCAT(g.name, ', ') FROM movie_genres mg
                     JOIN genres g ON g.id=mg.genre_id WHERE mg.movie_id=m.id) AS genres_csv,
                   m.is_watchlist
              FROM movies m
              LEFT JOIN drives d ON d.volume_serial=m.volume_serial
             WHERE m.is_missing = 0
             ORDER BY m.date_added DESC, m.id DESC
             LIMIT @lim";
        cmd.Parameters.ДобавитьWithValue("@lim", limit);
        var list = new List<MovieListItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var serial = r.GetString(9);
            list.Добавить(new MovieListItem
            {
                Id = r.GetInt32(0),
                Название = r.GetString(1),
                Год = r.IsDBNull(2) ? null : r.GetInt32(2),
                Рейтинг = r.IsDBNull(3) ? null : r.GetDouble(3),
                Продолжительность = r.IsDBNull(4) ? null : r.GetInt32(4),
                LocalPoster = r.IsDBNull(5) ? null : r.GetString(5),
                IsMissing = r.GetInt32(6) == 1,
                IsИзбранное = r.GetInt32(7) == 1,
                IsПросмотрено = r.GetInt32(8) == 1,
                VolumeSerial = serial,
                DriveLabel = r.IsDBNull(10) ? null : r.GetString(10),
                ЖанрыCsv = r.IsDBNull(11) ? null : r.GetString(11),
                IsСписок просмотра = !r.IsDBNull(12) && r.GetInt32(12) == 1,
                IsВкл.line = connected.ContainsKey(serial),
            });
        }
        return list;
    }

    /// <summary>
    /// v2.9 — Filter-aware "Surprise Me". Same WHERE as GetФильмы for the
    /// supplied ListOptions, but ORDER BY RANDOM() LIMIT 1. Lets the toolbar
    /// dice button pick from whatever the user is currently viewing (e.g.
    /// random comedy from the 90s, random movie in a list). Returns null if
    /// the filter set is empty.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int? GetRandomMovieIdMatching(ListOptions opts, Dictionary<string, string> connected)
    {
        var (whereStr, _) = BuildMovieListWhere(opts);
        // Prefer movies on connected drives so the user can actually play
        // the pick. Fall back to any match if every match happens to be on
        // an offline drive (still better than "no result").
        if (connected.Count > 0)
        {
            var serials = string.Join(",", connected.Keys.Select(s => $"'{s.Replace("'", "''")}'"));
            var extraWhere = string.IsNullOrEmpty(whereStr)
                ? $"WHERE m.volume_serial IN ({serials}) AND m.is_missing=0"
                : whereStr + $" AND m.volume_serial IN ({serials}) AND m.is_missing=0";
            using var cmd = _conn.СоздатьCommand();
            cmd.CommandText = $"SELECT m.id FROM movies m {extraWhere} ORDER BY RANDOM() LIMIT 1";
            BindMovieListParams(cmd, opts, includePaging: false);
            var v = cmd.ExecuteScalar();
            if (v != null && v != DBNull.Value) return Convert.ToInt32(v);
        }
        // Fallback — any matching movie regardless of online status.
        using (var fb = _conn.СоздатьCommand())
        {
            var extraWhere = string.IsNullOrEmpty(whereStr) ? "WHERE m.is_missing=0" : whereStr + " AND m.is_missing=0";
            fb.CommandText = $"SELECT m.id FROM movies m {extraWhere} ORDER BY RANDOM() LIMIT 1";
            BindMovieListParams(fb, opts, includePaging: false);
            var v = fb.ExecuteScalar();
            return v == null || v == DBNull.Value ? null : Convert.ToInt32(v);
        }
    }

    /// <summary>
    /// v2.9 — Название/original-title search across Сериалы so the global
    /// search box on the Медиатека page also surfaces shows. Multi-token AND
    /// (same shape as the movie search): each whitespace-separated word has
    /// to match somewhere. Returns at most `limit` items, ordered by
    /// sort_title for stable display.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public List<Models.TvShowListItem> ПоискTvShows(
        string query, IReadВкл.lyDictionary<string, string> connected, int limit = 24)
    {
        var list = new List<Models.TvShowListItem>();
        if (string.IsNullOrWhiteSpace(query)) return list;
        var tokens = TokenizeПоиск(query);
        if (tokens.Length == 0) return list;

        var clauses = new List<string>();
        for (int i = 0; i < tokens.Length; i++)
        {
            var p = $"@q{i}";
            // Название / original title / year / cast — no plot (same reason as
            // the movie search: short words flood the results via plot text).
            clauses.Добавить($@"(s.title LIKE {p} ESCAPE '\' OR s.original_title LIKE {p} ESCAPE '\'
                OR АКТЁРЫ(s.year AS TEXT) LIKE {p} ESCAPE '\'
                OR EXISTS (SELECT 1 FROM tv_show_actors sa JOIN actors a ON a.id=sa.actor_id
                            WHERE sa.show_id=s.id AND a.name LIKE {p} ESCAPE '\'))");
        }
        var whereStr = "WHERE s.is_missing=0 AND " + string.Join(" AND ", clauses);

        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = $@"
            SELECT s.id, s.title, s.year, s.rating, s.local_poster, s.is_missing,
                   s.volume_serial, d.label, s.is_favorite, s.is_watchlist,
                   (SELECT COUNT(*) FROM tv_episodes e WHERE e.show_id=s.id) AS ep_count,
                   (SELECT COUNT(*) FROM tv_episodes e WHERE e.show_id=s.id AND e.is_watched=1) AS watched_count,
                   (SELECT GROUP_CONCAT(g.name, ', ') FROM tv_show_genres sg
                     JOIN genres g ON g.id=sg.genre_id WHERE sg.show_id=s.id) AS genres
              FROM tv_shows s
              LEFT JOIN drives d ON d.volume_serial = s.volume_serial
              {whereStr}
             ORDER BY s.sort_title, s.title
             LIMIT @lim";
        for (int i = 0; i < tokens.Length; i++)
            cmd.Parameters.ДобавитьWithValue($"@q{i}", $"%{EscapeLike(tokens[i])}%");
        cmd.Parameters.ДобавитьWithValue("@lim", limit);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var serial = r.GetString(6);
            list.Добавить(new Models.TvShowListItem
            {
                Id = r.GetInt32(0),
                Название = r.GetString(1),
                Год = r.IsDBNull(2) ? null : r.GetInt32(2),
                Рейтинг = r.IsDBNull(3) ? null : r.GetDouble(3),
                LocalPoster = r.IsDBNull(4) ? null : r.GetString(4),
                IsMissing = r.GetInt32(5) == 1,
                VolumeSerial = serial,
                DriveLabel = r.IsDBNull(7) ? null : r.GetString(7),
                IsИзбранное = r.GetInt32(8) == 1,
                IsСписок просмотра = r.GetInt32(9) == 1,
                ЭпизодCount = r.GetInt32(10),
                ПросмотреноCount = r.GetInt32(11),
                ЖанрыCsv = r.IsDBNull(12) ? null : r.GetString(12),
                IsВкл.line = connected.ContainsKey(serial),
            });
        }
        return list;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Exec(string sql)
    {
        using var cmd = _conn.СоздатьCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Dispose() => _conn.Dispose();
}
