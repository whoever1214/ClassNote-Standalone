using System.Data.SQLite;
using System.IO;

namespace ClassNote.Services;

/// <summary>
/// 单机本地数据存储（替代原服务端 PostgreSQL）。
/// 数据库文件位于 %LOCALAPPDATA%/ClassNote/classnote.db，
/// 保存会话、截图、笔记等全部数据，无需任何服务端。
/// </summary>
public sealed class LocalRepository : IDisposable
{
    private readonly string _dbPath;
    private readonly object _writeLock = new();

    public static LocalRepository Instance { get; } = new();

    private LocalRepository()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassNote");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "classnote.db");
        InitDb();
    }

    private void InitDb()
    {
        using var db = NewConnection();
        db.Open();
        using var cmd = new SQLiteCommand(@"
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                course TEXT NOT NULL,
                title TEXT,
                start_time TEXT NOT NULL,
                end_time TEXT,
                duration INTEGER,
                status TEXT NOT NULL DEFAULT 'recording',
                audio_path TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS screenshots (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                seq_no INTEGER NOT NULL,
                timestamp REAL NOT NULL,
                type TEXT NOT NULL,
                image_path TEXT NOT NULL,
                ocr_text TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(session_id, seq_no)
            );
            CREATE TABLE IF NOT EXISTS notes (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL UNIQUE,
                title TEXT,
                content_markdown TEXT,
                mindmap_data TEXT,
                summary TEXT,
                key_points TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );", db);
        cmd.ExecuteNonQuery();

        // 启动时把长期停留在非终态（recording/processing/ended）的会话标记为失败，
        // 避免进程中途退出后遗留"永久处理中"的会话造成主页无休止轮询。
        RecoverStaleSessions();
    }

    /// <summary>
    /// 将停留超过 <paramref name="staleAfterSeconds"/>（默认 1 小时）仍未到达终态
    /// （completed/failed）的会话标记为 failed。返回被标记的数量。
    /// </summary>
    public int RecoverStaleSessions(int staleAfterSeconds = 3600)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-staleAfterSeconds).ToString("O");
        lock (_writeLock)
        {
            using var db = NewConnection();
            db.Open();
            using var cmd = new SQLiteCommand(
                "UPDATE sessions SET status = 'failed' " +
                "WHERE status NOT IN ('completed','failed') AND created_at < @cutoff", db);
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }

    private SQLiteConnection NewConnection() => new($"Data Source={_dbPath}");

    private static string Now() => DateTime.UtcNow.ToString("O");

    // ── 会话 ──────────────────────────────────────────────

    public Guid CreateSession(string course, string? title)
    {
        var id = Guid.NewGuid();
        lock (_writeLock)
        {
            using var db = NewConnection();
            db.Open();
            using var cmd = new SQLiteCommand(
                "INSERT INTO sessions (id, course, title, start_time, status, created_at) VALUES (@id, @c, @t, @st, 'recording', @ca)", db);
            cmd.Parameters.AddWithValue("@id", id.ToString());
            cmd.Parameters.AddWithValue("@c", course);
            cmd.Parameters.AddWithValue("@t", (object?)title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@st", Now());
            cmd.Parameters.AddWithValue("@ca", Now());
            cmd.ExecuteNonQuery();
        }
        return id;
    }

    public List<Models.Session> ListSessions(int limit = 20)
    {
        using var db = NewConnection();
        db.Open();
        using var cmd = new SQLiteCommand(
            "SELECT id, course, title, start_time, end_time, duration, status FROM sessions ORDER BY created_at DESC LIMIT @n", db);
        cmd.Parameters.AddWithValue("@n", limit);
        var list = new List<Models.Session>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Models.Session
            {
                Id = Guid.Parse(reader.GetString(0)),
                Course = reader.GetString(1),
                Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                StartTime = DateTime.Parse(reader.GetString(3)).ToLocalTime(),
                EndTime = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)).ToLocalTime(),
                Duration = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Status = reader.GetString(6),
            });
        }
        return list;
    }

    public Models.Session? GetSession(Guid id)
    {
        using var db = NewConnection();
        db.Open();
        using var cmd = new SQLiteCommand(
            "SELECT id, course, title, start_time, end_time, duration, status, audio_path FROM sessions WHERE id = @id", db);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new Models.Session
        {
            Id = Guid.Parse(reader.GetString(0)),
            Course = reader.GetString(1),
            Title = reader.IsDBNull(2) ? null : reader.GetString(2),
            StartTime = DateTime.Parse(reader.GetString(3)).ToLocalTime(),
            EndTime = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)).ToLocalTime(),
            Duration = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            Status = reader.GetString(6),
        };
    }

    public void UpdateSessionStatus(Guid id, string status)
    {
        lock (_writeLock)
        {
            using var db = NewConnection();
            db.Open();
            using var cmd = new SQLiteCommand("UPDATE sessions SET status = @s WHERE id = @id", db);
            cmd.Parameters.AddWithValue("@s", status);
            cmd.Parameters.AddWithValue("@id", id.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    public void EndSession(Guid id, int durationSeconds)
    {
        lock (_writeLock)
        {
            using var db = NewConnection();
            db.Open();
            using var cmd = new SQLiteCommand(
                "UPDATE sessions SET end_time = @et, duration = @d, status = 'processing' WHERE id = @id", db);
            cmd.Parameters.AddWithValue("@et", Now());
            cmd.Parameters.AddWithValue("@d", durationSeconds);
            cmd.Parameters.AddWithValue("@id", id.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    public void SetSessionAudioPath(Guid id, string audioPath)
    {
        lock (_writeLock)
        {
            using var db = NewConnection();
            db.Open();
            using var cmd = new SQLiteCommand("UPDATE sessions SET audio_path = @p WHERE id = @id", db);
            cmd.Parameters.AddWithValue("@p", audioPath);
            cmd.Parameters.AddWithValue("@id", id.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    // ── 截图 ──────────────────────────────────────────────

    public void SaveScreenshot(Guid sessionId, int seqNo, double timestamp, string type,
        byte[] imageData, string? url)
    {
        var id = Guid.NewGuid();
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassNote", "screenshots", sessionId.ToString());
        Directory.CreateDirectory(dir);
        var imagePath = Path.Combine(dir, $"{seqNo:0000}.jpg");
        File.WriteAllBytes(imagePath, imageData);

        lock (_writeLock)
        {
            using var db = NewConnection();
            db.Open();
            // 同 seq 已存在则覆盖（唯一约束）
            using var del = new SQLiteCommand("DELETE FROM screenshots WHERE session_id = @s AND seq_no = @n", db);
            del.Parameters.AddWithValue("@s", sessionId.ToString());
            del.Parameters.AddWithValue("@n", seqNo);
            del.ExecuteNonQuery();

            using var cmd = new SQLiteCommand(@"
                INSERT INTO screenshots (id, session_id, seq_no, timestamp, type, image_path, created_at)
                VALUES (@id, @s, @n, @ts, @ty, @p, @ca)", db);
            cmd.Parameters.AddWithValue("@id", id.ToString());
            cmd.Parameters.AddWithValue("@s", sessionId.ToString());
            cmd.Parameters.AddWithValue("@n", seqNo);
            cmd.Parameters.AddWithValue("@ts", timestamp);
            cmd.Parameters.AddWithValue("@ty", type);
            cmd.Parameters.AddWithValue("@p", imagePath);
            cmd.Parameters.AddWithValue("@ca", Now());
            cmd.ExecuteNonQuery();
        }
    }

    public List<(int SeqNo, double Timestamp, string Type, string ImagePath)> ListScreenshots(Guid sessionId)
    {
        using var db = NewConnection();
        db.Open();
        using var cmd = new SQLiteCommand(
            "SELECT seq_no, timestamp, type, image_path FROM screenshots WHERE session_id = @s ORDER BY seq_no", db);
        cmd.Parameters.AddWithValue("@s", sessionId.ToString());
        var list = new List<(int, double, string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add((reader.GetInt32(0), reader.GetDouble(1), reader.GetString(2), reader.GetString(3)));
        return list;
    }

    public void SetScreenshotOcr(Guid sessionId, int seqNo, string ocrText)
    {
        lock (_writeLock)
        {
            using var db = NewConnection();
            db.Open();
            using var cmd = new SQLiteCommand(
                "UPDATE screenshots SET ocr_text = @o WHERE session_id = @s AND seq_no = @n", db);
            cmd.Parameters.AddWithValue("@o", ocrText);
            cmd.Parameters.AddWithValue("@s", sessionId.ToString());
            cmd.Parameters.AddWithValue("@n", seqNo);
            cmd.ExecuteNonQuery();
        }
    }

    public List<(int SeqNo, double Timestamp, string Type, string? OcrText)> ListScreenshotsWithOcr(Guid sessionId)
    {
        using var db = NewConnection();
        db.Open();
        using var cmd = new SQLiteCommand(
            "SELECT seq_no, timestamp, type, ocr_text FROM screenshots WHERE session_id = @s ORDER BY seq_no", db);
        cmd.Parameters.AddWithValue("@s", sessionId.ToString());
        var list = new List<(int, double, string, string?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add((reader.GetInt32(0), reader.GetDouble(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return list;
    }

    // ── 笔记 ──────────────────────────────────────────────

    public void SaveNote(Models.Note note)
    {
        var id = Guid.NewGuid();
        lock (_writeLock)
        {
            using var db = NewConnection();
            db.Open();
            // 唯一约束：同一 session 仅一份笔记，重复保存则先删除旧行
            using var del = new SQLiteCommand("DELETE FROM notes WHERE session_id = @s", db);
            del.Parameters.AddWithValue("@s", note.SessionId.ToString());
            del.ExecuteNonQuery();

            using var cmd = new SQLiteCommand(@"
                INSERT INTO notes (id, session_id, title, content_markdown, mindmap_data, summary, key_points, created_at, updated_at)
                VALUES (@id, @s, @t, @mk, @mm, @sum, @kp, @ca, @ua)", db);
            cmd.Parameters.AddWithValue("@id", id.ToString());
            cmd.Parameters.AddWithValue("@s", note.SessionId.ToString());
            cmd.Parameters.AddWithValue("@t", (object?)note.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mk", (object?)note.ContentMarkdown ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mm", note.MindmapData == null ? (object)DBNull.Value :
                Newtonsoft.Json.JsonConvert.SerializeObject(note.MindmapData));
            cmd.Parameters.AddWithValue("@sum", (object?)note.Summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@kp", DBNull.Value);
            cmd.Parameters.AddWithValue("@ca", Now());
            cmd.Parameters.AddWithValue("@ua", Now());
            cmd.ExecuteNonQuery();
        }
    }

    public Models.Note? GetNote(Guid sessionId)
    {
        using var db = NewConnection();
        db.Open();
        using var cmd = new SQLiteCommand(
            "SELECT id, session_id, title, content_markdown, mindmap_data, summary FROM notes WHERE session_id = @s", db);
        cmd.Parameters.AddWithValue("@s", sessionId.ToString());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var note = new Models.Note
        {
            Id = Guid.Parse(reader.GetString(0)),
            SessionId = Guid.Parse(reader.GetString(1)),
            Title = reader.IsDBNull(2) ? null : reader.GetString(2),
            ContentMarkdown = reader.IsDBNull(3) ? null : reader.GetString(3),
            Summary = reader.IsDBNull(5) ? null : reader.GetString(5),
        };
        if (!reader.IsDBNull(4))
        {
            try
            {
                note.MindmapData = Newtonsoft.Json.JsonConvert.DeserializeObject(reader.GetString(4));
            }
            catch { note.MindmapData = null; }
        }
        return note;
    }

    public void Dispose()
    {
        // 连接按需创建并释放，无长生命周期资源
    }
}
