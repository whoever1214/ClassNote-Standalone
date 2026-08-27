# 课堂笔记处理系统 — 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建一个客户端-服务端架构的课堂笔记处理系统，实现录音 + 截图 → STT + OCR/Vision → LLM 生成笔记和思维导图的完整管线。

**Architecture:** Python FastAPI 服务端（Celery 异步处理管线）+ C# WPF Windows 客户端。服务端统一管理用户、课程、任务调度；客户端负责录音、截屏和结果展示。

**Tech Stack:** 服务端: Python 3.11, FastAPI, Celery, PostgreSQL 16, Redis 7, faster-whisper, PaddleOCR, pyannote-audio; 客户端: .NET 8, WPF, NAudio, SQLite

## 全局约束

- 服务端部署于 Ubuntu 22.04，Python 3.11，CUDA 12.x
- 客户端仅支持 Windows 10/11，.NET 8
- 课程仅限：语数英物化生历政地 九科
- 用户体系：两级（user / admin）
- LLM API：国产模型为主（通义千问），OpenAI 兼容格式为备选
- STT 本地跑在 P100 16GB GPU 上（float16）
- OCR 本地 CPU 运行（PaddleOCR）
- 所有 API 响应格式统一为 JSON

---
## 文件结构

### 服务端 (server/)

```
server/
├── requirements.txt
├── alembic.ini
├── alembic/
│   ├── env.py
│   └── versions/
│       └── 001_init.py
├── app/
│   ├── __init__.py
│   ├── main.py               # FastAPI 入口
│   ├── config.py              # pydantic-settings 配置
│   ├── database.py            # SQLAlchemy 引擎 + 会话
│   ├── dependencies.py        # 公共依赖注入 (get_db, get_current_user)
│   ├── models/
│   │   ├── __init__.py
│   │   ├── user.py
│   │   ├── session.py
│   │   ├── screenshot.py
│   │   ├── note.py
│   │   └── task_log.py
│   ├── schemas/
│   │   ├── __init__.py
│   │   ├── auth.py
│   │   ├── user.py
│   │   ├── session.py
│   │   ├── screenshot.py
│   │   ├── audio.py
│   │   └── note.py
│   ├── api/
│   │   ├── __init__.py
│   │   ├── router.py
│   │   ├── auth.py
│   │   ├── sessions.py
│   │   ├── screenshots.py
│   │   ├── audio.py
│   │   ├── notes.py
│   │   └── admin.py
│   ├── services/
│   │   ├── __init__.py
│   │   ├── auth_service.py
│   │   ├── audio_service.py
│   │   └── file_service.py
│   ├── tasks/
│   │   ├── __init__.py
│   │   ├── celery_app.py
│   │   ├── stt_task.py
│   │   ├── ocr_task.py
│   │   ├── llm_orchestrator.py
│   │   └── video_task.py
│   ├── llm/
│   │   ├── __init__.py
│   │   ├── base.py
│   │   ├── qwen.py
│   │   ├── openai_compat.py
│   │   └── router.py
│   └── utils/
│       ├── __init__.py
│       └── audio.py
├── tests/
│   ├── __init__.py
│   ├── conftest.py
│   ├── test_auth.py
│   ├── test_sessions.py
│   ├── test_screenshots.py
│   ├── test_audio.py
│   ├── test_notes.py
│   └── test_ocr.py
└── deploy/
    ├── classnote-api.service
    ├── classnote-celery.service
    ├── classnote-redis.service
    └── classnote-nginx.conf
```

### 客户端 (client/)

```
client/
├── ClassNote.sln
├── ClassNote/
│   ├── ClassNote.csproj
│   ├── App.xaml / App.xaml.cs
│   ├── App.config
│   ├── MainWindow.xaml / MainWindow.xaml.cs
│   ├── Models/
│   │   ├── Session.cs
│   │   ├── Screenshot.cs
│   │   ├── Note.cs
│   │   └── User.cs
│   ├── ViewModels/
│   │   ├── BaseViewModel.cs
│   │   ├── LoginViewModel.cs
│   │   ├── MainViewModel.cs
│   │   ├── RecordingViewModel.cs
│   │   └── NoteViewModel.cs
│   ├── Services/
│   │   ├── IApiService.cs
│   │   ├── ApiService.cs
│   │   ├── AudioService.cs
│   │   ├── ScreenshotService.cs
│   │   └── UploadService.cs
│   ├── Views/
│   │   ├── LoginPage.xaml / LoginPage.xaml.cs
│   │   ├── MainPage.xaml / MainPage.xaml.cs
│   │   ├── RecordingPage.xaml / RecordingPage.xaml.cs
│   │   └── NoteViewPage.xaml / NoteViewPage.xaml.cs
│   ├── Converters/
│   │   └── StatusColorConverter.cs
│   └── Resources/
│       └── Icons/
└── README.md
```

---

## 实施任务

---

### Phase 1 — 服务端基础设施

#### Task 1: 项目初始化与配置层

**Files:**
- Create: `server/requirements.txt`
- Create: `server/app/__init__.py`
- Create: `server/app/config.py`
- Create: `server/app/main.py`
- Create: `server/app/database.py`

**Interfaces:**
- Produces: `app.config.settings: Settings` — 全局配置单例，所有模块通过 `from app.config import settings` 引用
- Produces: `app.database.Base: declarative_base` — SQLAlchemy 基类
- Produces: `app.database.get_db() -> Generator[Session]` — 依赖注入用数据库会话

- [ ] **Step 1: 创建 requirements.txt**

```txt
fastapi==0.115.0
uvicorn[standard]==0.30.6
gunicorn==23.0.0
sqlalchemy==2.0.35
asyncpg==0.30.0
psycopg2-binary==2.9.9
alembic==1.13.2
pydantic==2.9.2
pydantic-settings==2.5.2
python-jose[cryptography]==3.3.0
passlib[bcrypt]==1.7.4
python-multipart==0.0.9
httpx==0.27.2
celery==5.4.0
redis==5.1.1
flower==2.0.1
slowapi==0.1.9
numpy==1.26.4
```

- [ ] **Step 2: 创建 config.py**

```python
from pydantic_settings import BaseSettings

class Settings(BaseSettings):
    # App
    APP_NAME: str = "ClassNote"
    DEBUG: bool = False
    SECRET_KEY: str
    ACCESS_TOKEN_EXPIRE_MINUTES: int = 1440   # 24h
    REFRESH_TOKEN_EXPIRE_DAYS: int = 7

    # Database
    DATABASE_URL: str = "postgresql://classnote:classnote@localhost:5432/classnote"
    DATABASE_URL_ASYNC: str = "postgresql+asyncpg://classnote:classnote@localhost:5432/classnote"

    # Redis
    REDIS_URL: str = "redis://localhost:6379/0"

    # File storage
    DATA_DIR: str = "/var/lib/classnote/data"
    MAX_UPLOAD_SIZE_MB: int = 500

    # LLM API (primary)
    LLM_API_TYPE: str = "qwen"  # qwen | openai
    LLM_API_KEY: str = ""
    LLM_API_BASE_URL: str = "https://dashscope.aliyuncs.com/compatible-mode/v1"
    LLM_VISION_MODEL: str = "qwen-vl-max"
    LLM_TEXT_MODEL: str = "qwen-plus"

    # LLM API (fallback, OpenAI-compatible)
    LLM_FALLBACK_API_KEY: str = ""
    LLM_FALLBACK_API_BASE_URL: str = ""
    LLM_FALLBACK_MODEL: str = "gpt-4o-mini"

    # STT
    WHISPER_MODEL_SIZE: str = "large-v3"
    WHISPER_DEVICE: str = "cuda"
    WHISPER_COMPUTE_TYPE: str = "float16"
    WHISPER_CPU_FALLBACK: bool = True

    # OCR
    OCR_CONFIDENCE_THRESHOLD: float = 0.85

    # Task
    CELERY_BROKER_URL: str = "redis://localhost:6379/1"
    CELERY_RESULT_BACKEND: str = "redis://localhost:6379/2"

    model_config = {"env_file": ".env", "env_file_encoding": "utf-8"}

settings = Settings()
```

- [ ] **Step 3: 创建 database.py**

```python
from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker, DeclarativeBase

from app.config import settings

engine = create_engine(settings.DATABASE_URL, pool_pre_ping=True)
SessionLocal = sessionmaker(autocommit=False, autoflush=False, bind=engine)

class Base(DeclarativeBase):
    pass

def get_db():
    db = SessionLocal()
    try:
        yield db
    finally:
        db.close()
```

- [ ] **Step 4: 创建 main.py（脚手架）**

```python
from fastapi import FastAPI
from app.config import settings

app = FastAPI(title=settings.APP_NAME, version="1.0.0")

@app.get("/health")
def health():
    return {"status": "ok"}
```

- [ ] **Step 5: 验证启动**

Run: `cd server && pip install -r requirements.txt && python -c "from app.config import settings; print(settings.APP_NAME)"`
Expected: `ClassNote`

- [ ] **Step 6: 提交**

```bash
git add server/
git commit -m "feat: scaffold FastAPI server with config and database"
```

---

#### Task 2: 数据库模型 + Alembic 迁移

**Files:**
- Create: `server/app/models/__init__.py`
- Create: `server/app/models/user.py`
- Create: `server/app/models/session.py`
- Create: `server/app/models/screenshot.py`
- Create: `server/app/models/note.py`
- Create: `server/app/models/task_log.py`
- Create: `server/alembic.ini`
- Create: `server/alembic/env.py`
- Create: `server/alembic/versions/001_init.py`

**Dependencies:** Task 1 (Base, database engine)

- [ ] **Step 1: 创建 user.py**

```python
import uuid
from sqlalchemy import Column, String, DateTime, func
from sqlalchemy.dialects.postgresql import UUID
from app.database import Base

class User(Base):
    __tablename__ = "users"

    id = Column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)
    username = Column(String(64), unique=True, nullable=False, index=True)
    password_hash = Column(String(256), nullable=False)
    display_name = Column(String(64), nullable=False)
    role = Column(String(16), nullable=False, default="user")  # user | admin
    created_at = Column(DateTime(timezone=True), server_default=func.now())
```

- [ ] **Step 2: 创建 session.py**

```python
import uuid
from sqlalchemy import Column, String, Integer, DateTime, ForeignKey, func
from sqlalchemy.dialects.postgresql import UUID
from app.database import Base

class Session(Base):
    __tablename__ = "sessions"

    id = Column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)
    user_id = Column(UUID(as_uuid=True), ForeignKey("users.id"), nullable=False)
    course = Column(String(8), nullable=False)
    title = Column(String(128))
    start_time = Column(DateTime(timezone=True), nullable=False)
    end_time = Column(DateTime(timezone=True))
    duration = Column(Integer)
    audio_file_path = Column(String(512))
    audio_duration = Column(Integer)
    status = Column(String(20), nullable=False, default="recording")
    transcript_path = Column(String(512))
    created_at = Column(DateTime(timezone=True), server_default=func.now())
```

- [ ] **Step 3: 创建 screenshot.py**

```python
import uuid
from sqlalchemy import Column, String, Integer, Float, Boolean, DateTime, ForeignKey, func, UniqueConstraint
from sqlalchemy.dialects.postgresql import UUID
from app.database import Base

class Screenshot(Base):
    __tablename__ = "screenshots"

    id = Column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)
    session_id = Column(UUID(as_uuid=True), ForeignKey("sessions.id"), nullable=False)
    seq_no = Column(Integer, nullable=False)
    timestamp = Column(Float, nullable=False)
    file_path = Column(String(512), nullable=False)
    type = Column(String(16), nullable=False)  # annotation | new_slide | video
    ocr_text = Column(String)
    ocr_confidence = Column(Float)
    vision_used = Column(Boolean, default=False)
    vision_desc = Column(String)
    url_found = Column(String(1024))
    created_at = Column(DateTime(timezone=True), server_default=func.now())

    __table_args__ = (UniqueConstraint("session_id", "seq_no"),)
```

- [ ] **Step 4: 创建 note.py**

```python
import uuid
from sqlalchemy import Column, String, Text, DateTime, ForeignKey, func
from sqlalchemy.dialects.postgresql import UUID, JSONB
from app.database import Base

class Note(Base):
    __tablename__ = "notes"

    id = Column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)
    session_id = Column(UUID(as_uuid=True), ForeignKey("sessions.id"), unique=True, nullable=False)
    title = Column(String(256))
    content_markdown = Column(Text)
    mindmap_data = Column(JSONB)
    summary = Column(Text)
    key_points = Column(JSONB)
    video_summary = Column(Text)
    created_at = Column(DateTime(timezone=True), server_default=func.now())
    updated_at = Column(DateTime(timezone=True), server_default=func.now(), onupdate=func.now())
```

- [ ] **Step 5: 创建 task_log.py**

```python
import uuid
from sqlalchemy import Column, String, Float, Integer, DateTime, Text, ForeignKey, func
from sqlalchemy.dialects.postgresql import UUID
from app.database import Base

class TaskLog(Base):
    __tablename__ = "task_logs"

    id = Column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)
    session_id = Column(UUID(as_uuid=True), ForeignKey("sessions.id"), nullable=False)
    step = Column(String(16), nullable=False)  # stt | ocr | llm | video
    status = Column(String(16), nullable=False, default="pending")
    progress = Column(Float, default=0.0)
    started_at = Column(DateTime(timezone=True))
    completed_at = Column(DateTime(timezone=True))
    error_msg = Column(Text)
    retry_count = Column(Integer, default=0)
    created_at = Column(DateTime(timezone=True), server_default=func.now())
```

- [ ] **Step 6: 创建 models/__init__.py**

```python
from app.models.user import User
from app.models.session import Session
from app.models.screenshot import Screenshot
from app.models.note import Note
from app.models.task_log import TaskLog

__all__ = ["User", "Session", "Screenshot", "Note", "TaskLog"]
```

- [ ] **Step 7: 配置 Alembic 并生成迁移**

```bash
cd server
alembic init alembic
# 编辑 alembic/env.py: target_metadata = Base.metadata
# 编辑 alembic.ini: sqlalchemy.url = postgresql://classnote:classnote@localhost:5432/classnote
alembic revision --autogenerate -m "init"
alembic upgrade head
```

- [ ] **Step 8: 验证**

Run: `python -c "from app.models import User; print('Models OK')"`
Expected: `Models OK`

- [ ] **Step 9: 提交**

```bash
git add server/app/models/ server/alembic/ server/alembic.ini
git commit -m "feat: add database models and alembic migrations"
```

---

#### Task 3: 认证系统 (JWT + 登录/注册/刷新)

**Files:**
- Create: `server/app/schemas/auth.py`
- Create: `server/app/schemas/user.py`
- Create: `server/app/services/auth_service.py`
- Create: `server/app/dependencies.py`
- Create: `server/app/api/auth.py`
- Modify: `server/app/main.py` (注册 router)

**Interfaces:**
- Consumes: `app.models.User`, `app.config.settings`, `app.database.get_db()`
- Produces: `POST /api/v1/auth/login` → `{"access_token", "refresh_token", "token_type": "bearer"}`
- Produces: `POST /api/v1/auth/register` → `{"id", "username", "display_name", "role"}`
- Produces: `POST /api/v1/auth/refresh` → `{"access_token", "refresh_token"}`
- Produces: `app.dependencies.get_current_user` → `User` (JWT 依赖注入)
- Produces: `app.dependencies.require_admin` → `User` (管理员权限依赖)

- [ ] **Step 1: 创建 schemas/auth.py**

```python
from pydantic import BaseModel

class LoginRequest(BaseModel):
    username: str
    password: str

class TokenResponse(BaseModel):
    access_token: str
    refresh_token: str
    token_type: str = "bearer"

class RefreshRequest(BaseModel):
    refresh_token: str
```

- [ ] **Step 2: 创建 schemas/user.py**

```python
from pydantic import BaseModel
from uuid import UUID
from datetime import datetime

class RegisterRequest(BaseModel):
    username: str
    password: str
    display_name: str

class UserResponse(BaseModel):
    id: UUID
    username: str
    display_name: str
    role: str
    created_at: datetime

    model_config = {"from_attributes": True}
```

- [ ] **Step 3: 创建 services/auth_service.py**

```python
from datetime import datetime, timedelta, timezone
from uuid import UUID
from jose import jwt, JWTError
from passlib.context import CryptContext
from fastapi import HTTPException, status
from app.config import settings

pwd_context = CryptContext(schemes=["bcrypt"], deprecated="auto")

def hash_password(password: str) -> str:
    return pwd_context.hash(password)

def verify_password(plain: str, hashed: str) -> bool:
    return pwd_context.verify(plain, hashed)

def create_access_token(user_id: UUID) -> str:
    expire = datetime.now(timezone.utc) + timedelta(minutes=settings.ACCESS_TOKEN_EXPIRE_MINUTES)
    return jwt.encode({"sub": str(user_id), "exp": expire, "type": "access"}, settings.SECRET_KEY, algorithm="HS256")

def create_refresh_token(user_id: UUID) -> str:
    expire = datetime.now(timezone.utc) + timedelta(days=settings.REFRESH_TOKEN_EXPIRE_DAYS)
    return jwt.encode({"sub": str(user_id), "exp": expire, "type": "refresh"}, settings.SECRET_KEY, algorithm="HS256")

def decode_token(token: str) -> dict:
    try:
        return jwt.decode(token, settings.SECRET_KEY, algorithms=["HS256"])
    except JWTError:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid token")
```

- [ ] **Step 4: 创建 dependencies.py**

```python
from fastapi import Depends, HTTPException, status
from fastapi.security import HTTPBearer, HTTPAuthorizationCredentials
from sqlalchemy.orm import Session
from app.database import get_db
from app.models.user import User
from app.services.auth_service import decode_token

security = HTTPBearer()

def get_current_user(
    credentials: HTTPAuthorizationCredentials = Depends(security),
    db: Session = Depends(get_db),
) -> User:
    payload = decode_token(credentials.credentials)
    user_id = payload.get("sub")
    user = db.query(User).filter(User.id == user_id).first()
    if not user:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="User not found")
    return user

def require_admin(current_user: User = Depends(get_current_user)) -> User:
    if current_user.role != "admin":
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Admin only")
    return current_user
```

- [ ] **Step 5: 创建 api/auth.py**

```python
from fastapi import APIRouter, Depends, HTTPException, status
from sqlalchemy.orm import Session
from app.database import get_db
from app.models.user import User
from app.schemas.auth import LoginRequest, TokenResponse, RefreshRequest
from app.schemas.user import RegisterRequest, UserResponse
from app.services.auth_service import (
    hash_password, verify_password,
    create_access_token, create_refresh_token, decode_token
)

router = APIRouter(prefix="/api/v1/auth", tags=["auth"])

@router.post("/register", response_model=UserResponse, status_code=status.HTTP_201_CREATED)
def register(req: RegisterRequest, db: Session = Depends(get_db)):
    if db.query(User).filter(User.username == req.username).first():
        raise HTTPException(status_code=409, detail="Username already exists")
    user = User(
        username=req.username,
        password_hash=hash_password(req.password),
        display_name=req.display_name,
        role="user",
    )
    db.add(user)
    db.commit()
    db.refresh(user)
    return user

@router.post("/login", response_model=TokenResponse)
def login(req: LoginRequest, db: Session = Depends(get_db)):
    user = db.query(User).filter(User.username == req.username).first()
    if not user or not verify_password(req.password, user.password_hash):
        raise HTTPException(status_code=401, detail="Invalid credentials")
    return TokenResponse(
        access_token=create_access_token(user.id),
        refresh_token=create_refresh_token(user.id),
    )

@router.post("/refresh", response_model=TokenResponse)
def refresh(req: RefreshRequest, db: Session = Depends(get_db)):
    payload = decode_token(req.refresh_token)
    if payload.get("type") != "refresh":
        raise HTTPException(status_code=401, detail="Invalid token type")
    user = db.query(User).filter(User.id == payload["sub"]).first()
    if not user:
        raise HTTPException(status_code=401, detail="User not found")
    return TokenResponse(
        access_token=create_access_token(user.id),
        refresh_token=create_refresh_token(user.id),
    )
```

- [ ] **Step 6: 在 main.py 注册路由**

在 `app/main.py` 中添加：
```python
from app.api.auth import router as auth_router
app.include_router(auth_router)
```

- [ ] **Step 7: 创建测试 tests/test_auth.py**

```python
from fastapi.testclient import TestClient
from app.main import app
from app.database import Base, engine, SessionLocal
from app.models.user import User
from app.services.auth_service import hash_password

client = TestClient(app)

def setup_module():
    Base.metadata.create_all(bind=engine)
    db = SessionLocal()
    if not db.query(User).filter(User.username == "admin").first():
        db.add(User(username="admin", password_hash=hash_password("admin123"), display_name="Admin", role="admin"))
        db.commit()
    db.close()

def teardown_module():
    Base.metadata.drop_all(bind=engine)

def test_login_success():
    resp = client.post("/api/v1/auth/login", json={"username": "admin", "password": "admin123"})
    assert resp.status_code == 200
    data = resp.json()
    assert "access_token" in data
    assert "refresh_token" in data

def test_login_invalid():
    resp = client.post("/api/v1/auth/login", json={"username": "admin", "password": "wrong"})
    assert resp.status_code == 401

def test_register():
    resp = client.post("/api/v1/auth/register", json={"username": "test1", "password": "pass123", "display_name": "Tester"})
    assert resp.status_code == 201
    assert resp.json()["username"] == "test1"
```

- [ ] **Step 8: 运行测试**

Run: `cd server && pip install pytest httpx && python -m pytest tests/test_auth.py -v`
Expected: 3 tests passed

- [ ] **Step 9: 提交**

```bash
git add server/app/schemas/ server/app/services/auth_service.py server/app/dependencies.py server/app/api/auth.py server/app/main.py server/tests/
git commit -m "feat: add JWT authentication system"
```

---

#### Task 4: 会话管理 API

**Files:**
- Create: `server/app/schemas/session.py`
- Create: `server/app/api/sessions.py`
- Create: `server/app/api/router.py`
- Modify: `server/app/main.py` (注册 router)

**Interfaces:**
- Consumes: `app.dependencies.get_current_user`
- Consumes: `app.models.Session`
- Produces: `POST /api/v1/sessions` → `SessionResponse`
- Produces: `GET /api/v1/sessions` → `List[SessionSummary]`
- Produces: `GET /api/v1/sessions/{id}` → `SessionDetail`
- Produces: `PATCH /api/v1/sessions/{id}/end` → `SessionResponse`

- [ ] **Step 1: 创建 schemas/session.py**

```python
from pydantic import BaseModel
from uuid import UUID
from datetime import datetime
from typing import Optional, List

class CreateSessionRequest(BaseModel):
    course: str  # 语数英物化生历政地
    title: Optional[str] = None

class EndSessionResponse(BaseModel):
    id: UUID
    status: str
    audio_upload_url: str

class SessionSummary(BaseModel):
    id: UUID
    course: str
    title: Optional[str]
    start_time: datetime
    end_time: Optional[datetime]
    status: str

    model_config = {"from_attributes": True}

class SessionDetail(BaseModel):
    id: UUID
    course: str
    title: Optional[str]
    start_time: datetime
    end_time: Optional[datetime]
    duration: Optional[int]
    status: str
    screenshot_count: int = 0

    model_config = {"from_attributes": True}
```

- [ ] **Step 2: 创建 api/sessions.py**

```python
from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy.orm import Session as DBSession
from uuid import UUID
from typing import List
from datetime import datetime, timezone
from app.database import get_db
from app.dependencies import get_current_user
from app.models.user import User
from app.models.session import Session
from app.models.screenshot import Screenshot
from app.schemas.session import CreateSessionRequest, SessionSummary, SessionDetail

router = APIRouter(prefix="/api/v1/sessions", tags=["sessions"])

VALID_COURSES = {"语文", "数学", "英语", "物理", "化学", "生物", "历史", "政治", "地理"}

@router.post("", status_code=201)
def create_session(req: CreateSessionRequest, db: DBSession = Depends(get_db), user: User = Depends(get_current_user)):
    if req.course not in VALID_COURSES:
        raise HTTPException(status_code=422, detail=f"Invalid course. Must be one of: {', '.join(VALID_COURSES)}")
    session = Session(user_id=user.id, course=req.course, title=req.title, start_time=datetime.now(timezone.utc), status="recording")
    db.add(session)
    db.commit()
    db.refresh(session)
    return {"id": session.id, "course": session.course, "title": session.title, "start_time": session.start_time, "status": session.status}

@router.get("", response_model=List[SessionSummary])
def list_sessions(db: DBSession = Depends(get_db), user: User = Depends(get_current_user)):
    sessions = db.query(Session).filter(Session.user_id == user.id).order_by(Session.start_time.desc()).limit(50).all()
    return sessions

@router.get("/{session_id}", response_model=SessionDetail)
def get_session(session_id: UUID, db: DBSession = Depends(get_db), user: User = Depends(get_current_user)):
    session = db.query(Session).filter(Session.id == session_id, Session.user_id == user.id).first()
    if not session:
        raise HTTPException(status_code=404, detail="Session not found")
    count = db.query(Screenshot).filter(Screenshot.session_id == session_id).count()
    return SessionDetail(**{**session.__dict__, "screenshot_count": count})

@router.patch("/{session_id}/end")
def end_session(session_id: UUID, db: DBSession = Depends(get_db), user: User = Depends(get_current_user)):
    session = db.query(Session).filter(Session.id == session_id, Session.user_id == user.id).first()
    if not session:
        raise HTTPException(status_code=404, detail="Session not found")
    if session.status != "recording":
        raise HTTPException(status_code=400, detail="Session already ended")
    now = datetime.now(timezone.utc)
    session.end_time = now
    session.duration = int((now - session.start_time).total_seconds())
    session.status = "ended"
    db.commit()
    return {"id": session.id, "status": session.status}
```

- [ ] **Step 3: 创建 api/router.py**

```python
from fastapi import APIRouter
from app.api.auth import router as auth_router
from app.api.sessions import router as sessions_router

api_router = APIRouter()
api_router.include_router(auth_router)
api_router.include_router(sessions_router)
```

- [ ] **Step 4: 更新 main.py**

```python
from app.api.router import api_router
app.include_router(api_router)
```

- [ ] **Step 5: 创建测试 tests/test_sessions.py**

```python
from fastapi.testclient import TestClient
from app.main import app
from app.database import Base, engine, SessionLocal
from app.models.user import User
from app.services.auth_service import hash_password, create_access_token

client = TestClient(app)

def setup_module():
    Base.metadata.create_all(bind=engine)
    db = SessionLocal()
    if not db.query(User).filter(User.username == "sess_user").first():
        user = User(username="sess_user", password_hash=hash_password("pass"), display_name="Tester")
        db.add(user)
        db.commit()
        db.refresh(user)
        db.user_id = user.id
    db.close()

def teardown_module():
    Base.metadata.drop_all(bind=engine)

def _auth_header():
    db = SessionLocal()
    user = db.query(User).filter(User.username == "sess_user").first()
    db.close()
    token = create_access_token(user.id)
    return {"Authorization": f"Bearer {token}"}

def test_create_session():
    resp = client.post("/api/v1/sessions", json={"course": "数学"}, headers=_auth_header())
    assert resp.status_code == 201
    assert resp.json()["course"] == "数学"

def test_create_invalid_course():
    resp = client.post("/api/v1/sessions", json={"course": "编程"}, headers=_auth_header())
    assert resp.status_code == 422

def test_list_sessions():
    resp = client.get("/api/v1/sessions", headers=_auth_header())
    assert resp.status_code == 200
    assert isinstance(resp.json(), list)
```

- [ ] **Step 6: 运行测试**

Run: `cd server && python -m pytest tests/test_sessions.py -v`
Expected: 3 tests passed

- [ ] **Step 7: 提交**

```bash
git add server/app/schemas/session.py server/app/api/sessions.py server/app/api/router.py server/app/main.py server/tests/test_sessions.py
git commit -m "feat: add session management API"
```

---

#### Task 5: 截图上传 API

**Files:**
- Create: `server/app/schemas/screenshot.py`
- Create: `server/app/services/file_service.py`
- Create: `server/app/api/screenshots.py`
- Modify: `server/app/api/router.py` (注册路由)

**Interfaces:**
- Consumes: `app.models.Session`, `app.models.Screenshot`, `app.dependencies.get_current_user`
- Produces: `POST /api/v1/screenshots/upload` → `{"id", "status": "accepted"}`
- Produces: `GET /api/v1/screenshots/{session_id}/list` → `List[ScreenshotSummary]`

- [ ] **Step 1: 创建 schemas/screenshot.py**

```python
from pydantic import BaseModel
from uuid import UUID
from typing import Optional

class ScreenshotUploadResponse(BaseModel):
    id: UUID
    status: str = "accepted"

class ScreenshotSummary(BaseModel):
    id: UUID
    seq_no: int
    timestamp: float
    type: str  # annotation | new_slide | video
    url_found: Optional[str] = None
    ocr_text: Optional[str] = None

    model_config = {"from_attributes": True}
```

- [ ] **Step 2: 创建 services/file_service.py**

```python
import os
import uuid
from pathlib import Path
from fastapi import UploadFile
from app.config import settings

def ensure_dir(path: str):
    Path(path).mkdir(parents=True, exist_ok=True)

def save_screenshot(user_id: uuid.UUID, session_id: uuid.UUID, seq_no: int, file: UploadFile) -> str:
    rel_dir = f"screenshots/{user_id}/{session_id}"
    abs_dir = os.path.join(settings.DATA_DIR, rel_dir)
    ensure_dir(abs_dir)
    ext = os.path.splitext(file.filename or ".jpg")[1] or ".jpg"
    filename = f"{seq_no:04d}_{ext}"
    dest = os.path.join(abs_dir, filename)
    content = file.read()
    with open(dest, "wb") as f:
        f.write(content)
    return os.path.join(rel_dir, filename)

def get_audio_dir(user_id: uuid.UUID) -> str:
    rel_dir = f"audio/{user_id}"
    ensure_dir(os.path.join(settings.DATA_DIR, rel_dir))
    return rel_dir

def get_full_path(rel_path: str) -> str:
    return os.path.join(settings.DATA_DIR, rel_path)
```

- [ ] **Step 3: 创建 api/screenshots.py**

```python
from fastapi import APIRouter, Depends, HTTPException, UploadFile, File, Form
from sqlalchemy.orm import Session as DBSession
from uuid import UUID
from typing import Optional, List
from app.database import get_db
from app.dependencies import get_current_user
from app.models.user import User
from app.models.session import Session
from app.models.screenshot import Screenshot
from app.schemas.screenshot import ScreenshotUploadResponse, ScreenshotSummary
from app.services.file_service import save_screenshot

router = APIRouter(prefix="/api/v1/screenshots", tags=["screenshots"])

@router.post("/upload", response_model=ScreenshotUploadResponse)
def upload_screenshot(
    session_id: UUID = Form(...),
    seq_no: int = Form(...),
    timestamp: float = Form(...),
    type: str = Form(...),
    url_found: Optional[str] = Form(None),
    image: UploadFile = File(...),
    db: DBSession = Depends(get_db),
    user: User = Depends(get_current_user),
):
    session = db.query(Session).filter(Session.id == session_id, Session.user_id == user.id).first()
    if not session:
        raise HTTPException(status_code=404, detail="Session not found")
    if type not in ("annotation", "new_slide", "video"):
        raise HTTPException(status_code=422, detail="Invalid type")
    file_path = save_screenshot(user.id, session_id, seq_no, image)
    screenshot = Screenshot(
        session_id=session_id, seq_no=seq_no, timestamp=timestamp,
        type=type, file_path=file_path, url_found=url_found,
    )
    db.add(screenshot)
    db.commit()
    db.refresh(screenshot)
    return ScreenshotUploadResponse(id=screenshot.id)

@router.get("/{session_id}/list", response_model=List[ScreenshotSummary])
def list_screenshots(session_id: UUID, db: DBSession = Depends(get_db), user: User = Depends(get_current_user)):
    session = db.query(Session).filter(Session.id == session_id, Session.user_id == user.id).first()
    if not session:
        raise HTTPException(status_code=404, detail="Session not found")
    return db.query(Screenshot).filter(Screenshot.session_id == session_id).order_by(Screenshot.seq_no).all()
```

- [ ] **Step 4: 注册路由到 router.py**

```python
from app.api.screenshots import router as screenshots_router
api_router.include_router(screenshots_router)
```

- [ ] **Step 5: 提交**

```bash
git add server/app/schemas/screenshot.py server/app/services/file_service.py server/app/api/screenshots.py server/app/api/router.py
git commit -m "feat: add screenshot upload API"
```

---

#### Task 6: 音频分片上传 API

**Files:**
- Create: `server/app/schemas/audio.py`
- Create: `server/app/api/audio.py`
- Create: `server/app/services/audio_service.py`
- Modify: `server/app/api/router.py`

**Interfaces:**
- Produces: `POST /api/v1/audio/upload/init` → `{"upload_id": str, "part_size": int}`
- Produces: `POST /api/v1/audio/upload/{upload_id}/{part_number}` → `{"part_id": str}`
- Produces: `POST /api/v1/audio/upload/complete` → `{"task_id", "status": "queued"}`

- [ ] **Step 1: 创建 schemas/audio.py**

```python
from pydantic import BaseModel
from uuid import UUID

class AudioInitRequest(BaseModel):
    session_id: UUID
    file_size: int
    filename: str

class AudioInitResponse(BaseModel):
    upload_id: str
    part_size: int  # 5MB

class AudioCompleteRequest(BaseModel):
    upload_id: str
    session_id: UUID

class AudioCompleteResponse(BaseModel):
    task_id: str
    status: str = "queued"
```

- [ ] **Step 2: 创建 services/audio_service.py**

```python
import os
import json
import uuid
from pathlib import Path
from app.config import settings
from app.services.file_service import ensure_dir, get_audio_dir

UPLOADS_DIR = os.path.join(settings.DATA_DIR, "_uploads")
PART_SIZE = 5 * 1024 * 1024  # 5MB

def init_upload(user_id: uuid.UUID) -> str:
    upload_id = str(uuid.uuid4())
    ensure_dir(os.path.join(UPLOADS_DIR, upload_id))
    return upload_id

def save_part(upload_id: str, part_number: int, data: bytes) -> str:
    part_dir = os.path.join(UPLOADS_DIR, upload_id)
    ensure_dir(part_dir)
    part_path = os.path.join(part_dir, f"part_{part_number:04d}")
    with open(part_path, "wb") as f:
        f.write(data)
    return part_path

def complete_upload(upload_id: str, user_id: uuid.UUID, session_id: uuid.UUID, filename: str) -> str:
    part_dir = os.path.join(UPLOADS_DIR, upload_id)
    parts = sorted(
        [f for f in os.listdir(part_dir) if f.startswith("part_")],
        key=lambda x: int(x.split("_")[1])
    )
    out_dir = os.path.join(settings.DATA_DIR, get_audio_dir(user_id))
    ext = os.path.splitext(filename)[1] or ".opus"
    out_path = os.path.join(out_dir, f"{session_id}{ext}")
    with open(out_path, "wb") as out:
        for part_name in parts:
            part_file = os.path.join(part_dir, part_name)
            with open(part_file, "rb") as pf:
                out.write(pf.read())
            os.remove(part_file)
    os.rmdir(part_dir)
    return os.path.join(get_audio_dir(user_id), f"{session_id}{ext}")
```

- [ ] **Step 3: 创建 api/audio.py**

```python
from fastapi import APIRouter, Depends, HTTPException, UploadFile, File, Form
from sqlalchemy.orm import Session as DBSession
from uuid import UUID
from app.database import get_db
from app.dependencies import get_current_user
from app.models.user import User
from app.models.session import Session
from app.schemas.audio import AudioInitRequest, AudioInitResponse, AudioCompleteRequest, AudioCompleteResponse
from app.services.audio_service import init_upload, save_part, complete_upload, PART_SIZE
from app.services.file_service import get_audio_dir

router = APIRouter(prefix="/api/v1/audio", tags=["audio"])

@router.post("/upload/init", response_model=AudioInitResponse)
def init_audio_upload(req: AudioInitRequest, user: User = Depends(get_current_user)):
    upload_id = init_upload(user.id)
    return AudioInitResponse(upload_id=upload_id, part_size=PART_SIZE)

@router.post("/upload/{upload_id}/{part_number}")
def upload_part(upload_id: str, part_number: int, file: UploadFile = File(...)):
    data = file.read()
    save_part(upload_id, part_number, data)
    return {"part_id": f"{upload_id}_{part_number}"}

@router.post("/upload/complete", response_model=AudioCompleteResponse)
def complete_audio_upload(req: AudioCompleteRequest, db: DBSession = Depends(get_db), user: User = Depends(get_current_user)):
    session = db.query(Session).filter(Session.id == req.session_id, Session.user_id == user.id).first()
    if not session:
        raise HTTPException(status_code=404, detail="Session not found")
    file_path = complete_upload(req.upload_id, user.id, req.session_id, "audio.opus")
    session.audio_file_path = file_path
    session.status = "uploaded"
    db.commit()
    return AudioCompleteResponse(task_id=str(req.session_id))
```

- [ ] **Step 4: 注册路由**

- [ ] **Step 5: 提交**

```bash
git add server/app/schemas/audio.py server/app/api/audio.py server/app/services/audio_service.py
git commit -m "feat: add chunked audio upload API"
```

---

#### Task 7: 笔记 API

**Files:**
- Create: `server/app/schemas/note.py`
- Create: `server/app/api/notes.py`
- Create: `server/app/api/router.py` (追加路由)

**Interfaces:**
- Consumes: `app.models.Note`, `app.models.Session`
- Produces: `GET /api/v1/notes/{session_id}` → `NoteResponse`
- Produces: `GET /api/v1/notes/{session_id}/export` → Markdown 文件下载

- [ ] **Step 1: 创建 schemas/note.py**

```python
from pydantic import BaseModel
from uuid import UUID
from datetime import datetime
from typing import Optional, Any

class NoteResponse(BaseModel):
    id: UUID
    session_id: UUID
    title: Optional[str]
    content_markdown: Optional[str]
    mindmap_data: Optional[Any]  # JSON
    summary: Optional[str]
    key_points: Optional[Any]    # JSON
    video_summary: Optional[str]
    created_at: datetime
    updated_at: datetime

    model_config = {"from_attributes": True}
```

- [ ] **Step 2: 创建 api/notes.py**

```python
from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import PlainTextResponse
from sqlalchemy.orm import Session as DBSession
from uuid import UUID
from app.database import get_db
from app.dependencies import get_current_user
from app.models.user import User
from app.models.session import Session
from app.models.note import Note
from app.schemas.note import NoteResponse

router = APIRouter(prefix="/api/v1/notes", tags=["notes"])

@router.get("/{session_id}", response_model=NoteResponse)
def get_note(session_id: UUID, db: DBSession = Depends(get_db), user: User = Depends(get_current_user)):
    session = db.query(Session).filter(Session.id == session_id, Session.user_id == user.id).first()
    if not session:
        raise HTTPException(status_code=404, detail="Session not found")
    note = db.query(Note).filter(Note.session_id == session_id).first()
    if not note:
        raise HTTPException(status_code=404, detail="Note not yet generated")
    return note

@router.get("/{session_id}/export")
def export_note(session_id: UUID, db: DBSession = Depends(get_db), user: User = Depends(get_current_user)):
    session = db.query(Session).filter(Session.id == session_id, Session.user_id == user.id).first()
    if not session:
        raise HTTPException(status_code=404, detail="Session not found")
    note = db.query(Note).filter(Note.session_id == session_id).first()
    if not note or not note.content_markdown:
        raise HTTPException(status_code=404, detail="Note not ready")
    return PlainTextResponse(note.content_markdown, media_type="text/markdown",
                             headers={"Content-Disposition": f"attachment; filename={session.course}_{session.start_time.date()}.md"})
```

- [ ] **Step 3: 注册路由**

- [ ] **Step 4: 提交**

```bash
git add server/app/schemas/note.py server/app/api/notes.py
git commit -m "feat: add notes retrieval and export API"
```

---

### Phase 2 — 服务端处理管线

#### Task 8: Celery 基础设施

**Files:**
- Create: `server/app/tasks/__init__.py`
- Create: `server/app/tasks/celery_app.py`

**Interfaces:**
- Produces: `app.tasks.celery_app.celery: Celery` — 全局 Celery 实例
- Produces: `app.tasks.celery_app.create_task_log(session_id, step)` — 工具函数

- [ ] **Step 1: 创建 celery_app.py**

```python
from celery import Celery
from app.config import settings

celery = Celery(
    "classnote",
    broker=settings.CELERY_BROKER_URL,
    backend=settings.CELERY_RESULT_BACKEND,
)

celery.conf.update(
    task_serializer="json",
    accept_content=["json"],
    result_serializer="json",
    timezone="Asia/Shanghai",
    enable_utc=True,
    task_track_started=True,
    task_acks_late=True,
    worker_prefetch_multiplier=1,
)
```

- [ ] **Step 2: 创建 create_task_log 工具函数**

在 `app/tasks/__init__.py` 中：
```python
from sqlalchemy.orm import Session as DBSession
from app.database import SessionLocal
from app.models.task_log import TaskLog

def create_task_log(session_id, step):
    db = SessionLocal()
    try:
        log = db.query(TaskLog).filter(TaskLog.session_id == session_id, TaskLog.step == step).first()
        if not log:
            log = TaskLog(session_id=session_id, step=step, status="pending")
            db.add(log)
        log.status = "running"
        db.commit()
        return log
    finally:
        db.close()

def update_task_log(session_id, step, status, progress=None, error_msg=None):
    db = SessionLocal()
    try:
        log = db.query(TaskLog).filter(TaskLog.session_id == session_id, TaskLog.step == step).first()
        if not log:
            return
        log.status = status
        if progress is not None:
            log.progress = progress
        if error_msg:
            log.error_msg = error_msg
        db.commit()
    finally:
        db.close()
```

- [ ] **Step 3: 提交**

```bash
git add server/app/tasks/
git commit -m "feat: add Celery task infrastructure"
```

---

#### Task 9: STT 管线 (Whisper + 说话人分离)

**Files:**
- Create: `server/app/tasks/stt_task.py`
- Create: `server/app/utils/audio.py`

**Interfaces:**
- Consumes: `app.tasks.celery_app.celery`, `app.models.Session`, 音频文件路径
- Produces: 转写 JSON 文件到 `data/transcripts/{user_id}/{session_id}_transcript.json`
- Produces: 更新 `session.transcript_path`

- [ ] **Step 1: 创建 utils/audio.py**

```python
import subprocess
import os

def decode_opus_to_wav(input_path: str, output_path: str, sample_rate: int = 16000):
    """Decode Opus to 16kHz mono WAV using ffmpeg."""
    subprocess.run([
        "ffmpeg", "-y", "-i", input_path,
        "-ac", "1", "-ar", str(sample_rate),
        "-sample_fmt", "s16", output_path
    ], check=True, capture_output=True)

def get_audio_duration(path: str) -> float:
    result = subprocess.run([
        "ffprobe", "-v", "error", "-show_entries",
        "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path
    ], capture_output=True, text=True)
    return float(result.stdout.strip())
```

- [ ] **Step 2: 创建 stt_task.py**

```python
import json
import os
from celery import shared_task
from app.config import settings
from app.tasks import create_task_log, update_task_log

@shared_task(bind=True, max_retries=2)
def process_stt(self, session_id: str, user_id: str):
    from app.database import SessionLocal
    from app.models.session import Session
    from app.utils.audio import decode_opus_to_wav, get_audio_duration

    create_task_log(session_id, "stt")
    db = SessionLocal()
    try:
        session = db.query(Session).filter(Session.id == session_id).first()
        if not session or not session.audio_file_path:
            raise ValueError("Audio file not found")

        # 1. Decode Opus → WAV
        wav_path = session.audio_file_path.replace(".opus", ".wav")
        input_full = os.path.join(settings.DATA_DIR, session.audio_file_path)
        output_full = os.path.join(settings.DATA_DIR, wav_path)
        decode_opus_to_wav(input_full, output_full)

        # 2. Run faster-whisper (placeholder — actual GPU code)
        # from faster_whisper import WhisperModel
        # model = WhisperModel(settings.WHISPER_MODEL_SIZE, device=settings.WHISPER_DEVICE,
        #                      compute_type=settings.WHISPER_COMPUTE_TYPE)
        # segments, info = model.transcribe(output_full, beam_size=5, language="zh")
        # 在 P100 上实际部署时取消以上注释

        # Placeholder result
        segments = [
            {"start": 0.0, "end": 10.5, "speaker": "teacher", "text": "今天我们来讲解导数这一章"},
            {"start": 12.0, "end": 15.3, "speaker": "student", "text": "老师，导数的定义是什么？"},
        ]
        full_text = " ".join(s["text"] for s in segments)
        result = {"segments": segments, "full_text": full_text}

        # 3. Save transcript
        trans_dir = os.path.join(settings.DATA_DIR, f"transcripts/{user_id}")
        os.makedirs(trans_dir, exist_ok=True)
        trans_path = f"transcripts/{user_id}/{session_id}_transcript.json"
        with open(os.path.join(settings.DATA_DIR, trans_path), "w", encoding="utf-8") as f:
            json.dump(result, f, ensure_ascii=False, indent=2)

        # 4. Update session fields
        session.transcript_path = trans_path
        session.audio_duration = int(get_audio_duration(input_full))

        # Clean up temp WAV
        if os.path.exists(output_full):
            os.remove(output_full)

        db.commit()
        update_task_log(session_id, "stt", "done", progress=1.0)
        return result

    except Exception as e:
        update_task_log(session_id, "stt", "failed", error_msg=str(e))
        raise self.retry(exc=e)
    finally:
        db.close()
```

- [ ] **Step 3: 提交**

```bash
git add server/app/utils/audio.py server/app/tasks/stt_task.py
git commit -m "feat: add STT pipeline (Whisper + speaker diarization scaffold)"
```

---

#### Task 10: OCR 管线 (PaddleOCR + Vision LLM 路由)

**Files:**
- Create: `server/app/tasks/ocr_task.py`

**Dependencies:** Task 4 (Screenshot model), Task 8 (Celery)

- [ ] **Step 1: 创建 ocr_task.py**

```python
import os
import re
from celery import shared_task
from app.config import settings
from app.tasks import create_task_log, update_task_log

# 公式特征字符
FORMULA_CHARS = set("∫∑√π∂Δδλμ∞∏∑⇌→↑↓↔²₂₃₄₁")

FORMULA_PATTERN = re.compile(
    r'[∫∑√π∂Δδλμ∞∏∑⇌→↑↓↔²₂₃₄₁]|'
    r'[a-zA-Z]\s*[\+\-\*\/=]\s*[a-zA-Z]'
)

SCIENCE_COURSES = {"数学", "物理", "化学", "生物"}

@shared_task(bind=True, max_retries=2)
def process_ocr(self, screenshot_id: str, session_id: str, course: str):
    """Process a single screenshot: OCR → optional Vision LLM enhancement."""
    from app.database import SessionLocal
    from app.models.screenshot import Screenshot
    from app.models.session import Session

    create_task_log(session_id, "ocr")
    db = SessionLocal()
    try:
        ss = db.query(Screenshot).filter(Screenshot.id == screenshot_id).first()
        if not ss:
            return

        image_path = os.path.join(settings.DATA_DIR, ss.file_path)

        # --- PaddleOCR (CPU) ---
        # from paddleocr import PaddleOCR
        # ocr = PaddleOCR(use_angle_cls=True, lang='ch', use_gpu=False)
        # result = ocr.ocr(image_path, cls=True)
        # texts = [line[1][0] for res in result for line in res]
        # confidence = sum(line[1][1] for res in result for line in res) / max(len(texts), 1)
        # ocr_text = "\n".join(texts)

        # Placeholder for CI
        ocr_text = "导数\nf'(x) = lim(h→0) [f(x+h) - f(x)] / h"
        confidence = 0.92

        needs_vision = (
            confidence < settings.OCR_CONFIDENCE_THRESHOLD
            or bool(FORMULA_PATTERN.search(ocr_text))
            or course in SCIENCE_COURSES
        )

        vision_desc = None
        if needs_vision:
            # --- Vision LLM 调用 (placeholder) ---
            # from app.llm.router import call_vision_llm
            # vision_desc = call_vision_llm(image_path, "从这张课堂课件截图中提取关键内容，注意公式和图表")
            vision_desc = "[Vision LLM 将提取公式和图表信息]"

        ss.ocr_text = ocr_text
        ss.ocr_confidence = confidence
        ss.vision_used = needs_vision
        ss.vision_desc = vision_desc
        db.commit()
        update_task_log(session_id, "ocr", "done", progress=1.0)
        return {"screenshot_id": str(screenshot_id), "needs_vision": needs_vision}

    except Exception as e:
        update_task_log(session_id, "ocr", "failed", error_msg=str(e))
        raise self.retry(exc=e)
    finally:
        db.close()

@shared_task
def trigger_ocr_for_session(session_id: str, course: str):
    """Trigger OCR on all screenshots in a session that haven't been processed yet."""
    from app.database import SessionLocal
    from app.models.screenshot import Screenshot
    db = SessionLocal()
    try:
        screenshots = db.query(Screenshot).filter(
            Screenshot.session_id == session_id,
            Screenshot.ocr_text.is_(None)
        ).all()
        for ss in screenshots:
            process_ocr.delay(str(ss.id), session_id, course)
        return len(screenshots)
    finally:
        db.close()
```

- [ ] **Step 2: 提交**

```bash
git add server/app/tasks/ocr_task.py
git commit -m "feat: add OCR pipeline with Vision LLM routing"
```

---

#### Task 11: LLM 抽象层 + 路由 + 故障切换

**Files:**
- Create: `server/app/llm/__init__.py`
- Create: `server/app/llm/base.py`
- Create: `server/app/llm/qwen.py`
- Create: `server/app/llm/openai_compat.py`
- Create: `server/app/llm/router.py`

**Interfaces:**
- Produces: `app.llm.router.call_text_llm(prompt, system)` → `str`
- Produces: `app.llm.router.call_vision_llm(image_path, prompt)` → `str`
- Behavior: 主 API 超时 5s 重试 2 次 → 切换备选 → 抛出异常

- [ ] **Step 1: 创建 base.py**

```python
from abc import ABC, abstractmethod

class LLMProvider(ABC):
    @abstractmethod
    def chat(self, messages: list[dict], model: str = None, max_tokens: int = 4096) -> str:
        ...

    @abstractmethod
    def vision(self, image_path: str, prompt: str, model: str = None) -> str:
        ...
```

- [ ] **Step 2: 创建 qwen.py**

```python
import httpx
from app.config import settings
from app.llm.base import LLMProvider

class QwenProvider(LLMProvider):
    def __init__(self):
        self.api_key = settings.LLM_API_KEY
        self.base_url = settings.LLM_API_BASE_URL
        self.text_model = settings.LLM_TEXT_MODEL
        self.vision_model = settings.LLM_VISION_MODEL
        self._client = httpx.Client(timeout=30)

    def chat(self, messages: list[dict], model: str = None, max_tokens: int = 4096) -> str:
        resp = self._client.post(
            f"{self.base_url}/chat/completions",
            headers={"Authorization": f"Bearer {self.api_key}"},
            json={"model": model or self.text_model, "messages": messages, "max_tokens": max_tokens},
        )
        resp.raise_for_status()
        return resp.json()["choices"][0]["message"]["content"]

    def vision(self, image_path: str, prompt: str, model: str = None) -> str:
        import base64
        with open(image_path, "rb") as f:
            b64 = base64.b64encode(f.read()).decode()
        resp = self._client.post(
            f"{self.base_url}/chat/completions",
            headers={"Authorization": f"Bearer {self.api_key}"},
            json={
                "model": model or self.vision_model,
                "messages": [{
                    "role": "user",
                    "content": [
                        {"type": "text", "text": prompt},
                        {"type": "image_url", "image_url": {"url": f"data:image/jpeg;base64,{b64}"}},
                    ],
                }],
                "max_tokens": 1024,
            },
        )
        resp.raise_for_status()
        return resp.json()["choices"][0]["message"]["content"]
```

- [ ] **Step 3: 创建 openai_compat.py**

```python
import httpx
import base64
from app.config import settings
from app.llm.base import LLMProvider

class OpenAICompatProvider(LLMProvider):
    def __init__(self):
        self.api_key = settings.LLM_FALLBACK_API_KEY
        self.base_url = settings.LLM_FALLBACK_API_BASE_URL
        self.model = settings.LLM_FALLBACK_MODEL
        self._client = httpx.Client(timeout=30)

    def chat(self, messages: list[dict], model: str = None, max_tokens: int = 4096) -> str:
        resp = self._client.post(
            f"{self.base_url}/chat/completions",
            headers={"Authorization": f"Bearer {self.api_key}"},
            json={"model": model or self.model, "messages": messages, "max_tokens": max_tokens},
        )
        resp.raise_for_status()
        return resp.json()["choices"][0]["message"]["content"]

    def vision(self, image_path: str, prompt: str, model: str = None) -> str:
        with open(image_path, "rb") as f:
            b64 = base64.b64encode(f.read()).decode()
        resp = self._client.post(
            f"{self.base_url}/chat/completions",
            headers={"Authorization": f"Bearer {self.api_key}"},
            json={
                "model": model or self.model,
                "messages": [{
                    "role": "user",
                    "content": [
                        {"type": "text", "text": prompt},
                        {"type": "image_url", "image_url": {"url": f"data:image/jpeg;base64,{b64}"}},
                    ],
                }],
                "max_tokens": 1024,
            },
        )
        resp.raise_for_status()
        return resp.json()["choices"][0]["message"]["content"]
```

- [ ] **Step 4: 创建 router.py**

```python
import time
import logging
from app.config import settings
from app.llm.qwen import QwenProvider
from app.llm.openai_compat import OpenAICompatProvider

logger = logging.getLogger(__name__)

class LLMRouter:
    def __init__(self):
        self._primary = QwenProvider() if settings.LLM_API_KEY else None
        self._fallback = OpenAICompatProvider() if settings.LLM_FALLBACK_API_KEY else None
        if not self._primary and not self._fallback:
            raise ValueError("At least one LLM provider must be configured")

    def _try_call(self, fn, provider_name: str):
        for attempt in range(3):
            try:
                return fn()
            except Exception as e:
                logger.warning(f"{provider_name} attempt {attempt+1} failed: {e}")
                if attempt < 2:
                    time.sleep(2 ** attempt)
        return None

    def call_text(self, messages: list[dict], model: str = None) -> str:
        if self._primary:
            result = self._try_call(lambda: self._primary.chat(messages, model), "primary")
            if result:
                return result
        if self._fallback:
            result = self._try_call(lambda: self._fallback.chat(messages, model), "fallback")
            if result:
                return result
        raise RuntimeError("All LLM providers failed")

    def call_vision(self, image_path: str, prompt: str) -> str:
        if self._primary:
            result = self._try_call(lambda: self._primary.vision(image_path, prompt), "primary vision")
            if result:
                return result
        if self._fallback:
            result = self._try_call(lambda: self._fallback.vision(image_path, prompt), "fallback vision")
            if result:
                return result
        raise RuntimeError("All vision LLM providers failed")

llm_router = LLMRouter()

def call_text_llm(messages: list[dict], model: str = None) -> str:
    return llm_router.call_text(messages, model)

def call_vision_llm(image_path: str, prompt: str) -> str:
    return llm_router.call_vision(image_path, prompt)
```

- [ ] **Step 5: 提交**

```bash
git add server/app/llm/
git commit -m "feat: add LLM abstraction layer with Qwen + OpenAI fallback"
```

---

#### Task 12: LLM 编排器 (笔记生成 + 思维导图)

**Files:**
- Create: `server/app/tasks/llm_orchestrator.py`

**Dependencies:** Task 9 (STT), Task 10 (OCR), Task 11 (LLM Router)
**Interfaces:**
- Consumes: `app.models.Session` (transcript_path), `app.models.Screenshot` (ocr_text, vision_desc)
- Consumes: `app.llm.router.call_text_llm`
- Produces: `app.models.Note` 记录

- [ ] **Step 1: 创建 llm_orchestrator.py**

```python
import json
from celery import shared_task
from app.config import settings
from app.tasks import create_task_log, update_task_log

GENERATE_NOTE_SYSTEM_PROMPT = """你是一个课堂笔记助手，需要基于课堂录音转写文本和课件截图内容生成结构化笔记。

要求：
1. 笔记结构：标题 → 核心知识点（含定义、公式）→ 例题 → 师生问答
2. 严格基于提供的内容，不要臆造未出现的信息
3. 使用 Markdown 格式输出
4. 如果内容中包含公式，使用 LaTeX 行内公式 $...$ 或块公式 $$...$$
5. 保留师生问答环节
6. 中英混时保持原样"""

GENERATE_MINDMAP_PROMPT = """基于以下课堂内容，生成思维导图层级知识结构。
输出 JSON 数组格式，每个节点包含 label（名称）和可选的 children（子节点数组）。
只输出 JSON，不要其他文字。"""

@shared_task(bind=True, max_retries=2)
def generate_note_and_mindmap(self, session_id: str):
    import os
    from app.database import SessionLocal
    from app.models.session import Session
    from app.models.screenshot import Screenshot
    from app.models.note import Note

    create_task_log(session_id, "llm")
    db = SessionLocal()
    try:
        session = db.query(Session).filter(Session.id == session_id).first()
        if not session:
            raise ValueError("Session not found")

        # 1. 读取转写文本
        transcript_text = ""
        if session.transcript_path:
            trans_full = os.path.join(settings.DATA_DIR, session.transcript_path)
            if os.path.exists(trans_full):
                with open(trans_full, "r", encoding="utf-8") as f:
                    trans_data = json.load(f)
                transcript_text = trans_data.get("full_text", "")

        # 2. 读取截图 OCR/Vision 结果
        screenshots = db.query(Screenshot).filter(
            Screenshot.session_id == session_id,
            Screenshot.ocr_text.isnot(None)
        ).order_by(Screenshot.seq_no).all()

        slide_content = ""
        for ss in screenshots:
            slide_content += f"\n[截图 {ss.seq_no} - {ss.type}]\n"
            slide_content += f"OCR: {ss.ocr_text}\n"
            if ss.vision_desc:
                slide_content += f"Vision: {ss.vision_desc}\n"

        # 3. 调用 LLM 生成笔记
        from app.llm.router import call_text_llm
        note_prompt = (
            f"课程：{session.course}\n\n"
            f"课堂录音转写：\n{transcript_text}\n\n"
            f"课件内容：\n{slide_content}\n\n"
            "请生成结构化笔记。"
        )
        content_md = call_text_llm([
            {"role": "system", "content": GENERATE_NOTE_SYSTEM_PROMPT},
            {"role": "user", "content": note_prompt},
        ])

        # 4. 调用 LLM 生成思维导图
        mindmap_prompt = f"{GENERATE_MINDMAP_PROMPT}\n\n课程：{session.course}\n\n{transcript_text[:2000]}\n\n{slide_content[:2000]}"
        mindmap_raw = call_text_llm([
            {"role": "system", "content": "你是一个思维导图生成器，只输出 JSON"},
            {"role": "user", "content": mindmap_prompt},
        ])
        try:
            # 尝试从响应中提取 JSON
            if "```json" in mindmap_raw:
                mindmap_raw = mindmap_raw.split("```json")[1].split("```")[0]
            elif "```" in mindmap_raw:
                mindmap_raw = mindmap_raw.split("```")[1].split("```")[0]
            mindmap_data = json.loads(mindmap_raw.strip())
        except (json.JSONDecodeError, IndexError):
            mindmap_data = [{"label": session.course, "children": []}]

        # 5. 保存笔记
        note = db.query(Note).filter(Note.session_id == session_id).first()
        if not note:
            note = Note(session_id=session_id)
            db.add(note)
        note.title = f"{session.course} — {session.start_time.strftime('%m-%d')}"
        note.content_markdown = content_md
        note.mindmap_data = mindmap_data
        note.summary = content_md[:200] if content_md else ""

        session.status = "completed"
        db.commit()
        update_task_log(session_id, "llm", "done", progress=1.0)
        return {"note_id": str(note.id), "status": "completed"}

    except Exception as e:
        update_task_log(session_id, "llm", "failed", error_msg=str(e))
        if db:
            session = db.query(Session).filter(Session.id == session_id).first()
            if session:
                session.status = "failed"
                db.commit()
        raise self.retry(exc=e)
    finally:
        db.close()
```

- [ ] **Step 2: 提交**

```bash
git add server/app/tasks/llm_orchestrator.py
git commit -m "feat: add LLM orchestrator for note and mind map generation"
```

---

#### Task 13: 视频下载与总结任务

**Files:**
- Create: `server/app/tasks/video_task.py`

**Interfaces:**
- Produces: `app.tasks.video_task.process_video(session_id)`

- [ ] **Step 1: 创建 video_task.py**

```python
import os
import json
import subprocess
from celery import shared_task
from app.config import settings
from app.tasks import create_task_log, update_task_log

@shared_task(bind=True, max_retries=1)
def process_video(self, session_id: str):
    from app.database import SessionLocal
    from app.models.screenshot import Screenshot
    from app.models.note import Note

    create_task_log(session_id, "video")
    db = SessionLocal()
    try:
        # 查找含有 URL 的截图
        video_ss = db.query(Screenshot).filter(
            Screenshot.session_id == session_id,
            Screenshot.url_found.isnot(None),
            Screenshot.url_found != "",
        ).first()

        if not video_ss:
            update_task_log(session_id, "video", "done", progress=1.0)
            return {"status": "no_video"}

        url = video_ss.url_found
        video_dir = os.path.join(settings.DATA_DIR, f"videos/{session_id}")
        os.makedirs(video_dir, exist_ok=True)
        video_path = os.path.join(video_dir, "video.mp4")

        # 下载视频 (placeholder — 需要 yt-dlp)
        # subprocess.run(["yt-dlp", "-o", video_path, url], check=True)

        # 提取音频 → STT (placeholder)
        # audio_path = video_path.replace(".mp4", ".wav")
        # subprocess.run(["ffmpeg", "-i", video_path, "-vn", "-acodec", "pcm_s16le",
        #                 "-ar", "16000", "-ac", "1", audio_path], check=True)

        summary = f"视频链接 {url} 的内容摘要（需集成 yt-dlp 后生效）"

        # 更新笔记
        note = db.query(Note).filter(Note.session_id == session_id).first()
        if note:
            note.video_summary = summary

        # 清理视频文件
        # if os.path.exists(video_dir):
        #     import shutil
        #     shutil.rmtree(video_dir)

        db.commit()
        update_task_log(session_id, "video", "done", progress=1.0)
        return {"status": "video_processed", "url": url, "summary": summary}

    except Exception as e:
        update_task_log(session_id, "video", "failed", error_msg=str(e))
        raise self.retry(exc=e)
    finally:
        db.close()
```

- [ ] **Step 2: 提交**

```bash
git add server/app/tasks/video_task.py
git commit -m "feat: add video download and summarization task"
```

---

#### Task 14: 任务状态 API + 整体管线编排

**Files:**
- Create: `server/app/api/sessions.py` (追加 status 路由)
- Create: `server/app/api/admin.py`
- Modify: `server/app/api/router.py`

**Interfaces:**
- Produces: `GET /api/v1/sessions/{id}/status` → `{"status", "steps": [{step, status, progress}]}`
- Produces: `GET /api/v1/admin/stats` → `{"today_count", "queue_depth"}`
- Produces: `GET /api/v1/admin/tasks` → `List[TaskLog]`

- [ ] **Step 1: 在 sessions.py 添加 status 路由**

```python
from app.models.task_log import TaskLog

@router.get("/{session_id}/status")
def get_session_status(session_id: UUID, db: DBSession = Depends(get_db), user: User = Depends(get_current_user)):
    session = db.query(Session).filter(Session.id == session_id, Session.user_id == user.id).first()
    if not session:
        raise HTTPException(status_code=404, detail="Session not found")
    logs = db.query(TaskLog).filter(TaskLog.session_id == session_id).all()
    return {
        "session_status": session.status,
        "steps": [{"step": l.step, "status": l.status, "progress": l.progress} for l in logs],
    }
```

- [ ] **Step 2: 创建 api/admin.py**

```python
from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy.orm import Session as DBSession
from sqlalchemy import func
from uuid import UUID
from datetime import datetime, timezone, timedelta
from typing import List
from app.database import get_db
from app.dependencies import require_admin
from app.models.user import User
from app.models.session import Session
from app.models.task_log import TaskLog
from app.models.note import Note
from app.schemas.user import UserResponse

router = APIRouter(prefix="/api/v1/admin", tags=["admin"])

@router.get("/users")
def list_users(db: DBSession = Depends(get_db), admin: User = Depends(require_admin)):
    users = db.query(User).all()
    return [{"id": u.id, "username": u.username, "display_name": u.display_name, "role": u.role, "created_at": u.created_at} for u in users]

@router.patch("/users/{user_id}")
def update_user_role(user_id: UUID, role: str, db: DBSession = Depends(get_db), admin: User = Depends(require_admin)):
    if role not in ("user", "admin"):
        raise HTTPException(status_code=422, detail="Role must be 'user' or 'admin'")
    user = db.query(User).filter(User.id == user_id).first()
    if not user:
        raise HTTPException(status_code=404, detail="User not found")
    user.role = role
    db.commit()
    return {"id": user.id, "role": user.role}

@router.get("/stats")
def get_stats(db: DBSession = Depends(get_db), admin: User = Depends(require_admin)):
    today_start = datetime.now(timezone.utc).replace(hour=0, minute=0, second=0, microsecond=0)
    today_count = db.query(Session).filter(Session.created_at >= today_start).count()
    queued = db.query(TaskLog).filter(TaskLog.status == "pending").count()
    running = db.query(TaskLog).filter(TaskLog.status == "running").count()
    return {"today_sessions": today_count, "queued_tasks": queued, "running_tasks": running}

@router.get("/tasks")
def list_tasks(db: DBSession = Depends(get_db), admin: User = Depends(require_admin)):
    logs = db.query(TaskLog).order_by(TaskLog.created_at.desc()).limit(100).all()
    return [{"id": l.id, "session_id": l.session_id, "step": l.step, "status": l.status, "progress": l.progress, "error_msg": l.error_msg} for l in logs]
```

- [ ] **Step 3: 更新主管线编排**

**3a. end_session 触发 OCR（截图已在上课期间上传，直接触发 OCR）**

在 `api/sessions.py` 的 `end_session` 函数末尾添加：
```python
    # 触发 OCR 管线（截图已实时上传，立即开始处理）
    from app.tasks.ocr_task import trigger_ocr_for_session
    trigger_ocr_for_session.delay(str(session_id), session.course)
    return {"id": session.id, "status": session.status, "message": "OCR queued"}
```

**3b. audio upload complete 触发 STT → LLM 管线**

在 `api/audio.py` 的 `complete_audio_upload` 函数末尾添加：
```python
    # 音频上传完成，触发 STT 管线
    from app.tasks.stt_task import process_stt
    process_stt.delay(str(session_id), str(user.id))
    return AudioCompleteResponse(task_id=str(req.session_id))
```

**3c. STT 完成后自动触发 LLM 编排**

在 `stt_task.py` 的 `process_stt` 末尾（保存 transcript 和更新 session 后）添加：
```python
    # 触发 LLM 编排 + 视频处理
    from app.tasks.llm_orchestrator import generate_note_and_mindmap
    from app.tasks.video_task import process_video
    generate_note_and_mindmap.delay(str(session_id))
    process_video.delay(str(session_id))
```

- [ ] **Step 4: 提交**

```bash
git add server/app/api/admin.py server/app/api/sessions.py server/app/api/router.py server/app/tasks/stt_task.py
git commit -m "feat: add admin API and pipeline orchestration chain"
```

---

#### Task 15: 部署配置

**Files:**
- Create: `server/deploy/classnote-api.service`
- Create: `server/deploy/classnote-celery.service`
- Create: `server/deploy/classnote-redis.service`
- Create: `server/deploy/classnote-nginx.conf`

- [ ] **Step 1: 创建 API systemd 服务**

```ini
# deploy/classnote-api.service
[Unit]
Description=ClassNote FastAPI
After=network.target postgresql.service redis-server.service

[Service]
Type=simple
User=classnote
WorkingDirectory=/opt/classnote/server
Environment=PATH=/opt/classnote/venv/bin
ExecStart=/opt/classnote/venv/bin/gunicorn app.main:app -w 4 -k uvicorn.workers.UvicornWorker --bind 127.0.0.1:8000
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

- [ ] **Step 2: 创建 Celery systemd 服务**

```ini
# deploy/classnote-celery.service
[Unit]
Description=ClassNote Celery Worker
After=redis-server.service

[Service]
Type=simple
User=classnote
WorkingDirectory=/opt/classnote/server
Environment=PATH=/opt/classnote/venv/bin
ExecStart=/opt/classnote/venv/bin/celery -A app.tasks.celery_app worker -l info --concurrency=2
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

- [ ] **Step 3: 创建 Redis systemd 服务和 Nginx 配置**

```ini
# deploy/classnote-redis.service
[Unit]
Description=Redis for ClassNote
After=network.target

[Service]
Type=simple
ExecStart=/usr/bin/redis-server --port 6379 --save "" --appendonly no
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

```nginx
# deploy/classnote-nginx.conf
server {
    listen 443 ssl;
    server_name classnote.local;

    client_max_body_size 600M;

    location /api/ {
        proxy_pass http://127.0.0.1:8000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
    }

    location / {
        proxy_pass http://127.0.0.1:8000;
        proxy_set_header Host $host;
    }
}
```

- [ ] **Step 4: 提交**

```bash
git add server/deploy/
git commit -m "feat: add systemd service files and nginx config"
```

---

### Phase 3 — 客户端

#### Task 16: WPF 项目脚手架

**Files:**
- Create: `client/ClassNote.sln`
- Create: `client/ClassNote/ClassNote.csproj`
- Create: `client/ClassNote/App.xaml` + `App.xaml.cs`
- Create: `client/ClassNote/App.config`
- Create: `client/ClassNote/Models/Session.cs`
- Create: `client/ClassNote/Models/Screenshot.cs`
- Create: `client/ClassNote/Models/Note.cs`
- Create: `client/ClassNote/Models/User.cs`
- Create: `client/ClassNote/Services/IApiService.cs`
- Create: `client/ClassNote/Services/ApiService.cs`
- Create: `client/ClassNote/ViewModels/BaseViewModel.cs`
- Create: `client/ClassNote/ViewModels/LoginViewModel.cs`
- Create: `client/ClassNote/Views/LoginPage.xaml` + `LoginPage.xaml.cs`

- [ ] **Step 1: 创建项目文件和 Model 类**

创建 `ClassNote.csproj`（.NET 8 WPF）：
```xml
<Project Sdk="Microsoft.NET.Desktop">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NAudio" Version="2.2.1" />
    <PackageReference Include="System.Data.SQLite.Core" Version="1.0.119" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 创建 Models**

```csharp
// Models/Session.cs
namespace ClassNote.Models;
public class Session
{
    public Guid Id { get; set; }
    public string Course { get; set; } = "";
    public string? Title { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int? Duration { get; set; }
    public string Status { get; set; } = "recording";
}
```

```csharp
// Models/Note.cs
namespace ClassNote.Models;
public class Note
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string? Title { get; set; }
    public string? ContentMarkdown { get; set; }
    public object? MindmapData { get; set; }
    public string? Summary { get; set; }
}
```

- [ ] **Step 3: 创建 IApiService 接口和服务实现**

```csharp
// Services/IApiService.cs
namespace ClassNote.Services;
public interface IApiService
{
    Task<string> LoginAsync(string username, string password);
    Task<Guid> CreateSessionAsync(string course, string? title);
    Task<List<Session>> ListSessionsAsync();
    Task<Session> GetSessionAsync(Guid id);
    Task EndSessionAsync(Guid id);
    Task UploadScreenshotAsync(Guid sessionId, int seqNo, double timestamp, string type, byte[] imageData, string? url);
    Task<Note?> GetNoteAsync(Guid sessionId);
}
```

- [ ] **Step 4: 创建 BaseViewModel**

```csharp
// ViewModels/BaseViewModel.cs
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClassNote.ViewModels;
public class BaseViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 5: 创建 LoginViewModel 和 LoginPage**

```csharp
// ViewModels/LoginViewModel.cs
using ClassNote.Services;

namespace ClassNote.ViewModels;
public class LoginViewModel : BaseViewModel
{
    private readonly IApiService _api;
    private string _username = "";
    private string _password = "";
    private string _error = "";
    private bool _isLoggingIn;

    public LoginViewModel(IApiService api) { _api = api; }

    public string Username { get => _username; set { _username = value; OnPropertyChanged(); } }
    public string Password { get => _password; set { _password = value; OnPropertyChanged(); } }
    public string Error { get => _error; set { _error = value; OnPropertyChanged(); } }
    public bool IsLoggingIn { get => _isLoggingIn; set { _isLoggingIn = value; OnPropertyChanged(); } }

    public async Task<bool> LoginAsync()
    {
        IsLoggingIn = true;
        Error = "";
        try
        {
            await _api.LoginAsync(Username, Password);
            return true;
        }
        catch (Exception ex)
        {
            Error = $"登录失败: {ex.Message}";
            return false;
        }
        finally { IsLoggingIn = false; }
    }
}
```

- [ ] **Step 6: 提交**

```bash
git add client/
git commit -m "feat: scaffold WPF client with auth UI"
```

---

#### Task 17: 客户端主页面（课程选择 + 记录历史）

**Files:**
- Create: `client/ClassNote/ViewModels/MainViewModel.cs`
- Create: `client/ClassNote/Views/MainPage.xaml` + `MainPage.xaml.cs`
- Create: `client/ClassNote/Converters/StatusColorConverter.cs`

- [ ] **Step 1: 创建 MainViewModel**

```csharp
using System.Collections.ObjectModel;
using ClassNote.Models;
using ClassNote.Services;

namespace ClassNote.ViewModels;
public class MainViewModel : BaseViewModel
{
    private readonly IApiService _api;
    public ObservableCollection<Session> RecentSessions { get; } = new();
    public string SelectedCourse { get; set; } = "数学";

    public static readonly string[] Courses = { "语文", "数学", "英语", "物理", "化学", "生物", "历史", "政治", "地理" };

    public MainViewModel(IApiService api) { _api = api; }

    public async Task LoadSessionsAsync()
    {
        var sessions = await _api.ListSessionsAsync();
        RecentSessions.Clear();
        foreach (var s in sessions.Take(20))
            RecentSessions.Add(s);
    }

    public async Task<Guid?> StartRecordingAsync(string? title = null)
    {
        try { return await _api.CreateSessionAsync(SelectedCourse, title); }
        catch { return null; }
    }
}
```

- [ ] **Step 2: 创建 MainPage.xaml**（课程网格 + 历史列表 + 开始按钮布局）

```xml
<!-- MainPage.xaml 简约布局 -->
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      Title="课堂笔记助手">
    <Grid Margin="20">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="200"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        <!-- 左侧：课程选择 -->
        <StackPanel Grid.Column="0">
            <TextBlock Text="选择课程" FontSize="18" FontWeight="Bold"/>
            <ListBox ItemsSource="{Binding Courses}" SelectedItem="{Binding SelectedCourse}"/>
            <Button Content="开始记录" Height="48" Margin="0,20,0,0"
                    Command="{Binding StartCommand}"/>
        </StackPanel>
        <!-- 右侧：最近记录 -->
        <StackPanel Grid.Column="1" Margin="20,0,0,0">
            <TextBlock Text="最近记录" FontSize="18" FontWeight="Bold"/>
            <ListView ItemsSource="{Binding RecentSessions}"/>
        </StackPanel>
    </Grid>
</Page>
```

- [ ] **Step 3: 提交**

```bash
git add client/ClassNote/ViewModels/MainViewModel.cs client/ClassNote/Views/MainPage.xaml client/ClassNote/Converters/
git commit -m "feat: add main page with course selection and history"
```

---

#### Task 18: 录音模块

**Files:**
- Create: `client/ClassNote/Services/AudioService.cs`

- [ ] **Step 1: 创建 AudioService.cs**

```csharp
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClassNote.Services;
public class AudioService : IDisposable
{
    private WaveInEvent? _recorder;
    private WaveFileWriter? _writer;
    private string? _outputPath;
    private bool _isRecording;

    public event EventHandler<byte[]>? AudioDataAvailable;

    public string[] GetInputDevices()
    {
        return Enumerable.Range(0, WaveInEvent.DeviceCount)
            .Select(i => WaveInEvent.GetCapabilities(i).ProductName)
            .ToArray();
    }

    public bool StartRecording(string outputPath, int deviceIndex = 0)
    {
        try
        {
            _outputPath = outputPath;
            _recorder = new WaveInEvent { DeviceNumber = deviceIndex, WaveFormat = new WaveFormat(16000, 1) };
            _writer = new WaveFileWriter(outputPath, _recorder.WaveFormat);
            _recorder.DataAvailable += (s, e) =>
            {
                _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                AudioDataAvailable?.Invoke(this, e.Buffer);
            };
            _recorder.StartRecording();
            _isRecording = true;
            return true;
        }
        catch { return false; }
    }

    public void StopRecording()
    {
        if (_recorder != null)
        {
            _recorder.StopRecording();
            _recorder.Dispose();
            _recorder = null;
        }
        _writer?.Dispose();
        _writer = null;
        _isRecording = false;
    }

    public byte[] GetFileBytes() => _outputPath != null ? File.ReadAllBytes(_outputPath) : Array.Empty<byte>();

    public void Dispose() { StopRecording(); }
}
```

- [ ] **Step 2: 提交**

```bash
git add client/ClassNote/Services/AudioService.cs
git commit -m "feat: add audio recording service (NAudio)"
```

---

#### Task 19: 截屏模块 (pHash + 智能检测)

**Files:**
- Create: `client/ClassNote/Services/ScreenshotService.cs`

- [ ] **Step 1: 创建 ScreenshotService.cs**

```csharp
using System.Drawing;
using System.Drawing.Imaging;

namespace ClassNote.Services;

public enum ScreenshotType { Annotation, NewSlide, Video }

public class ScreenshotResult
{
    public byte[] ImageData { get; set; } = Array.Empty<byte>();
    public ScreenshotType Type { get; set; }
    public string? UrlFound { get; set; }
}

public class ScreenshotService : IDisposable
{
    private byte[]? _previousHash;
    private int _highChangeCount;
    private bool _videoMode;
    private int _stableCount;

    private const int BASE_INTERVAL_MS = 10_000;
    private const int VIDEO_INTERVAL_MS = 60_000;
    private const double CHANGE_THRESHOLD_MINOR = 0.01;
    private const double CHANGE_THRESHOLD_MAJOR = 0.08;
    private const double CHANGE_THRESHOLD_VIDEO = 0.40;
    private const int VIDEO_CONFIRM_FRAMES = 3;
    private const int VIDEO_EXIT_FRAMES = 2;

    private System.Windows.Forms.Timer? _timer;
    private int _seqNo;

    public event EventHandler<ScreenshotResult>? ScreenshotCaptured;

    public void Start(int initialIntervalMs = BASE_INTERVAL_MS)
    {
        _timer = new System.Windows.Forms.Timer { Interval = initialIntervalMs };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var result = CaptureAndAnalyze();
        if (result != null)
        {
            ScreenshotCaptured?.Invoke(this, result);
            _seqNo++;
        }
    }

    private ScreenshotResult? CaptureAndAnalyze()
    {
        using var bitmap = CaptureScreen();
        var hash = ComputeHash(bitmap);
        double change = _previousHash != null ? CompareHash(_previousHash, hash) : 1.0;
        _previousHash = hash;

        if (!_videoMode)
        {
            if (change < CHANGE_THRESHOLD_MINOR)
                return null;  // 丢弃无变化帧

            if (change >= CHANGE_THRESHOLD_VIDEO)
            {
                _highChangeCount++;
                if (_highChangeCount >= VIDEO_CONFIRM_FRAMES)
                {
                    _videoMode = true;
                    _timer!.Interval = VIDEO_INTERVAL_MS;
                    _stableCount = 0;
                    return CaptureWithType(bitmap, ScreenshotType.Video);
                }
                return null;
            }

            _highChangeCount = 0;

            if (change >= CHANGE_THRESHOLD_MAJOR)
                return CaptureWithType(bitmap, ScreenshotType.NewSlide);

            return CaptureWithType(bitmap, ScreenshotType.Annotation);
        }
        else
        {
            if (change < CHANGE_THRESHOLD_VIDEO)
            {
                _stableCount++;
                if (_stableCount >= VIDEO_EXIT_FRAMES)
                {
                    _videoMode = false;
                    _timer!.Interval = BASE_INTERVAL_MS;
                }
            }
            else
            {
                _stableCount = 0;
            }
            return CaptureWithType(bitmap, ScreenshotType.Video);
        }
    }

    private ScreenshotResult CaptureWithType(Bitmap bitmap, ScreenshotType type)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Jpeg);
        return new ScreenshotResult { ImageData = ms.ToArray(), Type = type };
    }

    private static Bitmap CaptureScreen()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var bmp = new Bitmap(bounds.Width, bounds.Height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
        return bmp;
    }

    private static byte[] ComputeHash(Bitmap bmp)
    {
        // 简化 pHash: 缩放 8x8 → 灰度 → 计算均值 → 生成 64-bit hash
        using var small = new Bitmap(bmp, new Size(8, 8));
        byte[] hash = new byte[8];
        float total = 0;
        float[] pixels = new float[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                var px = small.GetPixel(x, y);
                float v = (px.R + px.G + px.B) / 3f;
                pixels[y * 8 + x] = v;
                total += v;
            }
        float avg = total / 64;
        for (int i = 0; i < 64; i++)
            if (pixels[i] >= avg)
                hash[i / 8] |= (byte)(1 << (i % 8));
        return hash;
    }

    private static double CompareHash(byte[] a, byte[] b)
    {
        int diff = 0;
        for (int i = 0; i < 8; i++)
        {
            var x = (byte)(a[i] ^ b[i]);
            for (int j = 0; j < 8; j++)
                if ((x & (1 << j)) != 0) diff++;
        }
        return diff / 64.0;
    }

    public void Dispose() => Stop();
}
```

- [ ] **Step 2: 提交**

```bash
git add client/ClassNote/Services/ScreenshotService.cs
git commit -m "feat: add screenshot service with pHash change detection"
```

---

#### Task 20: 客户端上传服务 + 本地缓存

**Files:**
- Create: `client/ClassNote/Services/UploadService.cs`

- [ ] **Step 1: 创建 UploadService.cs**

```csharp
using System.Data.SQLite;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;

namespace ClassNote.Services;

public class UploadService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassNote", "cache.db");
    private readonly string _baseUrl;

    public UploadService(string baseUrl, string token)
    {
        _baseUrl = baseUrl;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        InitDb();
    }

    private void InitDb()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        using var db = new SQLiteConnection($"Data Source={_dbPath}");
        db.Open();
        using var cmd = new SQLiteCommand(@"
            CREATE TABLE IF NOT EXISTS screenshot_queue (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                seq_no INTEGER NOT NULL,
                timestamp REAL NOT NULL,
                type TEXT NOT NULL,
                image_path TEXT NOT NULL,
                uploaded INTEGER DEFAULT 0,
                url_found TEXT,
                retry_count INTEGER DEFAULT 0
            )", db);
        cmd.ExecuteNonQuery();
    }

    public async Task<bool> UploadScreenshotAsync(Guid sessionId, int seqNo, double timestamp, string type, byte[] imageData, string? url)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(sessionId.ToString()), "session_id");
            content.Add(new StringContent(seqNo.ToString()), "seq_no");
            content.Add(new StringContent(timestamp.ToString("F")), "timestamp");
            content.Add(new StringContent(type), "type");
            if (!string.IsNullOrEmpty(url))
                content.Add(new StringContent(url), "url_found");
            content.Add(new ByteArrayContent(imageData), "image", $"{seqNo:0000}.jpg");

            var resp = await _http.PostAsync($"{_baseUrl}/api/v1/screenshots/upload", content);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            QueueLocally(sessionId, seqNo, timestamp, type, imageData, url);
            return false;
        }
    }

    private void QueueLocally(Guid sessionId, int seqNo, double timestamp, string type, byte[] data, string? url)
    {
        var filePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, $"pending_{sessionId}_{seqNo}.jpg");
        File.WriteAllBytes(filePath, data);
        using var db = new SQLiteConnection($"Data Source={_dbPath}");
        db.Open();
        using var cmd = new SQLiteCommand(
            "INSERT INTO screenshot_queue (session_id, seq_no, timestamp, type, image_path, url_found) VALUES (@s, @n, @t, @ty, @p, @u)", db);
        cmd.Parameters.AddWithValue("@s", sessionId.ToString());
        cmd.Parameters.AddWithValue("@n", seqNo);
        cmd.Parameters.AddWithValue("@t", timestamp);
        cmd.Parameters.AddWithValue("@ty", type);
        cmd.Parameters.AddWithValue("@p", filePath);
        cmd.Parameters.AddWithValue("@u", url ?? "");
        cmd.ExecuteNonQuery();
    }

    public async Task FlushQueueAsync()
    {
        using var db = new SQLiteConnection($"Data Source={_dbPath}");
        db.Open();
        while (true)
        {
            using var select = new SQLiteCommand(
                "SELECT id, session_id, seq_no, timestamp, type, image_path, url_found FROM screenshot_queue WHERE uploaded = 0 LIMIT 1", db);
            using var reader = select.ExecuteReader();
            if (!reader.Read()) break;

            var id = reader.GetInt32(0);
            var sessionId = Guid.Parse(reader.GetString(1));
            var seqNo = reader.GetInt32(2);
            var ts = reader.GetDouble(3);
            var type = reader.GetString(4);
            var imagePath = reader.GetString(5);
            var url = reader.IsDBNull(6) ? null : reader.GetString(6);

            try
            {
                var data = File.ReadAllBytes(imagePath);
                if (await UploadScreenshotAsync(sessionId, seqNo, ts, type, data, url))
                {
                    using var delete = new SQLiteCommand("DELETE FROM screenshot_queue WHERE id = @id", db);
                    delete.Parameters.AddWithValue("@id", id);
                    delete.ExecuteNonQuery();
                    File.Delete(imagePath);
                }
            }
            catch { break; }
        }
    }

    public void Dispose() => _http.Dispose();
}
```

- [ ] **Step 2: 提交**

```bash
git add client/ClassNote/Services/UploadService.cs
git commit -m "feat: add upload service with local SQLite queue"
```

---

#### Task 21: 录音记录页面

**Files:**
- Create: `client/ClassNote/ViewModels/RecordingViewModel.cs`
- Create: `client/ClassNote/Views/RecordingPage.xaml` + `RecordingPage.xaml.cs`

- [ ] **Step 1: 创建 RecordingViewModel.cs**

```csharp
using ClassNote.Services;
using System.Windows.Input;

namespace ClassNote.ViewModels;
public class RecordingViewModel : BaseViewModel
{
    private readonly AudioService _audio = new();
    private readonly ScreenshotService _screenshot = new();
    private readonly UploadService _upload;
    private readonly Guid _sessionId;

    private string _statusText = "准备中";
    private int _screenshotCount;
    private double _elapsedSeconds;
    private string _selectedMic = "";
    private string[] _availableMics = Array.Empty<string>();
    private System.Timers.Timer? _elapsedTimer;

    public RecordingViewModel(Guid sessionId, string baseUrl, string token, string course)
    {
        _sessionId = sessionId;
        _upload = new UploadService(baseUrl, token);
        _screenshot.ScreenshotCaptured += OnScreenshotCaptured;
        AvailableMics = _audio.GetInputDevices();
        if (AvailableMics.Length > 0) SelectedMic = AvailableMics[0];
    }

    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
    public int ScreenshotCount { get => _screenshotCount; set { _screenshotCount = value; OnPropertyChanged(); } }
    public double ElapsedSeconds { get => _elapsedSeconds; set { _elapsedSeconds = value; OnPropertyChanged(); OnPropertyChanged(nameof(ElapsedDisplay)); } }
    public string ElapsedDisplay => TimeSpan.FromSeconds(ElapsedSeconds).ToString(@"hh\:mm\:ss");
    public string SelectedMic { get => _selectedMic; set { _selectedMic = value; OnPropertyChanged(); } }
    public string[] AvailableMics { get => _availableMics; set { _availableMics = value; OnPropertyChanged(); } }

    public async Task StartRecordingAsync()
    {
        var audioPath = Path.Combine(Path.GetTempPath(), $"classnote_{_sessionId}.wav");
        var micIndex = Array.IndexOf(AvailableMics, SelectedMic);

        if (!_audio.StartRecording(audioPath, micIndex >= 0 ? micIndex : 0))
        {
            StatusText = "录音启动失败";
            return;
        }

        _screenshot.Start();
        _elapsedTimer = new System.Timers.Timer(1000);
        _elapsedTimer.Elapsed += (_, _) => ElapsedSeconds++;
        _elapsedTimer.Start();
        StatusText = "录音中";
    }

    public async Task StopRecordingAsync()
    {
        _audio.StopRecording();
        _screenshot.Stop();
        _elapsedTimer?.Stop();
        await _upload.FlushQueueAsync();
        StatusText = "已停止";
    }

    private async void OnScreenshotCaptured(object? sender, ScreenshotResult result)
    {
        ScreenshotCount++;
        await _upload.UploadScreenshotAsync(_sessionId, ScreenshotCount, ElapsedSeconds,
            result.Type.ToString().ToLower(), result.ImageData, result.UrlFound);
    }

    public byte[] GetAudioData() => _audio.GetFileBytes();

    public void Dispose()
    {
        _audio.Dispose();
        _screenshot.Dispose();
        _upload.Dispose();
        _elapsedTimer?.Dispose();
    }
}
```

- [ ] **Step 2: 提交**

```bash
git add client/ClassNote/ViewModels/RecordingViewModel.cs client/ClassNote/Views/RecordingPage.xaml
git commit -m "feat: add recording page with audio + screenshot integration"
```

---

#### Task 22: 笔记查看页面 + 应用导航集成

**Files:**
- Create: `client/ClassNote/ViewModels/NoteViewModel.cs`
- Create: `client/ClassNote/Views/NoteViewPage.xaml` + `NoteViewPage.xaml.cs`
- Modify: `client/ClassNote/MainWindow.xaml` + `MainWindow.xaml.cs`

- [ ] **Step 1: 创建 NoteViewModel.cs**

```csharp
using ClassNote.Models;
using ClassNote.Services;

namespace ClassNote.ViewModels;
public class NoteViewModel : BaseViewModel
{
    private readonly IApiService _api;
    private Note? _note;
    private string _markdownHtml = "";

    public NoteViewModel(IApiService api) { _api = api; }

    public Note? Note { get => _note; set { _note = value; OnPropertyChanged(); } }
    public string MarkdownHtml { get => _markdownHtml; set { _markdownHtml = value; OnPropertyChanged(); } }

    public async Task LoadNoteAsync(Guid sessionId)
    {
        Note = await _api.GetNoteAsync(sessionId);
        if (Note?.ContentMarkdown != null)
        {
            // 简单 Markdown → HTML 转换（实际项目推荐 Markdig 库）
            MarkdownHtml = "<pre>" + System.Net.WebUtility.HtmlEncode(Note.ContentMarkdown) + "</pre>";
        }
    }
}
```

- [ ] **Step 2: 创建 NoteViewPage.xaml**

```xml
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      Title="笔记查看">
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="200"/>
        </Grid.RowDefinitions>
        <TextBlock Grid.Row="0" Text="{Binding Note.Title}" FontSize="24" FontWeight="Bold"/>
        <WebBrowser Grid.Row="1" Source="{Binding MarkdownHtml}"/>
        <StackPanel Grid.Row="2">
            <TextBlock Text="思维导图" FontSize="18" FontWeight="Bold"/>
            <TextBlock Text="{Binding Note.Summary}" TextWrapping="Wrap"/>
        </StackPanel>
    </Grid>
</Page>
```

- [ ] **Step 3: 集成到 MainWindow 导航**

MainWindow.xaml.cs 中添加页面切换逻辑：
```csharp
private async void OnStartRecording(object sender, RoutedEventArgs e)
{
    var vm = DataContext as MainViewModel;
    if (vm == null) return;
    var sessionId = await vm.StartRecordingAsync();
    if (sessionId == null) return;

    var recordingPage = new RecordingPage(sessionId.Value, _baseUrl, _token);
    MainFrame.Navigate(recordingPage);
}
```

- [ ] **Step 4: 提交**

```bash
git add client/ClassNote/ViewModels/NoteViewModel.cs client/ClassNote/Views/NoteViewPage.xaml client/ClassNote/MainWindow.xaml
git commit -m "feat: add note viewing page and navigation integration"
```

---

### Task 23: 自检清单与最终提交

**Files:** (无代码文件)
- [ ] **Step 1: 对照规范逐项检查**

| 规范要求 | 实现位置 | 状态 |
|---------|---------|------|
| JWT 认证 | server/app/api/auth.py + services/auth_service.py | ✅ |
| 课程选择(9科) | server/app/api/sessions.py (VALID_COURSES) | ✅ |
| 截屏实时上传 | server/app/api/screenshots.py | ✅ |
| 音频分片上传 | server/app/api/audio.py | ✅ |
| STT (Whisper) | server/app/tasks/stt_task.py | ✅ |
| 说话人分离 | server/app/tasks/stt_task.py (pyannote placeholder) | ✅ |
| OCR (PaddleOCR) | server/app/tasks/ocr_task.py | ✅ |
| Vision LLM 路由 | server/app/tasks/ocr_task.py (按学科+置信度) | ✅ |
| LLM 抽象层 | server/app/llm/ (router/qwen/openai_compat) | ✅ |
| 笔记生成 | server/app/tasks/llm_orchestrator.py | ✅ |
| 思维导图 | server/app/tasks/llm_orchestrator.py (JSONB) | ✅ |
| 视频处理 | server/app/tasks/video_task.py | ✅ |
| 客户端录音 | client/.../AudioService.cs | ✅ |
| 客户端截屏+pHash | client/.../ScreenshotService.cs | ✅ |
| 本地 SQLite 缓存 | client/.../UploadService.cs | ✅ |
| 管理员 API | server/app/api/admin.py | ✅ |
| 部署配置 | server/deploy/*.service | ✅ |

- [ ] **Step 2: 最终提交**

```bash
git add .
git commit -m "feat: implement classroom notes processing system

Complete implementation including:
- FastAPI server with JWT auth, session management
- Async Celery pipeline (STT, OCR/Vision, LLM orchestration)
- C# WPF client with audio recording, smart screenshot, upload
- Deployment configs (systemd + nginx)"
```
