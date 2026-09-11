# ClassNote — 课堂笔记助手（单机版）

> 一款**纯客户端**课堂笔记工具：课堂录音 + 自动截屏 + 本地语音转文字（STT）+ 本地图片识别（OCR）+ LLM 生成结构化笔记。
> 无需任何服务端，开箱即用。

![version](https://img.shields.io/badge/version-0.4.2-blue)
![platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-lightgrey)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![license](https://img.shields.io/badge/license-MIT-green)

---

## 下载安装

前往 **[Releases](https://github.com/whoever1214/ClassNote-Standalone/releases/latest)** 下载最新安装包：

| 文件 | 说明 |
|------|------|
| [`ClassNote-0.4.2-setup-x64.exe`](https://github.com/whoever1214/ClassNote-Standalone/releases/download/v0.4.2/ClassNote-0.4.2-setup-x64.exe) | Windows x64 安装程序（约 275 MB，**自包含运行时，免装 .NET**） |

**系统要求**

- Windows 10 1809（build 17763）或更高 / Windows 11，**x64**
- 内存 ≥ 8 GB（STT 模型加载峰值约 600 MB）
- [Microsoft Edge WebView2 运行时](https://developer.microsoft.com/microsoft-edge/webview2/)（Win11 及较新 Win10 通常已内置；缺失时安装程序会提示）
- **麦克风**（课堂录音功能）

**安装说明**

- 按**当前用户**安装到 `%LOCALAPPDATA%\Programs\ClassNote`，**无需管理员权限**
- 安装向导支持简体中文 / 繁体中文 / English

首次启动后点击主页面右上角**「设置」**（齿轮图标）配置 LLM API Key 即可使用完整的笔记生成功能。

---

## 核心特性

- **课堂录音**：NAudio 采集 16kHz 单声道 WAV，自动保存到本地。
- **自动截屏**：周期性全屏抓取 + pHash 变化检测，智能区分"批注 / 新幻灯片 / 视频"三类画面。
- **本地 STT**：SenseVoice-Small ONNX（INT8 量化）+ onnxruntime CPU 推理，中文识别准、自带标点，全程离线。
- **本地 OCR**：调用 Windows 10/11 内置 OCR 引擎（Windows.Media.Ocr），识别截图文字。
- **LLM 笔记生成**：调用 OpenAI 兼容 API（默认 DeepSeek），把"转写 + 截图文字"整理为 Markdown 笔记。
- **PDF 导出**：笔记本地渲染为 PDF（QuestPDF），支持单条导出到桌面，或**勾选多条批量导出**到指定文件夹（自动跳过尚未生成笔记的记录，重名文件自动追加序号）。
- **记录管理**：最近记录支持**勾选 / 全选**（三态）、**批量导出 PDF**、**批量删除**（连同录音 / 截图 / 笔记一并移除），自动轮询刷新期间勾选状态不丢失。
- **数据本地化**：会话 / 截图 / 笔记 / 每周课表全部存于本机 SQLite（`%LOCALAPPDATA%/ClassNote/classnote.db`）。
- **定时记录 · 每周课表（v0.4.0）**：可视化编辑课表（「每周课表」七天时间轴网格，每天一列；课块高度按课程**真实时长等比**、相邻课次之间强制留间隙，**相邻的 10 分钟短课 / 45 分钟连堂课都不会互相覆盖**），到点**自动开始录音、下课自动结束**；**后台静默启动**（到点自动录音不弹出主界面，仅发托盘气泡提醒）；**托盘常驻 + 开机自启**（登录即驻留托盘，关闭主窗口不退出录音），定时默认麦克风可在设置中指定；唤醒后错过 ≤10 分钟自动补录、已在录音时自动跳过本节。
- **现代界面**：「云白 + 靛蓝」轻量主题，圆角卡片布局、渐变按钮、状态胶囊列表、录音呼吸指示、页面切换动效（样式体系集中于 `ClassNote/App.xaml`）。
- **流畅交互**：全局**平滑滚轮**（缓动动画 + 按视口比例自适应的步长，替代默认逐行跳变；Shift+滚轮横向滚动），覆盖主页列表与设置窗口。

---

## 从源码构建

### 环境要求

- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0) 或更高
- Windows 10/11 x64（WPF 项目，仅支持 Windows）
- Visual Studio 2022（可选，便于调试 XAML）

### 1. 还原 SenseVoice 模型（**必要**）

模型二进制约 **230 MB**，属非源码资产，**未纳入版本库**（见 `.gitignore`）。请将下列文件放入 `ClassNote/Models/sensevoice/`：

```
ClassNote/Models/sensevoice/model_quant.onnx   # INT8 量化模型
ClassNote/Models/sensevoice/tokens.json
ClassNote/Models/sensevoice/am.mvn
```

模型可从 [SenseVoice](https://github.com/FunAudioLLM/SenseVoice) 官方仓库获取并自行导出为 ONNX。

### 2. 编译

```bash
dotnet restore                 # 使用项目内的 NuGet.config（nuget.azure.cn 镜像源）
dotnet build -c Release
```

生成产物位于 `ClassNote/bin/Release/net8.0-windows10.0.19041.0/`。

### 3. 运行

确保输出目录下存在 `models/sensevoice/` 后，双击 `ClassNote.exe` 启动。

### 4. 打包安装程序（可选）

需先安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)，然后运行一键脚本：

```bash
build-installer.bat
```

脚本会依次执行：自包含发布（`win-x64`）→ 校验关键载荷文件 → 用 Inno Setup 编译。
产物：`dist/ClassNote-0.4.2-setup-x64.exe`。

> **发布前版本号维护（易漏点）**：版本号是**手写常量**，需同步 3 处 ——
> `ClassNote/ClassNote.csproj`（`Version`/`FileVersion`/`AssemblyVersion`）、
> `installer/ClassNote.iss`（`MyAppVersion`）、
> `build-installer.bat` 末尾的输出文件名提示。

### 5. 测试

```bash
dotnet test ClassNote.Tests/ClassNote.Tests.csproj
```

---

## 技术栈

| 领域 | 选型 |
|------|------|
| 客户端框架 | C# / WPF / .NET 8（`net8.0-windows10.0.19041.0`，Windows 专用） |
| 音频 | NAudio 2.2.1（WAVE 采集与写入） |
| STT 推理 | Microsoft.ML.OnnxRuntime 1.20.0（CPU） |
| OCR | Windows.Media.Ocr（系统内置） |
| LLM | OpenAI 兼容 HTTP API（Newtonsoft.Json 13.0.3） |
| Markdown 渲染 | Markdig 0.37.0（数学公式经内置 MathJax 离线渲染） |
| PDF 导出 | QuestPDF 2024.12.0（Lato 字体包 + 中文字体自动注册） |
| 数据存储 | SQLite（System.Data.SQLite.Core 1.0.119） |
| 笔记视图 | Microsoft.Web.WebView2 1.0.2903.40 |

---

## 项目结构

```
ClassNote-Standalone/
├─ ClassNote/                  # 主程序（WPF）
│  ├─ Models/                  # 数据模型（会话/截图/笔记/课表条目）
│  ├─ Services/                # 录音、截屏、STT、OCR、LLM、仓库、课表引擎
│  ├─ ViewModels/              # MVVM 视图模型
│  ├─ Views/                   # 页面与窗口（主页/录音/课表/设置/编辑器）
│  ├─ assets/                  # 内置 MathJax 等离线资源
│  └─ Models/sensevoice/       # STT 模型（不入库，需自行放置）
├─ ClassNote.Tests/            # xUnit 单元测试
├─ devtools/                   # 开发辅助工具（渲染校验 / 自启动探针）
├─ docs/                       # 开发文档、部署文档、UI 渲染图
├─ installer/                  # Inno Setup 安装脚本与中文语言包
├─ build-installer.bat         # 一键打包脚本
└─ ClassNote.sln
```

---

## 文档导航

| 文档 | 说明 |
|------|------|
| [开发文档](docs/开发文档.md) | 技术架构、模块说明、数据模型、构建与测试指南 |
| [部署文档](docs/部署文档.md) | 系统要求、发布打包、分发安装、常见故障排查 |
| [README（项目内，精简版）](ClassNote/README.md) | 代码目录快速索引 |

---

## 版本历史

| 版本 | 主要变更 |
|------|----------|
| **v0.4.2** | 「添加课表条目」课程名改为**纯下拉选择**（不支持手输，避免"数学 "与"数学"这类同课不同名）；渲染校验工具修正窗口内容根 Margin 造成的横向拉伸 / 箭头被挤出可视区的假象 |
| **v0.4.1** | 「添加课表条目」课程名不再显示为空白（可编辑 ComboBox 模板缺 `PART_EditableTextBox`）；每日课表相邻两课不再重叠（课块高度算而未用 + 固定 18px 下限）；定时记录改为后台静默启动（到点不再弹出主界面） |
| **v0.4.0** | 新增**定时记录 · 每周课表**：可视化课表编辑、到点自动录音 / 下课自动结束、托盘常驻、开机自启 |
| v0.3.5 | 修复 USB 外接麦克风无法录音；笔记注重新知识点整理；LLM 超时可配置 + v1 健康检查 |
| v0.3.4 | 修复「开始记录」配置窗口麦克风提示文字被裁剪 |
| v0.3.3 | 修复滚轮动画卡顿（外部滚动中断阈值按视口 3/4 缩放） |

---

## 许可与声明

- 本项目基于 **MIT License** 开源，详见 [LICENSE](LICENSE)。
- **SenseVoice-Small 模型**：`model_quant.onnx`（约 240 MB，INT8 量化）随安装包分发，存放于安装目录 `models/sensevoice/`；模型版权归 [FunAudioLLM/SenseVoice](https://github.com/FunAudioLLM/SenseVoice) 所有，遵循其原始许可。
- 首次打开笔记 / 录音长文件时，STT 模型需加载到内存（峰值约 600 MB），请确保机器内存充足（**≥ 8 GB 推荐**）。
- 笔记生成依赖第三方 LLM API（默认 DeepSeek），录音与转写**全程离线**，仅"转写 + OCR 文本"会在生成笔记时发送至所配置的 API。
