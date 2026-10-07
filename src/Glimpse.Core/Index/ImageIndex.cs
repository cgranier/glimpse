using System.Globalization;
using System.Text;
using System.Text.Json;
using Glimpse.Core.Ocr;
using Microsoft.Data.Sqlite;

namespace Glimpse.Core.Index;

public sealed record IndexedFile(long Id, string Path, long Size, long MtimeTicks);

/// <summary>One OCR result to store. <see cref="Page"/> is null when <see cref="Error"/> is set.</summary>
public sealed record IndexEntry(string Path, string Source, long Size, DateTime MtimeUtc, OcrPage? Page, string? Error);

public sealed record SearchHit(
    long Id,
    string Path,
    string Source,
    DateTime Modified,
    int Width,
    int Height,
    /// <summary>Snippet with matches wrapped in <see cref="ImageIndex.MatchStart"/>/<see cref="ImageIndex.MatchEnd"/>.</summary>
    string Snippet,
    /// <summary>Cosine similarity for visual matches; null for text matches.</summary>
    float? VisualScore = null);

public sealed record IndexStats(int Images, int WithText, int Errors, IReadOnlyDictionary<string, int> BySource, int Embedded = 0);

/// <summary>All embeddings for one model, plus the metadata visual search filters on.</summary>
public sealed record EmbeddingSet(long[] Ids, string[] Sources, long[] Mtimes, float[] Vectors, int Dim)
{
    public int Count => Ids.Length;
    public ReadOnlySpan<float> Vector(int i) => Vectors.AsSpan(i * Dim, Dim);
}

/// <summary>
/// SQLite store. `images` holds file metadata + OCR word boxes; `images_fts` is an FTS5 table
/// with the trigram tokenizer, so any 3+ character fragment matches ("moc" finds "MoCA").
/// </summary>
public sealed class ImageIndex : IDisposable
{
    public const char MatchStart = '\u0001';
    public const char MatchEnd = '\u0002';

    const int SchemaVersion = 2;
    const int SQLITE_CORRUPT = 11, SQLITE_NOTADB = 26;

    // A SqliteConnection must never be used by two threads at once (the app's startup scan and folder
    // watcher both write). Every public member takes this lock; transactions never escape the class.
    readonly Lock _gate = new();
    readonly SqliteConnection _db;

    public ImageIndex(string? path = null)
    {
        path ??= GlimpseConfig.DatabasePath;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        try
        {
            _db = Open(path, verify: true);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SQLITE_CORRUPT or SQLITE_NOTADB)
        {
            // The index is only a cache of OCR results: set the damaged file aside and start over.
            SqliteConnection.ClearAllPools();
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Move(path + suffix, $"{path}.corrupt-{stamp}{suffix}");
            _db = Open(path, verify: false);
            Recovered = true;
        }
    }

    /// <summary>True when the existing index was corrupt and a fresh one was created (needs a full scan).</summary>
    public bool Recovered { get; }

    SqliteConnection Open(string path, bool verify)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        try
        {
            Exec(db, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 10000;");
            if (verify && Convert.ToString(Scalar(db, "PRAGMA quick_check")) != "ok")
                throw new SqliteException("index failed quick_check", SQLITE_CORRUPT);
            Migrate(db);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    static void Migrate(SqliteConnection db)
    {
        var version = Convert.ToInt32(Scalar(db, "PRAGMA user_version"));
        if (version >= SchemaVersion) return;

        Exec(db, """
            CREATE TABLE IF NOT EXISTS images (
                id          INTEGER PRIMARY KEY,
                path        TEXT NOT NULL UNIQUE COLLATE NOCASE,
                source      TEXT NOT NULL,
                size        INTEGER NOT NULL,
                mtime       INTEGER NOT NULL,   -- UTC ticks
                width       INTEGER NOT NULL DEFAULT 0,
                height      INTEGER NOT NULL DEFAULT 0,
                indexed_at  INTEGER NOT NULL,
                status      TEXT NOT NULL,      -- ok | error
                error       TEXT,
                ocr_json    TEXT                -- lines + word boxes, for highlighting
            );
            CREATE INDEX IF NOT EXISTS images_source_mtime ON images(source, mtime);

            -- rowid = images.id. `name` lets file names match too.
            CREATE VIRTUAL TABLE IF NOT EXISTS images_fts USING fts5(text, name, tokenize = 'trigram');

            -- v2: CLIP image embeddings (float32, unit length). An empty vec marks "couldn't embed".
            CREATE TABLE IF NOT EXISTS embeddings (
                image_id    INTEGER PRIMARY KEY,
                model       TEXT NOT NULL,
                vec         BLOB NOT NULL
            );
            """);
        Exec(db, $"PRAGMA user_version = {SchemaVersion}");
    }

    // ---- writes ---------------------------------------------------------------------------

    /// <summary>Writes a batch of OCR results in one transaction.</summary>
    public void UpsertBatch(IEnumerable<IndexEntry> entries)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var e in entries) Upsert(e.Path, e.Source, e.Size, e.MtimeUtc, e.Page, e.Error);
            tx.Commit();
        }
    }

    public void RemoveMany(IEnumerable<long> ids)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var id in ids)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "DELETE FROM images_fts WHERE rowid = $id; DELETE FROM embeddings WHERE image_id = $id; DELETE FROM images WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    void Upsert(string path, string source, long size, DateTime mtimeUtc, OcrPage? page, string? error)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO images (path, source, size, mtime, width, height, indexed_at, status, error, ocr_json)
            VALUES ($path, $source, $size, $mtime, $w, $h, $now, $status, $error, $ocr)
            ON CONFLICT(path) DO UPDATE SET
                source = excluded.source, size = excluded.size, mtime = excluded.mtime,
                width = excluded.width, height = excluded.height, indexed_at = excluded.indexed_at,
                status = excluded.status, error = excluded.error, ocr_json = excluded.ocr_json
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$size", size);
        cmd.Parameters.AddWithValue("$mtime", mtimeUtc.Ticks);
        cmd.Parameters.AddWithValue("$w", page?.Width ?? 0);
        cmd.Parameters.AddWithValue("$h", page?.Height ?? 0);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("$status", error is null ? "ok" : "error");
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ocr", page is null ? DBNull.Value : JsonSerializer.Serialize(page.Lines));
        var id = (long)cmd.ExecuteScalar()!;

        using var fts = _db.CreateCommand();
        // The file changed (or is new), so any embedding is stale too.
        fts.CommandText = """
            DELETE FROM embeddings WHERE image_id = $id;
            DELETE FROM images_fts WHERE rowid = $id;
            INSERT INTO images_fts (rowid, text, name) VALUES ($id, $text, $name);
            """;
        fts.Parameters.AddWithValue("$id", id);
        fts.Parameters.AddWithValue("$text", page?.FullText ?? "");
        fts.Parameters.AddWithValue("$name", System.IO.Path.GetFileNameWithoutExtension(path));
        fts.ExecuteNonQuery();
    }

    // ---- reads ----------------------------------------------------------------------------

    public Dictionary<string, IndexedFile> GetFiles(string source)
    {
        lock (_gate) return GetFilesCore(source);
    }

    Dictionary<string, IndexedFile> GetFilesCore(string source)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, path, size, mtime FROM images WHERE source = $source";
        cmd.Parameters.AddWithValue("$source", source);
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
            result[r.GetString(1)] = new IndexedFile(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3));
        return result;
    }

    public IReadOnlyList<OcrLine> GetOcrLines(long id)
    {
        string? json;
        lock (_gate) json = Scalar(_db, "SELECT ocr_json FROM images WHERE id = $id", ("$id", id)) as string;
        return json is null ? [] : JsonSerializer.Deserialize<List<OcrLine>>(json) ?? [];
    }

    /// <summary>
    /// Query syntax: free words (all must match, 3+ chars use the trigram index),
    /// <c>in:source</c> (prefix match on source name), <c>after:2026-05</c>, <c>before:2026-06-15</c>.
    /// An empty query returns the most recent images.
    /// </summary>
    public List<SearchHit> Search(string query, int limit = 60) => Search(SearchQuery.Parse(query), limit);

    public List<SearchHit> Search(SearchQuery query, int limit = 60)
    {
        lock (_gate) return SearchCore(query, limit);
    }

    List<SearchHit> SearchCore(SearchQuery q, int limit)
    {
        var where = new List<string>();
        var cmd = _db.CreateCommand();

        if (q.Terms.Count > 0)
        {
            var longTerms = q.Terms.Where(t => t.Length >= 3).ToList();
            if (longTerms.Count > 0)
            {
                where.Add("images_fts MATCH $match");
                cmd.Parameters.AddWithValue("$match",
                    string.Join(" AND ", longTerms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\"")));
            }
            // Trigrams can't match 1–2 character fragments; fall back to a scan for those.
            var i = 0;
            foreach (var t in q.Terms.Where(t => t.Length < 3))
            {
                where.Add($"(images_fts.text LIKE $s{i} OR images_fts.name LIKE $s{i})");
                cmd.Parameters.AddWithValue($"$s{i++}", $"%{t}%");
            }
        }
        if (q.Source is not null)
        {
            where.Add("images.source LIKE $source");
            cmd.Parameters.AddWithValue("$source", q.Source + "%");
        }
        if (q.After is not null)
        {
            where.Add("images.mtime >= $after");
            cmd.Parameters.AddWithValue("$after", q.After.Value.Ticks);
        }
        if (q.Before is not null)
        {
            where.Add("images.mtime < $before");
            cmd.Parameters.AddWithValue("$before", q.Before.Value.Ticks);
        }

        var hasMatch = q.Terms.Any(t => t.Length >= 3);
        // With the trigram tokenizer snippet() counts trigrams, not words: 64 (the max) ≈ 60 characters.
        var snippet = hasMatch
            ? $"snippet(images_fts, 0, char({(int)MatchStart}), char({(int)MatchEnd}), '…', 64)"
            : "substr(images_fts.text, 1, 160)";
        var order = hasMatch ? "bm25(images_fts), images.mtime DESC" : "images.mtime DESC";

        cmd.CommandText = $"""
            SELECT images.id, images.path, images.source, images.mtime, images.width, images.height, {snippet}
            FROM images_fts JOIN images ON images.id = images_fts.rowid
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY {order}
            LIMIT {limit}
            """;

        var hits = new List<SearchHit>();
        using (cmd)
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                hits.Add(new SearchHit(r.GetInt64(0), r.GetString(1), r.GetString(2),
                    new DateTime(r.GetInt64(3), DateTimeKind.Utc).ToLocalTime(),
                    r.GetInt32(4), r.GetInt32(5), r.IsDBNull(6) ? "" : r.GetString(6)));
        }
        return hits;
    }

    // ---- embeddings -----------------------------------------------------------------------

    /// <summary>Images that decoded fine for OCR but have no embedding from <paramref name="model"/> yet, newest first.</summary>
    public List<(long Id, string Path)> GetImagesMissingEmbedding(string model)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT images.id, images.path FROM images
                LEFT JOIN embeddings ON embeddings.image_id = images.id AND embeddings.model = $model
                WHERE embeddings.image_id IS NULL AND images.status = 'ok'
                ORDER BY images.mtime DESC
                """;
            cmd.Parameters.AddWithValue("$model", model);
            using var r = cmd.ExecuteReader();
            var result = new List<(long, string)>();
            while (r.Read()) result.Add((r.GetInt64(0), r.GetString(1)));
            return result;
        }
    }

    /// <summary>Stores embeddings; a null vector records a failed attempt so it isn't retried every run.</summary>
    public void SaveEmbeddings(string model, IEnumerable<(long Id, float[]? Vector)> items)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO embeddings (image_id, model, vec) VALUES ($id, $model, $vec)";
            var id = cmd.Parameters.Add("$id", SqliteType.Integer);
            cmd.Parameters.AddWithValue("$model", model);
            var vec = cmd.Parameters.Add("$vec", SqliteType.Blob);
            foreach (var (imageId, vector) in items)
            {
                id.Value = imageId;
                vec.Value = vector is null ? Array.Empty<byte>() : System.Runtime.InteropServices.MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public EmbeddingSet LoadEmbeddings(string model)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT images.id, images.source, images.mtime, embeddings.vec FROM embeddings
                JOIN images ON images.id = embeddings.image_id
                WHERE embeddings.model = $model AND length(embeddings.vec) > 0
                """;
            cmd.Parameters.AddWithValue("$model", model);
            using var r = cmd.ExecuteReader();

            var ids = new List<long>();
            var sources = new List<string>();
            var mtimes = new List<long>();
            var vectors = new List<float>();
            var dim = 0;
            while (r.Read())
            {
                var bytes = (byte[])r.GetValue(3);
                var v = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
                if (dim == 0) dim = v.Length;
                if (v.Length != dim) continue;
                ids.Add(r.GetInt64(0));
                sources.Add(r.GetString(1));
                mtimes.Add(r.GetInt64(2));
                vectors.AddRange(v.ToArray());
            }
            return new EmbeddingSet([.. ids], [.. sources], [.. mtimes], [.. vectors], dim);
        }
    }

    /// <summary>Hits for the given image ids, in the given order (used to materialize visual results).</summary>
    public List<SearchHit> GetHits(IReadOnlyList<(long Id, float Score)> ranked)
    {
        if (ranked.Count == 0) return [];
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT images.id, images.path, images.source, images.mtime, images.width, images.height,
                       substr(images_fts.text, 1, 160)
                FROM images JOIN images_fts ON images_fts.rowid = images.id
                WHERE images.id IN ({string.Join(',', ranked.Select(r => r.Id))})
                """;
            using var r = cmd.ExecuteReader();
            var byId = new Dictionary<long, SearchHit>();
            while (r.Read())
                byId[r.GetInt64(0)] = new SearchHit(r.GetInt64(0), r.GetString(1), r.GetString(2),
                    new DateTime(r.GetInt64(3), DateTimeKind.Utc).ToLocalTime(),
                    r.GetInt32(4), r.GetInt32(5), r.IsDBNull(6) ? "" : r.GetString(6));
            return ranked.Where(x => byId.ContainsKey(x.Id)).Select(x => byId[x.Id] with { VisualScore = x.Score }).ToList();
        }
    }

    public IndexStats GetStats()
    {
        lock (_gate)
        {
            var bySource = new Dictionary<string, int>();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT source, count(*) FROM images GROUP BY source ORDER BY source";
                using var r = cmd.ExecuteReader();
                while (r.Read()) bySource[r.GetString(0)] = r.GetInt32(1);
            }
            return new IndexStats(
                Convert.ToInt32(Scalar(_db, "SELECT count(*) FROM images")),
                Convert.ToInt32(Scalar(_db, "SELECT count(*) FROM images_fts WHERE length(text) > 0")),
                Convert.ToInt32(Scalar(_db, "SELECT count(*) FROM images WHERE status = 'error'")),
                bySource,
                Convert.ToInt32(Scalar(_db, "SELECT count(*) FROM embeddings WHERE length(vec) > 0")));
        }
    }

    // ---- helpers --------------------------------------------------------------------------

    static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static object? Scalar(SqliteConnection db, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar();
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }
}

/// <summary>Parsed form of the search box text.</summary>
public sealed record SearchQuery(List<string> Terms, string? Source, DateTime? After, DateTime? Before, long? Like = null)
{
    public static SearchQuery Parse(string text)
    {
        var terms = new List<string>();
        string? source = null;
        DateTime? after = null, before = null;
        long? like = null;

        foreach (var token in Tokenize(text))
        {
            if (token.StartsWith("in:", StringComparison.OrdinalIgnoreCase) && token.Length > 3)
                source = token[3..];
            else if (token.StartsWith("after:", StringComparison.OrdinalIgnoreCase) && TryDate(token[6..], out var a, out _))
                after = a;
            else if (token.StartsWith("before:", StringComparison.OrdinalIgnoreCase) && TryDate(token[7..], out var b, out _))
                before = b;
            else if (token.StartsWith("like:", StringComparison.OrdinalIgnoreCase) && long.TryParse(token[5..].TrimStart('#'), out var id))
                like = id;
            else
                terms.Add(token);
        }
        return new SearchQuery(terms, source, after, before, like);
    }

    /// <summary>Whitespace split, honoring "quoted phrases".</summary>
    static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>Accepts 2026, 2026-05, 2026-05-14. Returns UTC start and the granularity end.</summary>
    static bool TryDate(string s, out DateTime start, out DateTime end)
    {
        string[] formats = ["yyyy-MM-dd", "yyyy-MM", "yyyy"];
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            start = local.ToUniversalTime();
            end = (s.Length switch { 4 => local.AddYears(1), 7 => local.AddMonths(1), _ => local.AddDays(1) }).ToUniversalTime();
            return true;
        }
        start = end = default;
        return false;
    }
}
