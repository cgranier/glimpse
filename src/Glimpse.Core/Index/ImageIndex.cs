using System.Globalization;
using System.Text;
using System.Text.Json;
using Glimpse.Core.Ocr;
using Microsoft.Data.Sqlite;

namespace Glimpse.Core.Index;

public sealed record IndexedFile(long Id, string Path, long Size, long MtimeTicks);

public sealed record SearchHit(
    long Id,
    string Path,
    string Source,
    DateTime Modified,
    int Width,
    int Height,
    /// <summary>Snippet with matches wrapped in <see cref="ImageIndex.MatchStart"/>/<see cref="ImageIndex.MatchEnd"/>.</summary>
    string Snippet);

public sealed record IndexStats(int Images, int WithText, int Errors, IReadOnlyDictionary<string, int> BySource);

/// <summary>
/// SQLite store. `images` holds file metadata + OCR word boxes; `images_fts` is an FTS5 table
/// with the trigram tokenizer, so any 3+ character fragment matches ("moc" finds "MoCA").
/// </summary>
public sealed class ImageIndex : IDisposable
{
    public const char MatchStart = '\u0001';
    public const char MatchEnd = '\u0002';

    const int SchemaVersion = 1;
    readonly SqliteConnection _db;

    public ImageIndex(string? path = null)
    {
        path ??= GlimpseConfig.DatabasePath;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;");
        Migrate();
    }

    void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version >= SchemaVersion) return;

        Exec("""
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
            """);
        Exec($"PRAGMA user_version = {SchemaVersion}");
    }

    // ---- writes ---------------------------------------------------------------------------

    public SqliteTransaction BeginTransaction() => _db.BeginTransaction();

    public void Upsert(string path, string source, long size, DateTime mtimeUtc, OcrPage? page, string? error)
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
        fts.CommandText = """
            DELETE FROM images_fts WHERE rowid = $id;
            INSERT INTO images_fts (rowid, text, name) VALUES ($id, $text, $name);
            """;
        fts.Parameters.AddWithValue("$id", id);
        fts.Parameters.AddWithValue("$text", page?.FullText ?? "");
        fts.Parameters.AddWithValue("$name", System.IO.Path.GetFileNameWithoutExtension(path));
        fts.ExecuteNonQuery();
    }

    public void Remove(long id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM images_fts WHERE rowid = $id; DELETE FROM images WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---- reads ----------------------------------------------------------------------------

    public Dictionary<string, IndexedFile> GetFiles(string source)
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
        var json = Scalar("SELECT ocr_json FROM images WHERE id = $id", ("$id", id)) as string;
        return json is null ? [] : JsonSerializer.Deserialize<List<OcrLine>>(json) ?? [];
    }

    /// <summary>
    /// Query syntax: free words (all must match, 3+ chars use the trigram index),
    /// <c>in:source</c> (prefix match on source name), <c>after:2026-05</c>, <c>before:2026-06-15</c>.
    /// An empty query returns the most recent images.
    /// </summary>
    public List<SearchHit> Search(string query, int limit = 60)
    {
        var q = SearchQuery.Parse(query);
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

    public IndexStats GetStats()
    {
        var bySource = new Dictionary<string, int>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT source, count(*) FROM images GROUP BY source ORDER BY source";
            using var r = cmd.ExecuteReader();
            while (r.Read()) bySource[r.GetString(0)] = r.GetInt32(1);
        }
        return new IndexStats(
            Convert.ToInt32(Scalar("SELECT count(*) FROM images")),
            Convert.ToInt32(Scalar("SELECT count(*) FROM images_fts WHERE length(text) > 0")),
            Convert.ToInt32(Scalar("SELECT count(*) FROM images WHERE status = 'error'")),
            bySource);
    }

    // ---- helpers --------------------------------------------------------------------------

    void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    object? Scalar(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar();
    }

    public void Dispose() => _db.Dispose();
}

/// <summary>Parsed form of the search box text.</summary>
public sealed record SearchQuery(List<string> Terms, string? Source, DateTime? After, DateTime? Before)
{
    public static SearchQuery Parse(string text)
    {
        var terms = new List<string>();
        string? source = null;
        DateTime? after = null, before = null;

        foreach (var token in Tokenize(text))
        {
            if (token.StartsWith("in:", StringComparison.OrdinalIgnoreCase) && token.Length > 3)
                source = token[3..];
            else if (token.StartsWith("after:", StringComparison.OrdinalIgnoreCase) && TryDate(token[6..], out var a, out _))
                after = a;
            else if (token.StartsWith("before:", StringComparison.OrdinalIgnoreCase) && TryDate(token[7..], out var b, out _))
                before = b;
            else
                terms.Add(token);
        }
        return new SearchQuery(terms, source, after, before);
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
