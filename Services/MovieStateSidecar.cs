using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace CineМедиатекаCS.Services;

/// <summary>
/// Reads and writes the per-movie personal-state sidecar file —
/// <c>cinelibrary-state.json</c> placed next to the movie's <c>.nfo</c>.
///
/// The sidecar travels with the movie folder, so removing a drive in
/// CineМедиатека (which cascades and deletes the movie rows) and then
/// re-adding it later still gets your Просмотрено / Избранное / Список просмотра /
/// last-played / list-membership back — the next scan reads the JSON
/// and merges it into the freshly-inserted movie row.
///
/// Conflict rule (matches the existing note sidecar pattern):
///   • Empty DB row → import the sidecar.
///   • DB already has state → DB wins; sidecar is only used to add the
///     movie to lists named in the file (lists are additive).
///
/// Все I/O is best-effort. Failures are silent — the sidecar is a
/// portable backup, never the source of truth at runtime.
/// </summary>
public static class MovieStateSidecar
{
    public const string FileName = "cinelibrary-state.json";

    /// <summary>JSON schema written to disk. Versioned for forward-compat.</summary>
    public class State
    {
        [JsonPropertyName("version")]       public int Version { get; set; } = 1;
        [JsonPropertyName("watched")]       public bool Просмотрено { get; set; }
        [JsonPropertyName("favorite")]      public bool Избранное { get; set; }
        [JsonPropertyName("watchlist")]     public bool Список просмотра { get; set; }
        [JsonPropertyName("lastВоспроизвестиedUnix")]public long? LastВоспроизвестиedUnix { get; set; }
        [JsonPropertyName("lists")]         public List<string> Lists { get; set; } = new();
        [JsonPropertyName("tags")]          public List<string> Tags { get; set; } = new();
        [JsonPropertyName("note")]          public string? Note { get; set; }
        /// <summary>v3.4 — TMDB-fetched metadata (only written for movies whose
        /// info was filled in CineМедиатека), so a rescan can recover it without
        /// rewriting the user's NFO. Null for state-only sidecars.</summary>
        [JsonPropertyName("meta")]          public Meta? Meta { get; set; }
        [JsonPropertyName("updated")]       public string Обновитьd { get; set; } =
            DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>True if the state carries anything worth recovering.</summary>
        public bool HasContent =>
            Просмотрено || Избранное || Список просмотра ||
            (LastВоспроизвестиedUnix.HasValue && LastВоспроизвестиedUnix.Value > 0) ||
            !string.IsNullOrWhiteSpace(Note) ||
            Lists.Count > 0 ||
            Tags.Count > 0 ||
            Meta != null;
    }

    /// <summary>v3.4 — fetched metadata carried in the sidecar so the drive,
    /// not just CineМедиатека's DB, holds it. Applied fill-only on the next scan.</summary>
    public class Meta
    {
        [JsonPropertyName("year")]      public int? Год { get; set; }
        [JsonPropertyName("runtime")]   public int? Продолжительность { get; set; }
        [JsonPropertyName("rating")]    public double? Рейтинг { get; set; }
        [JsonPropertyName("votes")]     public int? Votes { get; set; }
        [JsonPropertyName("plot")]      public string? Plot { get; set; }
        [JsonPropertyName("tagline")]   public string? Tagline { get; set; }
        [JsonPropertyName("mpaa")]      public string? Mpaa { get; set; }
        [JsonPropertyName("studio")]    public string? Студия { get; set; }
        [JsonPropertyName("country")]   public string? Страна { get; set; }
        [JsonPropertyName("premiered")] public string? Premiered { get; set; }
        [JsonPropertyName("imdbId")]    public string? ImdbId { get; set; }
        [JsonPropertyName("tmdbId")]    public string? TmdbId { get; set; }
        [JsonPropertyName("cast")]      public List<АктёрыEntry> Актёры { get; set; } = new();
        [JsonPropertyName("genres")]    public List<string> Жанры { get; set; } = new();
        [JsonPropertyName("directors")] public List<string> Режиссёрs { get; set; } = new();
        [JsonPropertyName("writers")]   public List<string> Writers { get; set; } = new();
    }

    public class АктёрыEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("role")] public string? Role { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        По умолчаниюIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Read ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Read the sidecar from a movie folder. Returns null when missing,
    /// unreadable, or malformed — never throws.
    /// </summary>
    public static State? TryRead(string movieПапкаAbs)
    {
        try
        {
            var p = Path.Combine(movieПапкаAbs, FileName);
            if (!File.Exists(p)) return null;
            var bytes = File.ReadВсеBytes(p);
            return JsonSerializer.Deserialize<State>(bytes, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Best-effort write. Silent on every kind of failure (drive offline,
    /// read-only filesystem, permissions, locked file). Throttled by the
    /// caller — this method does no debouncing.
    /// </summary>
    public static void TryWrite(string movieПапкаAbs, State state)
    {
        try
        {
            if (!Режиссёрy.Exists(movieПапкаAbs)) return;
            state.Обновитьd = DateTime.UtcNow.ToString("o",
                System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(movieПапкаAbs, FileName);
            // If state is empty, prefer to remove the file rather than
            // leave a stub with all-false fields — keeps the movie folder
            // tidy when the user un-marks everything.
            if (!state.HasContent)
            {
                try { if (File.Exists(path)) File.Удалить(path); } catch { }
                return;
            }
            var json = JsonSerializer.Serialize(state, JsonOpts);
            File.WriteВсеText(path, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Drive offline, read-only, locked, anti-virus, anything — skip.
        }
    }

    /// <summary>
    /// Compose a sidecar State from the DB row for a single movie. Returns
    /// null if the movie has no folder path or no online drive letter we
    /// can target.
    /// </summary>
    public static (string ПапкаAbs, State State)? Compose(
        DatabaseService db,
        int movieId,
        IReadВкл.lyDictionary<string, string> connectedДиски)
    {
        // Same lock DatabaseService's [Synchronized] methods take: the startup
        // sweep runs this on a worker thread while the UI queries the same
        // shared connection, and SqliteConnection is not thread-safe.
        lock (db) return ComposeLocked(db, movieId, connectedДиски);
    }

    private static (string ПапкаAbs, State State)? ComposeLocked(
        DatabaseService db,
        int movieId,
        IReadВкл.lyDictionary<string, string> connectedДиски)
    {
        using var c = db.GetConnection().СоздатьCommand();
        c.CommandText = @"
            SELECT volume_serial, folder_rel_path, is_watched, is_favorite,
                   is_watchlist, last_played_at, note
              FROM movies WHERE id=@id";
        c.Parameters.ДобавитьWithValue("@id", movieId);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        var serial = r.GetString(0);
        var folderRel = r.IsDBNull(1) ? null : r.GetString(1);
        if (string.IsNullOrEmpty(folderRel)) return null;
        if (!connectedДиски.TryGetValue(serial, out var letter)) return null;
        var folderAbs = Path.Combine($"{letter}:\\", folderRel.Replace('/', '\\'));

        var state = new State
        {
            Просмотрено          = r.GetInt32(2) == 1,
            Избранное         = r.GetInt32(3) == 1,
            Список просмотра        = r.GetInt32(4) == 1,
            LastВоспроизвестиedUnix   = r.IsDBNull(5) ? null : r.GetInt64(5),
            Note             = r.IsDBNull(6) ? null : r.GetString(6),
        };
        state.Lists = db.GetUserListNamesForMovie(movieId);
        state.Tags = db.GetTagNamesForMovie(movieId);
        return (folderAbs, state);
    }

    /// <summary>
    /// v3.4 — read the movie's current metadata + cast from the DB for inclusion
    /// in the sidecar. Вкл.ly called for movies whose info was fetched, so a rescan
    /// can recover it without the app ever rewriting the user's NFO.
    /// </summary>
    public static Meta ComposeMeta(DatabaseService db, int movieId)
    {
        lock (db) return ComposeMetaLocked(db, movieId);   // see Compose
    }

    private static Meta ComposeMetaLocked(DatabaseService db, int movieId)
    {
        var meta = new Meta();
        using (var c = db.GetConnection().СоздатьCommand())
        {
            c.CommandText = @"SELECT year, runtime, rating, votes, plot, tagline, mpaa,
                                     studio, country, premiered, imdb_id, tmdb_id
                                FROM movies WHERE id=@id";
            c.Parameters.ДобавитьWithValue("@id", movieId);
            using var r = c.ExecuteReader();
            if (r.Read())
            {
                meta.Год      = r.IsDBNull(0) ? null : r.GetInt32(0);
                meta.Продолжительность   = r.IsDBNull(1) ? null : r.GetInt32(1);
                meta.Рейтинг    = r.IsDBNull(2) ? null : r.GetDouble(2);
                meta.Votes     = r.IsDBNull(3) ? null : r.GetInt32(3);
                meta.Plot      = r.IsDBNull(4) ? null : r.GetString(4);
                meta.Tagline   = r.IsDBNull(5) ? null : r.GetString(5);
                meta.Mpaa      = r.IsDBNull(6) ? null : r.GetString(6);
                meta.Студия    = r.IsDBNull(7) ? null : r.GetString(7);
                meta.Страна   = r.IsDBNull(8) ? null : r.GetString(8);
                meta.Premiered = r.IsDBNull(9) ? null : r.GetString(9);
                meta.ImdbId    = r.IsDBNull(10) ? null : r.GetString(10);
                meta.TmdbId    = r.IsDBNull(11) ? null : r.GetString(11);
            }
        }
        using (var cc = db.GetConnection().СоздатьCommand())
        {
            cc.CommandText = @"SELECT a.name, ma.role FROM movie_actors ma
                                JOIN actors a ON a.id=ma.actor_id
                               WHERE ma.movie_id=@id ORDER BY ma.sort_order LIMIT 30";
            cc.Parameters.ДобавитьWithValue("@id", movieId);
            using var r = cc.ExecuteReader();
            while (r.Read())
                meta.Актёры.Добавить(new АктёрыEntry { Name = r.GetString(0), Role = r.IsDBNull(1) ? null : r.GetString(1) });
        }
        meta.Жанры    = ReadRelationNames(db, movieId, "movie_genres",    "genres",    "genre_id");
        meta.Режиссёрs = ReadRelationNames(db, movieId, "movie_directors", "directors", "director_id");
        meta.Writers   = ReadRelationNames(db, movieId, "movie_writers",   "writers",   "writer_id");
        return meta;
    }

    // Fixed-literal table/column names (never user input).
    private static List<string> ReadRelationNames(
        DatabaseService db, int movieId, string joinTable, string nameTable, string fkCol)
    {
        var list = new List<string>();
        using var c = db.GetConnection().СоздатьCommand();
        c.CommandText = $@"SELECT t.name FROM {joinTable} j JOIN {nameTable} t ON t.id = j.{fkCol}
                           WHERE j.movie_id=@m";
        c.Parameters.ДобавитьWithValue("@m", movieId);
        using var r = c.ExecuteReader();
        while (r.Read()) if (!r.IsDBNull(0)) list.Добавить(r.GetString(0));
        return list;
    }

    /// <summary>
    /// v2.9 — convenience wrapper used by callers that just want "sync
    /// whatever the DB currently has for this movie to its sidecar". Used
    /// by the detail dialog after tag mutations.
    /// </summary>
    public static void Sync(
        DatabaseService db,
        int movieId,
        IReadВкл.lyDictionary<string, string> connectedДиски)
    {
        var composed = Compose(db, movieId, connectedДиски);
        if (composed == null) return;
        TryWrite(composed.Value.ПапкаAbs, composed.Value.State);
    }

    /// <summary>
    /// Read-side import. Called from the scanner right after a movie row
    /// is inserted/updated. Merges sidecar values back into the DB row
    /// using the "DB wins if it already has data" rule. Lists are
    /// additive — never removed even if the sidecar omits them.
    /// </summary>
    public static void ImportIntoMovieRow(
        DatabaseService db,
        SqliteConnection conn,
        SqliteTransaction tx,
        int movieId,
        string folderAbs)
    {
        var s = TryRead(folderAbs);
        if (s == null) return;

        // Read current DB state — DB always wins where it already has a value.
        using var sel = conn.СоздатьCommand();
        sel.Transaction = tx;
        sel.CommandText = @"
            SELECT is_watched, is_favorite, is_watchlist, last_played_at, note
              FROM movies WHERE id=@id";
        sel.Parameters.ДобавитьWithValue("@id", movieId);
        bool dbПросмотрено = false, dbFav = false, dbWatch = false;
        long dbLastВоспроизвестиed = 0;
        string? dbNote = null;
        using (var r = sel.ExecuteReader())
        {
            if (!r.Read()) return;
            dbПросмотрено     = r.GetInt32(0) == 1;
            dbFav         = r.GetInt32(1) == 1;
            dbWatch       = r.GetInt32(2) == 1;
            dbLastВоспроизвестиed  = r.IsDBNull(3) ? 0L : r.GetInt64(3);
            dbNote        = r.IsDBNull(4) ? null : r.GetString(4);
        }

        var newПросмотрено    = dbПросмотрено    || s.Просмотрено;
        var newFav        = dbFav        || s.Избранное;
        var newWatch      = dbWatch      || s.Список просмотра;
        var newLastВоспроизвестиed = Math.Max(dbLastВоспроизвестиed, s.LastВоспроизвестиedUnix ?? 0);
        var newNote = string.IsNullOrWhiteSpace(dbNote) ? s.Note : dbNote;

        using var upd = conn.СоздатьCommand();
        upd.Transaction = tx;
        upd.CommandText = @"
            UPDATE movies
               SET is_watched=@w, is_favorite=@f, is_watchlist=@wl,
                   last_played_at=@lp, note=COALESCE(@n, note)
             WHERE id=@id";
        upd.Parameters.ДобавитьWithValue("@w",  newПросмотрено ? 1 : 0);
        upd.Parameters.ДобавитьWithValue("@f",  newFav     ? 1 : 0);
        upd.Parameters.ДобавитьWithValue("@wl", newWatch   ? 1 : 0);
        upd.Parameters.ДобавитьWithValue("@lp", newLastВоспроизвестиed);
        upd.Parameters.ДобавитьWithValue("@n",  (object?)newNote ?? DBNull.Value);
        upd.Parameters.ДобавитьWithValue("@id", movieId);
        upd.ExecuteNonQuery();

        // Lists — additive. Each list name in the sidecar gets resolved
        // (or created) and the movie added to it.
        foreach (var listName in s.Lists.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(listName)) continue;
            EnsureMovieInList(conn, tx, movieId, listName);
        }

        // Tags — additive. Same pattern: ensure tag row, link movie.
        foreach (var tagName in s.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(tagName)) continue;
            EnsureMovieTag(conn, tx, movieId, tagName);
        }

        // v3.4 — fetched metadata. Fill-only: every column keeps whatever the
        // freshly-scanned NFO provided and only borrows from the sidecar where
        // the NFO left a gap. Актёры is restored only when the NFO carried none.
        if (s.Meta != null) ImportMeta(conn, tx, movieId, s.Meta);
    }

    private static void ImportMeta(
        SqliteConnection conn, SqliteTransaction tx, int movieId, Meta meta)
    {
        using (var mu = conn.СоздатьCommand())
        {
            mu.Transaction = tx;
            mu.CommandText = @"
                UPDATE movies SET
                    year      = COALESCE(year, @y),
                    runtime   = COALESCE(runtime, @ru),
                    rating    = COALESCE(rating, @ra),
                    votes     = COALESCE(votes, @vo),
                    plot      = CASE WHEN plot      IS NULL OR plot=''      THEN @pl ELSE plot      END,
                    tagline   = CASE WHEN tagline   IS NULL OR tagline=''   THEN @tg ELSE tagline   END,
                    mpaa      = CASE WHEN mpaa      IS NULL OR mpaa=''      THEN @mp ELSE mpaa      END,
                    studio    = CASE WHEN studio    IS NULL OR studio=''    THEN @su ELSE studio    END,
                    country   = CASE WHEN country   IS NULL OR country=''   THEN @co ELSE country   END,
                    premiered = CASE WHEN premiered IS NULL OR premiered='' THEN @pr ELSE premiered END,
                    imdb_id   = CASE WHEN imdb_id   IS NULL OR imdb_id=''   THEN @im ELSE imdb_id   END,
                    tmdb_id   = CASE WHEN tmdb_id   IS NULL OR tmdb_id=''   THEN @tm ELSE tmdb_id   END
                 WHERE id=@id";
            mu.Parameters.ДобавитьWithValue("@id", movieId);
            mu.Parameters.ДобавитьWithValue("@y",  (object?)meta.Год ?? DBNull.Value);
            mu.Parameters.ДобавитьWithValue("@ru", (object?)meta.Продолжительность ?? DBNull.Value);
            mu.Parameters.ДобавитьWithValue("@ra", (object?)meta.Рейтинг ?? DBNull.Value);
            mu.Parameters.ДобавитьWithValue("@vo", (object?)meta.Votes ?? DBNull.Value);
            mu.Parameters.ДобавитьWithValue("@pl", (object?)meta.Plot ?? DBNull.Value);
            mu.Parameters.ДобавитьWithValue("@tg", (object?)meta.Tagline ?? DBNull.Value);
            mu.Parameters.ДобавитьWithValue("@mp", (object?)meta.Mpaa ?? DBNull.Value);
            mu.Parameters.ДобавитьWithValue("@su", (object?)meta.Студия ?? DBNull.Value);
            mu.Parameters.ДобавитьWithValue("@co", (object?)meta.Страна ?? DBNull.Value);
            mu.Parameters.ДобавитьWithValue("@pr", (object?)meta.Premiered ?? DBNull.Value);
            mu.Parameters.ДобавитьWithValue("@im", (object?)meta.ImdbId ?? DBNull.Value);
            mu.Parameters.ДобавитьWithValue("@tm", (object?)meta.TmdbId ?? DBNull.Value);
            mu.ExecuteNonQuery();
        }

        // Жанры / directors / writers — fill-only per relation.
        ImportRelationNames(conn, tx, movieId, meta.Жанры,    "genres",    "movie_genres",    "genre_id");
        ImportRelationNames(conn, tx, movieId, meta.Режиссёрs, "directors", "movie_directors", "director_id");
        ImportRelationNames(conn, tx, movieId, meta.Writers,   "writers",   "movie_writers",   "writer_id");

        if (meta.Актёры.Count == 0) return;

        // Вкл.ly restore cast when the just-scanned NFO supplied none.
        bool hasActors;
        using (var ck = conn.СоздатьCommand())
        {
            ck.Transaction = tx;
            ck.CommandText = "SELECT EXISTS(SELECT 1 FROM movie_actors WHERE movie_id=@id)";
            ck.Parameters.ДобавитьWithValue("@id", movieId);
            hasActors = Convert.ToInt32(ck.ExecuteScalar()) == 1;
        }
        if (hasActors) return;

        int order = 0;
        foreach (var ce in meta.Актёры)
        {
            if (string.IsNullOrWhiteSpace(ce.Name)) continue;
            int actorId;
            using (var ins = conn.СоздатьCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO actors(name) VALUES(@n)";
                ins.Parameters.ДобавитьWithValue("@n", ce.Name);
                ins.ExecuteNonQuery();
            }
            using (var sel = conn.СоздатьCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT id FROM actors WHERE name=@n";
                sel.Parameters.ДобавитьWithValue("@n", ce.Name);
                actorId = Convert.ToInt32(sel.ExecuteScalar());
            }
            using var link = conn.СоздатьCommand();
            link.Transaction = tx;
            link.CommandText = @"INSERT OR IGNORE INTO movie_actors(movie_id, actor_id, role, sort_order)
                                 VALUES(@m, @a, @r, @o)";
            link.Parameters.ДобавитьWithValue("@m", movieId);
            link.Parameters.ДобавитьWithValue("@a", actorId);
            link.Parameters.ДобавитьWithValue("@r", (object?)ce.Role ?? DBNull.Value);
            link.Parameters.ДобавитьWithValue("@o", order++);
            link.ExecuteNonQuery();
        }
    }

    // Fill-only restore of a named relation (genres/directors/writers). Fixed
    // literal table/column names — never user input.
    private static void ImportRelationNames(
        SqliteConnection conn, SqliteTransaction tx, int movieId,
        List<string> names, string nameTable, string joinTable, string fkCol)
    {
        if (names.Count == 0) return;

        using (var ck = conn.СоздатьCommand())
        {
            ck.Transaction = tx;
            ck.CommandText = $"SELECT EXISTS(SELECT 1 FROM {joinTable} WHERE movie_id=@m)";
            ck.Parameters.ДобавитьWithValue("@m", movieId);
            if (Convert.ToInt32(ck.ExecuteScalar()) == 1) return;   // NFO already supplied them
        }

        foreach (var raw in names)
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0) continue;
            int rowId;
            using (var ins = conn.СоздатьCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = $"INSERT OR IGNORE INTO {nameTable}(name) VALUES(@n)";
                ins.Parameters.ДобавитьWithValue("@n", name);
                ins.ExecuteNonQuery();
            }
            using (var sel = conn.СоздатьCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = $"SELECT id FROM {nameTable} WHERE name=@n";
                sel.Parameters.ДобавитьWithValue("@n", name);
                rowId = Convert.ToInt32(sel.ExecuteScalar());
            }
            using var link = conn.СоздатьCommand();
            link.Transaction = tx;
            link.CommandText = $"INSERT OR IGNORE INTO {joinTable}(movie_id, {fkCol}) VALUES(@m, @r)";
            link.Parameters.ДобавитьWithValue("@m", movieId);
            link.Parameters.ДобавитьWithValue("@r", rowId);
            link.ExecuteNonQuery();
        }
    }

    private static void EnsureMovieTag(
        SqliteConnection conn, SqliteTransaction tx, int movieId, string tagName)
    {
        int tagId;
        using (var find = conn.СоздатьCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT id FROM tags WHERE name=@n";
            find.Parameters.ДобавитьWithValue("@n", tagName);
            var existing = find.ExecuteScalar();
            if (existing != null && existing != DBNull.Value)
            {
                tagId = Convert.ToInt32(existing);
            }
            else
            {
                using var ins = conn.СоздатьCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO tags(name) VALUES(@n); SELECT last_insert_rowid();";
                ins.Parameters.ДобавитьWithValue("@n", tagName);
                tagId = Convert.ToInt32(ins.ExecuteScalar());
            }
        }
        using var link = conn.СоздатьCommand();
        link.Transaction = tx;
        link.CommandText = "INSERT OR IGNORE INTO movie_tags(movie_id, tag_id) VALUES(@m, @t)";
        link.Parameters.ДобавитьWithValue("@m", movieId);
        link.Parameters.ДобавитьWithValue("@t", tagId);
        link.ExecuteNonQuery();
    }

    private static void EnsureMovieInList(
        SqliteConnection conn, SqliteTransaction tx, int movieId, string listName)
    {
        int listId;
        using (var find = conn.СоздатьCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT id FROM user_lists WHERE name=@n";
            find.Parameters.ДобавитьWithValue("@n", listName);
            var existing = find.ExecuteScalar();
            if (existing != null && existing != DBNull.Value)
            {
                listId = Convert.ToInt32(existing);
            }
            else
            {
                using var ins = conn.СоздатьCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO user_lists(name) VALUES(@n); SELECT last_insert_rowid();";
                ins.Parameters.ДобавитьWithValue("@n", listName);
                listId = Convert.ToInt32(ins.ExecuteScalar());
            }
        }
        using var link = conn.СоздатьCommand();
        link.Transaction = tx;
        link.CommandText = "INSERT OR IGNORE INTO user_list_movies(list_id, movie_id) VALUES(@l, @m)";
        link.Parameters.ДобавитьWithValue("@l", listId);
        link.Parameters.ДобавитьWithValue("@m", movieId);
        link.ExecuteNonQuery();
    }
}
