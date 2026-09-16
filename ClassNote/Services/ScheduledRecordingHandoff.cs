namespace ClassNote.Services;

/// <summary>
/// 定时录音在"上一节还没停、下一节已经到点"时的处置决定。
/// </summary>
public enum ScheduledHandoff
{
    /// <summary>直接开始这一节（没有冲突）。</summary>
    StartNow = 0,

    /// <summary>先把上一节录音结束掉，再开始这一节。</summary>
    StopPreviousThenStart = 1,

    /// <summary>本次不开始（上一节停不下来）；等下一个调度周期再评估。</summary>
    AbortAndRetry = 2,
}

/// <summary>
/// 定时记录的两节交接逻辑（纯函数，便于单测）。
///
/// 背景（用户现场报告）：「语文课记录在上节物理课上，且物理课记录丢失」。
/// 原实现只判断"是否正在录音"，正在录音就**整节跳过**并写进"当天已触发"记忆：
/// 上一节（物理）没能按时自动结束时，下一节（语文）就永远不会被录，
/// 而语文课的内容全录进了那条仍标着"物理"的会话里——课表上物理那一节看着有记录，
/// 语文那一节什么也没有。
///
/// 正确的做法是：下一节到点时，若上一节还在录，就**先把上一节结束掉**（它的内容仍归它自己，
/// 因为录音已经落盘、会话已经带上它自己的课程名），然后正常开始这一节。
/// 只有"确实停不下来"时才放弃本次，交给下一个调度周期重试（而不是把这一节从当天划掉）。
/// </summary>
public static class ScheduledRecordingHandoff
{
    /// <summary>
    /// 决定如何开始一节到点的课。
    /// </summary>
    /// <param name="recordingActive">此刻是否正在录音（<c>MainWindow.IsRecordingActive</c>）。</param>
    /// <param name="previousStopped">
    /// 尝试结束上一节录音的结果：true = 已停止；false = 仍在录音/收尾失败。
    /// 仅在 <paramref name="recordingActive"/> 为 true 时有意义。
    /// </param>
    public static ScheduledHandoff Decide(bool recordingActive, bool previousStopped)
    {
        if (!recordingActive)
            return ScheduledHandoff.StartNow;

        return previousStopped
            ? ScheduledHandoff.StopPreviousThenStart
            : ScheduledHandoff.AbortAndRetry;
    }

    /// <summary>该决定是否应当真的创建并开始一节新录音。</summary>
    public static bool ShouldStartNewLesson(ScheduledHandoff decision)
        => decision is ScheduledHandoff.StartNow or ScheduledHandoff.StopPreviousThenStart;
}
