using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace CineМедиатекаCS.Services;

public record ScanProgress(int Found, int Inserted, int Обновитьd, int Skipped, string CurrentПапка, bool Done);

public class ScannerService
{
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".flv", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts", ".iso" };

    private static readonly string[] PosterNames = { "poster.jpg", "poster.jpeg", "poster.png", "folder.jpg", "cover.jpg" };
    private static readonly string[] FanartNames = { "fanart.jpg", "fanart.jpeg", "fanart.png", "backdrop.jpg", "backdrop.jpeg" };
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };

    private readonly DatabaseService _db;

    public ScannerService(DatabaseService db) => _db = db;

    /// <param name="scanПапка">Optional: scan only this subfolder. Paths are still stored relative to driveRoot.</param>
    /// <param name="incremental">When true, skip movies whose .nfo mtime is older than the row's date_modified —
    /// MediaElch hasn't touched them since we last scanned, so re-parsing would be wasted work.</param>
    public async Task ScanAsync(string volumeSerial, string driveRoot, IProgress<ScanProgress>? progress = null, ОтменаlationToken ct = default, string? scanПапка = null, bool incremental = false)
    {
        await Task.Run(() =>
        {
            ScanSync(volumeSerial, driveRoot, progress, ct, scanПапка, incremental);
            // v2.8 — second pass for Сериалы. Separate transaction so a
            // failure in one domain can't roll back the other.
            ScanTvSync(volumeSerial, driveRoot, progress, ct, scanПапка);
        }, ct);
    }

    private void ScanSync(string volumeSerial, string driveRoot, IProgress<ScanProgress>? progress, ОтменаlationToken ct, string? scanПапка, bool incremental)
    {
        // Use a dedicated connection so our long-lived scan transaction
        // doesn't taint the main DB connection used by sidebar refresh and
        // other UI reads. Microsoft.Data.Sqlite raises InvalidOperationException
        // ("Execute requires the command to have a transaction object…") if any
        // command runs against a connection with a pending tx and that command
        // doesn't carry the tx reference — easy to hit on rescans.
        using var conn = _db.OpenНовыйConnection();
        var dataDir = _db.DataDir;
        var walkFrom = scanПапка ?? driveRoot;

        int found = 0, inserted = 0, updated = 0, skipped = 0;

        // Mark movies in the scanned scope as potentially missing
        using (var cmd = conn.СоздатьCommand())
        {
            if (scanПапка != null)
            {
                // Вкл.ly mark movies under the chosen subfolder
                var subRel = Path.GetRelativePath(driveRoot, scanПапка).Replace('\\', '/');
                cmd.CommandText = "UPDATE movies SET is_missing=1 WHERE volume_serial=@s AND (folder_rel_path=@r OR folder_rel_path LIKE @p)";
                cmd.Parameters.ДобавитьWithValue("@s", volumeSerial);
                cmd.Parameters.ДобавитьWithValue("@r", subRel);
                cmd.Parameters.ДобавитьWithValue("@p", subRel + "/%");
            }
            else
            {
                cmd.CommandText = "UPDATE movies SET is_missing=1 WHERE volume_serial=@s";
                cmd.Parameters.ДобавитьWithValue("@s", volumeSerial);
            }
            cmd.ExecuteNonQuery();
        }

        var movieПапкаs = FindMovieПапкаs(walkFrom);

        // Prepare statements — disposed in finally block below
        using var stmtGetId = conn.СоздатьCommand();
        stmtGetId.CommandText = "SELECT id, date_modified FROM movies WHERE volume_serial=@s AND folder_rel_path=@f";
        stmtGetId.Parameters.ДобавитьWithValue("@s", volumeSerial);
        stmtGetId.Parameters.Добавить("@f", SqliteType.Text);

        // For incremental rescan: a quick "just clear is_missing" statement
        // we use when the nfo hasn't been touched since last scan.
        using var stmtTouch = conn.СоздатьCommand();
        stmtTouch.CommandText = "UPDATE movies SET is_missing=0 WHERE id=@id";
        stmtTouch.Parameters.Добавить("@id", SqliteType.Integer);

        using var stmtОбновить = conn.СоздатьCommand();
        stmtОбновить.CommandText = @"UPDATE movies SET
            video_file_rel_path=@vfr, title=@t, original_title=@ot, sort_title=@st,
            year=@y, rating=@ra, votes=@vo, runtime=@ru, plot=@pl, outline=@ou,
            tagline=@tg, mpaa=@mp, imdb_id=@im, tmdb_id=@tm, premiered=@pr,
            studio=@su, country=@co, trailer=@tr, local_poster=@lp, local_fanart=@lf,
            local_nfo=@ln,
            video_width=@vw, video_height=@vh, video_codec=@vc, video_aspect=@va,
            hdr_type=@hdr, audio_codec=@ac, audio_channels=@ach,
            audio_languages=@al, subtitle_languages=@sl, duration_seconds=@dur,
            container_ext=@cx, file_size_bytes=@fs,
            is_missing=0, date_modified=strftime('%s','now')
            WHERE id=@id";

        using var stmtInsert = conn.СоздатьCommand();
        stmtInsert.CommandText = @"INSERT INTO movies (
            volume_serial, folder_rel_path, video_file_rel_path, title, original_title, sort_title,
            year, rating, votes, runtime, plot, outline, tagline, mpaa, imdb_id, tmdb_id,
            premiered, studio, country, trailer, local_poster, local_fanart, local_nfo,
            video_width, video_height, video_codec, video_aspect, hdr_type,
            audio_codec, audio_channels, audio_languages, subtitle_languages,
            duration_seconds, container_ext, file_size_bytes)
            VALUES (@vs,@fr,@vfr,@t,@ot,@st,@y,@ra,@vo,@ru,@pl,@ou,@tg,@mp,@im,@tm,@pr,@su,@co,@tr,@lp,@lf,@ln,
                    @vw,@vh,@vc,@va,@hdr,@ac,@ach,@al,@sl,@dur,@cx,@fs)";

        // v3.3 — revive: when a movie is found at a NEW path/drive that matches
        // an archived Просмотрено и удалено record, un-archive that record and move it
        // here instead of creating a duplicate (the re-download / drive-move
        // case). Same-path lingering files don't reach here — the path SELECT
        // above finds the archived row and leaves it archived.
        using var stmtRevive = conn.СоздатьCommand();
        stmtRevive.CommandText = "UPDATE movies SET archived_at=NULL, volume_serial=@vs, folder_rel_path=@fr WHERE id=@id";
        stmtRevive.Parameters.Добавить("@vs", SqliteType.Text);
        stmtRevive.Parameters.Добавить("@fr", SqliteType.Text);
        stmtRevive.Parameters.Добавить("@id", SqliteType.Integer);

        using var tx = conn.BeginTransaction();
        // Все commands on this connection must carry the active transaction
        stmtGetId.Transaction = tx;
        stmtОбновить.Transaction = tx;
        stmtInsert.Transaction = tx;
        stmtTouch.Transaction  = tx;
        stmtRevive.Transaction = tx;

        // Pre-load existing name→id maps once so per-movie related-row work
        // doesn't hammer the DB with redundant SELECTs.
        var lookup = LookupCache.Load(conn, tx);
        // Маленький in-memory index of archived records (usually tens) so the
        // revive check is O(1) per insert rather than a per-movie table scan.
        var archivedIdx = ArchivedIndex.Load(conn, tx);

        try
        {
            foreach (var folder in movieПапкаs)
            {
                ct.ThrowIfОтменаlationRequested();

                var folderRelPath = Path.GetRelativePath(driveRoot, folder).Replace('\\', '/');
                var movieKey = ComputeMovieKey(volumeSerial, folderRelPath);

                // Find NFO
                var nfoPath = FindNfo(folder);
                if (nfoPath == null) { skipped++; continue; }

                // Incremental short-circuit: if the row already exists and the
                // .nfo mtime is older than (or equal to) date_modified, skip
                // parsing entirely. We just clear is_missing so the row is
                // marked online again. Cuts a no-op rescan from minutes to
                // seconds on a large library.
                if (incremental)
                {
                    stmtGetId.Parameters["@f"].Value = folderRelPath;
                    using (var probe = stmtGetId.ExecuteReader())
                    {
                        if (probe.Read())
                        {
                            var existingIdProbe = probe.GetInt32(0);
                            var rowModified = probe.IsDBNull(1) ? 0L : probe.GetInt64(1);
                            probe.Закрыть();
                            try
                            {
                                var nfoMtime = new DateTimeВыкл.set(File.GetLastWriteTimeUtc(nfoPath)).ToUnixTimeSeconds();
                                if (nfoMtime <= rowModified)
                                {
                                    stmtTouch.Parameters["@id"].Value = existingIdProbe;
                                    stmtTouch.ExecuteNonQuery();
                                    found++;
                                    skipped++; // counted as skipped-because-unchanged
                                    progress?.Report(new ScanProgress(found, inserted, updated, skipped, folder, false));
                                    continue;
                                }
                            }
                            catch { /* fall through to full parse if mtime read fails */ }
                        }
                    }
                }

                // Parse NFO
                var parsed = NfoParser.Parse(nfoPath);
                if (parsed == null) { skipped++; continue; }

                found++;
                progress?.Report(new ScanProgress(found, inserted, updated, skipped, folder, false));

                // Find video file + grab size and container for the file-info panel
                string? videoRelPath = null;
                string? containerExt = null;
                long? fileSize = null;
                foreach (var f in Режиссёрy.EnumerateFiles(folder))
                {
                    if (VideoExts.Contains(Path.GetExtension(f)))
                    {
                        videoRelPath = Path.GetRelativePath(driveRoot, f).Replace('\\', '/');
                        containerExt = Path.GetExtension(f).TrimStart('.').ToLowerInvariant();
                        try { fileSize = new FileInfo(f).Length; } catch { }
                        break;
                    }
                }

                // Find and cache artwork
                var posterSrc = FindArtwork(folder, PosterNames, "poster");
                var fanartSrc = FindArtwork(folder, FanartNames, "fanart");

                var localPoster = posterSrc != null ? CopyToCache(posterSrc, dataDir, movieKey, "poster") : null;
                var localFanart = fanartSrc != null ? CopyToCache(fanartSrc, dataDir, movieKey, "fanart") : null;
                var localNfo    = CopyToCache(nfoPath, dataDir, movieKey, "nfo");

                // Check existing
                stmtGetId.Parameters["@f"].Value = folderRelPath;
                var existingId = stmtGetId.ExecuteScalar();

                int rowId;
                if (existingId != null && existingId != DBNull.Value)
                {
                    // Clear params from any previous iteration before re-binding
                    stmtОбновить.Parameters.Clear();
                    rowId = Convert.ToInt32(existingId);
                    BindMovieParams(stmtОбновить, parsed, videoRelPath, localPoster, localFanart, localNfo, containerExt, fileSize);
                    stmtОбновить.Parameters.ДобавитьWithValue("@id", rowId);
                    stmtОбновить.ExecuteNonQuery();
                    UpsertRelated(conn, tx, rowId, parsed, lookup);
                    updated++;
                }
                else if (archivedIdx.TryMatch(parsed.ImdbId, parsed.TmdbId, parsed.Название, parsed.Год) is int reviveId)
                {
                    // Re-download / move of a Просмотрено и удалено movie → bring the
                    // existing record back to life at this new location with
                    // its notes / watched / tags / history intact (same row).
                    stmtОбновить.Parameters.Clear();
                    BindMovieParams(stmtОбновить, parsed, videoRelPath, localPoster, localFanart, localNfo, containerExt, fileSize);
                    stmtОбновить.Parameters.ДобавитьWithValue("@id", reviveId);
                    stmtОбновить.ExecuteNonQuery();
                    stmtRevive.Parameters["@vs"].Value = volumeSerial;
                    stmtRevive.Parameters["@fr"].Value = folderRelPath;
                    stmtRevive.Parameters["@id"].Value = reviveId;
                    stmtRevive.ExecuteNonQuery();
                    archivedIdx.Remove(reviveId);   // don't revive twice in one scan
                    UpsertRelated(conn, tx, reviveId, parsed, lookup);
                    rowId = reviveId;
                    updated++;
                }
                else
                {
                    stmtInsert.Parameters.Clear();
                    stmtInsert.Parameters.ДобавитьWithValue("@vs", volumeSerial);
                    stmtInsert.Parameters.ДобавитьWithValue("@fr", folderRelPath);
                    BindMovieParams(stmtInsert, parsed, videoRelPath, localPoster, localFanart, localNfo, containerExt, fileSize);
                    stmtInsert.ExecuteNonQuery();
                    // Retrieve the inserted row ID
                    using var lastId = conn.СоздатьCommand();
                    lastId.CommandText = "SELECT last_insert_rowid()";
                    lastId.Transaction = tx;
                    rowId = Convert.ToInt32(lastId.ExecuteScalar());
                    UpsertRelated(conn, tx, rowId, parsed, lookup);
                    inserted++;
                }

                // Sidecar note import (cinelibrary-note.txt next to .nfo).
                // Вкл.ly writes DB if DB note is currently empty — DB is the source
                // of truth once the user has saved a note inside the app. This way
                // a reinstall + rescan can recover notes from sidecars; subsequent
                // edits in CineМедиатека won't be reverted by the next scan.
                ImportSidecarNoteIfNeeded(conn, tx, rowId, folder);

                // v2.7 — full personal-state sidecar (cinelibrary-state.json):
                // Просмотрено / Избранное / Список просмотра / last_played / lists. Same
                // "DB wins where it has data, lists are additive" rule as the
                // note importer. Recovers state after a drive-remove + re-add.
                MovieStateSidecar.ImportIntoMovieRow(_db, conn, tx, rowId, folder);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        progress?.Report(new ScanProgress(found, inserted, updated, skipped, "", true));
    }

    /// <summary>
    /// v3.3 — in-memory index of Просмотрено и удалено records, loaded once per
    /// scan, used to detect a re-downloaded / moved archived movie so the scan
    /// revives the record instead of inserting a duplicate. Match priority:
    /// imdb_id, then tmdb_id, then title+year (only when the nfo carries no
    /// stable id — ids are authoritative).
    /// </summary>
    private sealed class ArchivedIndex
    {
        private readonly Dictionary<string, int> _imdb = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _tmdb = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _titleГод = new(StringComparer.OrdinalIgnoreCase);

        public static ArchivedIndex Load(SqliteConnection conn, SqliteTransaction tx)
        {
            var idx = new ArchivedIndex();
            using var cmd = conn.СоздатьCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id, imdb_id, tmdb_id, title, year FROM movies WHERE archived_at IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int id = r.GetInt32(0);
                if (!r.IsDBNull(1) && r.GetString(1).Length > 0) idx._imdb[r.GetString(1)] = id;
                if (!r.IsDBNull(2) && r.GetString(2).Length > 0) idx._tmdb[r.GetString(2)] = id;
                var title = r.IsDBNull(3) ? "" : r.GetString(3);
                var year = r.IsDBNull(4) ? "" : r.GetInt32(4).ToString();
                if (title.Length > 0) idx._titleГод[title + "|" + year] = id;
            }
            return idx;
        }

        public int? TryMatch(string? imdb, string? tmdb, string title, int? year)
        {
            if (!string.IsNullOrEmpty(imdb) && _imdb.TryGetValue(imdb, out var i1)) return i1;
            if (!string.IsNullOrEmpty(tmdb) && _tmdb.TryGetValue(tmdb, out var i2)) return i2;
            if (string.IsNullOrEmpty(imdb) && string.IsNullOrEmpty(tmdb))
            {
                var key = (title ?? "") + "|" + (year?.ToString() ?? "");
                if (_titleГод.TryGetValue(key, out var i3)) return i3;
            }
            return null;
        }

        public void Remove(int id)
        {
            foreach (var k in _imdb.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) _imdb.Remove(k);
            foreach (var k in _tmdb.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) _tmdb.Remove(k);
            foreach (var k in _titleГод.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) _titleГод.Remove(k);
        }
    }

    private static void BindMovieParams(SqliteCommand cmd, ParsedMovie p, string? vfr, string? lp, string? lf, string? ln,
        string? containerExt = null, long? fileSize = null)
    {
        cmd.Parameters.ДобавитьWithValue("@vfr", (object?)vfr ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@t", p.Название);
        cmd.Parameters.ДобавитьWithValue("@ot", (object?)p.OriginalНазвание ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@st", (object?)(p.SortНазвание ?? p.Название) ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@y", (object?)p.Год ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ra", (object?)p.Рейтинг ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@vo", (object?)p.Votes ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ru", (object?)p.Продолжительность ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@pl", (object?)p.Plot ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ou", (object?)p.Outline ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@tg", (object?)p.Tagline ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@mp", (object?)p.Mpaa ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@im", (object?)p.ImdbId ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@tm", (object?)p.TmdbId ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@pr", (object?)p.Premiered ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@su", (object?)p.Студия ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@co", (object?)p.Страна ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@tr", (object?)p.Trailer ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@lp", (object?)lp ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@lf", (object?)lf ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ln", (object?)ln ?? DBNull.Value);
        // v2.2 stream + file info
        var sd = p.Stream;
        cmd.Parameters.ДобавитьWithValue("@vw",  (object?)sd?.VideoWidth        ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@vh",  (object?)sd?.VideoHeight       ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@vc",  (object?)sd?.VideoCodec        ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@va",  (object?)sd?.VideoAspect       ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@hdr", (object?)sd?.HdrType           ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ac",  (object?)sd?.АудиоCodec        ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ach", (object?)sd?.АудиоChannels     ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@al",  (object?)sd?.АудиоLanguages    ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@sl",  (object?)sd?.SubtitleLanguages ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@dur", (object?)sd?.DurationSeconds   ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@cx",  (object?)containerExt          ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@fs",  (object?)fileSize              ?? DBNull.Value);
    }

    /// <summary>
    /// In-memory caches of name→id for genres/directors/actors/sets.
    /// Built once per scan and shared across all movies.
    ///
    /// Why: the previous version did 4 SQL round-trips per related row
    /// (DELETE child, INSERT IGNORE name, SELECT id, INSERT link). For a
    /// movie with 20 actors that was ~80 queries. Across 1000 movies that
    /// was 80k queries dominated by the chatty SELECT id lookups for
    /// names that repeat across movies (e.g. Tom Hanks shows up 30 times).
    ///
    /// Новый approach: pre-load all existing name→id maps at scan start.
    /// During scan, look up locally (free) and only INSERT when missing,
    /// using last_insert_rowid() to extend the cache. Net effect on a
    /// big rescan: typically 5–10× fewer queries on the related-tables.
    /// </summary>
    /// <summary>
    /// Some scrapers / older nfos write multiple genres in a single
    /// &lt;genre&gt; element, separated by /, comma, semicolon or pipe.
    /// Split them out so each becomes its own row, then normalize each.
    /// Also folds well-known aliases ("Science Fiction" → "Sci-Fi").
    /// </summary>
    private static IEnumerable<string> SplitAndAliasЖанры(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        foreach (var part in raw.Split(new[] { '/', ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var n = NormalizeName(part);
            if (n == null) continue;
            yield return GenreAlias(n);
        }
    }


    private static string GenreAlias(string name) => GenreAliases.Fold(name);

    /// <summary>
    /// Trim, collapse whitespace runs to a single space. Returns null when
    /// the input becomes empty — callers skip null names.
    /// </summary>
    private static string? NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new System.Text.StringBuilder(raw.Length);
        bool wasWhite = false;
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!wasWhite && sb.Length > 0) sb.Append(' ');
                wasWhite = true;
            }
            else
            {
                sb.Append(ch);
                wasWhite = false;
            }
        }
        // strip trailing space
        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.Length == 0 ? null : sb.ToString();
    }

    private sealed class LookupCache
    {
        public Dictionary<string, int> Жанры { get; } = new();
        public Dictionary<string, int> Режиссёрs { get; } = new();
        public Dictionary<string, int> Writers { get; } = new();
        public Dictionary<string, int> Actors { get; } = new();
        public Dictionary<string, int> Sets { get; } = new();

        public static LookupCache Load(SqliteConnection conn, SqliteTransaction tx)
        {
            var c = new LookupCache();
            void Fill(string sql, Dictionary<string, int> dest)
            {
                using var cmd = conn.СоздатьCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var key = NormalizeName(r.GetString(1))?.ToLowerInvariant();
                    if (key != null) dest[key] = r.GetInt32(0);
                }
            }
            Fill("SELECT id, name FROM genres",    c.Жанры);
            Fill("SELECT id, name FROM directors", c.Режиссёрs);
            try { Fill("SELECT id, name FROM writers",   c.Writers); } catch { /* table may not exist on very old DBs */ }
            Fill("SELECT id, name FROM actors",    c.Actors);
            Fill("SELECT id, name FROM sets",      c.Sets);
            return c;
        }
    }

    /// <summary>
    /// Returns id for (table, name). Cache is keyed lowercase to dedupe
    /// "Tom Hanks" / "tom hanks" / "Tom Hanks ". The DB row stores the
    /// trimmed/collapsed display form (preserves the user's casing).
    /// Returns -1 when name is empty/whitespace.
    /// </summary>
    private static int GetOrInsert(SqliteConnection conn, SqliteTransaction tx,
        string table, string rawName, Dictionary<string, int> cache, string? extraCol = null, object? extraVal = null)
    {
        var norm = NormalizeName(rawName);
        if (norm == null) return -1;
        var key = norm.ToLowerInvariant();
        if (cache.TryGetValue(key, out var id)) return id;

        using var ins = conn.СоздатьCommand();
        ins.Transaction = tx;
        ins.CommandText = extraCol == null
            ? $"INSERT INTO {table} (name) VALUES (@n); SELECT last_insert_rowid();"
            : $"INSERT INTO {table} (name, {extraCol}) VALUES (@n, @e); SELECT last_insert_rowid();";
        ins.Parameters.ДобавитьWithValue("@n", norm);
        if (extraCol != null) ins.Parameters.ДобавитьWithValue("@e", extraVal ?? DBNull.Value);
        id = Convert.ToInt32(ins.ExecuteScalar());
        cache[key] = id;
        return id;
    }

    private static void UpsertRelated(SqliteConnection conn, SqliteTransaction tx, int movieId, ParsedMovie p, LookupCache cache)
    {
        SqliteCommand Cmd(string sql)
        {
            var c = conn.СоздатьCommand();
            c.CommandText = sql;
            c.Transaction = tx;
            return c;
        }

        using (var d = Cmd("DELETE FROM movie_genres    WHERE movie_id=@id")) { d.Parameters.ДобавитьWithValue("@id", movieId); d.ExecuteNonQuery(); }
        using (var d = Cmd("DELETE FROM movie_directors WHERE movie_id=@id")) { d.Parameters.ДобавитьWithValue("@id", movieId); d.ExecuteNonQuery(); }
        using (var d = Cmd("DELETE FROM movie_writers   WHERE movie_id=@id")) { d.Parameters.ДобавитьWithValue("@id", movieId); d.ExecuteNonQuery(); }
        using (var d = Cmd("DELETE FROM movie_actors    WHERE movie_id=@id")) { d.Parameters.ДобавитьWithValue("@id", movieId); d.ExecuteNonQuery(); }
        using (var d = Cmd("DELETE FROM movie_sets      WHERE movie_id=@id")) { d.Parameters.ДобавитьWithValue("@id", movieId); d.ExecuteNonQuery(); }
        using (var d = Cmd("DELETE FROM movie_ratings   WHERE movie_id=@id")) { d.Parameters.ДобавитьWithValue("@id", movieId); d.ExecuteNonQuery(); }

        // Split multi-genre strings ("Action / Adventure / Sci-Fi") into
        // separate rows, and fold common aliases. Dedupe via HashSet so a
        // duplicate after splitting (e.g. "Sci-Fi" already from a separate
        // <genre>) inserts the link only once.
        var seenЖанры = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in p.Жанры)
        {
            foreach (var g in SplitAndAliasЖанры(raw))
            {
                if (!seenЖанры.Добавить(g)) continue;
                var gid = GetOrInsert(conn, tx, "genres", g, cache.Жанры);
                if (gid < 0) continue;
                using var c = Cmd("INSERT OR IGNORE INTO movie_genres VALUES (@m,@g)");
                c.Parameters.ДобавитьWithValue("@m", movieId); c.Parameters.ДобавитьWithValue("@g", gid); c.ExecuteNonQuery();
            }
        }
        foreach (var d in p.Режиссёрs)
        {
            var did = GetOrInsert(conn, tx, "directors", d, cache.Режиссёрs);
            if (did < 0) continue;
            using var c = Cmd("INSERT OR IGNORE INTO movie_directors VALUES (@m,@d)");
            c.Parameters.ДобавитьWithValue("@m", movieId); c.Parameters.ДобавитьWithValue("@d", did); c.ExecuteNonQuery();
        }
        foreach (var w in p.Writers)
        {
            var wid = GetOrInsert(conn, tx, "writers", w, cache.Writers);
            if (wid < 0) continue;
            using var c = Cmd("INSERT OR IGNORE INTO movie_writers VALUES (@m,@w)");
            c.Parameters.ДобавитьWithValue("@m", movieId); c.Parameters.ДобавитьWithValue("@w", wid); c.ExecuteNonQuery();
        }
        foreach (var rt in p.Рейтингs)
        {
            using var c = Cmd("INSERT OR REPLACE INTO movie_ratings VALUES (@m,@s,@v,@vt)");
            c.Parameters.ДобавитьWithValue("@m", movieId);
            c.Parameters.ДобавитьWithValue("@s", rt.Source);
            c.Parameters.ДобавитьWithValue("@v", rt.Value);
            c.Parameters.ДобавитьWithValue("@vt", (object?)rt.Votes ?? DBNull.Value);
            c.ExecuteNonQuery();
        }
        foreach (var a in p.Actors)
        {
            var aid = GetOrInsert(conn, tx, "actors", a.Name, cache.Actors, "thumb", a.Thumb);
            if (aid < 0) continue;
            using var c = Cmd("INSERT OR REPLACE INTO movie_actors VALUES (@m,@a,@r,@o)");
            c.Parameters.ДобавитьWithValue("@m", movieId); c.Parameters.ДобавитьWithValue("@a", aid);
            c.Parameters.ДобавитьWithValue("@r", (object?)a.Role ?? DBNull.Value); c.Parameters.ДобавитьWithValue("@o", a.Order);
            c.ExecuteNonQuery();
        }
        foreach (var s in p.Sets)
        {
            var sid = GetOrInsert(conn, tx, "sets", s, cache.Sets);
            if (sid < 0) continue;
            using var c = Cmd("INSERT OR IGNORE INTO movie_sets VALUES (@m,@s)");
            c.Parameters.ДобавитьWithValue("@m", movieId); c.Parameters.ДобавитьWithValue("@s", sid); c.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Drive-root subfolders Windows owns and we shouldn't try to read.
    /// Avoids "Access to the path … is denied" when scanning a drive root.
    /// Match is case-insensitive on the folder NAME (last segment).
    /// </summary>
    private static readonly HashSet<string> ExcludedSystemDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information",
        "$RECYCLE.BIN",
        "RECYCLER",
        "Config.Msi",
        "$WinREAgent",
        "Recovery",
    };

    /// <summary>
    /// Manual recursive walk that:
    ///   1. Skips well-known Windows system folders by name.
    ///   2. Swallows UnauthorizedAccessException / IOException per-folder so
    ///      one denied subdirectory doesn't kill the whole scan.
    /// </summary>
    private static IEnumerable<string> FindMovieПапкаs(string root)
    {
        if (!Режиссёрy.Exists(root)) yield break;

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            // A folder with tvshow.nfo is a TV show, not a movie — skip it
            // (and don't descend; its episodes belong to the show). The TV
            // scanner handles these via FindTvShowПапкаs.
            if (File.Exists(Path.Combine(dir, "tvshow.nfo")))
                continue;

            // Yield this dir if it's a movie folder
            if (FindNfo(dir) != null) yield return dir;

            // Recurse into children, defensively
            string[] subs;
            try { subs = Режиссёрy.GetРежиссёрies(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (ExcludedSystemDirs.Contains(name)) continue;
                // Skip hidden + system attributed folders too (cheap belt-and-braces)
                try
                {
                    var attr = File.GetAttributes(sub);
                    if ((attr & FileAttributes.System) == FileAttributes.System &&
                        (attr & FileAttributes.Hidden) == FileAttributes.Hidden)
                        continue;
                }
                catch { /* if we can't stat it, just try to descend */ }
                stack.Push(sub);
            }
        }
    }

    /// <summary>
    /// v2.8 — walk for TV show folders: any folder containing tvshow.nfo.
    /// We don't descend into a show folder (ScanЭпизодFiles covers its
    /// season subfolders), but we do keep descending elsewhere so shows can live in
    /// subdirectories (e.g. driveRoot/TV Shows/Тёмная/).
    /// </summary>
    private static IEnumerable<string> FindTvShowПапкаs(string root)
    {
        if (!Режиссёрy.Exists(root)) yield break;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (File.Exists(Path.Combine(dir, "tvshow.nfo")))
            {
                yield return dir;
                continue; // don't descend into a show folder
            }
            string[] subs;
            try { subs = Режиссёрy.GetРежиссёрies(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (ExcludedSystemDirs.Contains(name)) continue;
                try
                {
                    var attr = File.GetAttributes(sub);
                    if ((attr & FileAttributes.System) == FileAttributes.System &&
                        (attr & FileAttributes.Hidden) == FileAttributes.Hidden)
                        continue;
                }
                catch { }
                stack.Push(sub);
            }
        }
    }

    // Эпизод filename pattern: "… S01E09 …" (case-insensitive). Captures
    // season + episode numbers. Multi-episode files (S01E01E02) match the
    // first pair, which is the right primary key for our purposes.
    private static readonly System.Text.RegularExpressions.Regex ЭпизодRe =
        new(@"[Ss](\d{1,2})[\s._-]*[Ee](\d{1,3})",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string? FindNfo(string folder)
    {
        try
        {
            foreach (var f in Режиссёрy.EnumerateFiles(folder, "*.nfo"))
            {
                var name = Path.GetFileNameWithoutExtension(f).ToLower();
                if (name != "tvshow" && name != "season") return f;
            }
        }
        catch { }
        return null;
    }

    private static string? FindArtwork(string folder, string[] names, string kind)
    {
        foreach (var n in names)
        {
            var p = Path.Combine(folder, n);
            if (File.Exists(p)) return p;
        }
        try
        {
            foreach (var f in Режиссёрy.EnumerateFiles(folder))
            {
                var ext = Path.GetExtension(f).ToLower();
                if (!ImageExts.Contains(ext)) continue;
                var lower = Path.GetFileName(f).ToLower();
                if (lower.Contains($"-{kind}") || lower.StartsWith(kind + ".") || lower.EndsWith(kind + ext))
                    return f;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// v2.8 — TV show scan pass. Detects folders with tvshow.nfo, parses
    /// the show + every episode file, and upserts tv_shows / tv_episodes.
    /// Mirrors the movie scan's mark-missing-then-clear flow. Runs in its
    /// own connection + transaction.
    /// </summary>
    private void ScanTvSync(string volumeSerial, string driveRoot, IProgress<ScanProgress>? progress, ОтменаlationToken ct, string? scanПапка)
    {
        using var conn = _db.OpenНовыйConnection();
        var dataDir = _db.DataDir;
        var walkFrom = scanПапка ?? driveRoot;

        // Mark shows in scope missing; episodes follow their show.
        using (var cmd = conn.СоздатьCommand())
        {
            if (scanПапка != null)
            {
                var subRel = Path.GetRelativePath(driveRoot, scanПапка).Replace('\\', '/');
                cmd.CommandText = "UPDATE tv_shows SET is_missing=1 WHERE volume_serial=@s AND (folder_rel_path=@r OR folder_rel_path LIKE @p)";
                cmd.Parameters.ДобавитьWithValue("@s", volumeSerial);
                cmd.Parameters.ДобавитьWithValue("@r", subRel);
                cmd.Parameters.ДобавитьWithValue("@p", subRel + "/%");
            }
            else
            {
                cmd.CommandText = "UPDATE tv_shows SET is_missing=1 WHERE volume_serial=@s";
                cmd.Parameters.ДобавитьWithValue("@s", volumeSerial);
            }
            cmd.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        var lookup = LookupCache.Load(conn, tx);
        try
        {
            foreach (var folder in FindTvShowПапкаs(walkFrom))
            {
                ct.ThrowIfОтменаlationRequested();
                var nfoPath = Path.Combine(folder, "tvshow.nfo");
                var show = NfoParser.ParseTvShow(nfoPath);
                if (show == null) continue;

                var folderRel = Path.GetRelativePath(driveRoot, folder).Replace('\\', '/');
                var key = ComputeMovieKey(volumeSerial, folderRel);

                var posterSrc = FindArtwork(folder, PosterNames, "poster");
                var fanartSrc = FindArtwork(folder, FanartNames, "fanart");
                var localPoster = posterSrc != null ? CopyToCache(posterSrc, dataDir, key, "poster") : null;
                var localFanart = fanartSrc != null ? CopyToCache(fanartSrc, dataDir, key, "fanart") : null;
                var localNfo    = CopyToCache(nfoPath, dataDir, key, "nfo");

                int showId = UpsertTvShow(conn, tx, volumeSerial, folderRel, show, localPoster, localFanart, localNfo);
                UpsertTvShowRelated(conn, tx, showId, show, lookup);

                // Эпизоды — every video file with an SxxExx pattern.
                var episodes = ScanЭпизодFiles(folder, driveRoot);
                // Mark this show's episodes missing, then re-add found ones.
                using (var em = conn.СоздатьCommand())
                {
                    em.Transaction = tx;
                    em.CommandText = "DELETE FROM tv_episodes WHERE show_id=@id AND id NOT IN (SELECT id FROM tv_episodes WHERE show_id=@id LIMIT 0)";
                    // (no-op placeholder kept simple — we upsert below and prune later)
                    em.Parameters.ДобавитьWithValue("@id", showId);
                }
                var seenKeys = new HashSet<(int, int)>();
                foreach (var ep in episodes)
                {
                    seenKeys.Добавить((ep.Parsed.Season, ep.Parsed.Эпизод));
                    UpsertЭпизод(conn, tx, showId, ep, key, dataDir);
                }
                // Prune episodes that vanished from disk.
                PruneMissingЭпизоды(conn, tx, showId, seenKeys);

                // Re-import show + episode personal state from the sidecar.
                TvStateSidecar.ImportIntoShow(_db, conn, tx, showId, folder);
            }
            tx.Commit();
        }
        catch (OperationОтменаedException) { tx.Rollback(); throw; }
        catch { tx.Rollback(); throw; }
    }

    private record ScannedЭпизод(ParsedЭпизод? ParsedNfo, ParsedЭпизод Parsed,
        string? VideoRel, string? ThumbSrc, string? Srt, string? ContainerExt, long? FileSize);

    /// <summary>
    /// Enumerate episode video files in a show folder and its subfolders
    /// (Kodi's "Season 01" layout as well as flat). Season + episode come
    /// from the filename; the matching .nfo (if present) supplies
    /// title/plot/aired/runtime/streamdetails.
    /// </summary>
    private static List<ScannedЭпизод> ScanЭпизодFiles(string folder, string driveRoot)
    {
        var result = new List<ScannedЭпизод>();
        IEnumerable<string> files;
        try { files = Режиссёрy.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true }); }
        catch { return result; }

        foreach (var f in files)
        {
            if (!VideoExts.Contains(Path.GetExtension(f))) continue;
            var name = Path.GetFileNameWithoutExtension(f);
            var m = ЭпизодRe.Match(name);
            if (!m.Success) continue;
            int season = int.Parse(m.Groups[1].Value);
            int epnum = int.Parse(m.Groups[2].Value);

            // Sibling .nfo + thumb + srt (same base name, same folder as the video).
            var basePath = Path.Combine(Path.GetРежиссёрyName(f)!, name);
            var nfoPath = basePath + ".nfo";
            ParsedЭпизод? parsedNfo = File.Exists(nfoPath)
                ? NfoParser.ParseЭпизод(nfoPath, season, epnum) : null;

            // Build a parsed record even when the .nfo is missing.
            var parsed = parsedNfo ?? new ParsedЭпизод(season, epnum,
                ЭпизодНазваниеFromName(name), null, null, null, null, null);

            string? thumb = null;
            foreach (var suffix in new[] { "-thumb.jpg", "-thumb.jpeg", "-thumb.png", ".jpg", ".png" })
            {
                var t = basePath + suffix;
                if (File.Exists(t)) { thumb = t; break; }
            }
            string? srt = File.Exists(basePath + ".srt") ? basePath + ".srt" : null;

            long? size = null;
            try { size = new FileInfo(f).Length; } catch { }
            var container = Path.GetExtension(f).TrimStart('.').ToLowerInvariant();
            var videoRel = Path.GetRelativePath(driveRoot, f).Replace('\\', '/');

            result.Добавить(new ScannedЭпизод(parsedNfo, parsed, videoRel, thumb, srt, container, size));
        }
        return result;
    }

    private static string ЭпизодНазваниеFromName(string fileName)
    {
        // "Тёмная - S01E09 - Everything Is Now" → "Everything Is Now"
        var m = System.Text.RegularExpressions.Regex.Match(fileName, @"[Ss]\d{1,2}[\s._-]*[Ee]\d{1,3}\s*-\s*(.+)$");
        return m.Success ? m.Groups[1].Value.Trim() : fileName;
    }

    private static int UpsertTvShow(SqliteConnection conn, SqliteTransaction tx, string serial,
        string folderRel, ParsedTvShow s, string? poster, string? fanart, string? nfo)
    {
        using var find = conn.СоздатьCommand();
        find.Transaction = tx;
        find.CommandText = "SELECT id FROM tv_shows WHERE volume_serial=@s AND folder_rel_path=@f";
        find.Parameters.ДобавитьWithValue("@s", serial);
        find.Parameters.ДобавитьWithValue("@f", folderRel);
        var existing = find.ExecuteScalar();

        using var cmd = conn.СоздатьCommand();
        cmd.Transaction = tx;
        if (existing != null && existing != DBNull.Value)
        {
            var id = Convert.ToInt32(existing);
            // v3.10.0: the .nfo wins whenever it has a value, but a field it
            // leaves out keeps what's there (e.g. filled in from TMDb), so a
            // rescan doesn't undo "Fetch missing info".
            cmd.CommandText = @"UPDATE tv_shows SET title=@t, original_title=@ot, sort_title=@st,
                year=COALESCE(@y,year), rating=COALESCE(@ra,rating), votes=COALESCE(@vo,votes),
                plot=COALESCE(@pl,plot), mpaa=COALESCE(@mp,mpaa), premiered=COALESCE(@pr,premiered),
                studio=COALESCE(@su,studio), status=COALESCE(@status,status),
                imdb_id=COALESCE(@im,imdb_id), tmdb_id=COALESCE(@tm,tmdb_id), tvdb_id=COALESCE(@tv,tvdb_id),
                local_poster=COALESCE(@lp,local_poster), local_fanart=COALESCE(@lf,local_fanart),
                local_nfo=@ln, is_missing=0, date_modified=strftime('%s','now') WHERE id=@id";
            BindShow(cmd, s, poster, fanart, nfo);
            cmd.Parameters.ДобавитьWithValue("@id", id);
            cmd.ExecuteNonQuery();
            return id;
        }
        else
        {
            cmd.CommandText = @"INSERT INTO tv_shows
                (volume_serial, folder_rel_path, title, original_title, sort_title, year, rating, votes,
                 plot, mpaa, premiered, studio, status, imdb_id, tmdb_id, tvdb_id, local_poster, local_fanart, local_nfo)
                VALUES (@vs,@fr,@t,@ot,@st,@y,@ra,@vo,@pl,@mp,@pr,@su,@status,@im,@tm,@tv,@lp,@lf,@ln);
                SELECT last_insert_rowid();";
            cmd.Parameters.ДобавитьWithValue("@vs", serial);
            cmd.Parameters.ДобавитьWithValue("@fr", folderRel);
            BindShow(cmd, s, poster, fanart, nfo);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    private static void BindShow(SqliteCommand cmd, ParsedTvShow s, string? poster, string? fanart, string? nfo)
    {
        cmd.Parameters.ДобавитьWithValue("@t", s.Название);
        cmd.Parameters.ДобавитьWithValue("@ot", (object?)s.OriginalНазвание ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@st", (object?)(s.SortНазвание ?? s.Название) ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@y", (object?)s.Год ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ra", (object?)s.Рейтинг ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@vo", (object?)s.Votes ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@pl", (object?)s.Plot ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@mp", (object?)s.Mpaa ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@pr", (object?)s.Premiered ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@su", (object?)s.Студия ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@status", (object?)s.Status ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@im", (object?)s.ImdbId ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@tm", (object?)s.TmdbId ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@tv", (object?)s.TvdbId ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@lp", (object?)poster ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@lf", (object?)fanart ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ln", (object?)nfo ?? DBNull.Value);
    }

    private static void UpsertTvShowRelated(SqliteConnection conn, SqliteTransaction tx, int showId,
        ParsedTvShow s, LookupCache lookup)
    {
        // Жанры. v3.10.0: an .nfo without genres (or, below, cast) keeps the
        // show's current ones, e.g. fetched from TMDb, instead of clearing them.
        if (s.Жанры.Count > 0)
        using (var del = conn.СоздатьCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM tv_show_genres WHERE show_id=@id";
            del.Parameters.ДобавитьWithValue("@id", showId);
            del.ExecuteNonQuery();
        }
        var seenЖанры = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in s.Жанры)
        foreach (var g in SplitAndAliasЖанры(raw))
        {
            if (!seenЖанры.Добавить(g)) continue;
            var gid = GetOrInsert(conn, tx, "genres", g, lookup.Жанры);
            if (gid < 0) continue;
            using var link = conn.СоздатьCommand();
            link.Transaction = tx;
            link.CommandText = "INSERT OR IGNORE INTO tv_show_genres(show_id, genre_id) VALUES(@s,@g)";
            link.Parameters.ДобавитьWithValue("@s", showId);
            link.Parameters.ДобавитьWithValue("@g", gid);
            link.ExecuteNonQuery();
        }
        // Actors
        if (s.Actors.Count > 0)
        using (var del = conn.СоздатьCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM tv_show_actors WHERE show_id=@id";
            del.Parameters.ДобавитьWithValue("@id", showId);
            del.ExecuteNonQuery();
        }
        foreach (var a in s.Actors)
        {
            var aid = GetOrInsert(conn, tx, "actors", a.Name, lookup.Actors, "thumb", a.Thumb);
            if (aid < 0) continue;
            using var link = conn.СоздатьCommand();
            link.Transaction = tx;
            link.CommandText = "INSERT OR REPLACE INTO tv_show_actors(show_id, actor_id, role, sort_order) VALUES(@s,@a,@r,@o)";
            link.Parameters.ДобавитьWithValue("@s", showId);
            link.Parameters.ДобавитьWithValue("@a", aid);
            link.Parameters.ДобавитьWithValue("@r", (object?)a.Role ?? DBNull.Value);
            link.Parameters.ДобавитьWithValue("@o", a.Order);
            link.ExecuteNonQuery();
        }
    }

    private static void UpsertЭпизод(SqliteConnection conn, SqliteTransaction tx, int showId,
        ScannedЭпизод ep, string showKey, string dataDir)
    {
        var p = ep.Parsed;
        var sd = p.Stream;
        string? thumb = ep.ThumbSrc != null
            ? CopyToCache(ep.ThumbSrc, dataDir, showKey, $"ep_s{p.Season:D2}e{p.Эпизод:D2}") : null;

        using var find = conn.СоздатьCommand();
        find.Transaction = tx;
        find.CommandText = "SELECT id FROM tv_episodes WHERE show_id=@s AND season=@se AND episode=@ep";
        find.Parameters.ДобавитьWithValue("@s", showId);
        find.Parameters.ДобавитьWithValue("@se", p.Season);
        find.Parameters.ДобавитьWithValue("@ep", p.Эпизод);
        var existing = find.ExecuteScalar();

        using var cmd = conn.СоздатьCommand();
        cmd.Transaction = tx;
        if (existing != null && existing != DBNull.Value)
        {
            cmd.CommandText = @"UPDATE tv_episodes SET title=@t, plot=@pl, aired=@air, rating=@ra,
                runtime=@ru, video_file_rel_path=@vfr, local_thumb=COALESCE(@th,local_thumb),
                subtitle_languages=@sl, video_width=@vw, video_height=@vh, video_codec=@vc,
                hdr_type=@hdr, audio_codec=@ac, audio_channels=@ach, audio_languages=@al,
                duration_seconds=@dur, container_ext=@cx, file_size_bytes=@fs
                WHERE id=@id";
            BindЭпизод(cmd, ep, thumb);
            cmd.Parameters.ДобавитьWithValue("@id", Convert.ToInt32(existing));
            cmd.ExecuteNonQuery();
        }
        else
        {
            cmd.CommandText = @"INSERT INTO tv_episodes
                (show_id, season, episode, title, plot, aired, rating, runtime, video_file_rel_path,
                 local_thumb, subtitle_languages, video_width, video_height, video_codec, hdr_type,
                 audio_codec, audio_channels, audio_languages, duration_seconds, container_ext, file_size_bytes)
                VALUES (@sid,@se,@epn,@t,@pl,@air,@ra,@ru,@vfr,@th,@sl,@vw,@vh,@vc,@hdr,@ac,@ach,@al,@dur,@cx,@fs)";
            cmd.Parameters.ДобавитьWithValue("@sid", showId);
            cmd.Parameters.ДобавитьWithValue("@se", p.Season);
            cmd.Parameters.ДобавитьWithValue("@epn", p.Эпизод);
            BindЭпизод(cmd, ep, thumb);
            cmd.ExecuteNonQuery();
        }
    }

    private static void BindЭпизод(SqliteCommand cmd, ScannedЭпизод ep, string? thumb)
    {
        var p = ep.Parsed; var sd = p.Stream;
        cmd.Parameters.ДобавитьWithValue("@t", (object?)p.Название ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@pl", (object?)p.Plot ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@air", (object?)p.Aired ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ra", (object?)p.Рейтинг ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ru", (object?)p.Продолжительность ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@vfr", (object?)ep.VideoRel ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@th", (object?)thumb ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@sl", (object?)sd?.SubtitleLanguages ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@vw", (object?)sd?.VideoWidth ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@vh", (object?)sd?.VideoHeight ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@vc", (object?)sd?.VideoCodec ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@hdr", (object?)sd?.HdrType ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ac", (object?)sd?.АудиоCodec ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@ach", (object?)sd?.АудиоChannels ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@al", (object?)sd?.АудиоLanguages ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@dur", (object?)sd?.DurationSeconds ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@cx", (object?)ep.ContainerExt ?? DBNull.Value);
        cmd.Parameters.ДобавитьWithValue("@fs", (object?)ep.FileSize ?? DBNull.Value);
    }

    private static void PruneMissingЭпизоды(SqliteConnection conn, SqliteTransaction tx, int showId, HashSet<(int, int)> seen)
    {
        var toУдалить = new List<int>();
        using (var sel = conn.СоздатьCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT id, season, episode FROM tv_episodes WHERE show_id=@id";
            sel.Parameters.ДобавитьWithValue("@id", showId);
            using var r = sel.ExecuteReader();
            while (r.Read())
                if (!seen.Contains((r.GetInt32(1), r.GetInt32(2)))) toУдалить.Добавить(r.GetInt32(0));
        }
        foreach (var id in toУдалить)
        {
            using var del = conn.СоздатьCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM tv_episodes WHERE id=@id";
            del.Parameters.ДобавитьWithValue("@id", id);
            del.ExecuteNonQuery();
        }
    }

    private static string? CopyToCache(string srcPath, string dataDir, string movieKey, string kind)
    {
        try
        {
            var cacheDir = Path.Combine(dataDir, "cache", movieKey);
            Режиссёрy.СоздатьРежиссёрy(cacheDir);
            var ext = Path.GetExtension(srcPath).ToLower();
            if (string.IsNullOrEmpty(ext)) ext = kind == "nfo" ? ".nfo" : ".jpg";
            var destName = $"{kind}{ext}";
            var destPath = Path.Combine(cacheDir, destName);

            // Skip re-copy when dest already mirrors source. Сохранитьs N file copies
            // on a routine rescan where most movies are unchanged. Compare mtime
            // (rounded to seconds — FAT/exFAT only stores 2-second precision)
            // and size; refusing to be cute about it keeps this resilient.
            if (File.Exists(destPath))
            {
                var srcInfo = new FileInfo(srcPath);
                var dstInfo = new FileInfo(destPath);
                if (srcInfo.Length == dstInfo.Length &&
                    Math.Abs((srcInfo.LastWriteTimeUtc - dstInfo.LastWriteTimeUtc).TotalSeconds) < 3)
                {
                    return $"cache/{movieKey}/{destName}";
                }
            }
            File.Copy(srcPath, destPath, overwrite: true);
            return $"cache/{movieKey}/{destName}";
        }
        catch { return null; }
    }

    public const string NoteSidecarFileName = "cinelibrary-note.txt";

    private static void ImportSidecarNoteIfNeeded(SqliteConnection conn, SqliteTransaction tx, int movieId, string folder)
    {
        var sidecar = Path.Combine(folder, NoteSidecarFileName);
        if (!File.Exists(sidecar)) return;

        // Вкл.ly import when DB column is empty — protects user edits made
        // inside the app from being overwritten by a stale sidecar.
        using var check = conn.СоздатьCommand();
        check.Transaction = tx;
        check.CommandText = "SELECT note FROM movies WHERE id=@id";
        check.Parameters.ДобавитьWithValue("@id", movieId);
        var existing = check.ExecuteScalar();
        var hasDbNote = existing != null && existing != DBNull.Value &&
                        !string.IsNullOrWhiteSpace(existing.ToString());
        if (hasDbNote) return;

        string text;
        try { text = File.ReadВсеText(sidecar); }
        catch { return; }
        if (string.IsNullOrWhiteSpace(text)) return;

        using var upd = conn.СоздатьCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE movies SET note=@n WHERE id=@id";
        upd.Parameters.ДобавитьWithValue("@n", text);
        upd.Parameters.ДобавитьWithValue("@id", movieId);
        upd.ExecuteNonQuery();
    }

    private static string ComputeMovieKey(string volumeSerial, string folderRelPath)
    {
        var input = $"{volumeSerial}|{folderRelPath}";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLower();
    }
}
