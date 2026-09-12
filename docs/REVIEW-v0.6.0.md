# ClassNote v0.6.0 代码审查报告

> **状态：本次审查的 18 项问题已全部修复**，逐项对照表见 [`FIXES-v0.6.0.md`](FIXES-v0.6.0.md)。
> 本文保留原貌作为审查记录——其中的行号与代码片段对应的是**修复前**的状态。

- **审查对象**：工作区未提交改动（`git diff`，17 个文件 / +696 −150）+ 4 个未跟踪新文件
  - `ClassNote/Services/NotePromptBuilder.cs`（新增，256 行）
  - `ClassNote/Services/NoteSegmenter.cs`（新增，278 行）
  - `ClassNote.Tests/Services/NotePromptBuilderTests.cs`、`NoteSegmenterTests.cs`（新增）
- **提交基线**：`79251a8`（v0.5.0）
- **严格程度**：以"可发布"为门槛
- **已执行的验证**
  - `dotnet test ClassNote.Tests/ClassNote.Tests.csproj -c Debug` → **182 通过 / 0 失败**
  - `dotnet build devtools/AudioProbe/AudioProbe.csproj` → **0 警告 0 错误**
  - 针对混音字节处理写了独立最小复现程序（见 🔴-1）
  - 反射检查 `NAudio.Core.dll` 的 `WaveFileWriter` 字段，确认无大块写缓冲
  - **未执行**真机录音（`AudioProbe` 会采集本机环境声音，未获授权；且这台机器是否有声卡/是否有声未知）。因此 🟡-5、🟡-9 属静态推断，结论依据已在条目内标注。

---

## 总结

### 总体印象

这是一次**方向正确、工程素养明显在线**的重构。把"素材超限直接 `Truncate()` 静默丢弃"换成"分段整理 → 逐级合并 → 失败降级为机械拼接"，是这一版真正的价值所在；而且降级链设计得很稳：段内失败不牵连其它段，合并失败退回 `AssembleNote`，轮次守卫触发返回 `null` 而不是"只交回一份"。提示词被抽成 `NotePromptBuilder` 纯函数并用 `InternalsVisibleTo` 暴露给测试，测试锁的也是真正会退化的约束（"三处提示词都必须带保真规则"），而不是文案字面量——这个测试品位值得表扬。

### 主要顾虑（按重要性）

1. **混音路径写盘写的是 float 的位模式，不是 PCM16**（🔴-1）。"麦克风和系统声音"这一版被改名、被主推，但它录出来的 WAV 是噪声。这是先于本次改动引入的问题（`git log -S` 指向 `79251a8` / v0.5.0），但本版把它当作新特性包装并与"来源路由优化"一起交付，因此必须先修再发。
2. **单路直写把磁盘 I/O 搬进了采集回调线程**（🟡-9）。省掉一个混音线程的代价，是让 WASAPI 采集线程直接承受文件写入，并且是在持有通道锁的情况下。这是一个真实的取舍，需要作者确认接受。
3. **分段生成的"失败/截断"语义与用户看到的文案不一致**（🟡-1、🟡-3、🟡-4）。`TruncatedChars` 报告"未纳入"但内容实际被全部塞进超限窗口；分段失败的错误文本会被当作知识点混进合并输入；配置类错误（没填 API Key）会被当成可重试错误，导致长录音产出一篇满是错误占位符的笔记并置为 `completed`。
4. **几处"注释承诺了、代码没做到"**（🟡-5、🟡-6）。回退播放设备的提示永远不会显示；`SystemSourceNotice` 新增的"两路"分支在当前 XAML 下不可达。这类不一致比缺功能更危险——下一个改这里的人会相信注释。

### 优点

- 分段 + 逐级合并 + 双降级（机械拼接 / `AssembleNote`）的失败模型，比"抛异常让整节课重跑"或"丢后半节课"都好一个量级。
- `NotePromptBuilder` 把"保真口径"集中为 `VerbatimRules` 并被三处 prompt 共享，从设计上排除了"分段把保真要求稀释掉"这个最可能出问题的点。
- `NoteSegmenter.ParseOcrEntries` 修复了"页码跨幻灯片污染"，实现（`pendingNumeric` + 只在遇到下一条正文或条目结束时 flush）和测试（`ParseOcrEntries_TrailingPageNumber_StaysWithItsOwnSlide`）是配套的，不是补丁式修法。
- `AudioProbe` 顺手修掉了旧版事件订阅累积的 bug（`+=` 从不 `-=`，`frames` 计数从第二轮起偏大），并把时长容差从 `1.0s` 收紧到 `0.3s`——这个改动让冒烟工具从"基本没用"变成"有分辨力"。
- `AudioSourceKinds.DisplayNames` 的"文案即契约"测试 + "顺序错位会静默录错来源"的注释，是对真实事故模式（`SelectedIndex` 直映枚举）的正确防御。
- `ParseSegmentJson` 区分"合法 JSON 但字段为空（`{}`）"与"根本不是 JSON"，并明确不回退——边界处理很细。
- 版本号三处 + README 已同步到 `0.6.0`，README 自己点出的"易漏点"这次没有漏。

---

## 🔴 阻塞问题

### 🔴 **Correctness: 两路合成写盘的是 float 位模式，混音录音被录成噪声**

`ClassNote/Services/AudioService.cs:766`（配合 `:644-648`）

```csharp
// MixSampleProvider.Read（644-648）：按 ISampleProvider 契约产出 [-1,1] 浮点
for (int i = 0; i < count; i++)
{
    int s = Math.Clamp(accumulator[i], short.MinValue, short.MaxValue);
    buffer[offset + i] = s / 32768f;
}

// MixLoop（766-768）：把浮点缓冲的前一半**字节**当成 PCM16 写盘
Buffer.BlockCopy(floatBuffer, 0, byteBuffer, 0, samples * 2);
_onFrame(byteBuffer);
_writer.Write(byteBuffer, 0, samples * 2);
```

**Why：**

`_mixerSource` 是 `ISampleProvider`，其 `Read(float[], …)` 按契约返回 [-1,1] 的浮点采样，值为 `int16 / 32768f`。`Buffer.BlockCopy(floatBuffer, 0, byteBuffer, 0, samples * 2)` 拷贝的是 `samples * 2` **字节**，即 IEEE-754 位模式（而且是前 `samples/2` 个 float 的完整 4 字节），随后被直接当 int16 PCM 写进 WAV。结果是每 4 字节浮点被切成两个 PCM16 采样点，波形与原始音频无关——**不是"音质差"，是完全不同的信号**。

我在隔离环境中复现了这 4 行的行为（正弦输入、int16 → float → BlockCopy）：

```
bytes differing from true PCM16: 636 / 640
first 16 bytes written : 00-00-00-00-00-E0-56-3D-00-B0-D3-3D-00-D0-1A-3E
first 16 bytes true PCM: 00-00-B7-06-3B-0D-5A-13-E6-18-B4-1D-9F-21-8A-24
```

只有第 0 个采样（值 0.0f）偶然相等。**触发条件**：`RecordingConfig.Source == AudioSourceKind.Both`（`channels.Count == 2` → `ActiveRecorder` → `MixLoop`），即 v0.6.0 主推的"麦克风和系统声音"。`Microphone` / `System` 走本次新增的 `DirectRecorder` 直写路径，不经此处，所以问题被"单路优化"掩盖了一部分。

**归类说明**：这不是本次 diff 引入的（`git log -S "Buffer.BlockCopy(floatBuffer"` → `79251a8`，即 v0.5.0），但它是本次改动的直接风险面：本版改名并推广了这条路径，且 README v0.6.0 条目把它写成卖点。所以按"阻塞"处理。

**Suggestion：**

把"混音结果"从浮点契约改成 int16 契约，或显式转换后再写。最省事的做法是让 `MixSampleProvider` 不再冒充 `ISampleProvider`：

```csharp
// 让混音器直接产出 int16 字节，避免 float 中转
private int ReadPcm16(byte[] dest, int offset, int countSamples) { … }  // 复用 632-648 的累加逻辑
```

或者保留 `ISampleProvider` 但正确转换：

```csharp
for (int i = 0; i < samples; i++)
{
    short v = (short)Math.Clamp((int)Math.Round(floatBuffer[i] * 32768f), short.MinValue, short.MaxValue);
    byteBuffer[i * 2] = (byte)(v & 0xFF);
    byteBuffer[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
}
```

顺带建议补一条**不需要声卡**的回归测试：把 `MixSampleProvider` / 混音写盘逻辑变成可测的纯函数，喂已知正弦，断言输出的 int16 字节序列与输入一致（这件事现在做不了，正是因为混音逻辑藏在 `private` 内部类里——这本身也说明它缺一个可测边界）。

也建议加强 `devtools/AudioProbe/Program.cs`：它当前只校验 `format / length / peak > 0`，而**噪声的 peak 同样 > 0**，所以这个工具对上述缺陷完全无感。对 `mic-and-system` 用例至少加一条"与 `mic` 用例的 RMS 量级相近"或"能量集中在语音频段"的粗判。

---

## 🟡 建议问题

### 🟡 **Correctness: `TruncatedChars` 报告"未能纳入"，但末段实际"吃满到底"**

`ClassNote/Services/NoteSegmenter.cs:105-107`、`:118-119`、`:157`；`ClassNote/Services/NotePromptBuilder.cs:244-246`；`ClassNote.Tests/Services/NoteSegmenterTests.cs:113-124`

**Why：**

两处实现互相矛盾：

- `:118` `int end = isLast ? transcript.Length : …` —— 最后一段**无条件吃掉剩余全部内容**，注释也明说"宁可单段超限，也不静默丢内容"。
- `:105-107` 却按 `transcript.Length - segCount * stride` 计算 `TruncatedChars`，其文档（`:14`）写的是"**未能进入任何窗口**的转写字符数"。

现有测试把这个矛盾一起固化了：`Build_BeyondSegmentCap_FlagsTruncationInsteadOfSilentDrop` 同时断言 `TruncatedChars == 50000 - 2*5600` **和** `Segments[^1].EndChar == 50000`（即末段确实包含了那 38800 字）。于是用户在笔记信息里看到"约 38800 字未纳入笔记"（`NoteProcessor.cs:106`）与正文里的警告块（`LlmService.cs:149-152`），而实际上这些内容**全部被送进了那一次超限调用**。真实风险是相反的：那个 38800 字的窗口很可能超过模型输入上限，片段会失败或静默截断，此时报告的数字"碰巧"是对的，但报告的理由是错的。两种情况被同一个字段名糊在一起了。

**Suggestion：**

二选一，明确语义：

- 想让这个数字真的表示"丢弃"：把末段也按 `maxCharsPerSegment` 截断（`:118`），此时字段名、文档、用户文案都成立，但要接受真正的丢内容；
- 想坚持"不丢内容"：把字段改名为 `OversizedChars` / `UnsplitChars` 之类，文档与 `TruncationNotice` 文案同步改成"约 N 字超出单段上限、未再切分（该段可能被模型截断）"。

另外 `TruncationNotice`（`NotePromptBuilder.cs:244-246`）建议用户"调大单段上限后重新生成"，但 `NoteSegmenter.Build` 的参数写死在 `LlmService.cs:76`（全用默认值），**界面上没有任何地方能调**——这是让用户去做做不到的事，要么暴露设置项，要么删掉这半句。

### 🟡 **Correctness（潜在错误输出）: `MergeCascadeAsync` 在只有 1 份输入时原样返回分段 JSON**

`ClassNote/Services/LlmService.cs:173`、`:204`

**Why：**

`while (current.Count > 1)` 在 `current.Count == 1` 时直接跳过，`:204` 返回 `current[0]`，也就是那一段的**原始 JSON 字符串**。调用链上没有清洗：`:138` 的 `CleanMarkdown` 只剥 Markdown 代码块，不解析 JSON。于是"1 段但 `IsSegmented == true`"这一组合会让最终笔记正文变成 `{"coreKnowledge":"…","pitfalls":"…"}`，而 `NoteProcessor.IsPlausibleNote`（长度 ≥ 100 或含 `#`，通常都成立）会放行并落库。

今天从生产代码**不可达**（`NoteSegmenter.Build` 用默认 `maxSegments: 40`，此时 `Segments.Count == 1` ⟺ `needed <= 1` ⟺ `IsSegmented == false`），但 `NoteSegmenter.Build` 是带参数的 public API，`LlmService.cs:137` 的 `total > 1 ? … : (null, null)` 也说明作者已经意识到 `total == 1` 会出现。这是一个"静默产出错误正文"的陷阱，值得堵掉。

**Suggestion：**

在 `MergeCascadeAsync` 入口显式处理单份输入，别让它流到"返回原文"：

```csharp
if (current.Count == 1)
{
    var (core, pitfalls) = NotePromptBuilder.ParseSegmentJson(current[0]);
    return NotePromptBuilder.AssembleNote(course, new[] { (core, pitfalls) });
}
```

（或反过来：把 `GenerateNoteAsync:78` 的入口条件改成只看 `plan.Segments.Count`，并让 `IsSegmented` 只承担展示语义。）

### 🟡 **Robustness: 分段失败的错误文本被手工拼进 JSON，并进入合并输入**

`ClassNote/Services/LlmService.cs:122-123`（配合 `:445`、`NotePromptBuilder.cs:40`）

```csharp
segmentJsons.Add($"{{\"coreKnowledge\":\"（第 {i + 1} 段整理失败：{ShortError(retryEx.Message)}）\",\"pitfalls\":\"\"}}");
```

**Why：**

两个问题叠在一起：

1. **手工拼 JSON**。`ShortError` 的输入来自 `:445` 的 `"LLM API 调用失败 (" + status + "): " + Truncate(body, 300)`，而 `body` 是服务端返回的 JSON 错误体，天然含 `"` 和 `{`。插值后得到的是非法 JSON。合并阶段只是把字符串贴进 prompt（不解析），所以不会崩，但模型收到的是坏掉的 JSON。
2. **错误文本会被当成知识点**。`MergeSystemPrompt`（`NotePromptBuilder.cs:40`）明确要求"所有分段中的核心知识点都必须进入最终笔记、不得整体省略"，模型很可能把"（第 3 段整理失败：LLM API 调用失败 (500): …）"**当作一条知识点抄进用户笔记**。这不是理论风险——占位符的写法（句首括号 + 冒号）与模型眼中的"列表项"非常接近。

**Suggestion：**

不要把手写字符串拼成 JSON：用 `JsonConvert.SerializeObject(new { coreKnowledge = $"（第 {i+1} 段整理失败：{msg}）", pitfalls = "" })`。更彻底的做法是**让失败信息根本不进合并输入**——失败段的正文留空，把"第 N 段失败"作为独立元数据（`failedSegmentIndexes`）在合并之后追加提示，正如 `AssembleNote` 的降级路径已经在做的事。

### 🟡 **Error handling: 配置类错误被当成可重试错误，长录音会产出满是错误占位符的笔记**

`ClassNote/Services/LlmService.cs:108-125`

**Why：**

`catch (Exception)` 无条件重试一次。对"尚未配置 LLM API Key"（`:375` 抛 `InvalidOperationException`）这类**重试一万次也不会变**的错误，长录音会为每一段发 2 次注定失败的请求，然后把 `failures == total` 的笔记置为 `completed`，正文是 `（第 1 段整理失败：尚未配置 LLM API Key…）` 的堆叠。对比之下，短录音（单段路径 `:78-87`）异常会向上抛，由 `NoteProcessor.cs:114-119` 给出干净提示 + 原文兜底笔记。**同一个错误，长录音和短录音的用户体验完全不同**。

**Suggestion：**

只对瞬时错误重试，配置/参数类错误直接向上抛：

```csharp
catch (Exception ex) when (IsTransient(ex))   // 超时 / 网络 / 5xx / 429
{ … 重试 … }
```

`IsTransient` 至少排除 `InvalidOperationException`（未配置 / 4xx 业务错误）。同时把"全部段失败"视为整体失败，让 `NoteProcessor` 走它的兜底分支，而不是产出一篇只含错误文本的"完成"笔记。

### 🟡 **Correctness: "已回退系统默认播放设备"这条提示永远不会到达用户**

`ClassNote/Services/AudioService.cs:406-410`（配合 `:152`、`:229-238`）

**Why：**

`:408` 往 `errors` 里写入回退提示，但 `TryOpenSystemChannel` 随后**成功返回 `true`**（`:413-422`），`StartRecording` 也返回 `true`。而 `errors` 唯一的出口是 `BuildError`（`:241-251`），只在**失败**路径（`Fail`）里被调用；成功路径上 `_lastError` 保持 `:152` 设的 `null`。所以这条提示 100% 被丢弃。

它偏偏是这次刻意加上的：方法注释 `:392` 写"显式指定优先（失效则回退并**明确告知**，避免'录到别的设备却以为录到了'）"。**代码没有实现它承诺的告知**——用户配了 A 设备、实际录的是 B 设备，且不会有任何提示，正是注释要防的那个场景。

**Suggestion：**

给 `IAudioService` 加一个"成功但有降级"的独立通道（`string? LastWarning { get; }` 或 `event Action<string> Warning`），并在 `RecordingViewModel.StartRecordingAsync` 里把它显示出来；退而求其次，至少 `Debug.WriteLine` + 让 `RecordingSetupWindow` 提前校验设备可用性（弹窗里就能发现选中设备已失效）。

### 🟡 **View 层: `SystemSourceNotice` 的"两路"分支在当前 XAML 下不可达（新增的死代码）**

`ClassNote/ViewModels/RecordingViewModel.cs:94-96`；`ClassNote/Views/RecordingPage.xaml:170`、`:177`

**Why：**

新增的属性注释写着"'麦克风和系统声音'来源下两路都在录，说明也要跟着变"，但承载它的 `Border`（`RecordingPage.xaml:169-179`）可见性绑定的是 `UsesMicrophone` 的**反值**：

```xml
Visibility="{Binding UsesMicrophone, Converter={StaticResource InverseBoolToVisibilityConverter}}"
```

而 `UsesMicrophone => _config.NeedsMicrophone`，对 `Both` 为 **true**（`AudioConfig.cs:36`）→ 该 Border 隐藏 → `SystemSourceNotice` 的三元表达式**永远只走第二个分支**。全仓 grep 确认该属性仅此一处绑定，所以第一分支是纯粹的死代码，注释也与实际行为相反。

副作用：`Both` 来源下录音页只显示麦克风选择（`:161`），**完全不显示当前在用哪个播放设备**——而"选错播放设备 → 回环录成静音"正是这一版要杀的问题。

**Suggestion：**

把可见性改为"来源需要系统声音时显示"（`UsesSystemAudio`），此时 `Both` 与 `System` 都会显示，而且 `SystemSourceNotice` 已经备好了两套正确文案；顺便在同一区域加一行 `SelectedOutputDevice` 显示/选择。若确定不显示，就删掉不可达分支，别留下与注释相反的代码。

### 🟡 **Consistency: 弹窗内切换来源时会丢掉"设置"里已保存的播放设备**

`ClassNote/Views/RecordingSetupWindow.xaml.cs:65`

**Why：**

```csharp
private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (_suppressSourceChanged) return;
    ApplySource(null);          // ← 传 null，而不是 _initial
}
```

`ApplySource` 的预设逻辑（`:86-88`）依赖第二个参数。构造时传的是 `initial`（`:55`），所以"设置里已经是 Both/System"时能正确预选播放设备；但用户**在弹窗里**把来源改成"麦克风和系统声音"时，`initial` 传的是 `null` → `ResolveIndex` 返回 0 → 下拉框落到第 0 个设备（而非设置里保存的那个）。触发链正好命中本版要修的场景：用户没在弹窗里重选 → 回环落到系统默认设备 → 录成静音。而 `_initial` 字段（`:19-20`，注释"用于保留用户未在本次弹窗中改动的那一路设备"）就是为这个目的存的。

**Suggestion：**

`ApplySource(_initial);`（一处改动）。另可在 `ApplySource` 里对 `initial == null` 的情形也用 `_initial` 兜底，避免下次有人再传 `null`。

### 🟡 **Dead feature: `MicName` 恒为 `null`，整条"按名称回退匹配设备"路径不可达**

`ClassNote/Services/AppSettings.cs:114-117`；`ClassNote/Services/AudioConfig.cs:23-25`；`ClassNote/ViewModels/RecordingViewModel.cs:59-67`；`ClassNote/Views/RecordingSetupWindow.xaml.cs:87-88`、`:156-157`

**Why：**

`ToRecordingConfig` 用**位置参数**只传了 3 个：

```csharp
public static RecordingConfig ToRecordingConfig(AppSettingsData settings) => new(
    AudioSourceKinds.FromStorage(settings.RecordingSource),
    string.IsNullOrWhiteSpace(settings.RecordingMicId) ? null : settings.RecordingMicId,
    string.IsNullOrWhiteSpace(settings.RecordingOutputDeviceId) ? null : settings.RecordingOutputDeviceId);
```

`MicName` 没传 → 恒为 `null`。而 `RecordingMicName` 是被持久化、并在设置界面展示的字段（`AppSettings.cs:45`）。结果是：`RecordingViewModel:59-67` 的"ID 失效时按显示名回退"分支、`AudioConfig.cs:23-25` 的文档承诺、以及**本次新增**的 `MicName: _initial?.MicName`（`RecordingSetupWindow.xaml.cs:157`）全部是死路径。手动录音（`MainPage.xaml.cs:131`）与定时录音（`MainWindow.xaml.cs:153`）都走 `ToRecordingConfig`，**两处都是 null**。

后果：设备 ID 失效（USB 麦克风换口/重装驱动、MME 回退设备的 `mme:{index}` 漂移）时无法按名称兜底 → 静默录到别的设备。这跟 🟡-5 是同一类"用户以为录的是 A"的失败模式。

**Suggestion：**

补上第 4 个参数 `settings.RecordingMicName`（并考虑给 `RecordingConfig` 用命名参数，避免下次又漏）。若判定不必支持名称回退，则应删除 `MicName` 参数与三处回退分支——留着会让人以为它有效。

### 🟡 **Newly introduced / 性能与鲁棒性: 单路直写把磁盘 I/O 搬到了采集回调线程**

`ClassNote/Services/AudioService.cs:565-571`、`:692-705`、`:768`

**Why：**

`Pump()` 在 `lock (_lock)` 内触发 `SamplesProduced`，而 `DirectRecorder.OnSamplesProduced` 在其中**同步写盘**：

```csharp
var output = _converter.Process(_readScratch, want);
if (output.Length > 0)
{
    Output.AddSamples(output, 0, output.Length);
    try { SamplesProduced?.Invoke(output); } catch { }   // ← 采集回调线程 + 持锁
}
```

`OnDataAvailable` → `Append` → `Pump` 全部在 NAudio 的采集线程上，而 `_writer.Write` 的调用栈是 `WaveFileWriter → BinaryWriter → outStream(FileStream)`。我反射检查了 `NAudio.Core.dll` 的 `WaveFileWriter` 字段，只有 `outStream` / `writer`，**没有大块写缓冲**（据此估算 16kHz 单声道下约每 4KB、即约每秒 8 次真实 syscall）。**依据强度**：字段列表是实测，写盘频率是基于字段的推断。

风险：采集线程被一次慢落盘（磁盘忙、杀软扫描、网络盘、休眠前刷盘）拖住时，WASAPI 采集缓冲会静默丢帧——录音出现缺口，而旧的合成路径把写盘放在独立线程，没有这个暴露面。另外 `_onFrame(data)` 先于写盘执行，而 `AudioDataAvailable` 是 public 事件：现在任何订阅方（未来若有 UI 订阅）都会在采集线程上被同步调用，实现"进度条"这类操作就会直接拖慢采集。`IAudioService` 的文档没有说明这个线程亲和性变化。

同样值得注意：`Stop()` 里 `_stopRequested` 先置位、`_channel.Stop()` 之后才摘订阅（`:711-713`），注释说这样"以免丢掉设备在停止过程中冲刷出来的最后一批数据"——但标志位在 `Stop()` **之前**就置了，`OnSamplesProduced:694` 会直接 return，**恰好把注释想保留的尾部数据丢掉**。注释与实现相反（影响很小，最多几十毫秒尾巴；但注释是错的）。

**Suggestion（请作者判断取舍）：**

- 若保留直写：在注释里写明前提（本地 SSD）、把"注释想保留尾部数据"的意图用 `_channel.Stop()` 之后再置标志的方式实现，或修正注释；并给 `AudioDataAvailable` 加线程亲和性说明（"回调在采集线程上同步触发，订阅方必须立即返回"）。
- 若想两全：保留一个**只做写盘**的专用线程（去掉 float 往返与混音器即可，这本来就是本版优化的主要收益），写盘失败/积压时可观测。

---

## 💭 小问题

### 💭 **死代码：`Clipped` / `lastClipped` 全程未使用**

`ClassNote/Services/NoteSegmenter.cs:111`、`:119-121`、`:144`、`:157`

`windows` 元组里的 `Clipped` 在 `:144` 被 `_` 丢弃，`lastClipped` 在 `:157` 用 `_ = lastClipped;` 显式压掉。这是"截断语义没想清楚"的残留（与 🟡-1 同源），建议删掉，或改成真正驱动 `TruncatedChars` 的依据。

### 💭 **`Truncate` 的默认参数已无意义**

`ClassNote/Services/LlmService.cs:470` —— `max = 20000` 的默认值来自已删除的 `BuildNotePrompt`；现在唯一调用点（`:445`）显式传 300。建议收紧为必填参数。

### 💭 **分段上限等常量无法配置**

`ClassNote/Services/NoteSegmenter.cs:53-62` 与 `ClassNote/Services/LlmService.cs:76` —— `DefaultMaxCharsPerSegment` / `DefaultMaxSegments` / `MergeCascadeThreshold` / `MergeExcerptChars` 都是写死常量，`GenerateNoteAsync` 也不透传。对"本地慢模型"用户，40 段 × 最多 2 次调用可能意味着数小时。建议至少让段数上限可配，或在进度里明确告知总调用次数。

### 💭 **重试进度文案有歧义**

`ClassNote/Services/LlmService.cs:111` + `:207-211` —— `FormatSegmentProgress("重试第", i + 1, total, …)` 产出"重试第 3/8 段…"，读起来像"8 次重试中的第 3 次"。建议 `$"第 {i}/{total} 段失败，正在重试…"`。

### 💭 **共享的设备选择助手，其注释与调用点前提不一致**

`ClassNote/Services/AudioConfig.cs:92-107` 声明用于"含系统默认占位项"的下拉框（因此 `byId > 0` 才算命中），但 `RecordingSetupWindow.xaml.cs:53-54` 用的是**没有占位项**的列表（注释也明说不加）。当前恰好无害（两种情况下"没命中"的返回值都是 0，而 0 正是正确项），但这是一个"下次改动会踩"的隐性耦合，建议在 `ResolveIndex` 的注释里点明两种用法或加一个 `hasPlaceholderItem` 参数。

### 💭 **参数被静默夹取，无任何反馈**

`ClassNote/Services/NoteSegmenter.cs:90-93` —— `maxCharsPerSegment < 1000` 会被静默抬到 1000，`overlapChars > maxCharsPerSegment / 2` 会被静默砍半。测试 `Build_RespectsExplicitLimits` 传的正好是 1000，擦边通过。建议 `Debug.Assert` 或文档里明写下限。

### 💭 **类注释里的优化收益描述不完全准确**

`ClassNote/Services/AudioService.cs:19-20` 说单路直写"省掉一路 2 秒环形缓冲"，但 `:568` 的 `Output.AddSamples` 在直写路径下**照旧执行**，环形缓冲仍然分配、仍然拷贝。实际省掉的是合成线程和 float 往返。注释小改一下即可（或者顺手在单路路径下跳过 `AddSamples`，那才真的省）。

### 💭 **`--silent-ok` 会掩盖真问题**

`devtools/AudioProbe/Program.cs:96-133` —— `--silent-ok` 让"完全没有信号"也算通过。它有正当用途（安静机器上验回环），但它同时也放过了"麦克风根本没录到声音"的用例。建议只对系统声音用例生效，或把"哪一路允许静默"作为参数。

---

## 鼓励与后续步骤

**做得好的地方值得保持**：分段 + 降级链的失败模型、把提示词抽成可测纯函数并锁住"会退化的约束"、`AudioProbe` 顺手把旧的事件累积 bug 修掉——这三件事说明作者在"改动的影响面"上是有意识的。这次审查提出的问题里，大部分不是设计错误，而是**注释/文档与实际行为脱节**（🟡-5、🟡-6、🟡-7、🟡-8 和三条 💭），这类问题最有效的处理方式不是逐条改，而是建立一个习惯：**写下"明确告知/跟随变化/用于回退"这类承诺时，顺手补一条能失败的测试**。比如 🟡-5 只需要一条 `StartRecording` 返回 true 时 `LastWarning != null` 的断言；🟡-6 只需要一条"Both 来源下录音页应显示系统声音说明"的断言。

建议的落地顺序：

1. **先修 🔴-1**（混音写盘），并补一条无声卡可跑的混音单测 —— 这是唯一会让用户拿到坏数据的问题。
2. 修 🟡-1 / 🟡-2 / 🟡-3 / 🟡-4（分段语义与错误处理），它们同属"分段生成的质量"这一块，一起改一起补测试收益最高。
3. 修 🟡-5 / 🟡-6 / 🟡-7 / 🟡-8（设备与提示一致性），这四条都是小改动 + 一条断言。
4. 🟡-9 是取舍问题，请作者判断；无论选哪边，把结论写进注释。
5. 💭 项可随手清理，其中 `Clipped`/`lastClipped` 死代码建议与 🟡-1 一起处理。

**需要我接着做的话**：

- 我可以按 🔴-1 的建议写一份**不含声卡依赖的混音回归测试**草案（把混音逻辑抽成可测函数 + 断言 int16 字节一致），你确认方案后我再动代码；
- 如果你的机器有声卡，也可以授权我跑 `dotnet run --project devtools/AudioProbe -c Release -- <目录> 3`，用真机产物交叉验证混音问题（会采集本机环境声音，所以我没擅自执行）；
- 想把范围扩到"整仓审查"（而非本次 diff）也可以，`ClassNote/Services/*` 与 `Views/*` 我还没有做完整通读。
