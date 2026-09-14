# OCR 识别：引擎选择、内网 PaddleOCR 部署与降噪

ClassNote 的截图文字识别有两个引擎，在 **设置 →「OCR 识别」** 页签切换：

| 引擎 | 说明 | 适用 |
| --- | --- | --- |
| **内置 Windows OCR**（默认） | `Windows.Media.Ocr`，离屏、零配置 | 单机、无内网服务、对公式/代码要求不高 |
| **内网 PaddleOCR 服务** | 自建 HTTP 服务，客户端把截图 POST 过去 | 有内网高性能服务器；需要更好的中英混排、公式、代码识别 |

> ⚠️ 隐私：选择远程引擎后，**截图会离开本机**发往所填地址。内置引擎不出本机；语音转写（STT）在任何情况下都在本地完成。

---

## 一、为什么值得加这个开关（实测数据）

Windows OCR 对中文是"一个字一个词"的结果，拼行时会在每个字之间插空格。用一条真实的 21 分钟课堂记录（75 张截图）统计：

| 指标 | 实测值 |
| --- | --- |
| OCR 正文字符数 | 18134 |
| 去掉空白后 | 9885（**空白占 45.5%**） |
| 逐字被空格拆开的行 | 359 / 825 行 |
| 误识 `主`（`m 主 n` = `min`） | 47 次 |
| 误识 `巨`（`f 巨 一 1 ]` = `f[i-1]`） | 22 次 |

噪声有两个后果：白烧近一半的提示词 token；模型要么把乱码抄进笔记，要么按自己的理解"修"错公式
（实测出现过把 `f(n-1)+1` 修成 `f(n-1)` 的情况）。

---

## 二、服务器端部署（二选一）

### A. PaddleOCR 2.x · PaddleHub Serving（`/predict/ocr_system`）

文档：[deploy/hubserving/readme.md](https://github.com/PaddlePaddle/PaddleOCR/blob/release/2.7/deploy/hubserving/readme.md)

```bash
# 1. 安装 PaddleHub（需要 python > 3.6.2）
pip3 install paddlehub==2.1.0 --upgrade

# 2. 按官方文档把推理模型放到 deploy/hubserving/ocr_system/params.py 指定的路径
#    （检测 ch_PP-OCRv3_det_infer / 识别 ch_PP-OCRv3_rec_infer / 方向分类 ch_ppocr_mobile_v2.0_cls_infer）

# 3. 安装 ocr_system 服务模块
hub install deploy/hubserving/ocr_system

# 4. 启动（CPU、单进程；默认端口 8866）
hub serving start -m ocr_system

# 4'. 用 GPU / 指定端口：改 config.json（官方示例里 port=8868、use_gpu=true）后
hub serving start -c deploy/hubserving/ocr_system/config.json
```

- 客户端填的服务地址：`http://<服务器IP>:8868/predict/ocr_system`
- 客户端「请求方式」选：**JSON + images 数组（PaddleOCR 2.x hubserving）**
- 请求：`POST {"images": ["<base64>"]}`
- 响应：`{"msg":"","results":[[{"text":"…","confidence":0.99,"text_region":[[x,y],…]}]],"status":"000"}`
- 注意：Windows 上只支持单进程（`--use_multiprocess` 不可用），建议部署在 Linux 服务器

### B. PaddleOCR 3.x / PaddleX Serving（`/ocr`）

文档：[PaddleX OCR pipeline serving](https://github.com/PaddlePaddle/PaddleX/blob/develop/docs/pipeline_usage/tutorials/ocr_pipelines/OCR.en.md)

```bash
pip install paddlex

# CPU
paddlex --serve --pipeline OCR --port 8080
# GPU（多卡用 CUDA_VISIBLE_DEVICES 指定）
paddlex --serve --pipeline OCR --device gpu:0 --port 8080
```

- 客户端填的服务地址：`http://<服务器IP>:8080/ocr`
- 客户端「请求方式」选：**JSON + file 字段（PaddleOCR 3.x / PaddleX serving）**
- 请求：`POST {"file": "<base64>", "fileType": 1, "visualize": false}`
  （客户端**总是**带 `visualize:false`：不关的话服务端会为每张图回传一张标注图 base64，
  一节课上百张截图，白占的带宽远超识别本身）
- 响应：`{"logId":"…","errorCode":0,"errorMsg":"Success","result":{"ocrResults":[{"prunedResult":{"rec_texts":[…],"rec_scores":[…],"rec_polys":[…]}}]}}`

### 内网部署注意事项

- 固定服务器 IP / 主机名，放行对应端口；客户端与服务器需在同一可路由网段。
- 想让它吃满机器：PaddleX 用 `--device gpu:0`；hubserving 用配置文件里的 `use_gpu`。
- 并发：本客户端一节课上百张截图、**串行**发送（后台队列单线程），
  单实例 1–2 个 worker 足够，不必为它开高并发。
- 如果服务前面挂了网关做鉴权，把令牌填进客户端「访问密钥」，客户端按 `Authorization: Bearer <key>` 发送。

---

## 三、客户端设置

设置 →「OCR 识别」：

| 字段 | 说明 |
| --- | --- |
| 截图文字识别引擎 | 内置 Windows OCR / 内网 PaddleOCR 服务 |
| 服务地址 | 必须是 `http://` 或 `https://` 开头的绝对地址（保存时会校验，写错不让保存） |
| 请求方式 | 必须与服务器部署方式一致；**选错表现为"能连上但一个字都识别不出"** |
| 访问密钥 | 可留空；非空时按 `Authorization: Bearer` 发送，落盘前 DPAPI 加密 |
| 请求超时（秒） | 3–300，默认 20（内网单张截图通常 &lt; 1 秒） |
| 服务失败时回退内置 OCR | 默认开。详见下文"降级行为" |
| **测试识别** | 拿**最近一张真实课堂截图**打一遍（没有截图就现场渲染一张测试卡），报告字数、耗时与开头内容 |

保存后**下一次开始录音生效**（OCR 服务在录音开始时按设置装配）。

### 降级行为（重要）

开启回退后：

- 远程服务抛错（连不上 / 超时 / HTTP 错误 / 服务报错 / 返回结构无法解析）→ 该张图改用内置引擎识别；
- **并进入 2 分钟冷却**：冷却期内不再逐张去等超时——一节课 100+ 张截图，
  否则"100 × 20 秒"就是半小时纯等待；
- 冷却结束后再试一次远程，服务恢复则自动用回远程；
- 单张失败不会中断整节课：**宁可这一次用精度低一点的内置引擎，也不能让课件文字整块消失**。

只有"服务可达且合法地没有文字"（例如空白幻灯片）才返回空文本，不算失败、不触发回退。

---

## 四、客户端的字符级降噪（`OcrTextNormalizer`）

不管用哪个引擎，素材送进提示词之前都会做一轮**确定性**规范化（在 `NoteProcessor` 装配 OCR 素材时执行；
数据库里保存的仍是**原始 OCR**，便于对拍与追溯）：

| 规则 | 例子 |
| --- | --- |
| 合并汉字之间、汉字与全角标点之间、字母数字与全角标点之间的空格 | `动 态 规 划` → `动态规划`；`用 f （ n ）` → `用 f(n)` |
| **绝不合并 ASCII 记号之间的空格** | `1 7 3 5 9 4 8` 必须原样保留（合并成 `1735948` 是彻底的语义事故） |
| 公式行（含 `=`、`min(`、`f(`、`for(`、`int ` 等特征）里全角符号转半角、括号与运算符两侧紧排 | `f （ n ） = m 主 n （ … ）` → `f(n)=min(…)` |
| 极保守的误识替换 | `m 主 n` → `min`；公式行里夹在字母数字之间的「一」→ `-`（OCR 把减号认成汉字） |
| 其余可疑字符（`巨`、`丿`、`刂`…）**一律不动** | 猜符号是模型的活，而且必须按"确定不了就标注"的口径来 |

**在真实记录上的实测效果**：OCR 素材 18735 字 → 12068 字（**减少 35.6%**），
`m 主 n（5，3，5）=3` → `min(5,3,5)=3`，`1 7 3 5 9 4 8` 原样保留。

### 残余噪声交给提示词（v1.1 追加的两条规则）

规范化只能处理字符级问题；语义级判断（这个 `巨` 到底是不是 `[`）不能猜。因此提示词里补了两条：

1. **噪声兜底**：能确定含义的按正确写法写出；**确定不了的一律原样保留并注明「OCR 不清」**，
   禁止猜一个看起来合理的符号——宁可留一个看得见的疑点，也不要写一个看不出错的错公式。
2. **来源标注**：来自【课件/板书截图 OCR】的内容 `source` 一律填「课件」；判不准就留空。

（这两条由 `NotePromptBuilder.OcrNoiseRules` / `OcrSourceRules` 提供，单测锁住关键措辞。）

---

## 五、故障排查

### 先跑一遍本地联调（不需要真实服务器）

仓库外配套一套假 PaddleOCR 服务与联调脚本，用来在动内网服务器之前确认客户端没问题（这两个辅助脚本属本机调试工具，**未随仓库发布**，需要时按本节说明自建）：

```powershell
python devtools\fake-paddle-ocr-server.py 18868      # 手动起也行（脚本会自己起/停）
pwsh -NoProfile -File devtools\check-paddle-ocr-client.ps1
```

脚本会用**生产代码**的 `PaddleOcrService` 走真实 HTTP，依次验证：
hubserving 协议（含"按坐标还原阅读顺序"）、PaddleX 协议（含 `Authorization: Bearer`）、HTTP 500 必须抛错（否则无法回退）。

### 常见现象

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 测试识别提示"服务可达，但没有识别出文字" | 请求方式与部署方式不一致 | hubserving → 选 images 数组；PaddleX → 选 file 字段 |
| 测试识别提示"返回了无法解析的内容" | 地址打到了别的服务（例如打到 Web 首页）或协议不符 | 核对 URL 是否精确到 `/predict/ocr_system` 或 `/ocr` |
| HTTP 401/403 | 网关鉴权未通过 | 在「访问密钥」填入令牌 |
| 提示超时 | 服务器排队/模型在 CPU 上跑得很慢 | 调大「请求超时」，或让服务端用 GPU；也可接受回退 |
| 识别结果里仍有 `巨`、`丿` 这类怪字 | OCR 误识无法确定性还原 | 正常现象：提示词会要求模型标注「OCR 不清」，而不是猜 |
| 保存设置时报"服务地址不合法" | URL 缺协议头（如填了 `10.0.0.5:8868/...`） | 补上 `http://` |
| 想确认当前实际用了哪个引擎 | 地址没填时客户端会自动退回内置引擎 | 看设置页签；`OcrServiceFactory.ResolveEngine` 就是这条口径 |

### 相关代码位置

| 文件 | 职责 |
| --- | --- |
| `ClassNote/Services/OcrEngineConfig.cs` | 引擎/请求方式枚举、展示名、默认值与存储口径 |
| `ClassNote/Services/PaddleOcrService.cs` | 远程 OCR 客户端 + 响应解析（纯函数 `PaddleOcrResponseParser`） |
| `ClassNote/Services/OcrServiceFactory.cs` | 按设置装配；`FallbackOcrService` 提供失败回退与冷却 |
| `ClassNote/Services/OcrTextNormalizer.cs` | 字符级降噪（纯函数） |
| `ClassNote/Views/SettingsWindow.xaml(.cs)` | 「OCR 识别」页签与"测试识别" |
| `ClassNote.Tests/Services/PaddleOcrServiceTests.cs` 等 | 协议、解析、回退、降噪的回归测试 |
