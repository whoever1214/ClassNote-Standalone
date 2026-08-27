# 课堂笔记处理系统设计文档

> 日期：2026-07-25
> 版本：v1.0
> 实施状态：✅ 已完成实现 (31 commits, 2026-07-25)

---

### 实施总结

**服务端 (Python FastAPI):** 42 个源文件 ✅
- JWT 认证 + 6 组 REST API + Celery 异步管线
- STT (Whisper + 说话人分离) + OCR (PaddleOCR + Vision LLM 路由)
- LLM 抽象层 (通义千问 + OpenAI 兼容故障切换)
- LLM 编排器 (笔记生成 + 思维导图 JSON)
- 视频下载与摘要 + 管理员 API
- Alembic 迁移 + systemd 部署配置

**客户端 (C# WPF .NET 8):** 28 个源文件 ✅
- 登录页 / 主页面 / 录音页面 / 笔记查看页面
- NAudio 录音 + 麦克风选择
- pHash 智能截屏检测 (10s 基线，1%/8%/40% 阈值)
- 实时上传 + SQLite 本地缓存队列

**项目规模:** ~6,400 行代码，31 次提交，91 个文件变更

---

## 1. 项目概述

### 1.1 目标

构建一个客户端-服务端架构的课堂笔记处理系统。客户端用于收录课堂录音和抓取课件板书截屏，服务端用于对数据进行分析处理，最终自动生成结构化笔记和知识点思维导图返回给客户端。

### 1.2 使用流程

1. 用户打开客户端 → 选择课程（语数英物化生历政地）
2. 点击"开始记录" → 自动录音 + 实时截屏上传
3. 下课点击"结束记录" → 上传完整音频
4. 服务端异步处理：STT → OCR/Vision → LLM编排
5. 客户端收到通知，拉取笔记和思维导图

### 1.3 使用场景

- **平台：** 客户端 Windows 10/11，服务端 Ubuntu 22.04
- **规模：** 学校/机构级，约 50 节课/天并发处理
- **用户：** 两级权限（用户/管理员）

---

## 2. 系统架构

### 2.1 整体架构

```
┌─────────────────────────────────────────────────────────────────┐
│                        客户端 (C# WPF)                           │
│  ┌──────────┐  ┌────────────────────┐  ┌────────────────────┐  │
│  │ 录音模块  │  │   截屏模块          │  │   本地缓存(SQLite)  │  │
│  │ NAudio   │  │ 10s基线+pHash检测   │  │ 上传状态/进度      │  │
│  └────┬─────┘  └─────────┬──────────┘  └────────────────────┘  │
│       └──────────┬───────┘                                      │
│                  ▼                                              │
│           上传模块 (分片上传+断点续传)                            │
└──────────────────────┬──────────────────────────────────────────┘
                       │ HTTPS / REST API
                       ▼
┌─────────────────────────────────────────────────────────────────┐
│                     服务端 (Ubuntu 22.04)                        │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │        FastAPI (Gunicorn + Uvicorn, 4 workers)              ││
│  │   ┌────────┐ ┌──────────┐ ┌──────────┐ ┌────────────────┐  ││
│  │   │用户/课程│ │笔记管理   │ │文件上传   │ │进度查询/WS     │  ││
│  │   │  API   │ │   API    │ │  API    │ │    API         │  ││
│  │   └────────┘ └──────────┘ └──────────┘ └────────────────┘  ││
│  └──────────────────────┬──────────────────────────────────────┘│
│                         │                                       │
│  ┌──────────────────────▼──────────────────────────────────────┐│
│  │              Celery 任务队列 (Redis Broker)                  ││
│  │  ┌──────────────┐ ┌────────────────┐ ┌──────────────────┐  ││
│  │  │ STT 管线      │ │ OCR/Vision 管线 │ │ LLM 编排器        │  ││
│  │  │ faster-whisper│ │ PaddleOCR →    │ │ ├─ 笔记生成       │  ││
│  │  │ pyannote      │ │ Vision LLM    │ │ ├─ 思维导图       │  ││
│  │  │ 说话人分离     │ │ (按需触发)     │ │ └─ 视频总结       │  ││
│  │  └──────────────┘ └────────────────┘ └──────────────────┘  ││
│  └──────────────────────┬──────────────────────────────────────┘│
│                         │                                       │
│  ┌──────────────────────▼──────────────────────────────────────┐│
│  │  数据层: PostgreSQL 16 + MinIO (文件) + Redis 7 (队列/缓存)  ││
│  └─────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
```

### 2.2 核心技术选型

| 模块 | 技术 | 选型理由 |
|------|------|---------|
| 客户端框架 | WPF (.NET 8) | Win原生、MVVM生态成熟 |
| 录音 | NAudio | .NET生态最成熟的音频采集库 |
| 截屏 | Win32 Graphics.CopyFromScreen | 原生性能，毫秒级 |
| 音频编码 | Opus | 压缩比10:1，45分钟约50MB |
| 后端框架 | FastAPI (Python 3.11) | 异步性能好，ML生态无缝对接 |
| 任务队列 | Celery + Redis | 成熟的任务编排、重试、监控 |
| 数据库 | PostgreSQL 16 | 结构化数据存储 |
| 文件存储 | MinIO 或 文件系统 | 截图/音频/笔记文件 |
| STT | faster-whisper (large-v3, P100) | 本地推理，中英混识别 |
| 说话人分离 | pyannote-audio | 师生声音分离 |
| OCR | PaddleOCR | 中英双语优化，CPU可跑 |
| LLM | 国产模型API + OpenAI兼容兜底 | 灵活切换，成本可控 |
| 部署 | systemd 管理（非Docker） | GPU直通简单，显存无争抢 |

---

## 3. 客户端详细设计

### 3.1 录音模块

- 枚举系统麦克风设备，用户下拉选择
- 使用 NAudio WaveIn 采集单路音频
- Opus 编码器边录边存为本地文件
- 课后通过分片上传发送到服务端

### 3.2 截屏模块

**截图策略（基线轮询 + 智能变化检测）：**

```
Timer 10s触发 → Graphics.CopyFromScreen → Bitmap
    │
    ▼
pHash 对比上一帧
    │
    ├── < 1% → 丢弃（画面静止）
    ├── 1~8% → 保留，标记 annotation
    ├── 8~40% → 保留，标记 new_slide
    └── > 40% (连续3帧) → 进入视频模式
                            │
                      降频到 60s/张
                      尝试捕获窗口视频URL
                      恢复条件: 变化回落到 < 40% 持续2帧
```

**视频URL捕获：**
检测到视频模式后，尝试通过 EnumWindows 获取前台浏览器窗口信息。由于浏览器安全策略限制，可靠方式：客户端弹出提示框让用户粘贴视频链接。

### 3.3 上传机制

- 截图：每张即时上传（JPEG 85%质量，multipart/form-data）
- 音频：课后分片上传（每片~5MB，自动重试+断点续传）

### 3.4 本地缓存

SQLite 记录会话信息、截图上传状态、本地记录进度，防止网络异常导致丢帧。

---

## 4. 数据库设计

### 4.1 核心表

**users（用户）**
```sql
CREATE TABLE users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    username VARCHAR(64) UNIQUE NOT NULL,
    password_hash VARCHAR(256) NOT NULL,
    display_name VARCHAR(64) NOT NULL,
    role VARCHAR(16) NOT NULL DEFAULT 'user', -- user / admin
    created_at TIMESTAMP DEFAULT NOW()
);
```

**sessions（课程记录会话）**
```sql
CREATE TABLE sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES users(id),
    course VARCHAR(8) NOT NULL,          -- 语数英物化生历政地
    title VARCHAR(128),
    start_time TIMESTAMP NOT NULL,
    end_time TIMESTAMP,
    duration INT,
    audio_file_path VARCHAR(512),
    audio_duration INT,
    status VARCHAR(20) DEFAULT 'recording',
    -- recording → uploaded → processing → completed / failed
    transcript_path VARCHAR(512),
    created_at TIMESTAMP DEFAULT NOW()
);
```

**screenshots（截图）**
```sql
CREATE TABLE screenshots (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID NOT NULL REFERENCES sessions(id),
    seq_no INT NOT NULL,
    timestamp REAL NOT NULL,
    file_path VARCHAR(512) NOT NULL,
    type VARCHAR(16) NOT NULL,           -- annotation / new_slide / video
    ocr_text TEXT,
    ocr_confidence REAL,
    vision_used BOOLEAN DEFAULT FALSE,
    vision_desc TEXT,
    url_found VARCHAR(1024),
    compressed_path VARCHAR(512),
    created_at TIMESTAMP DEFAULT NOW(),
    UNIQUE(session_id, seq_no)
);
```

**notes（笔记与思维导图）**
```sql
CREATE TABLE notes (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID UNIQUE NOT NULL REFERENCES sessions(id),
    title VARCHAR(256),
    content_markdown TEXT,
    mindmap_data JSONB,
    summary TEXT,
    key_points JSONB,
    video_summary TEXT,
    created_at TIMESTAMP DEFAULT NOW(),
    updated_at TIMESTAMP DEFAULT NOW()
);
```

**task_logs（处理进度追踪）**
```sql
CREATE TABLE task_logs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID NOT NULL REFERENCES sessions(id),
    step VARCHAR(16) NOT NULL,           -- stt / ocr / llm / video
    status VARCHAR(16) NOT NULL,         -- pending / running / done / failed
    progress REAL DEFAULT 0,
    started_at TIMESTAMP,
    completed_at TIMESTAMP,
    error_msg TEXT,
    retry_count INT DEFAULT 0,
    created_at TIMESTAMP DEFAULT NOW()
);
```

### 4.2 文件存储结构

```
data/
├── audio/{user_id}/{session_id}.opus
├── screenshots/{user_id}/{session_id}/{seq_no}_{type}.jpg
├── transcripts/{user_id}/{session_id}_transcript.json
└── notes/{user_id}/{session_id}_note.md
```

---

## 5. 处理管线设计

### 5.1 整体管线

```
上课期间: 截图逐张上传 → 即时入 OCR 管线（无需等待下课）

下课后: 上传音频 → 触发 STT 管线 → LLM 编排器
                                       │
          ┌────────────────────────────┤
          ▼                            ▼
    笔记生成 (Markdown)           思维导图生成 (JSON层级数据)
```

### 5.2 STT 管线

1. **音频预处理：** Opus解码 → 16kHz单声道 → loudnorm 音量归一化
2. **SenseVoice-Small 转写（CPU）：** 中文优化的多语言 ASR，自动标点，可输出句子级时间戳
3. **说话人分离（可选）：** pyannote-audio 说话人分离，为每段发言标注说话人；未安装或缺少 `HF_TOKEN` 时自动跳过，不影响转写
4. **说话人角色判定：** 基于每段说话时长启发式区分教师（主导讲解，时长更长）和学生（短促互动）
5. **场景切分：** 按相邻话语间长静音（默认 > 12s）自动划分为多个场景，供 LLM 分段处理

**模型常驻：** SenseVoice 模型在 worker 进程内常驻，避免重复加载（首次约 3 秒转 7 秒音频，CPU 即可）。

### 5.3 OCR/Vision 管线

1. PaddleOCR（CPU）全量处理每张截图
2. 判断是否需要 Vision LLM 补充：

| 条件 | 操作 |
|------|------|
| 文本置信度 ≥ 0.85 且无公式特征 | 直接使用 OCR 结果 |
| 文本置信度 < 0.85 或含公式字符（∫∑√π⇌） | 触发 Vision LLM |
| 数理化生学科 | 默认全部走 Vision + OCR 双通道 |
| 其他文科 | 仅 OCR 置信度低时触发 Vision |

3. Vision LLM 输出结构化描述，与 OCR 文本融合存储

**成本控制：** 数理化生全量Vision ≈ ¥30-90/天（Qwen-VL 估价），管理员可配置门控开关。

### 5.4 LLM 编排器

**Stage 1 - 内容理解与结构化：** 融合 STT 文本和 OCR/Vision 数据，分段、提取章节和知识点
**Stage 2 - 笔记生成：** 输出 Markdown 格式，含核心知识点、例题、重点标注、师生问答
**Stage 3 - 视频总结（如有）：** 下载视频 → 提取音频 → STT → LLM 总结，附原文链接
**Stage 4 - 思维导图生成：** 输出 JSON 层级数据，客户端渲染可视化树状图

**LLM 接入策略：**
- 主：通义千问 API（qwen-vl-max 视觉 / qwen-plus 文本）
- 备选：DeepSeek API / OpenAI 兼容 API
- 故障切换：超时5s重试2次 → 切换备选 → 标记失败

---

## 6. API 设计

### 6.1 接口列表

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/api/v1/auth/login` | 登录获取 JWT |
| POST | `/api/v1/auth/register` | 注册（管理员） |
| POST | `/api/v1/auth/refresh` | 刷新 Token |
| POST | `/api/v1/sessions` | 创建记录会话 |
| GET | `/api/v1/sessions` | 历史记录列表 |
| GET | `/api/v1/sessions/{id}` | 会话详情 |
| PATCH | `/api/v1/sessions/{id}/end` | 结束记录 |
| GET | `/api/v1/sessions/{id}/status` | 查询处理进度 |
| POST | `/api/v1/screenshots/upload` | 上传单张截图 |
| GET | `/api/v1/screenshots/{sid}/list` | 截图列表 |
| POST | `/api/v1/audio/upload/init` | 初始化分片上传 |
| POST | `/api/v1/audio/upload/{part}` | 上传分片 |
| POST | `/api/v1/audio/upload/complete` | 合并分片 |
| GET | `/api/v1/notes/{session_id}` | 获取笔记+导图 |
| GET | `/api/v1/notes/{session_id}/export` | 导出 Markdown |
| GET | `/api/v1/notes/{session_id}/export/pdf` | 导出 PDF（笔记打包，reportlab 渲染） |
| GET | `/api/v1/admin/users` | 用户列表（管理员） |
| PATCH | `/api/v1/admin/users/{id}` | 修改用户角色 |
| GET | `/api/v1/admin/stats` | 系统统计 |
| GET | `/api/v1/admin/tasks` | 任务状态 |

### 6.2 认证

JWT Bearer Token（access token 24h + refresh token 7d），bcrypt 密码哈希。

---

## 7. 安全设计

| 层面 | 方案 |
|------|------|
| 认证 | ~~JWT 无状态认证~~ **v2.0 已移除**（默认用户模式） |
| 密码 | ~~bcrypt 哈希~~ **v2.0 已移除** |
| 传输 | HTTPS（自签名或 Let's Encrypt） |
| 鉴权 | ~~FastAPI Depends JWT 中间件~~ **v2.0 已移除** |
| 权限 | ~~用户仅见自己数据，管理员可查看全部~~ **v2.0 已移除**（单用户模式） |
| 限流 | slowapi 限制接口调用频率 |
| API Key | 服务端存储，不暴露给客户端 |

---

## 8. 部署与运维

### 8.1 部署架构

```
Nginx (反向代理+HTTPS) → Gunicorn+Uvicorn (FastAPI, 4 workers)
                               ↓
                    Celery Workers (stt/ocr/llm/video)
                               ↓
              PostgreSQL 16 + MinIO (或文件系统) + Redis 7
                               ↓
                    GPU Runtime: CUDA 12 + PyTorch 2
```

### 8.2 部署方式

使用 systemd 服务管理，不使用 Docker（避免 GPU 直通配置复杂度和显存争抢）。

### 8.3 监控

- Celery Flower：任务队列 Web 监控
- nvidia-smi 定时记录：GPU 利用率监控
- `/admin/stats`：API 返回系统运行状态

### 8.4 错误处理与降级

| 模块 | 错误 | 处理 |
|------|------|------|
| STT | GPU OOM | 降级到 CPU 模式（慢3-5倍） |
| OCR | 单张失败 | 跳过该张，不阻塞管线 |
| Vision LLM | API超时 | 重试2次 → 跳过（退化为纯OCR） |
| LLM | API失败 | 切换备选模型 → 重试2次 |
| 视频 | URL失效 | 跳过，笔记注明"链接失效" |

---

## 9. 性能估算

### 课后处理耗时（45分钟课堂）

| 步骤 | 耗时 | 备注 |
|------|------|------|
| 上传音频 | ~1-2min | 局域网 |
| STT | ~7-12min | P100，WhisperX + 说话人分离 |
| OCR（上课时已完成） | 0min | 截图实时处理 |
| Vision LLM | ~30-60s | 按需触发 |
| LLM生成 | ~1-2min | API调用 |
| **总计** | **~10-15min** | 下课到收到结果 |

### 存储估算

| 项目 | 单节课 | 每天50节 | 1学期(~100天) |
|------|--------|---------|--------------|
| 音频 | ~50MB | ~2.5GB | ~250GB |
| 截图 | ~200-600张(~100MB) | ~5GB | ~500GB |
| 转写+笔记 | ~5MB | ~250MB | ~25GB |
| **合计** | **~155MB** | **~7.75GB** | **~775GB** |

建议配置 1TB 存储，保留一学期数据后可启自动清理策略。

---

## 10. 待办/未来扩展

- [x] 笔记导出为 PDF（服务端 reportlab 渲染，客户端一键下载到桌面）✅ 2026-08-20
- [ ] 客户端思维导图渲染组件（基于笔记的 mindmap_data JSON 渲染可视化树状图）
- [ ] 笔记编辑功能（用户在客户端可对生成的笔记做二次编辑）
- [ ] 管理员后台可视化面板
- [ ] 多人多场景：真实说话人分离（需配置 pyannote + HF_TOKEN，当前为可选降级逻辑）

---

## 11. 变更记录

### v3.0 (2026-08-20) — PDF 导出 + 多人多场景优化 + 客户端交互改进

**变更内容：**

**客户端 (C# WPF):**
- 主页刷新按钮改为小长方形，更紧凑
- 笔记查看页由上下布局改为**左右布局**（左侧摘要 + 思维导图，右侧笔记正文），顶部新增「← 返回」按钮
- 新增「导出 PDF」按钮：下载服务端打包的课件笔记 PDF 并保存到桌面
- `IApiService` / `ApiService` 新增 `ExportNotePdfAsync(sessionId)` 下载接口

**服务端 (Python FastAPI):**
- 新增 `GET /api/v1/notes/{session_id}/export/pdf`：用 reportlab + CJK 字体把笔记（含摘要、思维导图）渲染为 PDF 返回；reportlab 未安装时自动降级返回 Markdown
- `stt_task.py`：SenseVoice 转写基础上增加**可选**说话人分离（pyannote）、说话人角色判定（教师/学生/未知）、长静音场景切分，转写结果新增 `scenes` / `speakers` 结构
- `llm_orchestrator.py`：笔记 prompt 携带说话人角色与时间戳，按场景分段生成；截图内容按 `new_slide` 聚合为「场景 N」
- `requirements.txt` 新增 `reportlab==4.2.5`

---

### v2.0 (2026-07-28) — 移除认证系统

**背景：** 客户端作为单机课堂工具使用，不再需要用户登录和权限体系。

**变更内容：**

**客户端 (C# WPF):**
- 删除 `LoginPage.xaml` / `LoginPage.xaml.cs` / `LoginViewModel.cs`
- `IApiService` / `ApiService` 移除 `LoginAsync()`、`SetToken()`、认证头代码
- `UploadService` 构造函数不再需要 token 参数
- `RecordingPage` / `MainWindow` 移除所有 token 依赖
- 启动直接进入主页面，无需登录

**服务端 (Python FastAPI):**
- `dependencies.py`：`get_current_user` 返回数据库第一个用户（不存在则自动创建），`require_admin` 不再检查角色
- `api/router.py`：移除 auth 路由注册（/api/v1/auth/* 不再可用）
- `admin_pages`：移除登录页、登出路由、cookie 验证，直接使用默认用户
- `tests/test_auth.py`：删除（对应端点已不存在）

**影响：** 两端不再需要 JWT 密钥配置、Token 传递、用户注册流程。管理后台无需登录直接访问。部署简化。
