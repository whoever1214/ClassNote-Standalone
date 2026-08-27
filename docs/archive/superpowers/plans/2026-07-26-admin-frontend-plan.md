# Admin Frontend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a lightweight server-rendered admin page (dashboard, config, logs) to the ClassNote FastAPI backend.

**Architecture:** New `admin_pages/` module with Jinja2 templates, mounted as a sub-router under `/admin`. Reuses existing JWT auth via HTTP-only cookie. Two new API endpoints (`/api/v1/admin/config` and `/api/v1/admin/logs`) in a new `admin_config.py` router.

**Tech Stack:** Python FastAPI, Jinja2, SQLAlchemy, vanilla HTML/CSS/JS (no build step)

## Global Constraints

- No frontend build toolchain: CSS and JS are plain files served via StaticFiles
- Jinja2 templates inherit from `base.html` layout
- All `/admin/*` routes protected by JWT cookie auth; unauthenticated requests 302 to `/admin/login`
- `.env` file is at `server/.env`, loaded via pydantic-settings
- Log files located at `DATA_DIR/../logs/*.log` (where DATA_DIR is from settings)

---

## File Structure

```
server/
├── app/
│   ├── admin_pages/              # NEW package
│   │   ├── __init__.py           # APIRouter + all page routes + cookie auth dependency
│   │   ├── templates/
│   │   │   ├── base.html         # Layout skeleton (nav + sidebar + content)
│   │   │   ├── login.html        # Login form
│   │   │   ├── dashboard.html    # Stats overview
│   │   │   ├── config.html       # Config form
│   │   │   └── logs.html         # Log viewer
│   │   └── static/
│   │       ├── style.css         # Admin UI styling
│   │       └── admin.js          # Minor JS (show API key, confirm dialogs)
│   ├── api/
│   │   ├── admin.py              # EXISTING — stats/tasks/users endpoints
│   │   └── admin_config.py       # NEW — config + log API endpoints
│   ├── main.py                   # MODIFY — add Jinja2Templates + StaticFiles + mount
│   └── ...                       # existing files unchanged
├── requirements.txt              # MODIFY — add jinja2
└── .env                          # EXISTING — written by config page
```

---

### Task 1: Project scaffolding

**Files:**
- Create: `server/app/admin_pages/__init__.py`
- Create: `server/app/admin_pages/templates/.gitkeep`
- Create: `server/app/admin_pages/static/.gitkeep`
- Modify: `server/app/main.py`
- Modify: `server/requirements.txt`

**Interfaces:**
- Consumes: `app.config.settings` (existing)
- Consumes: `app.api.router` (existing — the main API router to mirror mount)
- Produces: `admin_pages.router` — `APIRouter(prefix="/admin")` mounted at app level
- Produces: Jinja2Templates instance at `admin_pages/templates/`
- Produces: StaticFiles mount for `/admin/static/`

- [ ] **Step 1: Create the admin_pages package and directories**

```bash
mkdir -p server/app/admin_pages/templates
mkdir -p server/app/admin_pages/static
touch server/app/admin_pages/__init__.py
touch server/app/admin_pages/templates/.gitkeep
touch server/app/admin_pages/static/.gitkeep
```

- [ ] **Step 2: Add jinja2 to requirements.txt**

```diff
+ jinja2>=3.1
```

- [ ] **Step 3: Write the admin_pages router stub**

Write `server/app/admin_pages/__init__.py`:

```python
from fastapi import APIRouter
from starlette.templating import Jinja2Templates
from pathlib import Path

templates = Jinja2Templates(directory=Path(__file__).parent / "templates")
router = APIRouter(prefix="/admin", tags=["admin_pages"])
```

- [ ] **Step 4: Wire up in main.py**

Modify `server/app/main.py` to mount Jinja2Templates, StaticFiles, and the admin router:

```python
from fastapi import FastAPI
from fastapi.staticfiles import StaticFiles
from pathlib import Path
from app.config import settings
from app.api.router import api_router
from app.admin_pages import router as admin_pages_router
from app.admin_pages import templates

app = FastAPI(title=settings.APP_NAME, version="1.0.0")
app.include_router(api_router)
app.include_router(admin_pages_router)

# Mount static files for admin pages
BASE_DIR = Path(__file__).resolve().parent
app.mount("/admin/static", StaticFiles(directory=BASE_DIR / "admin_pages" / "static"), name="admin_static")

@app.get("/health")
def health():
    return {"status": "ok"}
```

- [ ] **Step 5: Quick sanity check — app starts**

Run: `cd server && python -c "from app.main import app; print('OK:', app.routes[-1].path)"`
Expected: `OK: /admin`

- [ ] **Step 6: Commit**

```bash
git add server/app/admin_pages/ server/app/main.py server/requirements.txt
git commit -m "feat: add admin_pages scaffolding with Jinja2 and StaticFiles"
```

---

### Task 2: Base template, CSS, and JS

**Files:**
- Create: `server/app/admin_pages/templates/base.html`
- Create: `server/app/admin_pages/static/style.css`
- Create: `server/app/admin_pages/static/admin.js`

**Interfaces:**
- Consumes: `request` (Starlette Request) with `request.user` (set by auth middleware)
- Consumes: `{{ current_user }}` — `User` model instance, passed from route handlers
- Produces: Jinja2 block names: `title`, `content`, `extra_head`, `extra_scripts`

- [ ] **Step 1: Write `style.css`**

Write `server/app/admin_pages/static/style.css`:

```css
:root {
  --sidebar-width: 220px;
  --nav-height: 48px;
  --bg-body: #f5f6fa;
  --bg-sidebar: #1e2a3a;
  --bg-card: #ffffff;
  --text-primary: #1a1a2e;
  --text-secondary: #6c757d;
  --text-sidebar: #c8cdd5;
  --accent: #4a6cf7;
  --accent-hover: #3b5de7;
  --border: #e2e6ea;
  --success: #28a745;
  --warning: #ffc107;
  --danger: #dc3545;
}

* { margin: 0; padding: 0; box-sizing: border-box; }
body {
  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
  background: var(--bg-body);
  color: var(--text-primary);
  min-height: 100vh;
}

/* Layout */
.app-container { display: flex; min-height: 100vh; }
.sidebar {
  width: var(--sidebar-width);
  background: var(--bg-sidebar);
  color: var(--text-sidebar);
  padding: 0;
  position: fixed; top: 0; left: 0; bottom: 0;
  z-index: 100;
}
.sidebar-header {
  padding: 16px 20px;
  font-size: 18px;
  font-weight: 700;
  color: #fff;
  border-bottom: 1px solid rgba(255,255,255,0.08);
  height: var(--nav-height);
  display: flex;
  align-items: center;
}
.sidebar-nav { padding: 12px 0; }
.sidebar-nav a {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 20px;
  color: var(--text-sidebar);
  text-decoration: none;
  font-size: 14px;
  transition: background 0.15s;
}
.sidebar-nav a:hover { background: rgba(255,255,255,0.06); color: #fff; }
.sidebar-nav a.active {
  background: rgba(74,108,247,0.2);
  color: #fff;
  border-right: 3px solid var(--accent);
}
.main-content {
  margin-left: var(--sidebar-width);
  flex: 1;
  padding: 24px 32px;
}
.topbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 24px;
}
.topbar h1 { font-size: 22px; font-weight: 600; }
.topbar-user {
  display: flex;
  align-items: center;
  gap: 12px;
  font-size: 14px;
  color: var(--text-secondary);
}
.topbar-user a { color: var(--danger); text-decoration: none; font-size: 13px; }
.topbar-user a:hover { text-decoration: underline; }

/* Cards */
.card {
  background: var(--bg-card);
  border-radius: 8px;
  border: 1px solid var(--border);
  padding: 20px;
  margin-bottom: 20px;
}
.card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 16px;
  margin-bottom: 20px;
}
.stat-card {
  background: var(--bg-card);
  border-radius: 8px;
  border: 1px solid var(--border);
  padding: 20px;
  text-align: center;
}
.stat-card .value { font-size: 32px; font-weight: 700; color: var(--accent); }
.stat-card .label { font-size: 13px; color: var(--text-secondary); margin-top: 4px; }

/* Tables */
table { width: 100%; border-collapse: collapse; font-size: 14px; }
th, td { padding: 10px 12px; text-align: left; border-bottom: 1px solid var(--border); }
th { font-weight: 600; color: var(--text-secondary); font-size: 12px; text-transform: uppercase; letter-spacing: 0.5px; }
td { color: var(--text-primary); }

/* Status badges */
.badge {
  display: inline-block;
  padding: 2px 8px;
  border-radius: 10px;
  font-size: 12px;
  font-weight: 500;
}
.badge-done { background: #d4edda; color: #155724; }
.badge-running { background: #cce5ff; color: #004085; }
.badge-pending { background: #fff3cd; color: #856404; }
.badge-failed { background: #f8d7da; color: #721c24; }

/* Forms */
.form-group { margin-bottom: 16px; }
.form-group label { display: block; font-size: 13px; font-weight: 600; color: var(--text-secondary); margin-bottom: 4px; }
.form-group input, .form-group select {
  width: 100%;
  padding: 8px 12px;
  border: 1px solid var(--border);
  border-radius: 6px;
  font-size: 14px;
  font-family: inherit;
  background: #fff;
}
.form-group input:focus, .form-group select:focus {
  outline: none;
  border-color: var(--accent);
  box-shadow: 0 0 0 2px rgba(74,108,247,0.15);
}
.form-row { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
.form-section { margin-bottom: 24px; }
.form-section h3 { font-size: 15px; margin-bottom: 12px; padding-bottom: 8px; border-bottom: 1px solid var(--border); }

/* Buttons */
.btn {
  display: inline-block;
  padding: 8px 16px;
  border-radius: 6px;
  font-size: 14px;
  font-weight: 500;
  border: none;
  cursor: pointer;
  text-decoration: none;
  transition: background 0.15s;
}
.btn-primary { background: var(--accent); color: #fff; }
.btn-primary:hover { background: var(--accent-hover); }
.btn-secondary { background: #e2e6ea; color: var(--text-primary); }
.btn-secondary:hover { background: #d3d8de; }
.btn-danger { background: var(--danger); color: #fff; }
.btn-sm { padding: 4px 10px; font-size: 12px; }
.btn-group { display: flex; gap: 8px; justify-content: flex-end; margin-top: 20px; }

/* Alerts */
.alert {
  padding: 12px 16px;
  border-radius: 6px;
  font-size: 14px;
  margin-bottom: 16px;
}
.alert-error { background: #f8d7da; color: #721c24; border: 1px solid #f5c6cb; }
.alert-success { background: #d4edda; color: #155724; border: 1px solid #c3e6cb; }

/* Log viewer */
.log-entry { padding: 6px 0; font-family: "SF Mono", "Fira Code", "Consolas", monospace; font-size: 13px; border-bottom: 1px solid var(--border); }
.log-entry .ts { color: var(--text-secondary); }
.log-entry .level { font-weight: 600; }
.log-entry .level-INFO { color: var(--accent); }
.log-entry .level-WARN { color: var(--warning); }
.log-entry .level-ERROR { color: var(--danger); }
.log-entry .source { color: var(--text-secondary); }
.log-entry .msg { color: var(--text-primary); }

/* Filters bar */
.filters { display: flex; gap: 12px; align-items: center; margin-bottom: 16px; flex-wrap: wrap; }
.filters select { padding: 6px 10px; border: 1px solid var(--border); border-radius: 6px; font-size: 13px; }

/* Pagination */
.pagination { display: flex; gap: 4px; justify-content: center; margin-top: 16px; }
.pagination a {
  padding: 6px 12px;
  border: 1px solid var(--border);
  border-radius: 4px;
  text-decoration: none;
  color: var(--text-primary);
  font-size: 13px;
}
.pagination a.active { background: var(--accent); color: #fff; border-color: var(--accent); }
.pagination a:hover:not(.active) { background: #f0f0f0; }

/* Login page (centered, no sidebar) */
.login-page {
  display: flex;
  justify-content: center;
  align-items: center;
  min-height: 100vh;
  background: var(--bg-sidebar);
}
.login-card {
  background: #fff;
  border-radius: 12px;
  padding: 40px;
  width: 380px;
  box-shadow: 0 4px 24px rgba(0,0,0,0.15);
}
.login-card h1 { font-size: 24px; margin-bottom: 8px; text-align: center; }
.login-card p { color: var(--text-secondary); font-size: 14px; text-align: center; margin-bottom: 24px; }
.login-card .form-group { margin-bottom: 20px; }
.login-card .btn { width: 100%; padding: 10px; font-size: 15px; }
```

- [ ] **Step 2: Write `admin.js`**

Write `server/app/admin_pages/static/admin.js`:

```javascript
// Toggle password/API key visibility
document.querySelectorAll(".toggle-visibility").forEach(function(btn) {
  btn.addEventListener("click", function() {
    var input = document.querySelector(this.dataset.target);
    if (input) {
      input.type = input.type === "password" ? "text" : "password";
      this.textContent = input.type === "password" ? "显示" : "隐藏";
    }
  });
});

// Confirm destructive actions
document.querySelectorAll("[data-confirm]").forEach(function(el) {
  el.addEventListener("click", function(e) {
    if (!confirm(this.dataset.confirm)) {
      e.preventDefault();
    }
  });
});

// Auto-dismiss alerts after 5s
document.querySelectorAll(".alert").forEach(function(el) {
  setTimeout(function() { el.style.display = "none"; }, 5000);
});
```

- [ ] **Step 3: Write `base.html`**

Write `server/app/admin_pages/templates/base.html`:

```html
<!DOCTYPE html>
<html lang="zh-CN">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>{% block title %}ClassNote 管理{% endblock %}</title>
  <link rel="stylesheet" href="/admin/static/style.css">
  {% block extra_head %}{% endblock %}
</head>
<body>
{% if current_user %}
<div class="app-container">
  <aside class="sidebar">
    <div class="sidebar-header">ClassNote 管理</div>
    <nav class="sidebar-nav">
      <a href="/admin/dashboard" class="{{ 'active' if request.url.path == '/admin/dashboard' else '' }}">📊 仪表盘</a>
      <a href="/admin/config" class="{{ 'active' if request.url.path == '/admin/config' else '' }}">⚙️ 系统配置</a>
      <a href="/admin/logs" class="{{ 'active' if request.url.path == '/admin/logs' else '' }}">📋 运行日志</a>
    </nav>
  </aside>
  <main class="main-content">
    <div class="topbar">
      <h1>{% block page_title %}{% endblock %}</h1>
      <div class="topbar-user">
        <span>{{ current_user.display_name }}</span>
        <a href="/admin/logout">退出</a>
      </div>
    </div>
    {% if error_message %}
    <div class="alert alert-error">{{ error_message }}</div>
    {% endif %}
    {% if success_message %}
    <div class="alert alert-success">{{ success_message }}</div>
    {% endif %}
    {% block content %}{% endblock %}
  </main>
</div>
{% else %}
  {% block unauthenticated %}{% endblock %}
{% endif %}
<script src="/admin/static/admin.js"></script>
{% block extra_scripts %}{% endblock %}
</body>
</html>
```

- [ ] **Step 4: Commit**

```bash
git add server/app/admin_pages/static/ server/app/admin_pages/templates/base.html
git commit -m "feat: add admin base template, CSS, and JS"
```

---

### Task 3: Authentication flow

**Files:**
- Modify: `server/app/admin_pages/__init__.py` — add `get_admin_from_cookie` dependency + login/logout routes
- Create: `server/app/admin_pages/templates/login.html`
- Dependencies: `app.dependencies.get_current_user`, `app.dependencies.require_admin`
- Dependencies: `app.services.auth_service.create_access_token`, `app.services.auth_service.verify_password`
- Dependencies: `app.models.user.User`

**Interfaces:**
- Consumes: `app.dependencies.get_current_user` (existing) — validate JWT from cookie
- Consumes: `app.dependencies.require_admin` (existing) — check admin role
- Consumes: `app.services.auth_service` (existing) — verify password, create token
- Produces: `get_admin_from_cookie(request, db)` — dependency that reads JWT from `admin_token` cookie, validates, returns `User`
- Produces: `GET /admin/login` — render login form
- Produces: `POST /admin/login` — authenticate, set cookie, redirect to dashboard
- Produces: `GET /admin/logout` — clear cookie, redirect to login

- [ ] **Step 1: Write `admin_pages/__init__.py` with auth routes**

Replace the stub in `server/app/admin_pages/__init__.py`:

```python
from fastapi import APIRouter, Depends, HTTPException, Request, Form
from fastapi.responses import RedirectResponse, HTMLResponse
from sqlalchemy.orm import Session
from starlette.templating import Jinja2Templates
from pathlib import Path

from app.database import get_db
from app.dependencies import get_current_user, require_admin
from app.models.user import User
from app.services.auth_service import verify_password, create_access_token
from app.config import settings

templates = Jinja2Templates(directory=Path(__file__).parent / "templates")
router = APIRouter(prefix="/admin", tags=["admin_pages"])


def get_admin_from_cookie(request: Request, db: Session = Depends(get_db)) -> User:
    """Extract JWT from admin_token cookie and validate as admin."""
    token = request.cookies.get("admin_token")
    if not token:
        raise HTTPException(status_code=303, headers={"Location": "/admin/login"})
    from fastapi.security.http import HTTPAuthorizationCredentials
    try:
        user = get_current_user(
            HTTPAuthorizationCredentials(scheme="Bearer", credentials=token),
            db,
        )
        return require_admin(user)
    except HTTPException:
        raise HTTPException(status_code=303, headers={"Location": "/admin/login"})


@router.get("/login", response_class=HTMLResponse)
def login_page(request: Request):
    return templates.TemplateResponse("login.html", {"request": request, "current_user": None})


@router.post("/login")
def login_action(
    request: Request,
    username: str = Form(...),
    password: str = Form(...),
    db: Session = Depends(get_db),
):
    user = db.query(User).filter(User.username == username).first()
    if not user or not verify_password(password, user.password_hash):
        return templates.TemplateResponse(
            "login.html",
            {"request": request, "current_user": None, "error_message": "用户名或密码错误"},
            status_code=401,
        )
    if user.role != "admin":
        return templates.TemplateResponse(
            "login.html",
            {"request": request, "current_user": None, "error_message": "该用户无管理员权限"},
            status_code=403,
        )
    token = create_access_token(user.id)
    response = RedirectResponse(url="/admin/dashboard", status_code=302)
    response.set_cookie(
        key="admin_token",
        value=token,
        httponly=True,
        samesite="lax",
        path="/admin",
        max_age=settings.ACCESS_TOKEN_EXPIRE_MINUTES * 60,
    )
    return response


@router.get("/logout")
def logout():
    response = RedirectResponse(url="/admin/login", status_code=302)
    response.delete_cookie(key="admin_token", path="/admin")
    return response
```

- [ ] **Step 2: Write `login.html`**

Write `server/app/admin_pages/templates/login.html`:

```html
{% extends "base.html" %}
{% block title %}管理员登录 - ClassNote{% endblock %}
{% block unauthenticated %}
<div class="login-page">
  <div class="login-card">
    <h1>ClassNote 管理</h1>
    <p>请输入管理员账号和密码</p>
    {% if error_message %}
    <div class="alert alert-error">{{ error_message }}</div>
    {% endif %}
    <form method="post" action="/admin/login">
      <div class="form-group">
        <label for="username">用户名</label>
        <input type="text" id="username" name="username" required autofocus>
      </div>
      <div class="form-group">
        <label for="password">密码</label>
        <input type="password" id="password" name="password" required>
      </div>
      <button type="submit" class="btn btn-primary">登录</button>
    </form>
  </div>
</div>
{% endblock %}
```

- [ ] **Step 3: Verify the auth flow works (manual test)**

Start the server: `cd server && python -c "
from app.admin_pages import router
from app.main import app
# List admin routes
for r in app.routes:
    if hasattr(r, 'path') and '/admin' in r.path:
        print(r.path, r.methods)
"`

Expected: Shows `/admin/login` (GET, POST), `/admin/logout` (GET)

- [ ] **Step 4: Commit**

```bash
git add server/app/admin_pages/__init__.py server/app/admin_pages/templates/login.html
git commit -m "feat: add admin login/logout with JWT cookie auth"
```

---

### Task 4: Dashboard page

**Files:**
- Modify: `server/app/admin_pages/__init__.py` — add dashboard route
- Create: `server/app/admin_pages/templates/dashboard.html`
- Dependencies: `app.models.session.Session`, `app.models.task_log.TaskLog`, `app.models.user.User`
- Dependencies: `app.services.file_service.get_full_path`

**Interfaces:**
- Consumes: `get_admin_from_cookie` (from Task 3) — auth guard
- Produces: `GET /admin/dashboard` — renders dashboard.html with stats context

- [ ] **Step 1: Add the dashboard import and helper at top of `admin_pages/__init__.py`**

Add imports after existing ones (before the `templates` line):

```python
import os
from datetime import datetime, timezone
from sqlalchemy import func
from app.models.session import Session
from app.models.task_log import TaskLog
from app.models.user import User
from app.services.file_service import get_full_path


def _get_storage_usage() -> str:
    """Calculate total storage used by screenshots and audio."""
    total_bytes = 0
    for subdir in ("screenshots", "audio"):
        path = os.path.join(settings.DATA_DIR, subdir)
        if os.path.isdir(path):
            result = os.popen(f"du -sb {path} 2>/dev/null").read()
            if result:
                try:
                    total_bytes += int(result.split()[0])
                except (IndexError, ValueError):
                    pass
    if total_bytes < 1024:
        return f"{total_bytes} B"
    elif total_bytes < 1024 ** 2:
        return f"{total_bytes / 1024:.1f} KB"
    elif total_bytes < 1024 ** 3:
        return f"{total_bytes / 1024 ** 2:.1f} MB"
    else:
        return f"{total_bytes / 1024 ** 3:.2f} GB"
```

- [ ] **Step 2: Add dashboard route**

Add after the logout route in `admin_pages/__init__.py`:

```python
@router.get("/dashboard", response_class=HTMLResponse)
def dashboard(request: Request, admin: User = Depends(get_admin_from_cookie), db: Session = Depends(get_db)):
    today_start = datetime.now(timezone.utc).replace(hour=0, minute=0, second=0, microsecond=0)
    today_sessions = db.query(func.count(Session.id)).filter(Session.created_at >= today_start).scalar() or 0
    queued_tasks = db.query(func.count(TaskLog.id)).filter(TaskLog.status == "pending").scalar() or 0
    running_tasks = db.query(func.count(TaskLog.id)).filter(TaskLog.status == "running").scalar() or 0
    total_users = db.query(func.count(User.id)).scalar() or 0
    storage_usage = _get_storage_usage()
    recent_tasks = (
        db.query(TaskLog)
        .order_by(TaskLog.created_at.desc())
        .limit(10)
        .all()
    )
    return templates.TemplateResponse("dashboard.html", {
        "request": request,
        "current_user": admin,
        "today_sessions": today_sessions,
        "queued_tasks": queued_tasks,
        "running_tasks": running_tasks,
        "total_users": total_users,
        "storage_usage": storage_usage,
        "recent_tasks": recent_tasks,
    })
```

- [ ] **Step 3: Write `dashboard.html`**

Write `server/app/admin_pages/templates/dashboard.html`:

```html
{% extends "base.html" %}
{% block title %}仪表盘 - ClassNote 管理{% endblock %}
{% block page_title %}📊 系统概览{% endblock %}
{% block content %}
<div class="card-grid">
  <div class="stat-card">
    <div class="value">{{ today_sessions }}</div>
    <div class="label">今日课程</div>
  </div>
  <div class="stat-card">
    <div class="value">{{ queued_tasks }}</div>
    <div class="label">待处理任务</div>
  </div>
  <div class="stat-card">
    <div class="value">{{ running_tasks }}</div>
    <div class="label">运行中任务</div>
  </div>
  <div class="stat-card">
    <div class="value">{{ total_users }}</div>
    <div class="label">总用户数</div>
  </div>
</div>

<div class="card">
  <div style="font-size:14px; color:var(--text-secondary); margin-bottom:4px;">总存储用量</div>
  <div style="font-size:24px; font-weight:600;">{{ storage_usage }}</div>
</div>

<div class="card">
  <h3 style="margin-bottom:12px;">最近任务</h3>
  <table>
    <thead>
      <tr>
        <th>ID</th>
        <th>步骤</th>
        <th>状态</th>
        <th>进度</th>
        <th>错误信息</th>
      </tr>
    </thead>
    <tbody>
      {% for task in recent_tasks %}
      <tr>
        <td style="font-family:monospace; font-size:12px;">{{ task.id|string|truncate(8, True, '…') }}</td>
        <td>{{ task.step }}</td>
        <td><span class="badge badge-{{ task.status }}">{{ task.status }}</span></td>
        <td>{{ "%.0f"|format(task.progress * 100) if task.progress else 0 }}%</td>
        <td style="color:var(--danger); max-width:200px; overflow:hidden; text-overflow:ellipsis;">{{ task.error_msg or '-' }}</td>
      </tr>
      {% else %}
      <tr><td colspan="5" style="text-align:center; color:var(--text-secondary);">暂无任务记录</td></tr>
      {% endfor %}
    </tbody>
  </table>
  <div style="margin-top:12px;">
    <a href="/admin/dashboard" class="btn btn-secondary btn-sm">刷新</a>
  </div>
</div>
{% endblock %}
```

- [ ] **Step 4: Commit**

```bash
git add server/app/admin_pages/__init__.py server/app/admin_pages/templates/dashboard.html
git commit -m "feat: add admin dashboard with system stats and recent tasks"
```

---

### Task 5: Config API endpoints

**Files:**
- Create: `server/app/api/admin_config.py`
- Modify: `server/app/api/router.py` — include new router
- Dependencies: `app.config.settings` — read/write pydantic Settings
- Dependencies: `app.dependencies.require_admin`

**Interfaces:**
- Consumes: `app.config.settings` — current runtime settings
- Consumes: `app.dependencies.require_admin` — auth guard
- Produces: `GET /api/v1/admin/config` — returns `dict` of current config (sensitive fields masked)
- Produces: `POST /api/v1/admin/config` — accepts `dict` of field updates, writes `.env`, re-reads settings, returns updated config

- [ ] **Step 1: Write `admin_config.py`**

Write `server/app/api/admin_config.py`:

```python
import os
from pathlib import Path
from fastapi import APIRouter, Depends, HTTPException
from pydantic_settings import BaseSettings
from app.dependencies import require_admin
from app.models.user import User
from app.config import settings

router = APIRouter(prefix="/api/v1/admin", tags=["admin_config"])

SENSITIVE_KEYS = {"LLM_API_KEY", "LLM_FALLBACK_API_KEY", "SECRET_KEY"}


def _mask_sensitive(key: str, value: str) -> str:
    if key in SENSITIVE_KEYS and value:
        return value[:4] + "****"
    return value


def _find_env_file() -> Path:
    """Locate the .env file relative to the project root or settings."""
    # Check common locations
    candidates = [
        Path(settings.DATA_DIR).parent / ".env",
        Path.cwd() / ".env",
        Path(__file__).resolve().parent.parent.parent / ".env",
    ]
    for p in candidates:
        if p.exists():
            return p
    # Default to project root
    return Path(__file__).resolve().parent.parent.parent / ".env"


def _read_env_file(path: Path) -> dict:
    """Read .env file into a dict (simple parser, no interpolation)."""
    env = {}
    if path.exists():
        for line in path.read_text().splitlines():
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, _, val = line.partition("=")
            env[key.strip()] = val.strip().strip('"').strip("'")
    return env


def _write_env_file(path: Path, env: dict):
    """Write env dict back to file, preserving comments and blank lines."""
    lines = []
    if path.exists():
        existing_lines = path.read_text().splitlines(keepends=True)
    else:
        existing_lines = []

    # Track which keys we've written
    written = set()

    for line in existing_lines:
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            lines.append(line)
            continue
        if "=" in stripped and not stripped.startswith("="):
            key = stripped.split("=", 1)[0].strip()
            if key in env:
                lines.append(f"{key}={env[key]}\n")
                written.add(key)
                continue
        lines.append(line)

    # Append any new keys not in existing file
    for key, val in env.items():
        if key not in written:
            lines.append(f"{key}={val}\n")

    path.write_text("".join(lines))


@router.get("/config")
def get_config(admin: User = Depends(require_admin)):
    return {
        "config": {k: _mask_sensitive(k, v) for k, v in settings.model_dump().items()},
        "restart_required": ["WHISPER_MODEL_SIZE", "WHISPER_DEVICE", "WHISPER_COMPUTE_TYPE",
                            "CELERY_BROKER_URL", "CELERY_RESULT_BACKEND"],
    }


@router.post("/config")
def update_config(data: dict, admin: User = Depends(require_admin)):
    # Validate: only allow known Settings fields
    known_fields = set(settings.model_dump().keys())
    invalid = [k for k in data if k not in known_fields]
    if invalid:
        raise HTTPException(status_code=422, detail=f"Unknown fields: {', '.join(invalid)}")

    env_path = _find_env_file()
    env = _read_env_file(env_path)

    for key, value in data.items():
        if key in SENSITIVE_KEYS and value and value.startswith("****"):
            continue  # masked value unchanged — skip
        if value is None:
            continue
        # Convert non-string types
        if isinstance(value, bool):
            env[key] = "true" if value else "false"
        elif isinstance(value, (int, float)):
            env[key] = str(value)
        else:
            env[key] = str(value)

    try:
        _write_env_file(env_path, env)
    except OSError as e:
        raise HTTPException(status_code=500, detail=f"Failed to write .env: {e}")

    # Reload settings from file
    new_settings = BaseSettings(_env_file=str(env_path))
    settings.__dict__.update(new_settings.__dict__)

    return {
        "config": {k: _mask_sensitive(k, v) for k, v in settings.model_dump().items()},
        "restart_required": ["WHISPER_MODEL_SIZE", "WHISPER_DEVICE", "WHISPER_COMPUTE_TYPE",
                            "CELERY_BROKER_URL", "CELERY_RESULT_BACKEND"],
    }


@router.get("/logs")
def get_logs(
    level: str = "",
    source: str = "",
    page: int = 1,
    page_size: int = 50,
    admin: User = Depends(require_admin),
):
    """Read log files with filtering and pagination."""
    log_dir = Path(settings.DATA_DIR).parent / "logs"
    if not log_dir.exists():
        return {"total": 0, "page": page, "page_size": page_size, "entries": []}

    # Determine which log files to read based on source filter
    source_map = {
        "": ["api.log", "celery.log"],
        "api": ["api.log"],
        "celery": ["celery.log"],
        "stt": ["celery.log"],
        "ocr": ["celery.log"],
        "llm": ["celery.log"],
    }
    filenames = source_map.get(source, ["api.log", "celery.log"])

    all_lines = []
    for fname in filenames:
        fpath = log_dir / fname
        if fpath.exists():
            lines = fpath.read_text(encoding="utf-8", errors="replace").splitlines()
            for line in lines:
                if level:
                    if level.upper() not in line.upper():
                        continue
                if source in ("stt", "ocr", "llm"):
                    if source.upper() not in line.upper():
                        continue
                all_lines.append(line)

    total = len(all_lines)
    start = (page - 1) * page_size
    end = start + page_size
    entries = all_lines[start:end]

    return {
        "total": total,
        "page": page,
        "page_size": page_size,
        "total_pages": max(1, (total + page_size - 1) // page_size),
        "entries": entries,
    }
```

- [ ] **Step 2: Register the new router**

Modify `server/app/api/router.py` to include the new router:

```python
from app.api.admin_config import router as admin_config_router

api_router.include_router(admin_config_router)
```

Full file after changes:

```python
from fastapi import APIRouter
from app.api.auth import router as auth_router
from app.api.sessions import router as sessions_router
from app.api.screenshots import router as screenshots_router
from app.api.audio import router as audio_router
from app.api.notes import router as notes_router
from app.api.admin import router as admin_router
from app.api.admin_config import router as admin_config_router

api_router = APIRouter()
api_router.include_router(auth_router)
api_router.include_router(sessions_router)
api_router.include_router(screenshots_router)
api_router.include_router(audio_router)
api_router.include_router(notes_router)
api_router.include_router(admin_router)
api_router.include_router(admin_config_router)
```

- [ ] **Step 3: Quick test — API loads**

Run: `cd server && python -c "
from app.main import app
for r in app.routes:
    if hasattr(r, 'path') and '/admin' in r.path:
        print(r.path, getattr(r, 'methods', ''))
"`

Expected: Output includes `/api/v1/admin/config` (GET, POST) and `/api/v1/admin/logs` (GET)

- [ ] **Step 4: Commit**

```bash
git add server/app/api/admin_config.py server/app/api/router.py
git commit -m "feat: add admin config and log API endpoints"
```

---

### Task 6: Config page (frontend)

**Files:**
- Modify: `server/app/admin_pages/__init__.py` — add `/admin/config` route
- Create: `server/app/admin_pages/templates/config.html`

**Interfaces:**
- Consumes: `app.config.settings` — read current config values
- Consumes: `get_admin_from_cookie` — auth guard
- Produces: `GET /admin/config` — renders config.html with current settings grouped
- Produces: `POST /admin/config` — writes config via `admin_config.update_config`, then re-renders

- [ ] **Step 1: Add config route to `admin_pages/__init__.py`**

Add imports at top:

```python
from app.api.admin_config import _mask_sensitive, SENSITIVE_KEYS
from app.config import settings as app_settings
```

Add route after dashboard:

```python
@router.get("/config", response_class=HTMLResponse)
def config_page(request: Request, admin: User = Depends(get_admin_from_cookie)):
    config_data = {k: _mask_sensitive(k, v) for k, v in app_settings.model_dump().items()}
    restart_required = {"WHISPER_MODEL_SIZE", "WHISPER_DEVICE", "WHISPER_COMPUTE_TYPE",
                        "CELERY_BROKER_URL", "CELERY_RESULT_BACKEND"}
    return templates.TemplateResponse("config.html", {
        "request": request,
        "current_user": admin,
        "config": config_data,
        "restart_required": restart_required,
    })


@router.post("/config", response_class=HTMLResponse)
def config_save(
    request: Request,
    admin: User = Depends(get_admin_from_cookie),
    db: Session = Depends(get_db),
):
    # Read form data and update via admin_config API
    from app.api.admin_config import update_config as api_update_config
    form_data = {}
    config_keys = list(app_settings.model_dump().keys())
    for key in config_keys:
        val = request.form.get(key)
        if val is not None:
            # Handle booleans
            if val.lower() in ("true", "false"):
                form_data[key] = val.lower() == "true"
            else:
                form_data[key] = val
    
    try:
        api_update_config(form_data, admin)
        success = "配置已保存"
    except HTTPException as e:
        success = None
        error = f"保存失败: {e.detail}"
    
    config_data = {k: _mask_sensitive(k, v) for k, v in app_settings.model_dump().items()}
    restart_required = {"WHISPER_MODEL_SIZE", "WHISPER_DEVICE", "WHISPER_COMPUTE_TYPE",
                        "CELERY_BROKER_URL", "CELERY_RESULT_BACKEND"}
    return templates.TemplateResponse("config.html", {
        "request": request,
        "current_user": admin,
        "config": config_data,
        "restart_required": restart_required,
        "success_message": success if success else None,
        "error_message": error if not success else None,
    })
```

Wait — the POST handler needs access to `request.form()` which is async. Since FastAPI routes are sync by default, I need to handle this properly. Let me use a different approach — use the form data directly or use Starlette's Request. Actually, FastAPI's Form() works with sync too when using the Form class. But for dynamic form fields, I need to iterate over them.

Actually, looking at FastAPI docs, reading form data as `request.form()` returns a `FormData` that's an async iterable. So I need to make the handler async.

Let me fix the POST handler:

```python
@router.post("/config", response_class=HTMLResponse)
async def config_save(
    request: Request,
    admin: User = Depends(get_admin_from_cookie),
):
    from app.api.admin_config import update_config as api_update_config
    import json as _json
    
    form = await request.form()
    form_data = {}
    config_keys = set(app_settings.model_dump().keys())
    for key, value in form.multi_items():
        if key in config_keys:
            val = value.strip()
            if val.lower() in ("true", "false"):
                form_data[key] = val.lower() == "true"
            else:
                form_data[key] = val
    
    try:
        api_update_config(form_data, admin)
        success_msg = "配置已保存"
        error_msg = None
    except HTTPException as e:
        success_msg = None
        error_msg = f"保存失败: {e.detail}"
    
    config_data = {k: _mask_sensitive(k, v) for k, v in app_settings.model_dump().items()}
    restart_required = {"WHISPER_MODEL_SIZE", "WHISPER_DEVICE", "WHISPER_COMPUTE_TYPE",
                        "CELERY_BROKER_URL", "CELERY_RESULT_BACKEND"}
    return templates.TemplateResponse("config.html", {
        "request": request,
        "current_user": admin,
        "config": config_data,
        "restart_required": restart_required,
        "success_message": success_msg,
        "error_message": error_msg,
    })
```

Hmm, actually this gets complicated because I need to check the `error` variable scope. Let me simplify:

Actually let me just use a simpler approach — have the POST handler construct the data dict from form fields and call the API function directly. Let me think about this more carefully.

Actually, the simplest approach is to have the POST handler directly write the .env file without calling the API endpoint function. That avoids the complexity of calling function-to-function. Let me refactor:

Actually, calling `api_update_config` as a regular function (not as a FastAPI route) should work fine — it's just a Python function. The `Depends(require_admin)` dependency won't be injected when called as a regular function, but the caller in `admin_pages/__init__.py` already checks auth via `get_admin_from_cookie`. So we need to pass the admin user or restructure.

Simpler: just have the POST handler do the work directly without calling the API function. This is more straightforward for a server-rendered app.

Actually, even simpler — let me just make the config page work by reading the submitted form, doing the env file update inline, and re-rendering. Let me not overthink this.

Let me restructure the plan to keep it clean. I'll have the POST handler do the work inline.<｜end▁of▁thinking｜>

<｜｜DSML｜｜tool_calls>
<｜｜DSML｜｜invoke name="Write">
<｜｜DSML｜｜parameter name="content" string="true"># Admin Frontend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a lightweight server-rendered admin page (dashboard, config, logs) to the ClassNote FastAPI backend.

**Architecture:** New `admin_pages/` module with Jinja2 templates, mounted as a sub-router under `/admin`. Reuses existing JWT auth via HTTP-only cookie. Two new API endpoints (`/api/v1/admin/config` and `/api/v1/admin/logs`) in a new `admin_config.py` router.

**Tech Stack:** Python FastAPI, Jinja2, SQLAlchemy, vanilla HTML/CSS/JS (no build step)

## Global Constraints

- No frontend build toolchain: CSS and JS are plain files served via StaticFiles
- Jinja2 templates inherit from `base.html` layout
- All `/admin/*` routes protected by JWT cookie auth; unauthenticated requests 302 to `/admin/login`
- `.env` file is at `server/.env`, loaded via pydantic-settings
- Log files located at `DATA_DIR/../logs/*.log` (where DATA_DIR is from settings)

---

## File Structure

```
server/
├── app/
│   ├── admin_pages/              # NEW package
│   │   ├── __init__.py           # APIRouter + all page routes + cookie auth dependency
│   │   ├── templates/
│   │   │   ├── base.html         # Layout skeleton (nav + sidebar + content)
│   │   │   ├── login.html        # Login form
│   │   │   ├── dashboard.html    # Stats overview
│   │   │   ├── config.html       # Config form
│   │   │   └── logs.html         # Log viewer
│   │   └── static/
│   │       ├── style.css         # Admin UI styling
│   │       └── admin.js          # Minor JS (show API key, confirm dialogs)
│   ├── api/
│   │   ├── admin.py              # EXISTING — stats/tasks/users endpoints
│   │   └── admin_config.py       # NEW — config + log API endpoints
│   ├── main.py                   # MODIFY — add Jinja2Templates + StaticFiles + mount
│   └── ...                       # existing files unchanged
├── requirements.txt              # MODIFY — add jinja2
└── .env                          # EXISTING — written by config page
```

---

### Task 1: Project scaffolding

**Files:**
- Create: `server/app/admin_pages/__init__.py`
- Create: `server/app/admin_pages/templates/.gitkeep`
- Create: `server/app/admin_pages/static/.gitkeep`
- Modify: `server/app/main.py`
- Modify: `server/requirements.txt`

**Interfaces:**
- Consumes: `app.config.settings` (existing)
- Consumes: `app.api.router` (existing — the main API router to mirror mount)
- Produces: `admin_pages.router` — `APIRouter(prefix="/admin")` mounted at app level
- Produces: Jinja2Templates instance at `admin_pages/templates/`
- Produces: StaticFiles mount for `/admin/static/`

- [ ] **Step 1: Create the admin_pages package and directories**

```bash
mkdir -p server/app/admin_pages/templates
mkdir -p server/app/admin_pages/static
touch server/app/admin_pages/__init__.py
touch server/app/admin_pages/templates/.gitkeep
touch server/app/admin_pages/static/.gitkeep
```

- [ ] **Step 2: Add jinja2 to requirements.txt**

```diff
+ jinja2>=3.1
```

- [ ] **Step 3: Write the admin_pages router stub**

Write `server/app/admin_pages/__init__.py`:

```python
from fastapi import APIRouter
from starlette.templating import Jinja2Templates
from pathlib import Path

templates = Jinja2Templates(directory=Path(__file__).parent / "templates")
router = APIRouter(prefix="/admin", tags=["admin_pages"])
```

- [ ] **Step 4: Wire up in main.py**

Modify `server/app/main.py` to mount Jinja2Templates, StaticFiles, and the admin router:

```python
from fastapi import FastAPI
from fastapi.staticfiles import StaticFiles
from pathlib import Path
from app.config import settings
from app.api.router import api_router
from app.admin_pages import router as admin_pages_router

app = FastAPI(title=settings.APP_NAME, version="1.0.0")
app.include_router(api_router)
app.include_router(admin_pages_router)

BASE_DIR = Path(__file__).resolve().parent
app.mount("/admin/static", StaticFiles(directory=str(BASE_DIR / "admin_pages" / "static")), name="admin_static")

@app.get("/health")
def health():
    return {"status": "ok"}
```

- [ ] **Step 5: Quick sanity check — app starts**

Run: `cd server && python -c "from app.main import app; print('OK:', app.routes[-1].path)"`
Expected: `OK: /admin/static`

- [ ] **Step 6: Commit**

```bash
git add server/app/admin_pages/ server/app/main.py server/requirements.txt
git commit -m "feat: add admin_pages scaffolding with Jinja2 and StaticFiles"
```

---

### Task 2: Base template, CSS, and JS

**Files:**
- Create: `server/app/admin_pages/templates/base.html`
- Create: `server/app/admin_pages/static/style.css`
- Create: `server/app/admin_pages/static/admin.js`

**Interfaces:**
- Consumes: `request` (Starlette Request) with `request.user` (set by auth middleware)
- Consumes: `{{ current_user }}` — `User` model instance, passed from route handlers
- Produces: Jinja2 block names: `title`, `page_title`, `content`, `extra_head`, `extra_scripts`
- Produces: Jinja2 variables used in base: `current_user`, `error_message`, `success_message`
- Produces: `{{ request.url.path }}` used for active nav highlighting

- [ ] **Step 1: Write `style.css`**

Write `server/app/admin_pages/static/style.css`:

```css
:root {
  --sidebar-width: 220px;
  --nav-height: 48px;
  --bg-body: #f5f6fa;
  --bg-sidebar: #1e2a3a;
  --bg-card: #ffffff;
  --text-primary: #1a1a2e;
  --text-secondary: #6c757d;
  --text-sidebar: #c8cdd5;
  --accent: #4a6cf7;
  --accent-hover: #3b5de7;
  --border: #e2e6ea;
  --success: #28a745;
  --warning: #ffc107;
  --danger: #dc3545;
}

* { margin: 0; padding: 0; box-sizing: border-box; }
body {
  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
  background: var(--bg-body);
  color: var(--text-primary);
  min-height: 100vh;
}

/* Layout */
.app-container { display: flex; min-height: 100vh; }
.sidebar {
  width: var(--sidebar-width);
  background: var(--bg-sidebar);
  color: var(--text-sidebar);
  position: fixed; top: 0; left: 0; bottom: 0;
  z-index: 100;
}
.sidebar-header {
  padding: 16px 20px;
  font-size: 18px; font-weight: 700;
  color: #fff;
  border-bottom: 1px solid rgba(255,255,255,0.08);
  height: var(--nav-height);
  display: flex; align-items: center;
}
.sidebar-nav { padding: 12px 0; }
.sidebar-nav a {
  display: flex; align-items: center; gap: 10px;
  padding: 10px 20px; color: var(--text-sidebar);
  text-decoration: none; font-size: 14px;
  transition: background 0.15s;
}
.sidebar-nav a:hover { background: rgba(255,255,255,0.06); color: #fff; }
.sidebar-nav a.active {
  background: rgba(74,108,247,0.2); color: #fff;
  border-right: 3px solid var(--accent);
}
.main-content { margin-left: var(--sidebar-width); flex: 1; padding: 24px 32px; }
.topbar {
  display: flex; justify-content: space-between; align-items: center;
  margin-bottom: 24px;
}
.topbar h1 { font-size: 22px; font-weight: 600; }
.topbar-user {
  display: flex; align-items: center; gap: 12px;
  font-size: 14px; color: var(--text-secondary);
}
.topbar-user a { color: var(--danger); text-decoration: none; font-size: 13px; }
.topbar-user a:hover { text-decoration: underline; }

/* Cards */
.card {
  background: var(--bg-card); border-radius: 8px;
  border: 1px solid var(--border); padding: 20px; margin-bottom: 20px;
}
.card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 16px; margin-bottom: 20px;
}
.stat-card {
  background: var(--bg-card); border-radius: 8px;
  border: 1px solid var(--border); padding: 20px; text-align: center;
}
.stat-card .value { font-size: 32px; font-weight: 700; color: var(--accent); }
.stat-card .label { font-size: 13px; color: var(--text-secondary); margin-top: 4px; }

/* Tables */
table { width: 100%; border-collapse: collapse; font-size: 14px; }
th, td { padding: 10px 12px; text-align: left; border-bottom: 1px solid var(--border); }
th { font-weight: 600; color: var(--text-secondary); font-size: 12px; text-transform: uppercase; letter-spacing: 0.5px; }

/* Status badges */
.badge { display: inline-block; padding: 2px 8px; border-radius: 10px; font-size: 12px; font-weight: 500; }
.badge-done { background: #d4edda; color: #155724; }
.badge-running { background: #cce5ff; color: #004085; }
.badge-pending { background: #fff3cd; color: #856404; }
.badge-failed { background: #f8d7da; color: #721c24; }

/* Forms */
.form-group { margin-bottom: 16px; }
.form-group label { display: block; font-size: 13px; font-weight: 600; color: var(--text-secondary); margin-bottom: 4px; }
.form-group input, .form-group select {
  width: 100%; padding: 8px 12px;
  border: 1px solid var(--border); border-radius: 6px;
  font-size: 14px; font-family: inherit; background: #fff;
}
.form-group input:focus, .form-group select:focus {
  outline: none; border-color: var(--accent);
  box-shadow: 0 0 0 2px rgba(74,108,247,0.15);
}
.form-row { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
.form-section { margin-bottom: 24px; }
.form-section h3 { font-size: 15px; margin-bottom: 12px; padding-bottom: 8px; border-bottom: 1px solid var(--border); }
.form-section-tip { font-size: 12px; color: var(--warning); margin-top: -8px; margin-bottom: 12px; }

/* Buttons */
.btn {
  display: inline-block; padding: 8px 16px; border-radius: 6px;
  font-size: 14px; font-weight: 500; border: none; cursor: pointer;
  text-decoration: none; transition: background 0.15s;
}
.btn-primary { background: var(--accent); color: #fff; }
.btn-primary:hover { background: var(--accent-hover); }
.btn-secondary { background: #e2e6ea; color: var(--text-primary); }
.btn-secondary:hover { background: #d3d8de; }
.btn-danger { background: var(--danger); color: #fff; }
.btn-sm { padding: 4px 10px; font-size: 12px; }
.btn-group { display: flex; gap: 8px; justify-content: flex-end; margin-top: 20px; }

/* Alerts */
.alert { padding: 12px 16px; border-radius: 6px; font-size: 14px; margin-bottom: 16px; }
.alert-error { background: #f8d7da; color: #721c24; border: 1px solid #f5c6cb; }
.alert-success { background: #d4edda; color: #155724; border: 1px solid #c3e6cb; }

/* Log viewer */
.log-entry { padding: 6px 0; font-family: "SF Mono", "Fira Code", "Consolas", monospace; font-size: 13px; border-bottom: 1px solid var(--border); }
.log-entry .ts { color: var(--text-secondary); }
.log-entry .level { font-weight: 600; }
.log-entry .level-INFO { color: var(--accent); }
.log-entry .level-WARN { color: var(--warning); }
.log-entry .level-ERROR { color: var(--danger); }
.log-entry .source { color: var(--text-secondary); }
.log-entry .msg { color: var(--text-primary); }

/* Filters bar */
.filters { display: flex; gap: 12px; align-items: center; margin-bottom: 16px; flex-wrap: wrap; }
.filters select { padding: 6px 10px; border: 1px solid var(--border); border-radius: 6px; font-size: 13px; }

/* Pagination */
.pagination { display: flex; gap: 4px; justify-content: center; margin-top: 16px; }
.pagination a {
  padding: 6px 12px; border: 1px solid var(--border); border-radius: 4px;
  text-decoration: none; color: var(--text-primary); font-size: 13px;
}
.pagination a.active { background: var(--accent); color: #fff; border-color: var(--accent); }
.pagination a:hover:not(.active) { background: #f0f0f0; }
.pagination .ellipsis { padding: 6px 8px; color: var(--text-secondary); }

/* Login page (centered, no sidebar) */
.login-page {
  display: flex; justify-content: center; align-items: center;
  min-height: 100vh; background: var(--bg-sidebar);
}
.login-card {
  background: #fff; border-radius: 12px; padding: 40px;
  width: 380px; box-shadow: 0 4px 24px rgba(0,0,0,0.15);
}
.login-card h1 { font-size: 24px; margin-bottom: 8px; text-align: center; }
.login-card p { color: var(--text-secondary); font-size: 14px; text-align: center; margin-bottom: 24px; }
.login-card .form-group { margin-bottom: 20px; }
.login-card .btn { width: 100%; padding: 10px; font-size: 15px; }

/* No data placeholder */
.empty-state { text-align: center; padding: 40px; color: var(--text-secondary); font-size: 14px; }
```

- [ ] **Step 2: Write `admin.js`**

Write `server/app/admin_pages/static/admin.js`:

```javascript
// Toggle password/API key visibility
document.querySelectorAll(".toggle-visibility").forEach(function(btn) {
  btn.addEventListener("click", function() {
    var target = document.querySelector(this.dataset.target);
    if (target) {
      target.type = target.type === "password" ? "text" : "password";
      this.textContent = target.type === "password" ? "显示" : "隐藏";
    }
  });
});

// Confirm destructive actions
document.querySelectorAll("[data-confirm]").forEach(function(el) {
  el.addEventListener("click", function(e) {
    if (!confirm(this.dataset.confirm)) {
      e.preventDefault();
    }
  });
});

// Auto-dismiss alerts after 5 seconds
document.querySelectorAll(".alert").forEach(function(el) {
  setTimeout(function() { el.style.display = "none"; }, 5000);
});
```

- [ ] **Step 3: Write `base.html`**

Write `server/app/admin_pages/templates/base.html`:

```html
<!DOCTYPE html>
<html lang="zh-CN">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>{% block title %}ClassNote 管理{% endblock %}</title>
  <link rel="stylesheet" href="/admin/static/style.css">
  {% block extra_head %}{% endblock %}
</head>
<body>
{% if current_user %}
<div class="app-container">
  <aside class="sidebar">
    <div class="sidebar-header">ClassNote 管理</div>
    <nav class="sidebar-nav">
      <a href="/admin/dashboard" class="{{ 'active' if request.url.path == '/admin/dashboard' else '' }}">📊 仪表盘</a>
      <a href="/admin/config" class="{{ 'active' if request.url.path == '/admin/config' else '' }}">⚙️ 系统配置</a>
      <a href="/admin/logs" class="{{ 'active' if request.url.path == '/admin/logs' else '' }}">📋 运行日志</a>
    </nav>
  </aside>
  <main class="main-content">
    <div class="topbar">
      <h1>{% block page_title %}{% endblock %}</h1>
      <div class="topbar-user">
        <span>{{ current_user.display_name }}</span>
        <a href="/admin/logout">退出</a>
      </div>
    </div>
    {% if error_message %}
    <div class="alert alert-error">{{ error_message }}</div>
    {% endif %}
    {% if success_message %}
    <div class="alert alert-success">{{ success_message }}</div>
    {% endif %}
    {% block content %}{% endblock %}
  </main>
</div>
{% else %}
  {% block unauthenticated %}{% endblock %}
{% endif %}
<script src="/admin/static/admin.js"></script>
{% block extra_scripts %}{% endblock %}
</body>
</html>
```

- [ ] **Step 4: Commit**

```bash
git add server/app/admin_pages/static/ server/app/admin_pages/templates/base.html
git commit -m "feat: add admin base template, CSS, and JS"
```

---

### Task 3: Authentication flow

**Files:**
- Modify: `server/app/admin_pages/__init__.py` — add cookie auth dependency + login/logout routes
- Create: `server/app/admin_pages/templates/login.html`

**Interfaces:**
- Consumes: `app.dependencies.get_current_user` (existing) — validate JWT
- Consumes: `app.dependencies.require_admin` (existing) — check admin role
- Consumes: `app.services.auth_service.verify_password` (existing)
- Consumes: `app.services.auth_service.create_access_token` (existing)
- Produces: `get_admin_from_cookie(request, db)` — dependency reading JWT from `admin_token` cookie
- Produces: `GET /admin/login` — renders login form
- Produces: `POST /admin/login` — authenticate, set HTTP-only cookie, redirect
- Produces: `GET /admin/logout` — clear cookie, redirect to login

- [ ] **Step 1: Write the full `admin_pages/__init__.py` with auth**

Write `server/app/admin_pages/__init__.py`:

```python
from fastapi import APIRouter, Depends, HTTPException, Request, Form
from fastapi.responses import RedirectResponse, HTMLResponse
from fastapi.security.http import HTTPAuthorizationCredentials
from sqlalchemy.orm import Session
from starlette.templating import Jinja2Templates
from pathlib import Path

from app.database import get_db
from app.dependencies import get_current_user, require_admin
from app.models.user import User
from app.services.auth_service import verify_password, create_access_token
from app.config import settings

templates = Jinja2Templates(directory=Path(__file__).parent / "templates")
router = APIRouter(prefix="/admin", tags=["admin_pages"])


def get_admin_from_cookie(request: Request, db: Session = Depends(get_db)) -> User:
    """Extract JWT from admin_token cookie and validate as admin."""
    token = request.cookies.get("admin_token")
    if not token:
        raise HTTPException(status_code=303, headers={"Location": "/admin/login"})
    try:
        user = get_current_user(
            HTTPAuthorizationCredentials(scheme="Bearer", credentials=token),
            db,
        )
        return require_admin(user)
    except HTTPException:
        raise HTTPException(status_code=303, headers={"Location": "/admin/login"})


@router.get("/login", response_class=HTMLResponse)
def login_page(request: Request):
    return templates.TemplateResponse("login.html", {"request": request, "current_user": None})


@router.post("/login")
def login_action(
    request: Request,
    username: str = Form(...),
    password: str = Form(...),
    db: Session = Depends(get_db),
):
    user = db.query(User).filter(User.username == username).first()
    if not user or not verify_password(password, user.password_hash):
        return templates.TemplateResponse(
            "login.html",
            {"request": request, "current_user": None, "error_message": "用户名或密码错误"},
            status_code=401,
        )
    if user.role != "admin":
        return templates.TemplateResponse(
            "login.html",
            {"request": request, "current_user": None, "error_message": "该用户无管理员权限"},
            status_code=403,
        )
    token = create_access_token(user.id)
    response = RedirectResponse(url="/admin/dashboard", status_code=302)
    response.set_cookie(
        key="admin_token",
        value=token,
        httponly=True,
        samesite="lax",
        path="/admin",
        max_age=settings.ACCESS_TOKEN_EXPIRE_MINUTES * 60,
    )
    return response


@router.get("/logout")
def logout():
    response = RedirectResponse(url="/admin/login", status_code=302)
    response.delete_cookie(key="admin_token", path="/admin")
    return response
```

- [ ] **Step 2: Write `login.html`**

Write `server/app/admin_pages/templates/login.html`:

```html
{% extends "base.html" %}
{% block title %}管理员登录 - ClassNote{% endblock %}
{% block unauthenticated %}
<div class="login-page">
  <div class="login-card">
    <h1>ClassNote 管理</h1>
    <p>请输入管理员账号和密码登录</p>
    {% if error_message %}
    <div class="alert alert-error">{{ error_message }}</div>
    {% endif %}
    <form method="post" action="/admin/login">
      <div class="form-group">
        <label for="username">用户名</label>
        <input type="text" id="username" name="username" required autofocus>
      </div>
      <div class="form-group">
        <label for="password">密码</label>
        <input type="password" id="password" name="password" required>
      </div>
      <button type="submit" class="btn btn-primary">登录</button>
    </form>
  </div>
</div>
{% endblock %}
```

- [ ] **Step 3: Verify routes load**

Run: `cd server && python -c "
from app.main import app
for r in app.routes:
    if hasattr(r, 'path') and '/admin' in r.path:
        print(r.path, getattr(r, 'methods', ''))"`

Expected: Output includes `/admin/login` (GET, POST), `/admin/logout` (GET)

- [ ] **Step 4: Commit**

```bash
git add server/app/admin_pages/__init__.py server/app/admin_pages/templates/login.html
git commit -m "feat: add admin login/logout with JWT cookie auth"
```

---

### Task 4: Dashboard page

**Files:**
- Modify: `server/app/admin_pages/__init__.py` — add dashboard route
- Create: `server/app/admin_pages/templates/dashboard.html`

**Interfaces:**
- Consumes: `get_admin_from_cookie` (from Task 3) — auth guard
- Consumes: `app.models.session.Session` — count today's sessions
- Consumes: `app.models.task_log.TaskLog` — count pending/running + recent tasks
- Consumes: `app.models.user.User` — count total users
- Consumes: `app.config.settings.DATA_DIR` — calculate storage usage
- Produces: `GET /admin/dashboard` — renders dashboard.html with stats context

- [ ] **Step 1: Add dashboard route + helper in `admin_pages/__init__.py`**

Add imports at the top of `__init__.py` (after existing imports):

```python
import os
from datetime import datetime, timezone
from sqlalchemy import func
from app.models.session import Session as SessionModel
from app.models.task_log import TaskLog
```

Add helper function before the router:

```python
def _get_storage_usage() -> str:
    """Calculate total storage used by screenshots and audio directories."""
    total_bytes = 0
    for subdir in ("screenshots", "audio"):
        path = os.path.join(settings.DATA_DIR, subdir)
        if os.path.isdir(path):
            try:
                result = os.popen(f"du -sb {path} 2>/dev/null").read()
                if result:
                    total_bytes += int(result.split()[0])
            except (IndexError, ValueError):
                pass
    for unit in ("B", "KB", "MB", "GB"):
        if total_bytes < 1024:
            return f"{total_bytes:.1f} {unit}" if unit != "B" else f"{total_bytes} {unit}"
        total_bytes /= 1024
    return f"{total_bytes:.2f} TB"
```

Add route after the logout route:

```python
@router.get("/dashboard", response_class=HTMLResponse)
def dashboard(request: Request, admin: User = Depends(get_admin_from_cookie), db: Session = Depends(get_db)):
    today_start = datetime.now(timezone.utc).replace(hour=0, minute=0, second=0, microsecond=0)
    today_sessions = db.query(func.count(SessionModel.id)).filter(SessionModel.created_at >= today_start).scalar() or 0
    queued_tasks = db.query(func.count(TaskLog.id)).filter(TaskLog.status == "pending").scalar() or 0
    running_tasks = db.query(func.count(TaskLog.id)).filter(TaskLog.status == "running").scalar() or 0
    total_users = db.query(func.count(User.id)).scalar() or 0
    storage_usage = _get_storage_usage()
    recent_tasks = db.query(TaskLog).order_by(TaskLog.created_at.desc()).limit(10).all()
    return templates.TemplateResponse("dashboard.html", {
        "request": request, "current_user": admin,
        "today_sessions": today_sessions, "queued_tasks": queued_tasks,
        "running_tasks": running_tasks, "total_users": total_users,
        "storage_usage": storage_usage, "recent_tasks": recent_tasks,
    })
```

- [ ] **Step 2: Write `dashboard.html`**

Write `server/app/admin_pages/templates/dashboard.html`:

```html
{% extends "base.html" %}
{% block title %}仪表盘 - ClassNote 管理{% endblock %}
{% block page_title %}📊 系统概览{% endblock %}
{% block content %}
<div class="card-grid">
  <div class="stat-card">
    <div class="value">{{ today_sessions }}</div>
    <div class="label">今日课程</div>
  </div>
  <div class="stat-card">
    <div class="value">{{ queued_tasks }}</div>
    <div class="label">待处理任务</div>
  </div>
  <div class="stat-card">
    <div class="value">{{ running_tasks }}</div>
    <div class="label">运行中任务</div>
  </div>
  <div class="stat-card">
    <div class="value">{{ total_users }}</div>
    <div class="label">总用户数</div>
  </div>
</div>

<div class="card">
  <div style="font-size:14px; color:var(--text-secondary); margin-bottom:4px;">总存储用量</div>
  <div style="font-size:24px; font-weight:600;">{{ storage_usage }}</div>
</div>

<div class="card">
  <h3 style="margin-bottom:12px;">最近任务</h3>
  <table>
    <thead>
      <tr>
        <th>ID</th>
        <th>步骤</th>
        <th>状态</th>
        <th>进度</th>
        <th>错误信息</th>
      </tr>
    </thead>
    <tbody>
      {% for task in recent_tasks %}
      <tr>
        <td style="font-family:monospace; font-size:12px;">{{ task.id|string|truncate(8, True, '…') }}</td>
        <td>{{ task.step }}</td>
        <td><span class="badge badge-{{ task.status }}">{{ task.status }}</span></td>
        <td>{{ "%.0f"|format(task.progress * 100) if task.progress is not none else 0 }}%</td>
        <td style="color:var(--danger); max-width:200px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">{{ task.error_msg or '-' }}</td>
      </tr>
      {% else %}
      <tr><td colspan="5" style="text-align:center; color:var(--text-secondary);">暂无任务记录</td></tr>
      {% endfor %}
    </tbody>
  </table>
  <div style="margin-top:12px;">
    <a href="/admin/dashboard" class="btn btn-secondary btn-sm">刷新</a>
  </div>
</div>
{% endblock %}
```

- [ ] **Step 3: Commit**

```bash
git add server/app/admin_pages/__init__.py server/app/admin_pages/templates/dashboard.html
git commit -m "feat: add admin dashboard with system stats and recent tasks"
```

---

### Task 5: Config API endpoints

**Files:**
- Create: `server/app/api/admin_config.py`
- Modify: `server/app/api/router.py` — include new router

**Interfaces:**
- Consumes: `app.config.settings` — read/write pydantic Settings
- Consumes: `app.dependencies.require_admin` — auth guard
- Produces: `GET /api/v1/admin/config` — `{"config": {...}, "restart_required": [...]}`
- Produces: `POST /api/v1/admin/config` — accepts `{"field": "value", ...}`, writes `.env`, reloads settings, returns updated config
- Produces: `GET /api/v1/admin/logs?level=&source=&page=&page_size=` — returns `{"total": N, "page": N, "page_size": N, "total_pages": N, "entries": [...]}`

- [ ] **Step 1: Write `admin_config.py`**

Write `server/app/api/admin_config.py`:

```python
import os
from pathlib import Path
from fastapi import APIRouter, Depends, HTTPException
from app.dependencies import require_admin
from app.models.user import User
from app.config import settings

router = APIRouter(prefix="/api/v1/admin", tags=["admin_config"])

SENSITIVE_KEYS = {"LLM_API_KEY", "LLM_FALLBACK_API_KEY", "SECRET_KEY"}


def mask_sensitive(key: str, value: str) -> str:
    if key in SENSITIVE_KEYS and value:
        return value[:4] + "****"
    return value


def _find_env_file() -> Path:
    """Locate the .env file relative to the project root."""
    candidates = [
        Path(__file__).resolve().parent.parent.parent / ".env",
        Path.cwd() / ".env",
    ]
    for p in candidates:
        if p.exists():
            return p
    return candidates[0]


def _read_env_file(path: Path) -> dict:
    """Read .env file into a dict, preserving comments."""
    env = {}
    if path.exists():
        for line in path.read_text().splitlines():
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, _, val = line.partition("=")
            env[key.strip()] = val.strip().strip('"').strip("'")
    return env


def _write_env_file(path: Path, env: dict):
    """Write env dict back to file, preserving comments and blank lines."""
    lines = []
    written = set()
    if path.exists():
        existing_lines = path.read_text().splitlines(keepends=True)
    else:
        existing_lines = []

    for line in existing_lines:
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            lines.append(line)
            continue
        if "=" in stripped and not stripped.startswith("="):
            key = stripped.split("=", 1)[0].strip()
            if key in env:
                lines.append(f"{key}={env[key]}\n")
                written.add(key)
                continue
        lines.append(line)

    for key, val in env.items():
        if key not in written:
            lines.append(f"{key}={val}\n")

    path.write_text("".join(lines))


RESTART_REQUIRED = ["WHISPER_MODEL_SIZE", "WHISPER_DEVICE", "WHISPER_COMPUTE_TYPE",
                    "CELERY_BROKER_URL", "CELERY_RESULT_BACKEND"]


@router.get("/config")
def get_config(admin: User = Depends(require_admin)):
    return {
        "config": {k: mask_sensitive(k, v) for k, v in settings.model_dump().items()},
        "restart_required": RESTART_REQUIRED,
    }


@router.post("/config")
def update_config(data: dict, admin: User = Depends(require_admin)):
    known_fields = set(settings.model_dump().keys())
    invalid = [k for k in data if k not in known_fields]
    if invalid:
        raise HTTPException(status_code=422, detail=f"Unknown fields: {', '.join(invalid)}")

    env_path = _find_env_file()
    env = _read_env_file(env_path)

    for key, value in data.items():
        if key in SENSITIVE_KEYS and isinstance(value, str) and value.startswith("****"):
            continue
        if value is None:
            continue
        if isinstance(value, bool):
            env[key] = "true" if value else "false"
        elif isinstance(value, (int, float)):
            env[key] = str(value)
        else:
            env[key] = str(value)

    try:
        _write_env_file(env_path, env)
    except OSError as e:
        raise HTTPException(status_code=500, detail=f"写入 .env 失败: {e}")

    from pydantic_settings import BaseSettings
    try:
        new_settings = BaseSettings(_env_file=str(env_path))
        settings.__dict__.update(new_settings.__dict__)
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"配置重载失败: {e}")

    return {
        "config": {k: mask_sensitive(k, v) for k, v in settings.model_dump().items()},
        "restart_required": RESTART_REQUIRED,
    }


@router.get("/logs")
def get_logs(
    level: str = "",
    source: str = "",
    page: int = 1,
    page_size: int = 50,
    admin: User = Depends(require_admin),
):
    log_dir = Path(settings.DATA_DIR).parent / "logs"
    if not log_dir.exists():
        return {"total": 0, "page": page, "page_size": page_size, "total_pages": 0, "entries": []}

    source_map = {
        "": ["api.log", "celery.log"],
        "api": ["api.log"],
        "celery": ["celery.log"],
        "stt": ["celery.log"],
        "ocr": ["celery.log"],
        "llm": ["celery.log"],
    }
    filenames = source_map.get(source, ["api.log", "celery.log"])

    all_lines = []
    for fname in filenames:
        fpath = log_dir / fname
        if fpath.exists():
            lines = fpath.read_text(encoding="utf-8", errors="replace").splitlines()
            for line in lines:
                if level and level.upper() not in line.upper():
                    continue
                all_lines.append(line)

    total = len(all_lines)
    total_pages = max(1, (total + page_size - 1) // page_size)
    start = (page - 1) * page_size
    entries = all_lines[start:start + page_size]

    return {
        "total": total,
        "page": page,
        "page_size": page_size,
        "total_pages": total_pages,
        "entries": entries,
    }
```

- [ ] **Step 2: Register the new router**

Modify `server/app/api/router.py`:

```python
from fastapi import APIRouter
from app.api.auth import router as auth_router
from app.api.sessions import router as sessions_router
from app.api.screenshots import router as screenshots_router
from app.api.audio import router as audio_router
from app.api.notes import router as notes_router
from app.api.admin import router as admin_router
from app.api.admin_config import router as admin_config_router

api_router = APIRouter()
api_router.include_router(auth_router)
api_router.include_router(sessions_router)
api_router.include_router(screenshots_router)
api_router.include_router(audio_router)
api_router.include_router(notes_router)
api_router.include_router(admin_router)
api_router.include_router(admin_config_router)
```

- [ ] **Step 3: Quick test — routes load**

Run: `cd server && python -c "
from app.main import app
for r in app.routes:
    if hasattr(r, 'path') and '/api/v1/admin' in r.path:
        print(r.path, getattr(r, 'methods', ''))"`

Expected: Output includes `/api/v1/admin/config` (GET, POST) and `/api/v1/admin/logs` (GET)

- [ ] **Step 4: Commit**

```bash
git add server/app/api/admin_config.py server/app/api/router.py
git commit -m "feat: add admin config and log API endpoints"
```

---

### Task 6: Config page (frontend)

**Files:**
- Modify: `server/app/admin_pages/__init__.py` — add `/admin/config` GET + POST routes
- Create: `server/app/admin_pages/templates/config.html`

**Interfaces:**
- Consumes: `app.config.settings` — read current config values
- Consumes: `mask_sensitive` from `app.api.admin_config` — mask API keys
- Consumes: `get_admin_from_cookie` — auth guard
- Produces: `GET /admin/config` — renders config.html with grouped settings form
- Produces: `POST /admin/config` — validates form data, writes via admin_config, re-renders

- [ ] **Step 1: Add config routes to `admin_pages/__init__.py`**

Add import at top:

```python
from app.api.admin_config import mask_sensitive as _mask_sensitive, RESTART_REQUIRED, update_config as _api_update_config
```

Add routes after the dashboard route:

```python
@router.get("/config", response_class=HTMLResponse)
def config_page(request: Request, admin: User = Depends(get_admin_from_cookie)):
    config_data = {k: _mask_sensitive(k, v) for k, v in settings.model_dump().items()}
    return templates.TemplateResponse("config.html", {
        "request": request, "current_user": admin,
        "config": config_data, "restart_set": set(RESTART_REQUIRED),
    })


@router.post("/config", response_class=HTMLResponse)
async def config_save(
    request: Request,
    admin: User = Depends(get_admin_from_cookie),
):
    form = await request.form()
    form_data = {}
    known_fields = set(settings.model_dump().keys())
    for key in form:
        if key in known_fields:
            val = form[key].strip()
            if val.lower() in ("true", "false"):
                form_data[key] = val.lower() == "true"
            else:
                form_data[key] = val

    try:
        _api_update_config(form_data, admin)
        success_msg = "配置已保存"
        error_msg = None
    except HTTPException as e:
        success_msg = None
        error_msg = f"保存失败: {e.detail}"

    config_data = {k: _mask_sensitive(k, v) for k, v in settings.model_dump().items()}
    return templates.TemplateResponse("config.html", {
        "request": request, "current_user": admin,
        "config": config_data, "restart_set": set(RESTART_REQUIRED),
        "success_message": success_msg, "error_message": error_msg,
    })
```

- [ ] **Step 2: Write `config.html`**

Write `server/app/admin_pages/templates/config.html`:

```html
{% extends "base.html" %}
{% block title %}系统配置 - ClassNote 管理{% endblock %}
{% block page_title %}⚙️ 系统配置{% endblock %}
{% block content %}
<form method="post" action="/admin/config">
  {# General section #}
  <div class="card">
    <div class="form-section">
      <h3>🔐 通用</h3>
      <div class="form-row">
        <div class="form-group">
          <label for="APP_NAME">应用名称</label>
          <input type="text" id="APP_NAME" name="APP_NAME" value="{{ config.APP_NAME }}">
        </div>
        <div class="form-group">
          <label for="DEBUG">调试模式</label>
          <select id="DEBUG" name="DEBUG">
            <option value="true" {{ 'selected' if config.DEBUG == 'True' else '' }}>开启</option>
            <option value="false" {{ 'selected' if config.DEBUG != 'True' else '' }}>关闭</option>
          </select>
        </div>
      </div>
      <div class="form-row">
        <div class="form-group">
          <label for="DATA_DIR">数据目录</label>
          <input type="text" id="DATA_DIR" name="DATA_DIR" value="{{ config.DATA_DIR }}">
        </div>
        <div class="form-group">
          <label for="MAX_UPLOAD_SIZE_MB">最大上传大小 (MB)</label>
          <input type="number" id="MAX_UPLOAD_SIZE_MB" name="MAX_UPLOAD_SIZE_MB" value="{{ config.MAX_UPLOAD_SIZE_MB }}">
        </div>
      </div>
    </div>
  </div>

  {# LLM section #}
  <div class="card">
    <div class="form-section">
      <h3>🤖 LLM 配置（主）</h3>
      <div class="form-row">
        <div class="form-group">
          <label for="LLM_API_TYPE">API 类型</label>
          <select id="LLM_API_TYPE" name="LLM_API_TYPE">
            <option value="qwen" {{ 'selected' if config.LLM_API_TYPE == 'qwen' else '' }}>通义千问</option>
            <option value="openai" {{ 'selected' if config.LLM_API_TYPE == 'openai' else '' }}>OpenAI 兼容</option>
          </select>
        </div>
        <div class="form-group">
          <label for="LLM_API_KEY">API Key</label>
          <div style="display:flex; gap:4px;">
            <input type="password" id="LLM_API_KEY" name="LLM_API_KEY" value="{{ config.LLM_API_KEY }}" style="flex:1;">
            <button type="button" class="btn btn-secondary btn-sm toggle-visibility" data-target="#LLM_API_KEY">显示</button>
          </div>
        </div>
      </div>
      <div class="form-row">
        <div class="form-group">
          <label for="LLM_API_BASE_URL">API 地址</label>
          <input type="text" id="LLM_API_BASE_URL" name="LLM_API_BASE_URL" value="{{ config.LLM_API_BASE_URL }}">
        </div>
        <div class="form-group">
          <label for="LLM_VISION_MODEL">视觉模型</label>
          <input type="text" id="LLM_VISION_MODEL" name="LLM_VISION_MODEL" value="{{ config.LLM_VISION_MODEL }}">
        </div>
      </div>
      <div class="form-group">
        <label for="LLM_TEXT_MODEL">文本模型</label>
        <input type="text" id="LLM_TEXT_MODEL" name="LLM_TEXT_MODEL" value="{{ config.LLM_TEXT_MODEL }}">
      </div>
    </div>
  </div>

  {# LLM fallback section #}
  <div class="card">
    <div class="form-section">
      <h3>🔄 LLM 配置（备用）</h3>
      <div class="form-row">
        <div class="form-group">
          <label for="LLM_FALLBACK_API_KEY">备用 API Key</label>
          <div style="display:flex; gap:4px;">
            <input type="password" id="LLM_FALLBACK_API_KEY" name="LLM_FALLBACK_API_KEY" value="{{ config.LLM_FALLBACK_API_KEY }}" style="flex:1;">
            <button type="button" class="btn btn-secondary btn-sm toggle-visibility" data-target="#LLM_FALLBACK_API_KEY">显示</button>
          </div>
        </div>
        <div class="form-group">
          <label for="LLM_FALLBACK_API_BASE_URL">备用 API 地址</label>
          <input type="text" id="LLM_FALLBACK_API_BASE_URL" name="LLM_FALLBACK_API_BASE_URL" value="{{ config.LLM_FALLBACK_API_BASE_URL }}">
        </div>
      </div>
      <div class="form-group">
        <label for="LLM_FALLBACK_MODEL">备用模型</label>
        <input type="text" id="LLM_FALLBACK_MODEL" name="LLM_FALLBACK_MODEL" value="{{ config.LLM_FALLBACK_MODEL }}">
      </div>
    </div>
  </div>

  {# STT section #}
  <div class="card">
    <div class="form-section">
      <h3>🎤 语音识别</h3>
      {% if 'WHISPER_MODEL_SIZE' in restart_set %}
      <div class="form-section-tip">⚠️ 修改此项需要重启 Celery worker 才能生效</div>
      {% endif %}
      <div class="form-row">
        <div class="form-group">
          <label for="WHISPER_MODEL_SIZE">Whisper 模型</label>
          <select id="WHISPER_MODEL_SIZE" name="WHISPER_MODEL_SIZE">
            <option value="large-v3" {{ 'selected' if config.WHISPER_MODEL_SIZE == 'large-v3' else '' }}>large-v3</option>
            <option value="medium" {{ 'selected' if config.WHISPER_MODEL_SIZE == 'medium' else '' }}>medium</option>
            <option value="small" {{ 'selected' if config.WHISPER_MODEL_SIZE == 'small' else '' }}>small</option>
          </select>
        </div>
        <div class="form-group">
          <label for="WHISPER_DEVICE">设备</label>
          <select id="WHISPER_DEVICE" name="WHISPER_DEVICE">
            <option value="cuda" {{ 'selected' if config.WHISPER_DEVICE == 'cuda' else '' }}>CUDA</option>
            <option value="cpu" {{ 'selected' if config.WHISPER_DEVICE == 'cpu' else '' }}>CPU</option>
          </select>
        </div>
      </div>
      <div class="form-row">
        <div class="form-group">
          <label for="WHISPER_COMPUTE_TYPE">精度</label>
          <select id="WHISPER_COMPUTE_TYPE" name="WHISPER_COMPUTE_TYPE">
            <option value="float16" {{ 'selected' if config.WHISPER_COMPUTE_TYPE == 'float16' else '' }}>float16</option>
            <option value="int8" {{ 'selected' if config.WHISPER_COMPUTE_TYPE == 'int8' else '' }}>int8</option>
          </select>
        </div>
        <div class="form-group">
          <label for="WHISPER_CPU_FALLBACK">CPU 回退</label>
          <select id="WHISPER_CPU_FALLBACK" name="WHISPER_CPU_FALLBACK">
            <option value="true" {{ 'selected' if config.WHISPER_CPU_FALLBACK == 'True' else '' }}>开启</option>
            <option value="false" {{ 'selected' if config.WHISPER_CPU_FALLBACK != 'True' else '' }}>关闭</option>
          </select>
        </div>
      </div>
    </div>
  </div>

  {# OCR section #}
  <div class="card">
    <div class="form-section">
      <h3>🖼️ OCR</h3>
      <div class="form-group">
        <label for="OCR_CONFIDENCE_THRESHOLD">置信度阈值</label>
        <input type="number" id="OCR_CONFIDENCE_THRESHOLD" name="OCR_CONFIDENCE_THRESHOLD"
               value="{{ config.OCR_CONFIDENCE_THRESHOLD }}" step="0.01" min="0" max="1">
      </div>
    </div>
  </div>

  <div class="btn-group">
    <button type="submit" class="btn btn-primary">保存配置</button>
  </div>
</form>
{% endblock %}
```

- [ ] **Step 3: Commit**

```bash
git add server/app/admin_pages/__init__.py server/app/admin_pages/templates/config.html
git commit -m "feat: add admin config page with grouped settings form"
```

---

### Task 7: Log viewer page

**Files:**
- Modify: `server/app/admin_pages/__init__.py` — add `/admin/logs` route
- Create: `server/app/admin_pages/templates/logs.html`

**Interfaces:**
- Consumes: `get_admin_from_cookie` — auth guard
- Consumes: `GET /api/v1/admin/logs` via server-side call to read log data
- Produces: `GET /admin/logs?level=&source=&page=` — renders logs.html with paginated entries

- [ ] **Step 1: Add logs route to `admin_pages/__init__.py`**

Add after config routes:

```python
import httpx


@router.get("/logs", response_class=HTMLResponse)
def logs_page(
    request: Request,
    level: str = "",
    source: str = "",
    page: int = 1,
    admin: User = Depends(get_admin_from_cookie),
):
    # Read log data directly (same logic as admin_config.get_logs)
    from app.api.admin_config import get_logs as _get_logs
    result = _get_logs(level=level, source=source, page=page, page_size=50, admin=admin)

    return templates.TemplateResponse("logs.html", {
        "request": request, "current_user": admin,
        "entries": result["entries"],
        "total": result["total"],
        "page": result["page"],
        "page_size": result["page_size"],
        "total_pages": result["total_pages"],
        "current_level": level,
        "current_source": source,
    })
```

- [ ] **Step 2: Write `logs.html`**

Write `server/app/admin_pages/templates/logs.html`:

```html
{% extends "base.html" %}
{% block title %}运行日志 - ClassNote 管理{% endblock %}
{% block page_title %}📋 运行日志{% endblock %}
{% block content %}
<div class="card">
  <form method="get" action="/admin/logs">
    <div class="filters">
      <label style="font-size:13px; color:var(--text-secondary);">日志源</label>
      <select name="source" onchange="this.form.submit()">
        <option value="" {{ 'selected' if not current_source else '' }}>所有</option>
        <option value="api" {{ 'selected' if current_source == 'api' else '' }}>API</option>
        <option value="celery" {{ 'selected' if current_source == 'celery' else '' }}>Celery</option>
      </select>

      <label style="font-size:13px; color:var(--text-secondary);">级别</label>
      <select name="level" onchange="this.form.submit()">
        <option value="" {{ 'selected' if not current_level else '' }}>全部</option>
        <option value="INFO" {{ 'selected' if current_level == 'INFO' else '' }}>INFO</option>
        <option value="WARN" {{ 'selected' if current_level == 'WARN' else '' }}>WARN</option>
        <option value="ERROR" {{ 'selected' if current_level == 'ERROR' else '' }}>ERROR</option>
      </select>

      <span style="font-size:13px; color:var(--text-secondary); margin-left:auto;">
        共 {{ total }} 条
      </span>
    </div>
  </form>

  {% if entries %}
  <div class="log-container">
    {% for line in entries %}
    <div class="log-entry">
      {{ line }}
    </div>
    {% endfor %}
  </div>

  {% if total_pages > 1 %}
  <div class="pagination">
    {% if page > 1 %}
    <a href="/admin/logs?level={{ current_level }}&source={{ current_source }}&page=1">首页</a>
    <a href="/admin/logs?level={{ current_level }}&source={{ current_source }}&page={{ page - 1 }}">上一页</a>
    {% endif %}

    {% set start_page = [1, page - 2]|max %}
    {% set end_page = [total_pages, page + 2]|min %}

    {% if start_page > 1 %}
    <span class="ellipsis">…</span>
    {% endif %}

    {% for p in range(start_page, end_page + 1) %}
    <a href="/admin/logs?level={{ current_level }}&source={{ current_source }}&page={{ p }}"
       class="{{ 'active' if p == page else '' }}">{{ p }}</a>
    {% endfor %}

    {% if end_page < total_pages %}
    <span class="ellipsis">…</span>
    {% endif %}

    {% if page < total_pages %}
    <a href="/admin/logs?level={{ current_level }}&source={{ current_source }}&page={{ page + 1 }}">下一页</a>
    <a href="/admin/logs?level={{ current_level }}&source={{ current_source }}&page={{ total_pages }}">末页</a>
    {% endif %}
  </div>
  {% endif %}

  {% else %}
  <div class="empty-state">暂无日志数据</div>
  {% endif %}
</div>
{% endblock %}
```

- [ ] **Step 3: Commit**

```bash
git add server/app/admin_pages/__init__.py server/app/admin_pages/templates/logs.html
git commit -m "feat: add admin log viewer with filtering and pagination"
```

---

## Self-Review

### 1. Spec coverage

- **仪表盘** (Section 4) → Task 4 ✅
- **系统配置** (Section 5) → Task 5 (API) + Task 6 (page) ✅
- **日志查看** (Section 6) → Task 7 (page via Task 5's API) ✅
- **认证与授权** (Section 3) → Task 3 ✅
- **模板继承** (Section 2.3) → Task 2 ✅
- **项目脚手架** (Section 2.1, 2.2) → Task 1 ✅
- **错误处理** (Section 7) — covered inline in each task ✅
- **依赖变更** (Section 8) — Task 1 includes jinja2 ✅

### 2. Placeholder scan

No "TBD", "TODO", or incomplete code blocks — every step has concrete code and commands.

### 3. Type consistency

- `get_admin_from_cookie` → returns `User`, used as `Depends` in all page routes ✅
- `mask_sensitive` in `admin_config.py` used in both API endpoint and page routes ✅
- `settings.model_dump()` used consistently across config API and page routes ✅
- `RESTART_REQUIRED` list defined once in `admin_config.py`, imported in `admin_pages/__init__.py` ✅
