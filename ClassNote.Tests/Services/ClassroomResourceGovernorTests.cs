using System;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 课堂资源让路策略的测试：这是"录音期间给 PPT / 视频留足资源"这条硬约束的可验证表达。
/// </summary>
public class ClassroomResourceGovernorTests
{
    /// <summary>可编排的假探测（不碰真实系统状态）。</summary>
    private sealed class FakeProbe : IClassroomResourceProbe
    {
        public bool Fullscreen { get; set; }
        public bool Video { get; set; }
        public bool Battery { get; set; }
        public double Cpu { get; set; }

        public bool IsFullscreenAppForeground() => Fullscreen;
        public bool IsScreenVideoPlaying() => Video;
        public bool IsOnBattery() => Battery;
        public double OwnCpuUtilization() => Cpu;
    }

    [Fact]
    public void IntraOpThreads_NeverExceedsHalfOfLogicalCores()
    {
        // 这条是"留一半核"的硬保证：ORT 线程池在会话创建时定死，所以必须在这里成立
        Assert.Equal(4, ClassroomPolicy.IntraOpThreads(8));
        Assert.Equal(2, ClassroomPolicy.IntraOpThreads(4));
        Assert.Equal(1, ClassroomPolicy.IntraOpThreads(1));   // 单核也不为 0
        Assert.Equal(8, ClassroomPolicy.IntraOpThreads(16));
        Assert.True(ClassroomPolicy.IntraOpThreads(6) <= 3);
    }

    [Fact]
    public void CooldownMs_FollowsDutyCycle()
    {
        Assert.Equal(0, ClassroomPolicy.CooldownMs(1000, 1.0));      // 全力：不歇
        Assert.Equal(1000, ClassroomPolicy.CooldownMs(1000, 0.5));   // 一半：跑 1 秒歇 1 秒
        Assert.Equal(3000, ClassroomPolicy.CooldownMs(1000, 0.25));  // 四分之一：跑 1 秒歇 3 秒
        Assert.Equal(0, ClassroomPolicy.CooldownMs(1000, 0));        // 暂停：由调用方另走暂停路径
    }

    [Fact]
    public void Policy_PresentingGetsMuchLessBudgetThanIdle()
    {
        var idle = ClassroomLoadPolicy.For(ClassroomTranscriptionMode.Auto, ClassroomLoad.Idle);
        var presenting = ClassroomLoadPolicy.For(ClassroomTranscriptionMode.Auto, ClassroomLoad.Presenting);
        var batteryPresenting = ClassroomLoadPolicy.For(ClassroomTranscriptionMode.Auto, ClassroomLoad.PresentingOnBattery);

        Assert.True(presenting.Duty < idle.Duty);
        Assert.True(batteryPresenting.Duty < presenting.Duty);
        Assert.True(presenting.MaxSelfUtilization <= 0.15);
        Assert.Contains("放映", presenting.Reason);
    }

    [Fact]
    public void Policy_OffMeansPaused()
    {
        var policy = ClassroomLoadPolicy.For(ClassroomTranscriptionMode.Off, ClassroomLoad.Idle);
        Assert.True(policy.PauseRequested);
        Assert.Equal(0, policy.Duty);
    }

    [Fact]
    public void Governor_IdleMachine_UsesConfiguredDuty()
    {
        var probe = new FakeProbe { Cpu = 0.05 };
        using var governor = new ClassroomResourceGovernor(probe, ClassroomTranscriptionMode.Auto);

        var budget = governor.Current;
        Assert.False(budget.IsPaused);
        Assert.False(budget.IsYielding);
        Assert.Equal(0.6, budget.Duty, 3); // 自动档空闲时的占空比
    }

    [Fact]
    public void Governor_FullscreenPresentation_YieldsWithoutStopping()
    {
        var probe = new FakeProbe { Fullscreen = true, Cpu = 0.05 };
        using var governor = new ClassroomResourceGovernor(probe, ClassroomTranscriptionMode.Auto);

        var budget = governor.Current;
        Assert.True(budget.IsYielding);
        Assert.False(budget.IsPaused);   // 让路 ≠ 停摆：仍然用很小的占空比往前挪
        Assert.True(budget.Duty > 0);
        Assert.True(budget.Duty <= 0.15);
    }

    [Fact]
    public void Governor_VideoPlayback_IsTreatedAsPresenting()
    {
        var probe = new FakeProbe { Video = true };
        using var governor = new ClassroomResourceGovernor(probe, ClassroomTranscriptionMode.Auto);
        Assert.True(governor.Current.IsYielding);
    }

    [Fact]
    public void Governor_Battery_ThrottlesHarder()
    {
        var ac = new FakeProbe { Cpu = 0.05 };
        var battery = new FakeProbe { Battery = true, Cpu = 0.05 };
        using var acGovernor = new ClassroomResourceGovernor(ac, ClassroomTranscriptionMode.Auto);
        using var batteryGovernor = new ClassroomResourceGovernor(battery, ClassroomTranscriptionMode.Auto);

        Assert.True(batteryGovernor.Current.Duty < acGovernor.Current.Duty);
        Assert.Contains("电池", batteryGovernor.Current.Reason);
    }

    [Fact]
    public void Governor_OverBudget_ScalesDutyDown()
    {
        // 自身 CPU 超过策略上限时按比例折减：实测一回落就自动恢复，不引入状态机
        var idle = new FakeProbe { Cpu = 0.02 };
        using var idleGovernor = new ClassroomResourceGovernor(idle, ClassroomTranscriptionMode.Auto);
        double idleDuty = idleGovernor.Current.Duty;

        var hot = new FakeProbe { Cpu = 0.9 }; // 远超空闲档 0.5 的上限
        using var hotGovernor = new ClassroomResourceGovernor(hot, ClassroomTranscriptionMode.Auto);
        double hotDuty = hotGovernor.Current.Duty;

        Assert.True(hotDuty < idleDuty, "自身 CPU 超预算时必须自动折减占空比");
        Assert.Equal(idleDuty * (0.5 / 0.9), hotDuty, 3);
    }

    [Fact]
    public void Governor_UserPause_StopsCompletely()
    {
        var probe = new FakeProbe();
        using var governor = new ClassroomResourceGovernor(probe, ClassroomTranscriptionMode.Auto);

        governor.SetUserPaused(true);
        Assert.True(governor.Current.IsPaused);

        governor.SetUserPaused(false);
        Assert.False(governor.Current.IsPaused);
    }

    [Fact]
    public void Governor_OffMode_IsPausedFromTheStart()
    {
        var probe = new FakeProbe();
        using var governor = new ClassroomResourceGovernor(probe, ClassroomTranscriptionMode.Off);
        Assert.True(governor.Current.IsPaused);
    }

    [Fact]
    public void ModeStorage_RoundTrips_AndFallsBackToAuto()
    {
        foreach (var mode in new[]
                 {
                     ClassroomTranscriptionMode.Off,
                     ClassroomTranscriptionMode.Auto,
                     ClassroomTranscriptionMode.Aggressive,
                 })
        {
            Assert.Equal(mode, ClassroomTranscriptionModes.FromStorage(mode.ToString()));
        }
        Assert.Equal(ClassroomTranscriptionMode.Auto, ClassroomTranscriptionModes.FromStorage(null));
        Assert.Equal(ClassroomTranscriptionMode.Auto, ClassroomTranscriptionModes.FromStorage("垃圾值"));
        Assert.Equal(ClassroomTranscriptionMode.Auto, ClassroomTranscriptionModes.FromStorage(""));
        Assert.Equal(3, ClassroomTranscriptionModes.DisplayNames.Length); // 界面下拉项与枚举一一对应
    }
}
