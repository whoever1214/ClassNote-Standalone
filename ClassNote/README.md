# ClassNote — 课堂笔记助手（单机版）

> 一款纯客户端课堂笔记工具：课堂录音 + 自动截屏 + 本地 STT（SenseVoice ONNX）+ 本地 OCR + LLM 生成结构化笔记。
> 无服务端，开箱即用。

## 文档

完整文档已迁移到项目根目录：

- **[开发文档](../docs/开发文档.md)** — 架构、模块、数据模型、构建与测试
- **[部署文档](../docs/部署文档.md)** — 系统要求、发布打包、安装配置、故障排查
- **[项目总览](../README.md)** — 根 README 导航

## 快速构建

```bash
dotnet restore        # 项目自带 NuGet.config（nuget.azure.cn 镜像）
dotnet build -c Release
```

产物：`bin/Release/net8.0-windows10.0.19041.0/ClassNote.exe`（已含 SenseVoice 模型文件）。

## 目录速览

- `Services/` — 本地服务层（STT / OCR / LLM / 存储 / PDF）
  - `SenseVoiceSttService.cs` — 本地语音转文字（SenseVoice-Small ONNX）
  - `FbankExtractor.cs` — fbank 特征提取 + LFR + CMVN（STT 前端）
  - `ISttService.cs` — STT 服务接口
  - `NoteProcessor.cs` — STT → OCR → LLM 编排管线
  - `LocalRepository.cs` — SQLite 数据存储
  - `PdfExportService.cs` — 本地 PDF 导出（QuestPDF）
- `Controls/` — `SmoothScroll.cs` 全局平滑滚轮（缓动动画、视口比例步长、Shift 横向滚动）
- `ViewModels/` — Main / Recording / Note 视图模型（MainViewModel 含最近记录多选/全选/批量导出/删除）
- `Views/` — 页面（MainPage / RecordingPage / NoteViewPage / SettingsWindow / RecordingSetupWindow）
- `Models/` — 数据模型 + `sensevoice/` 模型文件（model_quant.onnx / tokens.json / am.mvn）

## LLM 配置

主页面左下角「设置」→ 填写 API Key / 基础地址（默认 DeepSeek）/ 模型名，仅存本地 `settings.json`。