using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using ClassNote.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NoteRender;

/// <summary>
/// 笔记页渲染校验工具：把笔记 HTML 真正放进 **WebView2 + 本地 MathJax** 里跑一遍，
/// 用 DOM 断言 + 截图回答"用户到底看到了什么"。
///
/// 为什么需要它：笔记正文渲染在 WebView2 内部，WPF 侧的离屏渲染（RenderHarness）**抓不到它的内容**
/// （那边为了稳定出图是刻意把 WebView2 隐藏掉的），单测也只能验证到"Markdown → HTML"这一步。
/// 而历史上的公式事故恰恰发生在最后一步：HTML 没错，**MathJax 的分隔符配错了**，
/// 于是每一对方括号都变成行间公式、被居中并撑开空隙。
///
/// 用法：
///   NoteRender --demo                          内置样例（裸括号 / $行内$ / $$行间$$ 各一组）
///   NoteRender --md &lt;文件&gt; [--out x.png]       渲染指定 Markdown 文件
///   NoteRender --session &lt;id前缀&gt; [--out x.png]  直接渲染数据库里的真实笔记（只读）
///   NoteRender --legacy-config                 用**事故当时**的错误分隔符跑一遍，做前后对比
///
/// 退出码 0 = 断言通过（裸括号没有变成公式、真公式被渲染出来）；非 0 = 不通过。
/// </summary>
internal static class Program
{
    private sealed record DomReport(int Mjx, int MathSpans, int DisplayDivs, int Paragraphs,
        string FirstParagraph, string BodyText);

    [STAThread]
    private static int Main(string[] args)
    {
        var options = ParseOptions(args);
        string markdown;
        string label;

        if (options.TryGetValue("session", out var prefix))
        {
            var note = LoadNoteFromDb(prefix);
            if (note == null)
            {
                Console.Error.WriteLine($"数据库里找不到 session_id 以 {prefix} 开头的笔记");
                return 2;
            }
            markdown = note;
            label = $"DB 笔记 session={prefix}";
        }
        else if (options.TryGetValue("md", out var path))
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"找不到 Markdown 文件：{path}");
                return 2;
            }
            markdown = File.ReadAllText(path);
            label = Path.GetFileName(path);
        }
        else
        {
            markdown = DemoMarkdown;
            label = "内置样例";
        }

        string outPath = options.GetValueOrDefault("out")
            ?? Path.Combine(Path.GetTempPath(), $"classnote-noterender-{DateTime.Now:HHmmss}.png");
        bool legacy = options.ContainsKey("legacy-config");

        Console.WriteLine($"输入      : {label}（{markdown.Length} 字符）");
        Console.WriteLine($"配置      : {(legacy ? "事故当时的错误分隔符（--legacy-config）" : "当前生产配置")}");

        string html = legacy ? BuildLegacyDocument(markdown) : NoteHtmlRenderer.RenderDocument(markdown);

        // 与本应用一致：把 https://appassets.local 映射到输出目录的 assets（离线 MathJax）
        var assetsDir = Path.Combine(AppContext.BaseDirectory, "assets");
        if (!File.Exists(Path.Combine(assetsDir, "mathjax-tex-svg.js")))
        {
            Console.Error.WriteLine($"缺少离线 MathJax：{Path.Combine(assetsDir, "mathjax-tex-svg.js")}");
            return 2;
        }

        int exitCode = 3;
        var form = new Form
        {
            Width = 900,
            Height = 1200,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),   // 屏幕外，不打扰用户
            ShowInTaskbar = false,
            Text = "NoteRender",
        };
        var webView = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(webView);

        // WebView2 的回调靠窗口消息泵投递，**必须让消息循环跑起来**（Application.Run），
        // 否则在 UI 线程上同步等待 EnsureCoreWebView2Async/CapturePreviewAsync 会直接死锁。
        form.Shown += async (_, _) =>
        {
            try
            {
                exitCode = await RunAsync(form, webView, assetsDir, html, outPath, legacy, markdown)
                    .WaitAsync(TimeSpan.FromSeconds(90));   // 看门狗：任何一步卡住都不至于挂死
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("渲染超时（90 秒）——WebView2 未就绪或页面未加载");
                exitCode = 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"渲染失败：{ex.Message}");
                exitCode = 3;
            }
            finally
            {
                form.Close();
            }
        };

        Application.Run(form);
        return exitCode;
    }

    private static async Task<int> RunAsync(Form form, WebView2 webView, string assetsDir,
        string html, string outPath, bool legacy, string markdown)
    {
        var userData = Path.Combine(Path.GetTempPath(), "classnote-noterender-webview2");
        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await webView.EnsureCoreWebView2Async(env);
        var core = webView.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 初始化失败（未安装运行时？）");

        core.SetVirtualHostNameToFolderMapping("appassets.local", assetsDir,
            CoreWebView2HostResourceAccessKind.Allow);

        var navigated = new TaskCompletionSource<bool>();
        core.NavigationCompleted += (_, e) => navigated.TrySetResult(e.IsSuccess);
        core.NavigateToString(html);
        if (!await navigated.Task.WaitAsync(TimeSpan.FromSeconds(20)))
        {
            Console.Error.WriteLine("页面加载失败");
            return 3;
        }

        bool mathJaxReady = await WaitForMathJaxAsync(core, TimeSpan.FromSeconds(20));
        Console.WriteLine($"MathJax   : {(mathJaxReady ? "已就绪（离线脚本已加载并完成排版）" : "未就绪（超时）")}");

        var report = await QueryDomAsync(core);
        Console.WriteLine();
        Console.WriteLine("── DOM 实测 ──────────────────────────────");
        Console.WriteLine($"公式节点 mjx-container : {report.Mjx}");
        Console.WriteLine($"行内公式 span.math     : {report.MathSpans}");
        Console.WriteLine($"行间公式 div.math      : {report.DisplayDivs}");
        Console.WriteLine($"顶层段落 body>p        : {report.Paragraphs}");
        Console.WriteLine($"首段文本               : {Clip(report.FirstParagraph, 160)}");
        Console.WriteLine($"正文前 200 字          : {Clip(report.BodyText, 200)}");

        using (var stream = File.Create(outPath))
        {
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        }
        Console.WriteLine();
        Console.WriteLine($"截图      : {outPath}（{new FileInfo(outPath).Length / 1024} KB）");

        return AssertDom(report, legacy, markdown);
    }

    /// <summary>
    /// 核心断言：
    /// 1. 裸方括号写法（d[i][j] / d[7][6]）必须完整留在同一段文字里——事故当时它们会被拆成
    ///    「d ⏎ i ⏎ j」三段行间公式（这正是用户截图里的样子）；
    /// 2. 每一个真公式节点（Markdig 产出的 span.math / div.math）都必须被 MathJax 排出结果。
    /// </summary>
    private static int AssertDom(DomReport report, bool legacy, string markdown)
    {
        int failures = 0;

        bool hasBareBrackets = markdown.Contains("d[i][j]") || markdown.Contains("f(n)");
        if (hasBareBrackets)
        {
            // 判定看**全篇**（真实笔记里这句话可能在第 8 段），而不是只看首段。
            // 事故当时这些字符会被拆成「d ⏎ i ⏎ j」（中间夹换行与数学斜体），整串 d[i][j] 不再存在。
            bool intact = report.BodyText.Contains("d[i][j]");
            if (intact)
            {
                Console.WriteLine("✓ 裸方括号 d[i][j] 完整留在正文里（未被拆成公式）");
            }
            else
            {
                Console.WriteLine("✗ 裸方括号被拆成了公式 —— 正文里已找不到完整的 d[i][j]");
                failures++;
            }
        }

        if (markdown.Contains("d[7][6]"))
        {
            if (report.BodyText.Contains("d[7][6]"))
                Console.WriteLine("✓ 裸方括号 d[7][6] 完整留在正文里");
            else
            {
                Console.WriteLine("✗ d[7][6] 被拆成了公式");
                failures++;
            }
        }

        int expectedMath = report.MathSpans + report.DisplayDivs;
        if (expectedMath > 0 && report.Mjx < expectedMath)
        {
            Console.WriteLine($"✗ 公式未被排版：Markdig 产出 {expectedMath} 个公式节点，MathJax 只排出 {report.Mjx} 个");
            failures++;
        }
        else if (expectedMath > 0)
        {
            Console.WriteLine($"✓ {expectedMath} 个公式节点全部排版成功（mjx-container={report.Mjx}）");
        }

        if (failures == 0)
        {
            Console.WriteLine();
            Console.WriteLine(legacy ? "（这是事故当时的行为，仅作对比）" : "渲染校验通过 ✓");
        }
        return failures == 0 ? 0 : 1;
    }

    private static async Task<bool> WaitForMathJaxAsync(CoreWebView2 core, TimeSpan timeout)
    {
        const string probe = """
            (function () {
              if (!window.MathJax || !MathJax.startup) return 'loading';
              if (!window.__mjHooked) {
                window.__mjHooked = true;
                MathJax.startup.promise.then(function () { window.__mjDone = true; });
              }
              return window.__mjDone ? 'done' : 'pending';
            })()
            """;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var result = await core.ExecuteScriptAsync(probe);
                if (result.Contains("done"))
                    return true;
            }
            catch { /* 页面尚未就绪，继续等 */ }
            await Task.Delay(200);
        }
        return false;
    }

    private static async Task<DomReport> QueryDomAsync(CoreWebView2 core)
    {
        const string script = """
            (function () {
              var first = document.querySelector('body > p');
              return JSON.stringify({
                mjx: document.querySelectorAll('mjx-container').length,
                mathSpans: document.querySelectorAll('span.math').length,
                displayDivs: document.querySelectorAll('div.math').length,
                paragraphs: document.querySelectorAll('body > p').length,
                firstParagraph: first ? first.innerText : '',
                bodyText: document.body ? document.body.innerText : ''
              });
            })()
            """;

        var raw = await core.ExecuteScriptAsync(script);
        // ExecuteScriptAsync 返回的是 JSON 字符串字面量，需要再解一层
        var json = JsonSerializer.Deserialize<string>(raw) ?? "{}";
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new DomReport(
            root.GetProperty("mjx").GetInt32(),
            root.GetProperty("mathSpans").GetInt32(),
            root.GetProperty("displayDivs").GetInt32(),
            root.GetProperty("paragraphs").GetInt32(),
            root.GetProperty("firstParagraph").GetString() ?? "",
            root.GetProperty("bodyText").GetString() ?? "");
    }

    /// <summary>事故当时的分隔符（圆括号 / 方括号），仅用于前后对比。</summary>
    private static string BuildLegacyDocument(string markdown)
        => NoteHtmlRenderer.BuildDocument(Markdig.Markdown.ToHtml(markdown, NoteHtmlRenderer.Pipeline))
            .Replace(@"tex: { inlineMath: [['\\(', '\\)']], displayMath: [['\\[', '\\]']] }",
                     "tex: { inlineMath: [['(', ')']], displayMath: [['[', ']']] }");

    /// <summary>只读读取数据库里的真实笔记（绝不写入）。</summary>
    private static string? LoadNoteFromDb(string sessionPrefix)
    {
        var db = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassNote", "classnote.db");
        if (!File.Exists(db))
            return null;

        using var conn = new System.Data.SQLite.SQLiteConnection($"Data Source={db};Read Only=True;");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content_markdown FROM notes WHERE session_id LIKE @p ORDER BY updated_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@p", sessionPrefix + "%");
        var value = cmd.ExecuteScalar();
        return value as string;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
                continue;
            string key = args[i][2..];
            string value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
            options[key] = value;
        }
        return options;
    }

    private static string Clip(string text, int max)
    {
        var oneLine = text.Replace("\r", " ").Replace("\n", " ⏎ ");
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    /// <summary>内置样例：一份是事故现场那种裸括号写法，一份是真 LaTeX。</summary>
    private const string DemoMarkdown = """
        # 数学 · 渲染样例

        ## 裸括号（事故现场写法，绝不能被当成公式）

        **LCS 问题定义（状态定义）**：第一步还是定义问题：d[i][j] 等于 S1 的前 i 个字母和 S2 的前 j 个字母。S1 有 7 个字母，S2 有 6 个字母，所以最后要求的是 d[7][6] 到底等于几。

        **硬币问题**：f(n) = min(f(n-1)+1, f(n-5)+1, f(n-11)+1)

        ## 真 LaTeX（必须被排版出来）

        行内公式：$d_{i,j} = 1 + d_{i-1,j-1}$，其中 $1 \le i \le n$。

        行间公式：

        $$
        f(n) = \min_{k \in \{1,5,11\}} \bigl(f(n-k) + 1\bigr)
        $$
        """;
}
