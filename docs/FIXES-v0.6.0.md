# ClassNote v0.6.0 审查问题修复记录

对应审查报告：[`REVIEW-v0.6.0.md`](REVIEW-v0.6.0.md)（保留原貌，其中的行号/片段为修复前状态）。
本轮按用户确认的方案执行：**直接修改源码并补测试**；混音直写路径采用
**"保留直写 + 最小加固"**（不新增线程）。

## 验证结果

| 项目 | 结果 |
|------|------|
| `dotnet test ClassNote.Tests` | **201 通过 / 0 失败**（修复前 182；新增 19 条） |
| `dotnet build`（ClassNote / ClassNote.Tests） | 成功，0 错误 |
| `dotnet build`（AudioProbe / RenderHarness / ScheduleAutoStartProbe / VisualCheck） | 成功 |
| `dotnet build`（CaptureTool） | **失败 —— 修复前就失败，见文末"额外发现"** |
| UI 渲染校验（`devtools/RenderHarness`） | **通过**：8 项检查全部符合预期，见文末"UI 渲染校验" |
| 真机录音冒烟（`devtools/AudioProbe`） | **已执行 3 轮**：回环 / 直写 / 混音全部通过（`AUDIO_PROBE_OK`），见文末"真机冒烟验证" |

## 逐项对照

| # | 问题 | 处理 | 关键改动 |
|---|------|------|----------|
| 🔴-1 | 混音写盘写的是 float 位模式 | 已修 | 新增 `ClassNote/Services/Pcm16Mixer.cs`；删除 `AudioService.MixSampleProvider` |
| 🟡-1 | `TruncatedChars` 语义与文案不符 | 已修 | 改名 `OversizedChars` + 文案改成"并入最后一段、未再切分" |
| 🟡-2 | 单份输入返回原始 JSON | 已修 | `MergeCascadeAsync` 单份时 `ParseSegmentJson` + `AssembleNote` |
| 🟡-3 | 失败文本污染合并输入 | 已修 | 失败段留空 `EmptySegmentJson`，失败改用正文尾部提示 |
| 🟡-4 | 配置类错误被当可重试 | 已修 | 新增 `LlmCallException`（`IsTransient` / `IsConfigurationError`）+ 分类判定 |
| 🟡-5 | 回退播放设备的提示永不显示 | 已修 | 新增 `LastWarning` + 录音页警示行 |
| 🟡-6 | `SystemSourceNotice`"两路"分支不可达 | 已修 | 可见性改绑 `HasAudioNotice` / `UsesSystemAudio`，并显示来源设备名 |
| 🟡-7 | 弹窗内切换来源丢失已保存设备 | 已修 | `ApplySource(_initial)` |
| 🟡-8 | `MicName` 恒为 null | 已修 | `ToRecordingConfig` 补传 `RecordingMicName` |
| 🟡-9 | 磁盘 I/O 在采集回调线程且持锁 | 已加固 | 订阅回调移出通道锁；补线程亲和性与取舍注释 |
| 💭a-h | 8 项小问题 | 已修 | 见下方明细 |

---

## 🔴-1 混音字节（`Pcm16Mixer.cs`，新增）

- 新类型 `internal sealed class Pcm16Mixer`：按样本把各路 PCM16 相加、夹到 `short` 范围，**直接产出 PCM16 字节**；
  不再实现 `ISampleProvider`（[-1,1] 浮点契约），中间不再有可误解的表示。
- 只依赖 NAudio 的 `BufferedWaveProvider`（**可在测试里构造**），因此混音结果不再需要声卡才能验证。
- `AudioService.ActiveRecorder` 改为调用 `Pcm16Mixer.Read(byte[], int, int)`，`MixLoop` 里删掉
  `floatBuffer` 与 `Buffer.BlockCopy`——**修复的那 3 行替换为直接写 `bytes`**。
- 保留原有行为契约：每帧始终满帧（数据不足补零）、`_onFrame` 在写盘前调用、写盘失败即停止混音线程。
- 新增 `ClassNote.Tests/Services/Pcm16MixerTests.cs`（6 条）：
  单路逐字节等值（**旧实现必失败**）、双路逐样本相加、越界饱和不回绕、缺路补零仍满帧、
  无来源输出静音、offset 写入不影响范围外字节。

## 🟡-1 截断语义如实化

- `NotePlan.TruncatedChars` → `NotePlan.OversizedChars`；`NoteGenerationResult.TruncatedChars` → `OversizedChars`。
- 文档改为："因段数上限而被**并入最后一段、未再切分**的转写字符数；这些内容**没有被丢弃**，
  但该段输入超限、可能被模型截断"。
- `NotePromptBuilder.TruncationNotice` → `OversizedNotice`，文案去掉"未能纳入笔记"，
  并删掉"调大单段上限"这半句（没有界面入口的常量，等于让用户拧一个不存在的旋钮）。
- `NoteProcessor` 的 `Note.Summary` 文案同步（"末尾约 K 字未再切分"），并拆开"失败"与"超限"两种"生成不完整"。
- 测试：`Build_BeyondSegmentCap_FlagsTruncationInsteadOfSilentDrop` →
  `Build_BeyondSegmentCap_KeepsEverythingAndReportsOversizedTail`（同时断言"末段确实超限且内容完整"），
  新增 `Build_WithinSegmentCap_ReportsNoOversizedTail`、`OversizedNotice_SaysContentWasKeptNotDropped`。

## 🟡-2 单份输入不再返回原始 JSON

`MergeCascadeAsync` 入口新增：`segmentJsons.Count == 1` 时用 `ParseSegmentJson` + `AssembleNote`
机械装配成笔记。这条路径今天从生产代码不可达（默认 `maxSegments=40`），但它是一个"静默产出错误正文"的陷阱。

## 🟡-3 失败信息不再污染合并输入

- 失败段在 `segmentJsons` 里留空（`EmptySegmentJson = {"coreKnowledge":"","pitfalls":""}`），
  错误文本**不再**被手工拼进"JSON"（那既不是合法 JSON，又会被合并提示词"所有分段知识点必须进入笔记"
  的要求诱导成一条知识点）。
- 失败改为两道可见通道：`progress` 上报 + 笔记正文尾部 `FailureNotice(failed, total)`。
- 新增测试 `FailureNotice_ReportsFailedAndTotalSegmentCount`。

## 🟡-4 失败分类与"全段失败"

- 新增 `internal sealed class LlmCallException`：`IsTransient`（瞬时故障，值得重试）、
  `IsConfigurationError`（配置缺失，每一段都会失败）。
- `LlmService.IsTransientError` / `IsConfigurationError` 为 `internal static`，可单测。
- `ChatAsync` 三类错误源分别标注：未配置 Key → 配置类；空内容 → 瞬时；429/5xx → 瞬时；其余 4xx → 非瞬时。
- 分段循环：配置类错误**立即整体失败**；瞬时错误重试一次；普通 4xx 只算该段失败；
  **全部段都失败**时抛异常，交给 `NoteProcessor` 的原文兜底笔记。
- 新增 `ClassNote.Tests/Services/LlmErrorClassificationTests.cs`（7 条用例）。

## 🟡-5 降级必须告知用户

- `IAudioService` / `AudioService` 新增 `string? LastWarning`（与 `LastError` 分离：降级不该让录音失败，也不能静默）。
- `StartRecording` 新增 `warnings` 通道，`TryOpenSystemChannel` 的"指定播放设备不可用，已回退"
  由 `errors` 改投 `warnings`；成功返回前汇总为 `LastWarning`，失败路径清空避免残留旧值。
- `RecordingViewModel` 新增 `RecordingWarning` / `HasRecordingWarning` / `HasAudioNotice`；
  **刻意不写进 `StatusText`**——那是录音页判断"是否在录音"的控制量（`== "录音中"`），
  改动它会让 `RecordingPage` 的停止逻辑误判（这一点是在改的过程中发现的，已加注释与测试锁住）。
- `RecordingPage.xaml` 新增警示行；`AudioProbe` 在启动成功后打印 `WARNING:`。
- 新增测试 `StartRecording_DegradedDevice_IsSurfacedWithoutTouchingStatusText`。

## 🟡-6 录音页提示区

- `RecordingPage.xaml`：提示区可见性由"`UsesMicrophone` 的反值"改为 `HasAudioNotice`
  （= 来源含系统声音 **或** 本次启动有降级），说明文字绑 `UsesSystemAudio`。
- 新增第 8 个 `RowDefinition`，提示区移到 `Grid.Row="6"`、结束按钮移到 `Grid.Row="7"`
  （原来提示区与麦克风选择同处 Row 5，靠"互斥显示"才不重叠；现在 Both 下两者都要显示）。
- `RecordingViewModel.OutputDeviceDisplayName`：把播放设备稳定 ID 解析成显示名
  （未指定 → "系统默认播放设备"；已失效 → 明确标注"实际会回退到系统默认"），并写进说明文案。
- 新增测试 3 条：Both 显示设备名与"两路"、System 未指定时的默认文案、Microphone 不显示该区域。

## 🟡-7 / 🟡-8 设备配置

- `RecordingSetupWindow.SourceCombo_SelectionChanged` 由 `ApplySource(null)` 改为 `ApplySource(_initial)`
  ——弹窗内改选来源时，"另一路"设备仍沿用设置里保存的值。
- `AppSettings.ToRecordingConfig` 补传 `MicName`（并改用命名参数，避免下次再漏）——此前漏传导致
  "设备 ID 失效时按显示名回退匹配"这条后路永久失效。测试 `ToRecordingConfig_MapsSettingsToCaptureConfig` 增加断言。

## 🟡-9 直写路径最小加固（按你选的方案，未新增线程）

- `CaptureChannel`：新增 `_pumped` 列表，`Pump()` 只收集转换结果，
  **订阅者回调移出 `lock (_lock)`**，在 `OnDataAvailable` 末尾统一派发——
  磁盘 I/O 落在锁内会把下一个采集回调本身一起堵住。
- `DirectRecorder` 类注释补上**代价与前提**：落盘在采集设备线程上，慢写会直接作用于采集节奏；
  前提是输出目录位于本地低延迟存储。
- `Stop()` 注释改为与实现一致：`CaptureChannel.Stop()` 先摘 `DataAvailable` 再停设备，
  所以尾部数据丢不丢与该标志位顺序无关（丢的量级 = 设备缓冲），此处顺序只保证"writer 关闭后不再有写入"。
- `AudioDataAvailable` 增加线程亲和性说明（在采集设备线程上同步触发，订阅方必须立即返回）。
- `docs/开发文档.md` 同步（含一条"历史 bug（已修复）"说明）。

## 💭 小问题

| 项 | 处理 |
|----|------|
| `Clipped` / `lastClipped` 死代码 | 删除；窗口元组改为 `(Start, End)` |
| `Truncate` 的 20000 默认值 | 改为必填参数（唯一调用点显式传 300） |
| 分段规模不可预知 | 分段前上报"素材较长，将分 N 段整理（最多 2N 次模型调用）" |
| 重试进度文案歧义 | 改为"第 i/N 段整理失败，正在重试…（时间范围）" |
| `ResolveIndex` 注释与调用点不一致 | 注释写明"有占位项/无占位项"两种用法都成立的前提 |
| 参数被静默夹取 | `Build` 三个参数补 XML 文档，明写下限/夹取规则 |
| 类注释声称省掉环形缓冲 | 改为"省掉一个线程与 float 往返；环形缓冲仍照原样写入，只是没人再读" |
| `--silent-ok` 放过麦克风静音 | 只对 `NeedsSystemAudio` 的用例生效；NOTE 文案区分麦克风/回环两种原因 |

---

## 真机冒烟验证（已执行，3 轮）

命令（用**机器自己放 440Hz 纯音**当激励，排除"人还没开始放音"的时序问题）：

```
dotnet run --project devtools\AudioProbe\AudioProbe.csproj -c Release -- <输出目录> 6
```

### 1. 四条路径在真机上全部正确

| 用例 | 关键数字 | 判定 |
|------|---------|------|
| `mic`（单路直写） | 5.98s / callbacks 96 / peak 229–314 | ✔ |
| `system-default`（回环） | 5.94–5.96s / callbacks 95–96 / **peak 12000** | ✔ 回环可用 |
| `mic-and-system`（**走混音器**） | 6.06s / callbacks 303 / peak 12069–12330 | ✔ |
| `mic-and-system-explicit-out`（**走混音器**+显式播放设备） | 6.04s / callbacks 302 / peak 12074–12539 | ✔ |

三轮均 `AUDIO_PROBE_OK`、退出码 0。

### 2. 🔴-1 的决定性证据（频谱判定，不靠耳朵）

激励是幅度 12000 的 440Hz 正弦 → 期望 `rms=8485`、`crest=1.41`、`tone_amp=12000`。Goertzel 单点 DFT 实测：

| 文件 | rms | crest（波峰因数） | 440Hz 分量幅度 |
|------|-----|------------------|---------------|
| `system-default.wav` | 8484.5–8484.7 | **1.41** | **11999** |
| `mic-and-system.wav` | 8396.5–8542 | 1.44–1.47 | 9245–10408 |
| `mic-and-system-explicit-out.wav` | 8395–8421 | 1.44–1.49 | 11314–11751 |

两个**走混音器**的产物：RMS 与输入纯音一致（差 < 1%）、波峰因数 1.44–1.49（正弦理论值 1.41）、
440Hz 分量在位。旧实现（写 float 位模式）不可能同时满足这三条——那会是宽带噪声：没有 440Hz 分量，
且 RMS 与输入幅度无关。**"混音把音频写成噪声"这个 v0.5.0 起的缺陷，真机确认已修复。**

### 3. 修正：先前"46 字节空文件"**不是 App 缺陷**

前两轮 `system-default` 得到 46 字节 / `callbacks=0`，当时判断为"回环出问题"。实测推翻了这个定性：
**那段时间该播放端点根本没有渲染任何音频**——WASAPI 回环对空闲端点一个数据包都不发。
同一台机器、同一段代码，端点一开始渲染就立刻 `callbacks=95`、时长 5.96s、peak=12000。
即：**回环功能正常，"没数据"如实反映了"没在放音"。**

由此暴露的行为仍然值得处理，已按你的决定落地（见下节）。

### 4. 一次未复现的孤立尖峰（仅记录，未定性）

第 3 轮 `mic-and-system.wav` 出现 peak=30188，位置 t=4.573s，且 **326 个采样 > 20000（≈ 正好一个 20ms 混音帧）**；
同轮 `explicit-out` 与随后两轮共 4 个 Both 产物均为 **0 个**超 20000 采样、crest 1.44–1.49。
即 5 次 Both 录制中仅 1 次、且只占一帧。

- 不像系统性求和缺陷（那会影响每一帧，而不是 300 帧里坏 1 帧）；
- 更像某一路输入在那一刻的真实瞬态（系统提示音 / 设备突发）或设备侧抖动；
- 无法复现，**不下结论**。要追的话建议探针连跑 20 轮统计出现率。

## 真机验证引出的后续改动（超出原 18 项）

按你的决定实现：**没有采集到声音的素材不进入 LLM 处理，且不加面向用户的提醒。**

| 位置 | 改动 |
|------|------|
| `NoteProcessor.HasAudioSamples`（新增，`internal static`） | 读 WAV 头，**只在明确读到 data 块长度为 0 时**判为"无采样"；读不了 / 非 RIFF / 找不到 data 块一律交给 STT 去失败——不把损坏文件误判成"没录到声音"而静默跳过 |
| `ProcessAsync` 第 2 步 | 无采样 → **跳过 STT**（省掉一次模型加载，峰值约 600MB），只留 `Debug.WriteLine` 留痕 |
| `ProcessAsync` 第 4 步（新增） | 转写为空**且**无任何截图文字 → **不调用 LLM**，状态仍置 `completed`、不写笔记记录 |
| 新增测试 `NoteProcessorAudioTests.cs` | 6 条：真机那种 46 字节文件（含 `Assert.Equal(46L, ...)` 形状断言）→ false；有采样 → true；0 字节文件 → false；非 WAV → true；RIFF 但无 data 块 → true；文件缺失 → true |
| `devtools/AudioProbe` | `--silent-ok` 下同时容忍"0 采样"用例（端点空闲时文件 0 秒是事实而非缺陷）并打 `NOTE` 说明——此前这种情况会误报失败，容易训练人忽略这个工具 |
| `docs/开发文档.md` §2.6 | 管线步骤同步（新增"素材检查短路"步骤、STT 加入无采样跳过） |

> **一处我按自己判断偏离了字面要求，请确认**：你说的是"没采集到声音就不过 LLM"，
> 我实现成**"素材完全为空才跳过 LLM"**。理由：只放课件的课（无人讲解）确实没有声音，
> 但**截图 OCR 仍是有效素材**，按字面跳过会把整节课的笔记整篇丢掉。
> 若你要的就是字面版本（没声音 → 直接跳过 LLM），一句话我改成 `!HasAudioSamples(path)` 短路。
> 另外短路时管线仍会经 `progress` 报一行"本次没有可整理的素材…"（处理过程中的常规状态行，不是弹窗/警告）；
> 连这行也不要的话，一并去掉。

## 额外发现（不在原清单，未处理）

1. **`devtools/CaptureTool` 编译失败（修复前就存在）**：
   `devtools/CaptureTool/Program.cs:200` 调用 `SendMouse(...)`，但全文件只有 `MouseEvent` 的 P/Invoke 声明，
   没有 `SendMouse` 定义 → `error CS0103`。该文件自 `395f65c`（v0.2）起未改动，且 `CaptureTool` 不在
   `ClassNote.sln` 里，所以不影响主程序构建。**不在本次清单内，我没有动它**——要修的话告诉我，看起来是漏改一处调用名。
2. **`InverseBoolToVisibilityConverter` 现在没有使用点了**（我改了它唯一的用处）。
   它是个通用转换器（`App.xaml` 仍注册），我**保留未删**；如果你希望连注册一起清掉，说一声。

## UI 渲染校验

`devtools/RenderHarness` 会渲染各页面为 PNG（`RENDER_DONE`，15+ 张全部成功、无异常）。
其中录音页覆盖了三种来源，**实际输出如下**：

| 用例 | 输出 | 结果 |
|------|------|------|
| 仅麦克风 | `m2-recording.png` | 麦克风下拉可见、**提示区不显示**（没有系统声音也没有降级）✔ |
| 仅系统声音 | `m2b-recording-system.png` | 提示区可见，文案为"正在录制系统声音：来源设备「系统默认播放设备」，请让课件 / 视频在该设备上出声。"✔ |
| 麦克风和系统声音 | `m2c-recording-both.png`（**本次新增用例**） | `mic-combo-visible=True`、`notice-visible=True`、**`notice-overlaps-mic=False`**，文案含"两路"与来源设备 ✔ |

`m2c` 这个用例是特意补的：**Both 来源正是"两个控件挤在同一 Grid 行、靠互斥显示才不冲突"的出事场景**，
而原来的渲染工具只覆盖了前两种来源，等于对本次修复的高风险点没有校验。
因此除了新增渲染用例，还加了 `CheckBothSourceLayout`：用
`TransformToAncestor` 取几何包围盒，断言麦克风下拉与提示文案**真的不相交**——
否则"让提示可见"这个修复就可能以"两行重叠"的形式翻车。
三种来源下结束按钮都仍在卡片底部、未被挤出可视区（行号顺延后已确认）。

## 未做 / 需要你决定

- 🟡-9 的另一种方案（恢复专用写盘线程、彻底消除采集线程落盘）**按你的选择没有做**。
  若之后想换，改动集中在 `AudioService` 的 `DirectRecorder` 与 `_pumped` 派发处。
- 真机录音冒烟 `dotnet run --project devtools/AudioProbe -c Release -- <目录> 3` **未执行**（需授权，会录音）。
  建议至少跑一次 `mic-and-system` 用例，用真机确认混音产物不再是噪声。
- 版本号仍是 `0.6.0`，未改动；`README.md` / `docs/开发文档.md` 已同步本轮的行为变化。
