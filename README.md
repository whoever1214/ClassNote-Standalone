# ClassNote — 课堂笔记助手（单机版）

> 一款**纯客户端**课堂笔记工具：课堂录音 + 自动截屏 + 本地语音转文字（STT）+ 本地图片识别（OCR）+ LLM 生成结构化笔记。
> 无需任何服务端，开箱即用。

## 核心特性

- **课堂录音**：NAudio 采集 16kHz 单声道 WAV，自动保存到本地。
- **自动截屏**：周期性全屏抓取 + pHash 变化检测，智能区分"批注 / 新幻灯片 / 视频"三类画面。
- **本地 STT**：SenseVoice-Small ONNX（INT8 量化）+ onnxruntime CPU 推理，中文识别准、自带标点，全程离线。
- **本地 OCR**：调用 Windows 10/11 内置 OCR 引擎（Windows.Media.Ocr），识别截图文字。
- **LLM 笔记生成**：调用 OpenAI 兼容 API（默认 DeepSeek），把"转写 + 截图文字"整理为 Markdown 笔记。
- **PDF 导出**：笔记本地渲染为 PDF（QuestPDF），保存到桌面。
- **数据本地化**：会话 / 截图 / 笔记全部存于本机 SQLite（%LOCALAPPDATA%/ClassNote/classnote.db）。
- **现代界面**：「云白 + 靛蓝」轻量主题，圆角卡片布局、渐变按钮、状态胶囊列表、录音呼吸指示、页面切换动效（样式体系集中于 `ClassNote/App.xaml`）。

## 文档导航

| 文档 | 说明 |
|------|------|
| [开发文档](docs/开发文档.md) | 技术架构、模块说明、数据模型、构建与测试指南 |
| [部署文档](docs/部署文档.md) | 系统要求、发布打包、分发安装、常见故障排查 |
| [README（项目内，精简版）](ClassNote/README.md) | 代码目录快速索引 |

## 快速开始

### 运行（已有构建产物）

1. 进入 `ClassNote/bin/Release/net8.0-windows10.0.19041.0/`。
2. 确保该目录下存在 `models/sensevoice/`（model_quant.onnx / tokens.json / am.mvn）。
3. 双击 `ClassNote.exe` 启动，点击主页面右上角「设置」图标（齿轮）配置 LLM API Key 后即可使用完整功能。

### 从源码构建

```bash
cd ClassNote-Standalone/ClassNote
dotnet restore        # 使用项目 NuGet.config（nuget.azure.cn 镜像源）
dotnet build -c Release
```

生成产物位于 `bin/Release/net8.0-windows10.0.19041.0/`，其中已包含 SenseVoice 模型文件。

## 技术栈

- **客户端框架**：C# / WPF / .NET 8（net8.0-windows10.0.19041.0，Windows 平台专用）
- **音频**：NAudio 2.2.1（WAVE 采集与写入）
- **STT 推理**：Microsoft.ML.OnnxRuntime 1.20.0（CPU）
- **OCR**：Windows.Media.Ocr（系统内置）
- **LLM**：OpenAI 兼容 HTTP API（Newtonsoft.Json）
- **Markdown 渲染**：Markdig 0.37.0（数学公式经本地内置 MathJax 渲染，离线可用）
- **PDF 导出**：QuestPDF 2024.12.0（含 Lato 字体包 + 中文字体自动注册）
- **数据存储**：SQLite（System.Data.SQLite.Core 1.0.119）
- **WebView2**：Microsoft.Web.WebView2 1.0.2903.40（笔记渲染视图）

## 许可与声明

- SenseVoice-Small 模型：`model_quant.onnx`（约 240MB，INT8 量化）随程序自带，存放于 `ClassNote/Models/sensevoice/`。
- 首次打开笔记 / 录音长文件时，STT 模型需要加载到内存（峰值约 600MB），请确保机器内存充足（≥ 8GB 推荐）。
