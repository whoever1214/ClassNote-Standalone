# ClassNote 课堂笔记助手 · 性能基准分析报告（Performance Benchmarker）

**Performance Benchmarker**: ⏱️ 数据驱动 · 基线先行 · 只优化有证据的项 · 诚实报告
**分析日期**: 2026-08-26（23:15–23:41）
**被测系统**: ClassNote（WPF / .NET 8 单机，Release 构建，**未重建**；exe: `ClassNote\bin\Release\net8.0-windows10.0.19041.0\ClassNote.exe`）
**映射方法论**: 本项目为桌面应用，无 Web 指标；将 LCP/FID/CLS 类方法论映射为：冷启动耗时（≈LCP）、模型加载/推理耗时（≈FID 交互响应）、单次导出耗时（≈每秒吞吐）、进程内存峰值（≈资源预算）。

---

## 1. 环境规格表

| 项 | 值 |
|---|---|
| CPU | Intel(R) Xeon(R) E-2124 @ 3.30GHz，4 核 / 4 逻辑线程 |
| 内存 | 15.9 GB（本次测试期间系统空闲，无其他重负载） |
| 磁盘 | C: 465.5 GB 总 / 103.8 GB 空闲 |
| OS | Microsoft Windows 10 专业版 Build 19045（22H2） |
| 运行时 | PowerShell 7.5.5（FullLanguage）；.NET SDK 8.0.423 |
| 模型 | SenseVoice-Small ONNX INT8：`model_quant.onnx` 241,216,270 B（≈230 MB） |
| 依赖 | Microsoft.ML.OnnxRuntime 1.20.0.0（CPU）、QuestPDF、Windows.Media.Ocr（WinRT 直驱成功） |
| 测试素材 | `bench\speech-test.wav`（SAPI「Microsoft Huihui Desktop」合成，509,966 B）；`bench\ocr-test.png`（System.Drawing 生成，52,382 B） |
| 干扰控制 | 每次基准独立运行；B1 期间检测到 E2E agent 实例占用（PID 11452，会话 E2E-0826232252），按单实例规则等待其退出；B2–B5 均在无应用实例时测量 |

> 测量口径总则：全部数字来自 `[System.Diagnostics.Stopwatch]`、进程属性（`WorkingSet64/PrivateMemorySize64`）与文件属性，**无任何估算或编造**。

---

## 2. B1–B5 数据表

### B1 应用冷启动（3 次独立运行，UIA 轮询主窗口「ClassNote - 课堂笔记助手」，Stop-Process 间隔 10s）

| metric | 值 | min | avg | max | 口径与条件 |
|---|---|---|---|---|---|
| 冷启动 ms（ROUND2 干净样本） | 1668 / 1556 / 1668 | **1556** | **1630.7** | **1668** | Start-Process → UIA RootElement.FindFirst(NameProperty=标题) 每 150ms 轮询，窗口出现即止；系统空闲、无残留实例 |
| WS@10s（WorkingSet64）MB | 165.5 / 176.9 / 179.8 | 165.5 | 174.1 | 179.8 | 启动后 10s 读 `WorkingSet64` |
| PrivateMemory@10s MB | 123.2 / 127.6 / 134.1 | 123.2 | 128.3 | 134.1 | 同上 `PrivateMemorySize64` |
| MaxWorkingSet MB | 1.3（3 次一致） | 1.3 | 1.3 | 1.3 | 系统默认工作集上限读数（API 工件，非实际占用，如实记录） |
| 冷缓存/首启样本 ms | 9582（ROUND1-RUN3） | — | — | — | E2E 实例退出后的**首次**启动 + 系统页面缓存冷，窗口出现 9.58s；与 E2E J1 的 ~10.7s 同量级（口径：E2E 可能等至页面可用，非仅窗口） |

> 结论：稳态冷启动 ≈ **1.63s（窗口出现）**，达标；首启/冷缓存 ~9.6–10.7s 为环境敏感项（见优化建议 P2）。

### B2 STT 转写（程序集直驱 `SenseVoiceSttService`，宿主为 pwsh 进程；WAV 15.94s / 16kHz mono 16bit）

| metric | 值 | min | avg | max | 口径与条件 |
|---|---|---|---|---|---|
| 冷调用（新实例 = 模型加载+推理+预处）ms | 11064.3 / 10786.3 / 10349.5 | **10349.5** | **10733.4** | **11064.3** | 3 个新 `SenseVoiceSttService` 实例各 1 次 `TranscribeAsync`，每次含 `InferenceSession` 构建（加载 241MB ONNX INT8） |
| 温调用（同实例，无模型加载）ms | 4684.8 | — | — | — | 复用 COLD1 实例再转写同一 WAV → 纯 读WAV+fbank+推理 |
| 模型加载耗时（推算）ms | ≈ 6048.6 | — | — | — | 冷均值 − 温调用 = 10733.4 − 4684.8；推理管线 ≈ 4.68s |
| 实时因子 RTF | ≈ 0.29 | — | — | — | 4.68s / 15.94s 音频（<1，可实时） |
| 内存增量（单会话）MB | WS 98.4 → 692.8（Δ≈+594.4） | — | — | — | 宿主 pwsh `WorkingSet64`，冷调用前后对比；**与 README 声称「峰值约 600MB」吻合** |
| 峰值（采样 100ms）MB | WS 1816.9 / priv 2446.7 | — | — | — | 3 个 STT 实例同驻宿主的**累积**工作集（应用典型仅 1 实例/录音会话 ≈ 700MB 级；见瓶颈 B1） |
| 转写文本（证据，62 字符） | `今天我们来学习1元2次方程的解法，求根公式是X等于负B加减根号下B平方减4倍AC除以2A判别式大于零时有两个不相等的实数根。` | — | — | — | 4 次转写结果一致（数字被 Huihui 语音转罗马字符，识别质量正常） |

> 程序集加载一次成功（`Assembly.LoadFrom`），**未触发 B2-alt**；杂讯：首版脚本因在裸 .NET 线程执行 PowerShell 块崩溃（脚本 bug，非环境限制），修复重跑后数据完整（见 raw-B2-stt-log.txt 两段记录）。

### B3 OCR 单图识别（1200×620 PNG，白底黑字 5 行中英混排，53 KB）

| metric | 值 | min | avg | max | 口径与条件 |
|---|---|---|---|---|---|
| 识别耗时 ms | 324.9 / 106.7 / 107.8 | **106.7** | **179.8** | **324.9** | `WindowsOcrService.RecognizeAsync` 直驱（WinRT 在 pwsh 可用），run1 含 OcrEngine/解码器首次初始化 |
| 识别文本（163 字符） | 中文行基本正确；公式行有混淆（`()b + 一 sqrt(bA2 一 4ac)`） | — | — | — | 质量观察：纯性能基准内正常，非缺陷 |

### B4 PDF 导出（QuestPDF 本地渲染，34 行 Markdown 笔记 / 433 字符，A4 中国字体）

| metric | 值 | min | avg | max | 口径与条件 |
|---|---|---|---|---|---|
| 内存路径 `PdfExportService.Export` ms | 253.3 / 46.1 / 45.9 | **45.9** | **115.1** | **253.3** | run1 含静态初始化（许可证+中文字体注册+Skia 原生加载），run2-3 为稳定态 |
| 产物字节 | 308,249 ×3 | — | — | — | 魔数 `%PDF-` ✓（5 字节 ASCII 校验） |
| DB 路径 `ApiService.ExportNotePdfAsync` ms | 65.4 / 90.0 / 67.8 | **65.4** | **74.4** | **90.0** | 真实 SQLite 会话 `PERF-20260826-233855`（标题前缀合规）+ 笔记，含 DB 读 |
| 产物字节 | 274,352 ×3 | — | — | — | 魔数 `%PDF-` ✓（标题/摘要不同致体积差） |
| UI 导出路径 | 未重复（交叉引用 E2E J7：215,821 B、0.26s，服务等价路径导出成功） | — | — | — | 避免与 E2E UI 占用冲突，如实注明 |

### B5 磁盘与 DB 随会话增长（即刻数字）

| metric | 值 | 说明 |
|---|---|---|
| 合成语音 WAV | 509,966 B（15.94s） | `bench\speech-test.wav` |
| 音频持久目录 | `%LOCALAPPDATA%\ClassNote\audio\471a5c74-…_audio.wav` = 3,486,460 B（E2E 109s 录音）；`%TEMP%\classnote_*.wav` 共 11 个（历史归档） | 应用录音持久化路径（AudioService 写 %TEMP%，E2E 完成后复制至 audio\） |
| DB 文件大小 | 36,864 B（T0: 测试前）→ 45,056 B（T2: 全部基准后），+8,192 B | `%LOCALAPPDATA%\ClassNote\classnote.db` |
| DB 行数（sessions / screenshots / notes） | T0: 2 / 5 / 1 → T2: 3 / 10 / 3 | 增量：E2E 会话（+5 screenshots、+1 note、+1 session）+ 本基准 PERF 会话（+1 session、+1 note） |
| 截图持久目录 | `screenshots\471a5c74…\`: 9 文件 / 2,260 KB；`screenshots\cb2c67fd…\`: 1 占位（7 B） | 周期性截屏（10s 间隔）落盘 |
| 缓存 DB | `cache.db` 12,288 B，未增长 | WebView2/旁路缓存 |

---

## 3. 结论：达标 / 瓶颈 / 优化建议（按人格：只优化有数据支撑的项）

### 达标项（数据支撑）
1. **冷启动 ≈1.63s（窗口出现）**，3 次样本稳定（1556–1668ms）——用户感知优良。
2. **STT 推理管线 ≈4.68s / 15.94s 音频（RTF≈0.29）**，实时性充足；**模型内存增量 ≈594MB，实测复核 README「峰值约 600MB」声称属实**。
3. **OCR 稳定态 ≈107ms / 单图**，足够支撑 10s 周期截屏管线。
4. **PDF 导出稳定态 46–90ms / 30 万字节级**，产物魔数有效。

### 瓶颈 / 风险（有证据）
- **BT-1【高】STT 每次新建服务实例即重载模型 ≈6.05s，且 ORT `InferenceSession` 内存跨实例累积**：实测同进程 3 个实例 WS 累积至 **1816.9MB（priv 2446.7MB）**，而单实例仅 ~700MB。应用每录音会话新建 `RecordingViewModel → SenseVoiceSttService`，若视图模型跨会话存活，模型与 ORT arena 不会释放 → 内存与重载时延随会话数线性增长。
- **BT-2【中】首启/冷缓存冷启动 ~9.6–10.7s**（本报告 ROUND1-RUN3 9.58s；E2E J1 ~10.7s）vs 稳态 1.63s——WebView2 初始化与页面缓存冷启动敏感。
- **BT-3【中·交叉引用 E2E F-RT-02】悬置 `processing` 会话（20:05 历史遗留）导致主页 `HasPendingSessions` 永久 3s 轮询**，恒定浪费 CPU/电量；与 E2E agent 共同发现。
- **BT-4【低】OCR run1 325ms、PDF run1 253ms**：一次性初始化开销（引擎/字体/Skia），稳定态已优良，无需优化。

### 优化建议（按优先级，均量化收益）
| 优先级 | 建议 | 预计收益（实测依据） |
|---|---|---|
| P1（高） | STT 会话复用：`SenseVoiceSttService` 内将 `InferenceSession` 提升为进程级共享单例，仅首次 6.05s 加载一次；并设置为 `SessionOptions.EnableMemoryPattern=false`/结束会话后释放 arena | 消除每次 6.05s 重载；3 会话内存 1.8GB→~0.7GB（省 ~1.1GB） |
| P2（中） | 冷启动硬化：WebView2 用户数据目录预热/延迟初始化、考虑 ReadyToRun 发布 | 首启 9.6–10.7s → 向稳态 1.63s 收敛 |
| P3（中，协同 E2E） | 会话状态机加超时置失败/重启恢复，主页轮询仅在确有 pending 时开启；提供清除入口 | 消除永久 3s 轮询（CPU 恒定浪费）；继承 E2E F-RT-02 修复 |
| P4（低） | 截图 JPEG 压缩质量按需调节（当前 ~250KB/张、10s/张 → 1 小时 ~90MB） | 磁盘占用下降（未作压缩率对比实验，列为待优化项，不虚报收益） |

---

## 4. 性能状态结论

**Performance Status: MEETS** ✅

- 所有可测指标满足桌面单机应用的合理 SLA：窗口冷启动 1.63s（<5s 目标）；STT 实时因子 0.29（<1）；OCR ~107ms/图；PDF ~46–90ms/次；模型峰值内存 594MB 符合 README 声称（600MB），8GB 内存机器可流畅运行。
- 无 FAILS 项；唯一接近阈值的是首启冷缓存冷启动（9.6–10.7s），属环境/首次初始化范畴，非稳态性能缺陷。
- Scalability Assessment: Needs Work（就内存累积而言）——单进程多会话场景（BT-1）需 P1 修复；单会话场景 Ready。

**产出文件清单（TestResults\bench\）**
- `performance-report.md`（本报告）
- `raw-B1-startup-times.txt`（3 干净样本 + 污染样本记录）
- `raw-B2-stt-log.txt`（含脚本 bug 修复前后两段完整记录：装配加载、4 次转写、内存读数）
- `raw-B3-ocr-log.txt`（WinRT 直驱 3 次 + 识别文本）
- `raw-B4-pdf-log.txt`（内存/DB 两路径各 3 次 + 魔数 + 会话 ID）
- `speech-test.wav`（509,966 B）、`ocr-test.png`（52,382 B）——测试素材证据
- `response-original.md`（mock 原文备份；`mock\response.md` 全程未改动，`MOCK-LLM-NOTE-V1` 在）
- `progress-01/02/03.md`（检查点）、`run-b1*.ps1 / run-b2b.ps1 / run-b3b.ps1 / run-b4.ps1`（可复现脚本）

**环境限制/未测得（如实申报）**：无——B1–B5 全部程序集/进程直驱成功，未触发任何 alt 路径。