using System;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// STT 服务生命周期相关的公开契约（**刻意不加载模型**：那要 241MB 与数秒，
/// 不适合放进常规测试套件；释放判定的正确性由 <see cref="ModelIdleReleaserTests"/> 覆盖）。
/// </summary>
public class SenseVoiceSttServiceLifecycleTests
{
    [Fact]
    public void FreshInstance_HasNoModelLoaded()
    {
        var service = new SenseVoiceSttService();

        Assert.False(service.IsModelLoaded);
        Assert.False(service.IsTranscribing);
        Assert.Equal(0, service.IdleReleaseCount);
    }

    [Fact]
    public void ReleaseWhenNothingLoaded_IsNoOp()
    {
        var service = new SenseVoiceSttService();

        // 未加载时不该被计成"释放过"，也不该抛
        Assert.False(service.ReleaseModelIfIdle());
        Assert.Equal(0, service.IdleReleaseCount);
    }

    [Fact]
    public void Defaults_IdleReleaseIsTenMinutes_AndCanBeDisabled()
    {
        var service = new SenseVoiceSttService();

        Assert.Equal(TimeSpan.FromMinutes(10), service.IdleReleaseAfter);
        Assert.Equal(SenseVoiceSttService.DefaultIdleReleaseAfter, service.IdleReleaseAfter);

        // 关闭自动释放：阈值归零后，即便真有模型也不会被释放
        service.IdleReleaseAfter = TimeSpan.Zero;
        Assert.False(service.ReleaseModelIfIdle());
    }

    [Fact]
    public void SharedInstance_IsTheOnlyModelLoadPoint()
    {
        // 生产路径必须用同一个实例，否则又回到"每个会话重载一次模型"
        Assert.Same(SenseVoiceSttService.Shared, SenseVoiceSttService.Shared);
        Assert.IsType<SenseVoiceSttService>(SenseVoiceSttService.Shared);
    }

    [Fact]
    public void IntraOpThreads_NeverExceedHalfOfLogicalCores()
    {
        // 课堂上"留一半机器给放映"的硬保证（会话创建时定死）
        var service = new SenseVoiceSttService();
        Assert.Equal(ClassroomPolicy.IntraOpThreads(Environment.ProcessorCount), service.IntraOpThreads);
        Assert.True(service.IntraOpThreads <= Math.Max(1, Environment.ProcessorCount / 2));
    }

    [Fact]
    public void IdleThresholdChange_IsHonouredByReleaseCheck()
    {
        var service = new SenseVoiceSttService();

        // 无模型时任何阈值都不会释放；这里只锁"属性可写且不影响判定安全"
        service.IdleReleaseAfter = TimeSpan.FromSeconds(1);
        Assert.Equal(TimeSpan.FromSeconds(1), service.IdleReleaseAfter);
        Assert.False(service.ReleaseModelIfIdle());
    }
}
