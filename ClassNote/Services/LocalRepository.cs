using System.Data.SQLite;
using System.IO;

namespace ClassNote.Services;

/// <summary>一行截图记录（含图片路径与 OCR 文本）。</summary>
/// <param name="SeqNo">会话内序号。</param>
/// <param name="Timestamp">录音内秒数。</param>
/// <param name="Type">annotation / new_slide / video。</param>
/// <param name="ImagePath">图片文件路径。</param>
/// <param name="OcrText">OCR 文本；null = 还没识别过，"" = 识别过但图里没有文字。</param>
public sealed record ScreenshotRow(int SeqNo, double Timestamp, string Type, string ImagePath, string? OcrText);

/// <summary>
/// 单机本地数据存储（替代原服务端 PostgreSQL）。
/// 数据库文件位于 %LOCALAPPDATA%/ClassNote/classnote.db，
/// 保存会话、截图、笔记等全部数据，无需任何服务端。
/// </summary>
public sealed class LocalRepository : IDisposable, ITranscriptChunkStore
{
    private readonly string _dbPath;
    private readonly object _writeLock = new();

    public static LocalRepository Instance { get; } = new();

    private LocalRepository() : this(DefaultDbPath()) { }

    /// <summary>
    /// 指定库文件路径（单元测试用：绝不能让测试写进用户真实的 classnote.db）。
    /// </summary>
    internal LocalRepository(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _dbPath = dbPath;
        InitDb();
    }

    /// <summary>生产环境的库文件位置：%LOCALAPPDATA%/ClassNote/classnote.db。</summary>
    internal static string DefaultDbPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassNote", "classnote.db");

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
            );
            CREATE TABLE IF NOT EXISTS schedule_entries (
                id TEXT PRIMARY KEY,
                weekday_index INTEGER NOT NULL,
                start_min INTEGER NOT NULL,
                end_min INTEGER NOT NULL,
                course TEXT NOT NULL,
                title TEXT,
                enabled INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS transcript_chunks (
                session_id TEXT NOT NULL,
                track INTEGER NOT NULL,
                chunk_index INTEGER NOT NULL,
                start_seconds INTEGER NOT NULL DEFAULT 0,
                text TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (session_id, track, chunk_index)
            );", db);
        cmd.ExecuteNonQuery();

        // v0.6.0：分轨录音（麦克风 + 系统声音）需要存第二路音频路径。
        // 本仓库没有迁移框架，这里做一次幂等补列（SQLite 不支持 IF NOT EXISTS 加列，
        // 故先查 table_info 再决定是否 ALTER）。
        EnsureSessionAudioPathSystemColumn(db);

        // 启动时把长期停留在非终态（recording/processing/ended）的会话标记为失败，
        // 避免进程中途退出后遗留"永久处理中"的会话造成主页无休止轮询。
        RecoverStaleSessions();
    }

    /// <summary>
    /// 幂等补上 sessions.audio_path_system 列（旧库升级用）。
    /// 已存在则什么都不做；任何异常都吞掉——补列失败不该让整个应用起不来，
    /// 最坏情况只是分轨录音的第二路路径没记住。
    /// </summary>
    private static void EnsureSessionAudioPathSystemColumn(SQLiteConnection db)
    {
        try
        {
            bool exists = false;
            using (var check = new SQLiteCommand("PRAGMA table_info(sessions)", db))
            using (var reader = check.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), "audio_path_system", StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }
            if (exists)
                return;

            using var alter = new SQLiteCommand("ALTER TABLE sessions ADD COLUMN audio_path_system TEXT", db);
            alter.ExecuteNonQuery();
        }
        catch
        {
            // 补列失败不致命：分轨录音的系统声音路径会丢失，笔记仍按麦克风那一路生成
        }
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

    /// <summary>
    /// 一次性登记分轨录音的两路路径（麦克风 / 系统声音）。
    /// 单路录音时传 null 即可（对应列写 NULL）。
    /// </summary>
    public void SetSessionAudioTracks(Guid id, string? microphonePath, string? systemPath)
    {
        lock (_writeLock)
        {
            using var db = NewConnection();
            db.Open();
            using var cmd = new SQLiteCommand(
                "UPDATE sessions SET audio_path = @mic, audio_path_system = @sys WHERE id = @id", db);
            cmd.Parameters.AddWithValue("@mic", (object?)microphonePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sys", (object?)systemPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@id", id.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 读取会话的两路录音路径（供删除清理与排查用）。库中没有记录时返回两个 null。
    /// 列不存在（旧库未补列成功）同样返回两个 null，不抛异常。
    /// </summary>
    public (string? Microphone, string? System) GetSessionAudioTracks(Guid id)
    {
        try
        {
            using var db = NewConnection();
            db.Open();
            using var cmd = new SQLiteCommand(
                "SELECT audio_path, audio_path_system FROM sessions WHERE id = @id", db);
            cmd.Parameters.AddWithValue("@id", id.ToString());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return (null, null);
            var mic = reader.IsDBNull(0) ? null : reader.GetString(0);
            var sys = reader.IsDBNull(1) ? null : reader.GetString(1);
            return (mic, sys);
        }
        catch
        {
            return (null, null);
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

    /// <summary>截图行（含图片路径与 OCR 文本）。<c>OcrText == null</c> 表示"还没识别过"，
    /// 空串表示"识别过了，图里没有文字"——处理管线据此决定要不要现场补做 OCR。</summary>
    public List<ScreenshotRow> ListScreenshotsDetailed(Guid sessionId)
    {
        using var db = NewConnection();
        db.Open();
        using var cmd = new SQLiteCommand(
            "SELECT seq_no, timestamp, type, image_path, ocr_text FROM screenshots WHERE session_id = @s ORDER BY seq_no", db);
        cmd.Parameters.AddWithValue("@s", sessionId.ToString());
        var list = new List<ScreenshotRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ScreenshotRow(
                reader.GetInt32(0),
                reader.GetDouble(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return list;
    }

    // ── 增量转写块（v0.7 边录边转写）──────────────────────
    //
    // 存在的意义：录音期间每整理完 30 秒音频就落一块，因此
    // · 下课时绝大多数音频已经转写完，课后只需补最后一块；
    // · 中途崩溃/断电也不会白跑（旧实现一旦失败就是整条音轨重来）；
    // · 库里缺哪个块号，课后补算就重算哪一个 —— "已完成的块"本身就是有效性记录。

    public void SaveTranscriptChunk(Guid sessionId, RecordingAudioSource source, int chunkIndex, int startSeconds, string text)
    {
        lock (_writeLock)
        {
            using var db = NewConnection();
            db.Open();
            using var cmd = new SQLiteCommand(@"
                INSERT INTO transcript_chunks (session_id, track, chunk_index, start_seconds, text, created_at)
                VALUES (@s, @t, @i, @sec, @txt, @ca)
                ON CONFLICT(session_id, track, chunk_index)
                DO UPDATE SET text = @txt, start_seconds = @sec, created_at = @ca", db);
            cmd.Parameters.AddWithValue("@s", sessionId.ToString());
            cmd.Parameters.AddWithValue("@t", (int)source);
            cmd.Parameters.AddWithValue("@i", chunkIndex);
            cmd.Parameters.AddWithValue("@sec", startSeconds);
            cmd.Parameters.AddWithValue("@txt", text ?? "");
            cmd.Parameters.AddWithValue("@ca", Now());
            cmd.ExecuteNonQuery();
        }
    }

    public List<(int Index, string Text)> ListTranscriptChunks(Guid sessionId, RecordingAudioSource source)
    {
        using var db = NewConnection();
        db.Open();
        using var cmd = new SQLiteCommand(
            "SELECT chunk_index, text FROM transcript_chunks WHERE session_id = @s AND track = @t ORDER BY chunk_index", db);
        cmd.Parameters.AddWithValue("@s", sessionId.ToString());
        cmd.Parameters.AddWithValue("@t", (int)source);
        var list = new List<(int, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add((reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
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
                INSERT INTO notes (id, session_id, title, content_markdown, summary, key_points, created_at, updated_at)
                VALUES (@id, @s, @t, @mk, @sum, @kp, @ca, @ua)", db);
            cmd.Parameters.AddWithValue("@id", id.ToString());
            cmd.Parameters.AddWithValue("@s", note.SessionId.ToString());
            cmd.Parameters.AddWithValue("@t", (object?)note.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mk", (object?)note.ContentMarkdown ?? DBNull.Value);
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
            "SELECT id, session_id, title, content_markdown, summary FROM notes WHERE session_id = @s", db);
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
            Summary = reader.IsDBNull(4) ? null : reader.GetString(4),
        };
        return note;
    }

    /// <summary>
    /// 删除一个会话及其全部关联数据：截图行与图片文件、笔记行、音频文件、会话行。
    /// 幂等：会话不存在时静默返回。
    /// </summary>
    public void DeleteSession(Guid id)
    {
        var sid = id.ToString();

        // 1. 音频文件
        string? audioPath = null;
        {
            using var db = NewConnection();
            db.Open();
            using var cmd = new SQLiteCommand("SELECT audio_path FROM sessions WHERE id = @s", db);
            cmd.Parameters.AddWithValue("@s", sid);
            audioPath = cmd.ExecuteScalar() as string;
        }

        // 2. 截图图片文件（目录形式 %LOCALAPPDATA%/ClassNote/screenshots/{id}）
        try
        {
            var shotsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassNote", "screenshots", sid);
            if (Directory.Exists(shotsDir))
                Directory.Delete(shotsDir, recursive: true);
        }
        catch { /* 删除失败不阻断库记录删除 */ }

        // 3. 音频文件
        if (!string.IsNullOrWhiteSpace(audioPath))
        {
            try { if (File.Exists(audioPath)) File.Delete(audioPath); } catch { }
        }

        // 4. 库记录（截图/笔记/会话）
        lock (_writeLock)
        {
            using var db = NewConnection();
            db.Open();
            using (var cmd = new SQLiteCommand("DELETE FROM screenshots WHERE session_id = @s", db))
            { cmd.Parameters.AddWithValue("@s", sid); cmd.ExecuteNonQuery(); }
            using (var cmd = new SQLiteCommand("DELETE FROM notes WHERE session_id = @s", db))
            { cmd.Parameters.AddWithValue("@s", sid); cmd.ExecuteNonQuery(); }
            using (var cmd = new SQLiteCommand("DELETE FROM transcript_chunks WHERE session_id = @s", db))
            { cmd.Parameters.AddWithValue("@s", sid); cmd.ExecuteNonQuery(); }
            using (var cmd = new SQLiteCommand("DELETE FROM sessions WHERE id = @s", db))
            { cmd.Parameters.AddWithValue("@s", sid); cmd.ExecuteNonQuery(); }
        }
    }

    // ── 课表（定时记录）──────────────────────────────────────

    /// <summary>读取全部每周课表条目（未按时间排序）。</summary>
    public List<Models.ScheduleEntry> ListScheduleEntries()
    {
        using var db = NewConnection();
        db.Open();
        using var cmd = new SQLiteCommand(
            "SELECT id, weekday_index, start_min, end_min, course, title, enabled FROM schedule_entries", db);
        var list = new List<Models.ScheduleEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Models.ScheduleEntry
            {
                Id = Guid.Parse(reader.GetString(0)),
                WeekdayIndex = reader.GetInt32(1),
                StartMin = reader.GetInt32(2),
                EndMin = reader.GetInt32(3),
                Course = reader.GetString(4),
                Title = reader.IsDBNull(5) ? null : reader.GetString(5),
                Enabled = reader.GetInt32(6) != 0,
            });
        }
        return list;
    }

    /// <summary>
    /// 全量覆盖保存课表（事务内先删后插）。UI 编辑器每次"保存"整体提交，
    /// 条目少、频率低，全量替换比逐条 diff 更简单可靠，且保证与内存一致。
    /// </summary>
    public void ReplaceScheduleEntries(IEnumerable<Models.ScheduleEntry> entries)
    {
        lock (_writeLock)
        {
            using var db = NewConnection();
            db.Open();
            using var tx = db.BeginTransaction();
            using (var del = new SQLiteCommand("DELETE FROM schedule_entries", db))
                del.ExecuteNonQuery();

            using var cmd = new SQLiteCommand(@"
                INSERT INTO schedule_entries (id, weekday_index, start_min, end_min, course, title, enabled, created_at)
                VALUES (@id, @wd, @s, @e, @c, @t, @en, @ca)", db);
            var idP = cmd.Parameters.Add("@id", System.Data.DbType.String);
            var wdP = cmd.Parameters.Add("@wd", System.Data.DbType.Int32);
            var sP = cmd.Parameters.Add("@s", System.Data.DbType.Int32);
            var eP = cmd.Parameters.Add("@e", System.Data.DbType.Int32);
            var cP = cmd.Parameters.Add("@c", System.Data.DbType.String);
            var tP = cmd.Parameters.Add("@t", System.Data.DbType.String);
            var enP = cmd.Parameters.Add("@en", System.Data.DbType.Int32);
            var caP = cmd.Parameters.Add("@ca", System.Data.DbType.String);
            foreach (var entry in entries)
            {
                idP.Value = entry.Id.ToString();
                wdP.Value = entry.WeekdayIndex;
                sP.Value = entry.StartMin;
                eP.Value = entry.EndMin;
                cP.Value = entry.Course;
                tP.Value = (object?)entry.Title ?? DBNull.Value;
                enP.Value = entry.Enabled ? 1 : 0;
                caP.Value = Now();
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void Dispose()
    {
        // 连接按需创建并释放，无长生命周期资源
    }
}
