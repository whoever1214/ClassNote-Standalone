# 管理前端页面设计文档

> 日期：2026-07-26
> 版本：v1.0

---

## 1. 项目概述

### 1.1 目标

为 ClassNote 服务端（Python FastAPI）添加一个轻量级管理前端页面，供管理员通过浏览器查看系统状态、配置运行时参数、浏览服务端日志。

### 1.2 范围

仅包含三个功能模块：

- **仪表盘** — 系统概览（课程数、任务队列、存储用量、用户数、最近任务）
- **系统配置** — 查看和修改运行时配置（LLM 参数、存储路径、Whisper 参数等）
- **日志查看** — 分页浏览服务端日志，支持级别和来源过滤

### 1.3 技术选型

| 选项 | 选择 |
|------|------|
| 渲染方式 | 服务端渲染（Server-Side Rendering） |
| 模板引擎 | Jinja2 |
| 前端框架 | 无（vanilla HTML + CSS + 少量 JS） |
| CSS 框架 | 无（手写 CSS，简洁风格） |
| 认证方式 | 复用现有 JWT 体系，token 存入 HTTP-only cookie |
| 构建工具 | 无（无需构建步骤） |

---

## 2. 整体架构

管理页面作为 FastAPI 子路由挂载，与现有 API 同进程运行。

```
客户端浏览器                          FastAPI 服务端
     │                                   │
     ├── /admin/login       ──→   认证 → JWT cookie
     ├── /admin/dashboard   ──→   SQLAlchemy 查询 → Jinja2 渲染
     ├── /admin/config      ──→   读/写 Settings + .env
     └── /admin/logs        ──→   读日志文件 → Jinja2 分页
```

### 2.1 新增文件结构

```
server/app/
├── admin_pages/                 # 新增：管理页面模块
│   ├── __init__.py              # APIRouter，所有页面路由
│   ├── templates/
│   │   ├── base.html            # 布局骨架（导航栏 + 侧边栏）
│   │   ├── login.html           # 登录页
│   │   ├── dashboard.html       # 仪表盘
│   │   ├── config.html          # 系统配置
│   │   └── logs.html            # 日志查看
│   └── static/
│       ├── style.css            # 全局样式
│       └── admin.js             # 非必需交互（显示密码、确认弹窗）
├── api/
│   └── admin_config.py          # 新增：配置读写 + 日志查询 API
│   └── admin.py                 # 已有：stats + tasks + users 接口
└── main.py                      # 修改：挂载 Jinja2Templates + StaticFiles
```

### 2.2 路径约定

| 路径 | 类型 | 说明 |
|------|------|------|
| `/admin/login` | 页面 | 登录表单 |
| `/admin/logout` | 动作 | 清除 cookie 并重定向 |
| `/admin/dashboard` | 页面 | 仪表盘 |
| `/admin/config` | 页面 | 配置管理页 |
| `/admin/logs` | 页面 | 日志查看页 |
| `/api/v1/admin/config` | API | 配置读写（新增） |
| `/api/v1/admin/logs` | API | 日志查询（新增） |
| `/api/v1/admin/stats` | API | 系统统计数据（已有） |

### 2.3 模板继承

所有管理页面继承 `base.html`，后者包含：

- `<head>` 公共 meta + CSS 引用
- 顶部导航栏（显示 app 名称 + 当前用户 + 退出按钮）
- 侧边栏（仪表盘 / 配置 / 日志 导航链接）
- `<main>` 内容区由子模板填充
- 底部 JS 引用

---

## 3. 认证与授权

### 3.1 登录流程

```
用户访问 /admin/* (未登录)
        │
        ▼
302 重定向到 /admin/login
        │
        ▼
填写用户名/密码 → POST /admin/login
        │
        ▼
服务端验证 (复用 auth_service.authenticate)
        │
        ▼
检查 role == "admin"
        │
        ▼
签发 access_token → 写入 HTTP-only cookie → 302 /admin/dashboard
```

### 3.2 Cookie 配置

```
Set-Cookie: admin_token=<jwt>; HttpOnly; Secure; SameSite=Lax; Path=/admin; Max-Age=86400
```

- `HttpOnly`：不可被 JS 读取，防 XSS 盗取
- `SameSite=Lax`：防止 CSRF（管理页面无跨站 POST 需求）
- `Path=/admin`：仅管理页面路径携带 cookie
- `Max-Age=86400`：24 小时过期，与 access_token 有效期一致

### 3.3 路由保护

新增 `cookie_to_token` 依赖函数，从 cookie 提取 JWT 后调用现有的 `get_current_user` + `require_admin`。

```python
async def get_admin_from_cookie(request: Request, db: Session = Depends(get_db)) -> User:
    token = request.cookies.get("admin_token")
    if not token:
        raise HTTPException(status_code=303, headers={"Location": "/admin/login"})
    # 复用现有 JWT 解码逻辑
    return require_admin(get_current_user(HTTPAuthorizationCredentials(scheme="Bearer", credentials=token), db))
```

> **v2.0 变更（2026-07-28）：认证已移除。** `/admin/*` 路由不再需要 JWT cookie。`get_admin_from_cookie` 替换为 `get_current_user`（返回默认用户），登录/登出路由已删除，管理页面可直接访问。

---

## 4. 仪表盘 (/admin/dashboard)

### 4.1 数据来源

| 指标 | 来源 |
|------|------|
| 今日课程数 | `Session` 表，`created_at >= today` 计数 |
| 待处理任务 | `TaskLog` 表，`status == 'pending'` 计数 |
| 运行中任务 | `TaskLog` 表，`status == 'running'` 计数 |
| 总用户数 | `User` 表计数 |
| 总存储用量 | `du -sb` 统计 `DATA_DIR/screenshots/` + `DATA_DIR/audio/` 目录大小 |
| 最近任务 | `TaskLog` 表最新 10 条 |

### 4.2 页面布局

```
┌─────────────────────────────────────────────┐
│  📊 系统概览                    [刷新]       │
├──────────┬──────────┬──────────┬─────────────┤
│ 今日课程  │ 待处理   │ 运行中   │ 总用户数    │
│    5      │   12     │    2     │    18      │
├──────────┴──────────┴──────────┴─────────────┤
│  存储用量: 2.3 GB                             │
├──────────────────────────────────────────────┤
│  最近任务                                      │
│  ┌────┬──────────┬──────────┬────────────────┐│
│  │ #  │ 步骤     │ 状态     │ 错误信息       ││
│  ├────┼──────────┼──────────┼────────────────┤│
│  │ 1  │ stt      │ done     │ -              ││
│  │ 2  │ ocr      │ running  │ -              ││
│  │ 3  │ llm      │ pending  │ -              ││
│  └────┴──────────┴──────────┴────────────────┘│
└──────────────────────────────────────────────┘
```

### 4.3 实现

路由处理函数内直接 SQLAlchemy 聚合查询，结果传入 Jinja2 模板渲染。刷新按钮触发完整页面重载（非 AJAX）。

---

## 5. 系统配置 (/admin/config)

### 5.1 数据源

读取 `app.config.settings` 实例的值，该实例从环境变量 / `.env` 文件加载（pydantic-settings）。

### 5.2 配置分组

| 分组 | 字段 |
|------|------|
| 🔐 通用 | APP_NAME, DEBUG, DATA_DIR, MAX_UPLOAD_SIZE_MB |
| 🤖 LLM | LLM_API_TYPE, LLM_API_KEY, LLM_API_BASE_URL, LLM_VISION_MODEL, LLM_TEXT_MODEL |
| 🔄 LLM 备用 | LLM_FALLBACK_API_KEY, LLM_FALLBACK_API_BASE_URL, LLM_FALLBACK_MODEL |
| 🎤 语音识别 | WHISPER_MODEL_SIZE, WHISPER_DEVICE, WHISPER_COMPUTE_TYPE, WHISPER_CPU_FALLBACK |
| 🖼️ OCR | OCR_CONFIDENCE_THRESHOLD |

### 5.3 读写接口

**读取：** `GET /api/v1/admin/config` → 返回当前配置的 JSON（API Key 等敏感字段部分遮盖）

**写入：** `POST /api/v1/admin/config` → 接收 JSON 字段更新 → 写入 `.env` 文件 → 重新加载 `Settings` 实例

```python
SENSITIVE_KEYS = {"LLM_API_KEY", "LLM_FALLBACK_API_KEY", "SECRET_KEY"}

def mask_sensitive(key: str, value: str) -> str:
    if key in SENSITIVE_KEYS and value:
        return value[:4] + "****"
    return value

@router.get("/config")
def get_config(admin: User = Depends(require_admin)):
    return {k: mask_sensitive(k, v) for k, v in settings.model_dump().items()}

@router.post("/config")
def update_config(data: dict, admin: User = Depends(require_admin)):
    # 读取当前 .env 文件
    # 更新 data 中的字段
    # 写回 .env
    # 重新加载 settings 实例
    # 返回更新后的配置
```

### 5.4 需要重启的配置

以下配置修改后需要重启 Celery worker 才能生效，页面上用 ⚠️ 图标标注：

- WHISPER_MODEL_SIZE
- WHISPER_DEVICE
- WHISPER_COMPUTE_TYPE
- CELERY_BROKER_URL / CELERY_RESULT_BACKEND

---

## 6. 日志查看 (/admin/logs)

### 6.1 数据来源

`GET /api/v1/admin/logs?level=&source=&page=&page_size=`

使用**日志文件模式**（主要方案）：读取 `DATA_DIR/../logs/` 下的 `api.log`, `celery.log` 等文件，按行解析。
备选（systemd journal 模式）：如果日志文件不存在，则执行 `journalctl -u classnote-api.service --no-pager -n 5000` 获取。

日志读取接口缓存最后 10,000 行（按 LRU 每个日志源独立缓存），避免重复读盘。

### 6.2 页面布局

```
┌──────────────────────────────────────────────┐
│  📋 运行日志                    [刷新]        │
├──────────────────────────────────────────────┤
│  日志源: [所有 ▼]    级别: [全部 ▼]          │
│  ┌──────────────────────────────────────────┐│
│  │ 2026-07-26 10:23:45  INFO  STT task      ││
│  │   completed for session abc-123          ││
│  │ 2026-07-26 10:22:30  WARN  OCR low       ││
│  │   confidence (0.62) on img_0042.jpg      ││
│  │ 2026-07-26 10:21:15 ERROR Task 42-xyz   ││
│  │   failed: Celery task timeout (300s)     ││
│  │ ...                                      ││
│  └──────────────────────────────────────────┘│
│                                    [1] [2] [3]│
└──────────────────────────────────────────────┘
```

### 6.3 过滤选项

- **日志源筛选**：所有 / api / celery / stt / ocr / llm
- **级别筛选**：全部 / INFO / WARN / ERROR
- **分页**：每页 50 条

---

## 7. 错误处理

| 场景 | 行为 |
|------|------|
| JWT 过期 | 页面路由检测到过期 token → 302 到 `/admin/login` |
| 非管理员登录 | 登录页显示"该用户无管理员权限"错误消息 |
| `.env` 写入失败 | 配置页显示"保存失败：[原因]" |
| 日志文件不存在 | 日志页显示"暂无日志数据" |
| 数据库查询失败 | 对应页面显示"系统错误，请稍后重试" |
| 404 页面 | 管理页面的未知路径显示 404 简单提示页 |

---

## 8. 依赖变更

```diff
# requirements.txt 新增
+ jinja2>=3.1
```

Jinja2 已在 FastAPI 的依赖中（通过 `starlette` 间接引入），但显式声明以明确依赖。

---

## 9. 非功能性需求

- **安全性**：~~管理页面路径 `/admin/*` 全部受认证保护，无匿名访问入口~~ **v2.0 认证已移除，直接可访问**
- **响应性**：页面在桌面浏览器（1920x1080 及以上）设计，不做移动端适配
- **性能**：仪表盘数据查询应在 500ms 内完成；日志分页每次读取 ≤50 条
- **可维护性**：管理页面模块独立于业务 API，修改前端不影响 API 逻辑
