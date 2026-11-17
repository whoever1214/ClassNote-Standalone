using System.Data.SQLite;
using System.IO;

namespace ClassNote.Services;

/// <summary>一行截图记录（含图片路径与 OCR 文本）。</summary>
/// <param name="SeqNo">会话内序号。</param>
/// <param name="Timestamp">录音内秒数。</param>
/// <param name="Type">annotation / new_slide / video。</param>
/// <param name="ImagePath">图片文件路径。</param>
/// <param name="OcrText">OCR 文本；null = 还没识别过，"" = 识别过但图里没有文字。</param>
public sealed record ScreenshotRow(int SeqNo, double Timestamp, string Type, string ImagePath, string? OcrText,
    string? Phash = null);

/// <summary>
/// 单机本地数据存储（替代原服务端 PostgreSQL）。
/// 数据库文件位于 %LOCALAPPDATA%/ClassNote/classnote.db，
/// 保存会话、截图、笔记等全部数据，无需任何服务端。
/// </summary>
public sealed class LocalRepository : IDisposable, ITranscriptChunkStore
{
    private readonly string _dbPath;
    private readonly object _writeLock = new();

    public static LocalRepository Instance { get; private set; } = new();

    /// <summary>
    /// 把进程级实例指向另一个库文件（恢复工具/批处理用：拿别的机器上的 classnote.db 直接跑管线）。
    /// **只应在启动时调用一次**：管线里到处是 <c>LocalRepository.Instance</c>，运行中换库会让状态错位。
    /// </summary>
    internal static void RedirectTo(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath)) throw new ArgumentException("库路径不能为空", nameof(dbPath));
        Instance = new LocalRepository(dbPath);
    }

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
                phash TEXT,
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

        // v0.9：截图 pHash 落库。送模型前选图要用它做去重；不缓存的话每次生成笔记
        // 都要把整节课的 4K 截图重新解码一遍（实测 30–60ms/张）。
        EnsureScreenshotPhashColumn(db);

        // v1.2：收藏标记。收藏 = "这节的录音/转写/截图永久保留"，过期清理会跳过它。
        EnsureSessionFavoriteColumn(db);

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
    /// 幂等补上 sessions.is_favorite 列（旧库升级用）。异常吞掉：补列失败时收藏按钮会报错，
    /// 但录音与笔记本身不受影响。
    /// </summary>
    private static void EnsureSessionFavoriteColumn(SQLiteConnection db)
    {
        try
        {
            bool exists = false;
            using (var check = new SQLiteCommand("PRAGMA table_info(sessions)", db))
            using (var reader = check.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), "is_favorite", StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }
            if (exists)
                return;

            using var alter = new SQLiteCommand(
                "ALTER TABLE sessions ADD COLUMN is_favorite INTEGER NOT NULL DEFAULT 0", db);
            alter.ExecuteNonQuery();
        }
        catch
        {
            // 见上
        }
    }

    /// <summary>
    /// 幂等补上 screenshots.phash 列（旧库升级用）。已存在则什么都不做；异常吞掉——
    /// 补列失败只是"选图时得重新解码算哈希"，功能不受影响。
    /// </summary>
    private static void EnsureScreenshotPhashColumn(SQLiteConnection db)
    {
        try
        {
            bool exists = false;
            using (var check = new SQLiteCommand("PRAGMA table_info(screenshots)", db))
            using (var reader = check.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), "phash", StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }
            if (exists)
                return;

            using var alter = new SQLiteCommand("ALTER TABLE screenshots ADD COLUMN phash TEXT", db);
            alter.ExecuteNonQuery();
        }
        catch
        {
            // 见上：补列失败不影响功能，只是每次都要重算哈希
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
            "SELECT id, course, title, start_time, end_time, duration, status, is_favorite FROM sessions ORDER BY created_at DESC LIMIT @n", db);
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
                IsFavorite = reader.FieldCount > 7 && !reader.IsDBNull(7) && reader.GetInt32(7) != 0,
            });
        }
        return list;
    }

    public Models.Session? GetSession(Guid id)
    {
        using var db = NewConnection();
        db.Open();
        using var cmd = new SQLiteCommand(
            "SELECT id, course, title, start_time, end_time, duration, status, audio_path, is_favorite FROM sessions WHERE id = @id", db);
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
            IsFavorite = reader.FieldCount > 8 && !reader.IsDBNull(8) && reader.GetInt32(8) != 0,
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

    /// <summary>
    /// 缓存截图的感知哈希（见 <see cref="PerceptualHash"/>）。写失败不影响出图，
    /// 只是下次还得重算——因此这里不抛异常。
    /// </summary>
    public void SetScreenshotHash(Guid sessionId, int seqNo, string phash)
    {
        try
        {
            lock (_writeLock)
            {
                using var db = NewConnection();
                db.Open();
                using var cmd = new SQLiteCommand(
                    "UPDATE screenshots SET phash = @h WHERE session_id = @s AND seq_no = @n", db);
                cmd.Parameters.AddWithValue("@h", phash);
                cmd.Parameters.AddWithValue("@s", sessionId.ToString());
                cmd.Parameters.AddWithValue("@n", seqNo);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalRepository] 截图哈希落库失败: {ex.Message}");
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
            "SELECT seq_no, timestamp, type, image_path, ocr_text, phash FROM screenshots WHERE session_id = @s ORDER BY seq_no", db);
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
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
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
    /// 删除某个会话的音频文件（麦克风 + 系统声音两路），并把库里的路径置空。
    /// 转写成功后音频就是纯原料：重新生成笔记只需要转写与截图，音频留着只占磁盘
    /// （一节课 40 分钟约 73 MB）。转写**没**成功的会话绝不能调用它。
    /// </summary>
    /// <returns>释放的字节数；文件本来就没了时返回 0。</returns>
    public long DeleteSessionAudio(Guid id)
    {
        var (mic, sys) = GetSessionAudioTracks(id);
        long freed = 0;
        foreach (var path in new[] { mic, sys })
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;
            try
            {
                long size = new FileInfo(path).Length;
                File.Delete(path);
                freed += size;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocalRepository] 删除音频失败 {path}: {ex.Message}");
            }
        }
        SetSessionAudioTracks(id, null, null);
        return freed;
    }

    /// <summary>
    /// 标记/取消收藏。收藏的含义是"这节的录音、转写、截图永久保留"——
    /// 过期清理会跳过收藏的会话（见 <see cref="PurgeExpiredContent"/>）。
    /// </summary>
    public void SetSessionFavorite(Guid id, bool favorite)
    {
        lock (_writeLock)
        {
            using var db = NewConnection();
            db.Open();
            using var cmd = new SQLiteCommand("UPDATE sessions SET is_favorite = @f WHERE id = @id", db);
            cmd.Parameters.AddWithValue("@f", favorite ? 1 : 0);
            cmd.Parameters.AddWithValue("@id", id.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    public bool IsSessionFavorite(Guid id)
    {
        using var db = NewConnection();
        db.Open();
        using var cmd = new SQLiteCommand("SELECT is_favorite FROM sessions WHERE id = @id", db);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        var value = cmd.ExecuteScalar();
        return value != null && value != DBNull.Value && Convert.ToInt32(value) != 0;
    }

    /// <summary>
    /// 读取库里**已经算好的**转写（按音轨拼接），供"重新生成笔记"使用：
    /// 转写一次要几分钟到十几分钟，重新生成时绝不能再跑一遍。
    /// 返回空列表表示这节课没有转写数据（可能还没转写、或已被过期清理）。
    /// </summary>
    public List<(RecordingAudioSource Source, string Text)> ListStoredTranscripts(Guid sessionId)
    {
        var result = new List<(RecordingAudioSource, string)>();
        using var db = NewConnection();
        db.Open();
        using var cmd = new SQLiteCommand(
            "SELECT track, chunk_index, text FROM transcript_chunks WHERE session_id = @s ORDER BY track, chunk_index", db);
        cmd.Parameters.AddWithValue("@s", sessionId.ToString());

        var byTrack = new SortedDictionary<int, List<string>>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                int track = reader.GetInt32(0);
                string text = reader.IsDBNull(2) ? "" : reader.GetString(2);
                if (!byTrack.TryGetValue(track, out var list))
                    byTrack[track] = list = new List<string>();
                list.Add(text);
            }
        }

        foreach (var (track, texts) in byTrack)
        {
            // 与 SenseVoiceSttService.JoinChunks 同口径：丢空块、单空格连接
            string joined = string.Join(" ", texts.Where(t => !string.IsNullOrWhiteSpace(t))).Trim();
            result.Add(((RecordingAudioSource)track, joined));
        }
        return result;
    }

    /// <summary>过期清理结果（给托盘日志/设置页展示，也给单测断言）。</summary>
    /// <param name="SessionsScanned">扫描到的会话总数。</param>
    /// <param name="SessionsPurged">真正被清理的会话数。</param>
    /// <param name="FilesDeleted">删除的文件个数（音频 + 截图）。</param>
    /// <param name="BytesFreed">释放的磁盘字节数。</param>
    /// <param name="SkippedNoTranscript">
    /// 因"没有任何转写块"而被跳过的会话数：这类课**不能**清理——
    /// 音频是唯一还能补救的素材（正是"STT 失败但音频还在"的现场）。
    /// </param>
    public sealed record PurgeResult(
        int SessionsScanned, int SessionsPurged, int FilesDeleted, long BytesFreed,
        int SkippedNoTranscript = 0);

    /// <summary>
    /// 过期清理：把过期、**未收藏**会话的音频 / 截图（文件+行） / 转写块删掉，
    /// 只保留笔记与会话记录（用户在主页仍能看到并导出这节课的笔记）。
    ///
    /// 为什么保留笔记：笔记是产物，几十 KB；音频与截图是原料，几 GB。
    /// 清理的目的只是别让原料把磁盘吃满，不是删用户的课。
    /// </summary>
    /// <param name="retentionDays">保留天数；≤0 表示永不清理。</param>
    /// <param name="utcNow">注入当前时间（测试用）。</param>
    public PurgeResult PurgeExpiredContent(int retentionDays, DateTime? utcNow = null)
    {
        if (retentionDays <= 0)
            return new PurgeResult(0, 0, 0, 0);

        var cutoff = (utcNow ?? DateTime.UtcNow).AddDays(-retentionDays);

        var candidates = new List<(Guid Id, DateTime Start, bool Favorite)>();
        using (var db = NewConnection())
        {
            db.Open();
            using var cmd = new SQLiteCommand("SELECT id, start_time, is_favorite FROM sessions", db);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out var id))
                    continue;
                // 逐条在 C# 里解析时间，而不是把 cutoff 拼成字符串抛给 SQL：
                // start_time 存的是带偏移的 ISO-8601，字符串比较在跨时区/夏令时会错。
                if (!DateTime.TryParse(reader.GetString(1), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var start))
                    continue;
                bool favorite = !reader.IsDBNull(2) && reader.GetInt32(2) != 0;
                candidates.Add((id, start, favorite));
            }
        }

        int purged = 0, files = 0;
        long bytes = 0;

        int skippedNoTranscript = 0;
        foreach (var (id, start, favorite) in candidates)
        {
            if (favorite || start.ToUniversalTime() >= cutoff)
                continue;

            // 护栏：这节课一块转写都没有 → 音频是唯一能补救的素材（典型的"STT 失败但录音还在"），
            // 绝不能按时限删掉。等它转写成功后再按 7 天处理。
            if (!HasTranscriptChunks(id))
            {
                skippedNoTranscript++;
                continue;
            }

            var (mic, sys) = GetSessionAudioTracks(id);
            foreach (var path in new[] { mic, sys })
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    continue;
                try
                {
                    long size = new FileInfo(path).Length;
                    File.Delete(path);
                    files++;
                    bytes += size;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LocalRepository] 删除音频失败 {path}: {ex.Message}");
                }
            }
            SetSessionAudioTracks(id, null, null);

            // 截图：文件 + 行
            try
            {
                var shotsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClassNote", "screenshots", id.ToString());
                if (Directory.Exists(shotsDir))
                {
                    foreach (var file in Directory.EnumerateFiles(shotsDir, "*", SearchOption.AllDirectories))
                    {
                        try { bytes += new FileInfo(file).Length; } catch { }
                        files++;
                    }
                    Directory.Delete(shotsDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocalRepository] 删除截图目录失败: {ex.Message}");
            }

            lock (_writeLock)
            {
                using var db = NewConnection();
                db.Open();
                using (var cmd = new SQLiteCommand("DELETE FROM screenshots WHERE session_id = @s", db))
                { cmd.Parameters.AddWithValue("@s", id.ToString()); cmd.ExecuteNonQuery(); }
                using (var cmd = new SQLiteCommand("DELETE FROM transcript_chunks WHERE session_id = @s", db))
                { cmd.Parameters.AddWithValue("@s", id.ToString()); cmd.ExecuteNonQuery(); }
            }

            purged++;
        }

        return new PurgeResult(candidates.Count, purged, files, bytes, skippedNoTranscript);
    }

    /// <summary>该会话是否已经有转写块（= 语音识别成功过）。</summary>
    public bool HasTranscriptChunks(Guid sessionId)
    {
        using var db = NewConnection();
        db.Open();
        using var cmd = new SQLiteCommand(
            "SELECT 1 FROM transcript_chunks WHERE session_id = @s LIMIT 1", db);
        cmd.Parameters.AddWithValue("@s", sessionId.ToString());
        return cmd.ExecuteScalar() != null;
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

        // 3. 音频文件（两路都要删：旧实现只删了 audio_path，system 轨永远残留）
        if (!string.IsNullOrWhiteSpace(audioPath))
        {
            try { if (File.Exists(audioPath)) File.Delete(audioPath); } catch { }
        }
        var (_, systemAudioPath) = GetSessionAudioTracks(id);
        if (!string.IsNullOrWhiteSpace(systemAudioPath))
        {
            try { if (File.Exists(systemAudioPath)) File.Delete(systemAudioPath); } catch { }
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
