// 开发辅助工具：启动 ClassNote 应用 → 截图 → 通过 UI Automation 模拟点击遍历各页面截图。
// 仅用于本机 UI 验证，不入库。
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Automation;

var exe = args.Length > 0
    ? args[0]
    : @"C:UserswhoeverDesktopClassNote-StandaloneClassNoteinRelease
et8.0-windows10.0.19041.0ClassNote.exe";
var outDir = args.Length > 1 ? args[1] : @"C:UserswhoeverDesktopClassNote-Standalone.ui-captures";
var mode = args.Length > 2 ? args[2] : "all"; // all | main

Directory.CreateDirectory(outDir);
Native.SetProcessDPIAware();

try
{
    var existing = Process.GetProcessesByName("ClassNote");
    foreach (var e in existing) { try { e.Kill(); } catch { } }
    Thread.Sleep(1200);
}
catch { }

var proc = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
if (proc == null) { Fail("无法启动应用"); }

Process? window = WaitForMainWindow("ClassNote", 25);
if (window == null) { Fail("未找到主窗口"); }
Thread.Sleep(2600);

CaptureByHwnd(window.MainWindowHandle, Path.Combine(outDir, "1-main.png"));

if (mode == "main") { Console.WriteLine("DONE(main)"); return 0; }

var mainHwnd = window.MainWindowHandle;
var root = AutomationElement.FromHandle(mainHwnd);

// —— 开始记录（基于主截图扫描品牌蓝按钮，真实输入注入）→ 配置对话框 ——
CaptureByHwnd(mainHwnd, Path.Combine(outDir, "1-main.png"));
var startCenter = ScanColorCenter(Path.Combine(outDir, "1-main.png"), red: false);
if (startCenter != null)
{
    ScreenClickOnWindow(mainHwnd, startCenter.Value.Item1, startCenter.Value.Item2, dbl: false);
    Console.WriteLine("[test] clicked start via screen injection");
}
Thread.Sleep(1200);
Console.WriteLine("[diag] responding=" + (window?.Responding ?? false));
Native.EnumWindows((h, l) =>
{
    var sb3 = new System.Text.StringBuilder(256);
    Native.GetWindowText(h, sb3, sb3.Capacity);
    var t3 = sb3.ToString();
    if (t3.Contains("记录") || t3.Contains("ClassNote") || t3.Contains("配置") || t3.Contains("错误"))
        Console.WriteLine($"[diag-wnd] pid={Native.GetWindowThreadProcessId2(h)} '{t3}'");
    return true;
}, IntPtr.Zero);

var setupHwnd = WaitForWindowTitle("开始记录 - 配置", 8);
Console.WriteLine(setupHwnd != IntPtr.Zero ? "[ok] setup dialog opened" : "[fail] setup dialog NOT opened");
if (setupHwnd != IntPtr.Zero)
{
    DumpRect(setupHwnd, "setup dialog");
    CaptureByHwnd(setupHwnd, Path.Combine(outDir, "2-setup.png"));
    var recCenter = ScanColorCenter(Path.Combine(outDir, "2-setup.png"), red: false);
    if (recCenter != null)
        ScreenClickOnWindow(setupHwnd, recCenter.Value.Item1, recCenter.Value.Item2, dbl: false);
    Console.WriteLine("[test] clicked 开始录制 via screen injection");
}
Thread.Sleep(3000);

// —— 录音页 ——
CaptureByHwnd(mainHwnd, Path.Combine(outDir, "3-recording.png"));

// —— 结束录音（扫描红色按钮）→ 回主页 ——
var endCenter = ScanColorCenter(Path.Combine(outDir, "3-recording.png"), red: true);
if (endCenter != null)
    ScreenClickOnWindow(mainHwnd, endCenter.Value.Item1, endCenter.Value.Item2, dbl: false);
Console.WriteLine("[test] clicked 结束录音 via screen injection");
Thread.Sleep(2600);
CaptureByHwnd(mainHwnd, Path.Combine(outDir, "4-after-stop.png"));

// —— 双击一条会话 → 笔记页 ——
try
{
    var root2 = AutomationElement.FromHandle(mainHwnd);
    var items = root2.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
    if (items.Count > 0)
    {
        var first = items.Count > 1 ? items[1] : items[0];
        Console.WriteLine($"using item {(items.Count > 1 ? 1 : 0)} of {items.Count}");
        var rc = first.Current.BoundingRectangle;
        Native.GetWindowRect(mainHwnd, out var wr);
        int cx2 = (int)(rc.X - wr.Left + rc.Width / 2);
        int cy2 = (int)(rc.Y - wr.Top + rc.Height / 2);
        ScreenClick(cx2 + wr.Left, cy2 + wr.Top, dbl: true);
        Console.WriteLine($"double-clicked session at screen {cx2 + wr.Left},{cy2 + wr.Top}");
    }
    else Console.WriteLine("no session rows found");
}
catch (Exception ex) { Console.WriteLine("dblclick failed: " + ex.Message); }

Thread.Sleep(3200);
CaptureByHwnd(mainHwnd, Path.Combine(outDir, "5-note.png"));

Console.WriteLine("DONE");
return 0;

int Fail(string msg) { Console.Error.WriteLine(msg); return 1; }

Process? WaitForMainWindow(string name, int seconds)
{
    for (int i = 0; i < seconds * 2; i++)
    {
        var list = Process.GetProcessesByName(name);
        var p = list.FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero
                                         && (x.MainWindowTitle?.Contains("ClassNote") ?? false));
        if (p != null) return p;
        Thread.Sleep(500);
    }
    return null;
}

IntPtr WaitForWindowTitle(string title, int seconds)
{
    for (int i = 0; i < seconds * 2; i++)
    {
        var found = IntPtr.Zero;
        Native.EnumWindows((h, l) =>
        {
            var sb = new System.Text.StringBuilder(256);
            Native.GetWindowText(h, sb, sb.Capacity);
            if (sb.ToString().Contains(title)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (found != IntPtr.Zero) return found;
        Thread.Sleep(500);
    }
    return IntPtr.Zero;
}

void DumpRect(IntPtr hwnd, string label)
{
    try
    {
        Native.GetWindowRect(hwnd, out var rc);
        Console.WriteLine($"[rect] {label} = {rc.Left},{rc.Top} {rc.Right - rc.Left}x{rc.Bottom - rc.Top}");
    }
    catch { }
}

AutomationElement? FindByName(AutomationElement root, string name)
{
    var c = new PropertyCondition(AutomationElement.NameProperty, name);
    return root.FindFirst(TreeScope.Descendants, c);
}

// 用位置挑选按钮：topmost+rightmost → 设置；bottom-left(bottommost+leftmost) → 开始记录
AutomationElement? FindButtonNear(AutomationElement root, bool topmost, bool rightmost)
{
    try
    {
        var btns = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        AutomationElement? best = null;
        double bestKey = rightmost ? double.MinValue : double.MaxValue;
        for (int i = 0; i < btns.Count; i++)
        {
            var rc = btns[i].Current.BoundingRectangle;
            if (rc.Width < 20 || rc.Height < 10) continue; // 忽略滚动条等小元素
            double key = rightmost ? rc.X + rc.Width : rc.Y + rc.Height;
            if (topmost && rc.Y + rc.Height > 200) continue; // 只考虑窗口上方区域
            if (!topmost && rc.Y < 300) continue;           // 只考虑窗口下方区域
            if ((rightmost && key > bestKey) || (!rightmost && key < bestKey)) { bestKey = key; best = btns[i]; }
        }
        return best;
    }
    catch { return null; }
}

AutomationElement? FindByControlTypeName(AutomationElement root, ControlType type, string namePart)
{
    var c1 = new PropertyCondition(AutomationElement.ControlTypeProperty, type);
    var c2 = new PropertyCondition(AutomationElement.NameProperty, namePart, PropertyConditionFlags.IgnoreCase);
    return root.FindFirst(TreeScope.Descendants, new AndCondition(c1, c2));
}

void Click(AutomationElement? el, IntPtr hwnd)
{
    if (el == null) { Console.WriteLine("  [click] target not found"); return; }
    try
    {
        var rc = el.Current.BoundingRectangle;
        int chrome = WindowChromeTop(hwnd);
        Native.GetWindowRect(hwnd, out var wr);
        int cx = (int)(rc.X - wr.Left + rc.Width / 2);
        int cy = (int)(rc.Y - wr.Top + rc.Height / 2) - chrome;
        SendMouse(hwnd, cx, cy, false);
        Console.WriteLine($"  [click] sent click to '{el.Current.Name}' (client {cx},{cy})");
    }
    catch (Exception ex) { Console.WriteLine("  [click] failed " + ex.Message); }
}

int WindowChromeTop(IntPtr hwnd)
{
    try
    {
        Native.GetWindowRect(hwnd, out var wr);
        Native.GetClientRect(hwnd, out var cr);
        int chrome = (wr.Bottom - wr.Top) - (cr.Bottom - cr.Top);
        return Math.Max(0, chrome);
    }
    catch { return 0; }
}

// 基于已捕获图像扫描品牌蓝/危险红按“行密度带”求按钮中心（比质心可靠，避免被小元素拉偏）
(int, int)? ScanColorCenter(string imagePath, bool red, int skipTopPx = 120)
{
    try
    {
        using var bmp = new System.Drawing.Bitmap(imagePath);
        int w = bmp.Width, h = bmp.Height;
        var rowCounts = new int[h];
        var colCounts = new int[w];
        for (int y = skipTopPx; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                bool hit = red
                    ? (c.R >= 0xD0 && c.R <= 0xF6 && c.G >= 0x30 && c.G <= 0x62 && c.B >= 0x30 && c.B <= 0x5E)
                    : (c.R >= 0x3A && c.R <= 0x5F && c.G >= 0x57 && c.G <= 0x8F && c.B >= 0xE8 && c.B <= 0xFF);
                if (hit) { rowCounts[y]++; colCounts[x]++; }
            }
        int maxRow = 0;
        for (int y = skipTopPx; y < h; y++) if (rowCounts[y] > rowCounts[maxRow]) maxRow = y;
        if (rowCounts[maxRow] < 30) { Console.WriteLine($"  [scan] {(red ? "red" : "blue")} no dense band (max={rowCounts[maxRow]})"); return null; }
        int top = maxRow, bot = maxRow, thr = Math.Max(6, rowCounts[maxRow] / 4);
        while (top > skipTopPx && rowCounts[top - 1] > thr) top--;
        while (bot < h - 1 && rowCounts[bot + 1] > thr) bot++;
        int cy = (top + bot) / 2;
        long sx = 0, sc = 0;
        for (int x = 0; x < w; x++)
            for (int y = top; y <= bot; y++)
            {
                var c = bmp.GetPixel(x, y);
                bool hit = red
                    ? (c.R >= 0xD0 && c.R <= 0xF6 && c.G >= 0x30 && c.G <= 0x62 && c.B >= 0x30 && c.B <= 0x5E)
                    : (c.R >= 0x3A && c.R <= 0x5F && c.G >= 0x57 && c.G <= 0x8F && c.B >= 0xE8 && c.B <= 0xFF);
                if (hit) { sx += x; sc++; }
            }
        int cx = sc > 0 ? (int)(sx / sc) : w / 2;
        Console.WriteLine($"  [scan] {(red ? "red" : "blue")} band y={top}..{bot} center=({cx},{cy})");
        return (cx, cy);
    }
    catch (Exception ex) { Console.WriteLine("  [scan] failed " + ex.Message); return null; }
}

void DumpButtons(IntPtr hwnd, string label)
{
    try
    {
        var el = AutomationElement.FromHandle(hwnd);
        var btns = el.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        Native.ClientToScreen(hwnd, out var pt0);
        Console.WriteLine($"[buttons:{label}] count={btns.Count}");
        for (int i = 0; i < btns.Count; i++)
        {
            var rc = btns[i].Current.BoundingRectangle;
            int cx = (int)(rc.X - pt0.X + rc.Width / 2), cy = (int)(rc.Y - pt0.Y + rc.Height / 2);
            Console.WriteLine($"  [{i}] name='{btns[i].Current.Name}' class={btns[i].Current.ClassName} center=({cx},{cy}) size={rc.Width}x{rc.Height}");
        }
    }
    catch (Exception ex) { Console.WriteLine("[buttons] failed: " + ex.Message); }
}

// 真实输入注入：SetCursorPos + mouse_event（WPF 会忽略 SendMessage 伪造的鼠标消息）
void ScreenClickOnWindow(IntPtr hwnd, int winRelX, int winRelY, bool dbl)
{
    Native.GetWindowRect(hwnd, out var wr);
    ScreenClick(winRelX + wr.Left, winRelY + wr.Top, dbl);
}

void ScreenClick(int sx, int sy, bool dbl)
{
    Native.SetCursorPos(sx, sy);
    Thread.Sleep(90);
    if (dbl)
    {
        Native.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero); // LEFTDOWN
        Native.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero); // LEFTUP
        Thread.Sleep(60);
        Native.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
        Native.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
    else
    {
        Native.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
        Native.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
    Thread.Sleep(260);
}

void CaptureByHwnd(IntPtr hwnd, string path)
{
    try
    {
        Native.GetWindowRect(hwnd, out var rc);
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) { Console.WriteLine("bad rect " + path); return; }
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        bool ok;
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            ok = Native.PrintWindow(hwnd, hdc, 0x2);
            g.ReleaseHdc(hdc);
        }
        if (!ok)
        {
            using var bmp2 = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g2 = Graphics.FromImage(bmp2))
                g2.CopyFromScreen(rc.Left, rc.Top, 0, 0, new Size(w, h));
            bmp2.Save(path, ImageFormat.Png);
        }
        else bmp.Save(path, ImageFormat.Png);
        Console.WriteLine($"captured {path} ({w}x{h}, pw={ok})");
    }
    catch (Exception ex) { Console.WriteLine("capture failed " + path + " : " + ex.Message); }
}

static class Native
{
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] public static extern void MouseEvent(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] public static extern uint GetWindowThreadProcessId2(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, out POINT lpPoint);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
