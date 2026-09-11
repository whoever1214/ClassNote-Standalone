using System.IO;
using Microsoft.Win32;

namespace ClassNote.Services;

/// <summary>
/// 开机自启注册服务：向 HKCU\...\Run 写入/删除 ClassNote 启动项。
/// 启动参数 --autostart 让应用在登录后直接驻留托盘（不弹主窗口），
/// 由 MainWindow 在 Loaded 时按 ScheduleEnabled 决定是否隐藏到托盘。
/// 注册表写入失败（如受限环境）时静默降级，仅影响自启，不影响主功能。
/// </summary>
public static class StartupRegistrar
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClassNote";

    /// <summary>当前是否已注册开机自启。</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value
                   && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 注册或移除开机自启（带 --autostart 参数，登录后驻留托盘）。
    /// 返回是否成功写入/删除（注册表被系统策略拦截时返回 false）。
    /// </summary>
    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null)
                return false;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                    return false;
                // 路径带引号；参数 --autostart 表示启动后驻留托盘
                key.SetValue(ValueName, $"\"{exe}\" --autostart");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            // 注册表不可写（策略/权限）时告知调用方
            return false;
        }
    }

    /// <summary>应用启动时自修复：设置说自启但注册表丢了 → 重写。用于数据一致性。</summary>
    public static void SelfRepair()
    {
        try
        {
            if (AppSettings.Instance.Snapshot().ScheduleLaunchAtStartup && !IsRegistered())
                Apply(enabled: true);
        }
        catch
        {
            // 静默
        }
    }
}
