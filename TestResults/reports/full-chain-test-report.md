# ClassNote 全链路业务测试报告（最终汇总）

- **目标**：使用 [msitarzewski/agency-agents](https://github.com/msitarzewski/agency-agents) `testing` 部门的 5 个测试 agent，对 ClassNote 课堂笔记助手（WPF/.NET 8 单机应用）执行端到端全链路业务测试。
- **测试日期**：2026-08-26 ~ 2026-08-27（本地真实环境：Win10 19045、.NET 8.0.423、真实麦克风、真实截屏、真实 ONNX STT、真实 WinRT OCR；LLM 以 Mock OpenAI 兼容服务器（127.0.0.1:18888）可控注入）
- **测试会话语料**：E2E-0826232252（数学，录音 ~109s，9 张截图，转写+笔记+PDF 全链路）

## Agent 阵容与分工

| Agent（人格文件） | 职责 | 交付 |
|---|---|---|
| 🎭 Test Automation Engineer（testing-test-automation-engineer） | E2E 8 条真实 UI 业务旅程 | `e2e/plan.md`、`e2e/e2e-test-results.md`、证据（截图/UIA/DB/日志） |
| 🔌 API Tester（testing-api-tester） | LLM OpenAI 兼容集成（功能/安全/性能/文档，F1-F10+S1-S5） | `api/api-test-report.md`、case-01~11.ps1、evidence-requests.jsonl |
| ⏱️ Performance Benchmarker（testing-performance-benchmarker） | 冷启动/STT/OCR/PDF 基线（B1-B5） | `bench/performance-report.md`、raw-*.txt、speech-test.wav |
| 📸 Evidence Collector（testing-evidence-collector） | 证据真实性核查 + 缺陷清单（EV-01~09） | `evidence/evidence-report.md`、re-query.txt |
| 🧐 Reality Checker（testing-reality-checker） | README C1-C14 逐条认证 + 生产就绪度 | `reports/reality-check-report.md`、`reports/claims-checklist.md` |

## 一、业务链路实测结果（真实应用跑通）

**J1 启动 ✓ → J2 设置 LLM ✓ → J3 开始记录（真麦克风"录音中"+自动截屏）✓ → J4 结束→管线 42s（STT→OCR→LLM）✓ → J5 产物核查 ✓（音频 3.4MB WAV 落盘、9 张截图、笔记入库、Mock 请求留痕）→ J8 持久化 ✓（重启后会话/设置在）**
- J6 笔记页（WebView2）与 J7 导出 PDF：业务逻辑经**与 UI 按钮完全相同的代码路径**验证（Markdig 渲染 HTML 正确；PDF 215,821B、%PDF-1.7、0.26s、存桌面）；UI 双击手势在本环境无法用合成输入触发（输入注入限制，非应用缺陷）。
- 管线耗时：冷启动稳态 1.63s / 冷缓存 9.6-10.7s；结束→completed 42s（含 STT 模型加载 ~33s）；PDF 0.26s。

## 二、性能基线（B1-B5，全部真实测量，3 次取 min/avg/max）

| 项 | 结果 | 结论 |
|---|---|---|
| 冷启动（UIA 主窗口） | 1556 / 1631 / 1668 ms（稳态）；冷缓存首启 9.58s | MEETS |
| STT（15.94s 中文合成语音） | 冷调用（含模型加载）10.3-11.1s；温 4.68s；模型加载≈6.05s；RTF≈0.29 | MEETS |
| STT 内存峰值 | WS +594MB（98→693MB）——**实测复核 README "~600MB" 属实** | ✅ |
| OCR 单图 | 106.7 / 179.8 / 324.9 ms（含引擎初始化） | MEETS |
| PDF 导出 | 45.9-253.3 ms，%PDF 有效 | MEETS |

## 三、关键缺陷（跨 4 份报告去重后）

### 🔴 P0（新增·高隐蔽）
- **OCR→LLM 提示词断裂**：`NoteProcessor.FormatTime` 用未转义冒号的 TimeSpan 格式串 `hh:mm:ss`，.NET 8 每次调用抛 FormatException，被逐张截图 try/catch **静默吞掉** → LLM 笔记/思维导图**从不包含截图 OCR 内容**（但 ocr_text 先入库，表面"正常"）。三层证据 + 两次受控复现，详见 `reports/ocr-rootcause-finding.md`。修复：改 `hh\:mm\:ss` 或 `"c"` + 截图失败打日志。

### 🟠 P1
- **会话终态错乱**：`end_time/duration` 恒 NULL（EndSession 只写状态，`LocalRepository.EndSession` 全仓零调用=死代码）；`ended` 被 `HasPendingSessions` 计为 pending → 历史遗留会话（"全链路测试会话"，ended）致主页**永久 3s 轮询** + 状态显示"处理中"误导。
- **LLM 输出无校验**：200 但空 `choices`→空笔记静默入库；200 但垃圾内容→原样保存（异常兜底覆盖不到"返回了但没意义"）。
- **错误响应体 300 字符透传**进本地异常与 `note.Summary`（500 实验实证 BODY_EMBEDDED）。

### 🟡 P2
- **API Key 明文存储** settings.json（建议 DPAPI/Credential Manager）。
- **无重试/退避**，单一 5 分钟读超时；瞬时 5xx 直接丢 AI 整理能力。
- **备用 LLM Key/地址 = 死配置**（持久化但零引用、UI 无入口）＋ **HttpClientProvider = 死代码**（文档声称 vs 实现不符两处，开发文档.md:137/194）。
- **思维导图无 UI 入口**：数据级能力存在（F8 全 PASS、DB 有字段），但笔记页无任何展示/交互。

### 🟢 P3
- 截屏"pHash"为 8x8 均值哈希简化实现；**首帧必丢**（ts 从 19s 起）；"批注/新幻灯片/视频"三分类在真实场景仅 new_slide 触发，智能分类无实证。
- MathJax 依赖 CDN → "全程离线/开箱即用"表述夸大（其余功能离线成立）。
- 无删除/重试会话入口；状态含义（ended→处理中）误导。

## 四、README 声称认证（Reality Checker）
✅ 属实：数据本地化(C2)、16kHz 录音(C3)、离线 STT 推理(C5 离线部分)、PDF 导出(C8)、设置入口(C9)、240MB 随包(C10)、~600MB 内存(C11)、Windows 专用(C14)。
⚠️ 部分：开箱即用(C1，需 Key+WebView2+CDN)、pHash 智能分类(C4)、中文识别"准"(C5，仅合成样本证据)、OCR 全链路(C6，识别可用但内容不入笔记)、思维导图(C7，无 UI)、备用配置(C12，死配置)、Markdig+WebView2(C13，渲染样本正确、实时 UI 链缺 UIA 证据)。

## 五、最终结论
- **质量评级：B-**（API 层 10/10 runtime 全过、性能 MEETS、核心链路真实跑通；受 OCR 链路断裂、终态错乱、LLM 输出校验缺失、安全配置等拖累）
- **生产就绪度：NEEDS WORK**（Reality Checker 默认判定，需 ~2 轮返工）
- **必改前三**：① 修复 FormatTime + 截图失败日志（P0）；② 会话状态机/轮询治理（ended 终态 + 超时置失败 + 删除入口）；③ LLM 输出校验 + 错误体收敛。

## 六、交付物索引（TestResults\）
- 报告：`e2e/e2e-test-results.md`、`api/api-test-report.md`、`bench/performance-report.md`、`evidence/evidence-report.md`、`reports/reality-check-report.md`、`reports/claims-checklist.md`、`reports/ocr-rootcause-finding.md`、`reports/static-verification-notes.md`
- 证据：e2e/*.png·*.txt·*.html、mock/requests.jsonl（54 条含 E2E 会话）、api/case-*.ps1、bench/raw-*.txt、speech-test.wav、Desktop/E2E-0826232252.pdf、evidence/bin-original/（原始 dll 备份）
- 环境限制记录：ClassNote.Tests 因 NuGet 网络不可用无法运行（非应用缺陷）；UI 双击手势无法自动化注入；当前模型无图像能力（证据以 UIA/DB/日志/字节为主）。