using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 「语文课记录在上节物理课上，且物理课记录丢失」的回归测试。
///
/// 现场：物理 09:00–09:45、语文 10:00–10:45（相差 1 小时）。
/// 物理那节没有按时自动结束（"到点自动下课"是 DispatcherTimer，UI 线程一忙就被推迟），
/// 到 10:00 语文到点时原实现判定"正在录音"→ **整节跳过**，并且把这一节写进
/// "当天已触发"记忆，于是语文课再也没有自己的记录；语文课的内容全录进了
/// 那条仍标着"物理"的会话里。
///
/// 修复后的语义：能停就先停上一节再开始这一节；实在停不下来才放弃本次（留给下一个调度周期重试）。
/// </summary>
public class ScheduledRecordingHandoffTests
{
    [Fact]
    public void NoActiveRecording_StartsImmediately()
    {
        var decision = ScheduledRecordingHandoff.Decide(recordingActive: false, previousStopped: false);
        Assert.Equal(ScheduledHandoff.StartNow, decision);
        Assert.True(ScheduledRecordingHandoff.ShouldStartNewLesson(decision));
    }

    [Fact]
    public void ActiveRecording_Stopped_StartsNewLessonInsteadOfSkipping()
    {
        // 这一条就是本次事故的修复点：以前这里是"跳过"，于是整节记录消失
        var decision = ScheduledRecordingHandoff.Decide(recordingActive: true, previousStopped: true);
        Assert.Equal(ScheduledHandoff.StopPreviousThenStart, decision);
        Assert.True(ScheduledRecordingHandoff.ShouldStartNewLesson(decision));
    }

    [Fact]
    public void ActiveRecording_CouldNotStop_AbortsAndRetriesInsteadOfBurningTheLesson()
    {
        var decision = ScheduledRecordingHandoff.Decide(recordingActive: true, previousStopped: false);
        Assert.Equal(ScheduledHandoff.AbortAndRetry, decision);
        Assert.False(ScheduledRecordingHandoff.ShouldStartNewLesson(decision));
    }

    /// <summary>
    /// 语义守卫：唯一致命的分支是"正在录音却仍然开始新录音"——那会让两路采集抢同一个设备。
    /// </summary>
    [Fact]
    public void NeverStartsWhileRecordingIsStillActive()
    {
        Assert.False(ScheduledRecordingHandoff.ShouldStartNewLesson(
            ScheduledRecordingHandoff.Decide(recordingActive: true, previousStopped: false)));
    }

    /// <summary>
    /// 课表必须仍然允许"一天里同一节课只触发一次"，否则修复会退化成"每 10 秒重开一节课"。
    /// </summary>
    [Fact]
    public void ScheduleEngine_StillFiresEachEntryOnlyOncePerDay()
    {
        var entry = new ClassNote.Models.ScheduleEntry
        {
            Id = Guid.NewGuid(), WeekdayIndex = 0, StartMin = 10 * 60, EndMin = 10 * 60 + 45, Course = "语文",
        };
        var now = new DateTime(2026, 9, 14, 8, 0, 0);
        var engine = new ScheduleEngine(() => now);
        var entries = new List<ClassNote.Models.ScheduleEntry> { entry };

        now = new DateTime(2026, 9, 14, 10, 0, 0);
        Assert.Single(engine.Evaluate(entries));

        // 下一个调度周期（10 秒后）：不能再返回一遍
        now = new DateTime(2026, 9, 14, 10, 0, 10);
        Assert.Empty(engine.Evaluate(entries));
    }
}
