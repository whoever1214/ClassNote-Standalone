using System;
using System.Threading.Tasks;

namespace ClassNote.Services;

/// <summary>
/// 原料过期清理：非收藏会话的音频 / 截图 / 转写块超过保留天数就删掉，笔记始终保留。
///
/// 为什么需要：一节课 40 分钟的原料是"73 MB 音频 + 50 MB 截图 + 6 KB 转写"，
/// 一个学期（约 500 节）能到 60 GB，而其中绝大多数课用户再也不会重新生成。
/// 但**收藏过的课**是用户明确要留的，不能被自动清理碰。
///
/// 只在启动后跑一次（后台线程），失败不打扰用户：清理失败最坏也只是磁盘多占几天。
/// </summary>
public static class ContentRetention
{
    /// <summary>按设置执行一次清理，返回被清理的会话数（≤0 天 = 不清理，返回 0）。</summary>
    public static int RunOnce(AppSettingsData settings, LocalRepository? repository = null,
        DateTime? utcNow = null, IProgress<string>? progress = null)
    {
        if (settings == null || settings.ContentRetentionDays <= 0)
            return 0;

        var repo = repository ?? LocalRepository.Instance;
        try
        {
            var result = repo.PurgeExpiredContent(settings.ContentRetentionDays, utcNow);
            if (result.SessionsPurged > 0)
            {
                string message = $"已清理 {result.SessionsPurged} 节课的音频与截图" +
                                 $"（留存 {settings.ContentRetentionDays} 天，收藏的课除外），" +
                                 $"释放 {result.BytesFreed / 1024.0 / 1024.0:0.#} MB";
                progress?.Report(message);
                System.Diagnostics.Debug.WriteLine("[ContentRetention] " + message);
            }
            return result.SessionsPurged;
        }
        catch (Exception ex)
        {
            // 清理是后台维护动作：失败只留痕，绝不弹窗打断用户
            System.Diagnostics.Debug.WriteLine($"[ContentRetention] 清理失败: {ex}");
            return 0;
        }
    }

    /// <summary>启动后异步跑一次（不阻塞 UI）。</summary>
    public static Task<int> RunInBackgroundAsync(AppSettingsData settings)
        => Task.Run(() => RunOnce(settings));
}
