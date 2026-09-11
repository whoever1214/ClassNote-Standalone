using ClassNote.Models;

namespace ClassNote.Services;

/// <summary>一次课表触发的会话（到点应开始录音）。</summary>
public sealed record ScheduleOccurrence(ScheduleEntry Entry, DateTime Start, DateTime End, bool IsLate);

/// <summary>
/// 每周课表 → 定时录音 的纯逻辑引擎（无 UI / 无存储依赖，便于单测）。
/// 职责：给定条目与当前时间，判断哪些课的"到点时刻"已到达且尚未触发过，
/// 返回应触发开始录音的课表条目。错过策略：
///   1. 录音开始的时刻必须在本程序进程启动之后（避免用户事后打开应用时误触发）；
///   2. 若唤醒 / 卡顿错过开始时刻，仅当错过 ≤ <see cref="MissedGrace"/>（默认 10 分钟）时补触发，
///      否则静默跳过本次；
///   3. 同一周几同一条目一天只触发一次（触发后当天不再重复判定）。
/// 触发判定的上界是条目结束时刻——课已结束时不会再补触发。
/// </summary>
public sealed class ScheduleEngine
{
    private readonly Func<DateTime> _clock;
    private readonly DateTime _appStartedAt;

    /// <summary>唤醒/繁忙错过开始时刻后，允许补触发的最大滞后（默认 10 分钟）。</summary>
    public TimeSpan MissedGrace { get; init; } = TimeSpan.FromMinutes(10);

    private readonly Dictionary<Guid, DateTime> _firedOn = new();

    /// <summary>最后一次判定的条目来源快照对应的判定时间（用于测试与诊断）。</summary>
    public DateTime LastEvaluatedAt { get; private set; }

    public ScheduleEngine(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.Now);
        _appStartedAt = _clock();
    }

    /// <summary>触发容差：判定运行与开始时刻几乎重合时视为准点开始。</summary>
    private static readonly TimeSpan _startTolerance = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 评估当前时刻应触发的课表条目。已触发过的条目当天不会重复返回。
    /// 判定副作用会记录"已触发"，因此调用方即使放弃触发（如正在录音）也不应再期待重试。
    /// </summary>
    public IReadOnlyList<ScheduleOccurrence> Evaluate(IReadOnlyList<ScheduleEntry> entries)
    {
        var now = _clock();
        LastEvaluatedAt = now;
        var today = now.Date;
        var todayIdx = ScheduleEntry.WeekdayIndexOf(now.DayOfWeek);

        var due = new List<ScheduleOccurrence>();
        foreach (var entry in entries)
        {
            if (!entry.Enabled)
                continue;
            if (entry.WeekdayIndex != todayIdx)
                continue;

            // 当天已触发过 → 跳过（跨周同条目日期不同，自然会在下一周再次触发）
            if (_firedOn.TryGetValue(entry.Id, out var firedDay) && firedDay == today)
                continue;

            var start = today.AddMinutes(entry.StartMin);
            var end = today.AddMinutes(entry.EndMin);
            if (now < start)
                continue;               // 还没到点
            if (now >= end)
                continue;               // 课已结束，不再补触发

            // 开始时刻早于本进程启动：属于"事后打开应用"，不自动开始（避免误录）
            if (start < _appStartedAt - _startTolerance)
                continue;

            // 开始时刻已错过（唤醒/卡顿/启动竞态），超过宽限期 → 静默跳过本次
            var lateBy = now - start;
            if (lateBy > MissedGrace)
                continue;

            _firedOn[entry.Id] = today;
            due.Add(new ScheduleOccurrence(entry, start, end, IsLate: lateBy > _startTolerance));
        }
        return due;
    }

    /// <summary>清空"已触发"记忆（测试用；生产运行期不需要）。</summary>
    public void ResetFiredMemory() => _firedOn.Clear();
}
