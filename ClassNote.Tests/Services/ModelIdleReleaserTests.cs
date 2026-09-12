using System;
using System.Threading;
using System.Threading.Tasks;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 模型闲置释放策略的测试。
///
/// 这段逻辑单靠人工验证代价很高（要真的加载 241MB 模型、还要恰好在"有推理在跑"时触发检查），
/// 所以时间与"是否在忙"都做成了可注入的：这里全部是确定性断言，不加载模型。
///
/// 最要紧的一条是 <see cref="Busy_NeverReleases_EvenLongAfterThreshold"/>：
/// 推理进行中释放 ONNX 会话会让正在跑的 <c>InferenceSession.Run</c> 直接崩——
/// 课堂录音中途崩一次就是整条音轨白录，所以这条边界必须有测试守着。
/// </summary>
public class ModelIdleReleaserTests
{
    /// <summary>假时钟：手动推进时间。</summary>
    private sealed class FakeClock
    {
        private DateTime _now = new(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);
        public DateTime Now => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private static (ModelIdleReleaser Releaser, FakeClock Clock, Func<int> Count) Make(
        TimeSpan? idleAfter = null, bool enabled = true)
    {
        var clock = new FakeClock();
        int releases = 0;
        var releaser = new ModelIdleReleaser(() => releases++, () => clock.Now,
            idleAfter ?? TimeSpan.FromMinutes(10), enabled);
        return (releaser, clock, () => releases);
    }

    [Fact]
    public void BeforeThreshold_DoesNotRelease()
    {
        var (releaser, clock, count) = Make();

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(releaser.TryRelease());
        Assert.Equal(0, count());
    }

    [Fact]
    public void AtThreshold_Releases_Once()
    {
        var (releaser, clock, count) = Make();

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(releaser.TryRelease());
        Assert.Equal(1, count());
        Assert.Equal(1, releaser.ReleaseCount);

        // 释放后活动时间被重置：紧接着再检查不该重复释放
        Assert.False(releaser.TryRelease());
        Assert.Equal(1, count());
    }

    [Fact]
    public void ActivityResetsTheIdleWindow()
    {
        var (releaser, clock, count) = Make();

        clock.Advance(TimeSpan.FromMinutes(9));
        releaser.Touch();                       // 例如：又转写了一块
        clock.Advance(TimeSpan.FromMinutes(9)); // 距上次活动 9 分钟，仍未到阈值

        Assert.False(releaser.TryRelease());
        Assert.Equal(0, count());

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(releaser.TryRelease());
    }

    [Fact]
    public void Busy_NeverReleases_EvenLongAfterThreshold()
    {
        var (releaser, clock, count) = Make();

        releaser.EnterActivity();               // 推理开始
        clock.Advance(TimeSpan.FromHours(2));   // 一整节课都在跑

        Assert.True(releaser.IsBusy);
        Assert.False(releaser.TryRelease());
        Assert.Equal(0, count());

        releaser.ExitActivity();                // 推理结束
        Assert.False(releaser.IsBusy);

        // 退出时会 Touch，所以还要再等一个阈值；这是刻意的：刚跑完就释放会立刻又用上
        Assert.False(releaser.TryRelease());
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(releaser.TryRelease());
        Assert.Equal(1, count());
    }

    [Fact]
    public void NestedActivity_KeepsBusyUntilAllExit()
    {
        // 整段转写内部会逐块调用：嵌套计数必须仍然表达"有推理在跑"
        var (releaser, clock, count) = Make();

        releaser.EnterActivity();
        releaser.EnterActivity();
        releaser.ExitActivity();
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(releaser.IsBusy);
        Assert.False(releaser.TryRelease());

        releaser.ExitActivity();
        Assert.False(releaser.IsBusy);
        Assert.Equal(0, count());
    }

    [Fact]
    public void Disabled_NeverReleases()
    {
        var (releaser, clock, count) = Make(enabled: false);

        clock.Advance(TimeSpan.FromDays(1));
        Assert.False(releaser.TryRelease());
        Assert.Equal(0, count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveThreshold_MeansNoAutoRelease(int minutes)
    {
        // 阈值设为 0/负数 = 关闭自动释放（想让模型一直常驻的机器可以这样配）
        var (releaser, clock, count) = Make(TimeSpan.FromMinutes(minutes));

        clock.Advance(TimeSpan.FromDays(1));
        Assert.False(releaser.TryRelease());
        Assert.Equal(0, count());
    }

    [Fact]
    public void ShouldRelease_Matrix()
    {
        var threshold = TimeSpan.FromMinutes(10);

        Assert.False(ModelIdleReleaser.ShouldRelease(busy: true, threshold, threshold));                 // 忙 → 不释放
        Assert.False(ModelIdleReleaser.ShouldRelease(false, TimeSpan.FromMinutes(9), threshold));        // 未到阈值
        Assert.True(ModelIdleReleaser.ShouldRelease(false, threshold, threshold));                       // 正好到阈值
        Assert.True(ModelIdleReleaser.ShouldRelease(false, TimeSpan.FromHours(1), threshold));           // 远超
        Assert.False(ModelIdleReleaser.ShouldRelease(false, TimeSpan.FromHours(1), TimeSpan.Zero));      // 关闭
    }

    [Fact]
    public void ReleaseFailure_IsNotCounted_AndRetriedNextRound()
    {
        // 释放失败（例如 Dispose 抛异常）不能让定时器回调炸掉进程，也不该被计入"已释放"
        var clock = new FakeClock();
        int attempts = 0;
        var releaser = new ModelIdleReleaser(() =>
        {
            attempts++;
            throw new InvalidOperationException("模拟释放失败");
        }, () => clock.Now, TimeSpan.FromMinutes(1));

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(releaser.TryRelease());
        Assert.Equal(1, attempts);
        Assert.Equal(0, releaser.ReleaseCount);

        // 下一轮仍会再试
        Assert.False(releaser.TryRelease());
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void ConcurrentActivityAndChecks_NeverReleaseWhileBusy()
    {
        // 竞态冒烟：一边反复进出活动、一边反复检查，任何一次 release 发生时都必须"当时不忙"
        var clock = new FakeClock { };
        var idleAfter = TimeSpan.FromTicks(1);   // 阈值极小，让检查尽可能频繁地"想释放"
        int releasesWhileBusy = 0;
        int inFlight = 0;
        var releaser = new ModelIdleReleaser(
            () => { if (Volatile.Read(ref inFlight) > 0) Interlocked.Increment(ref releasesWhileBusy); },
            () => clock.Now, idleAfter);

        var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var workers = new Task[4];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    Interlocked.Increment(ref inFlight);
                    releaser.EnterActivity();
                    try { Thread.SpinWait(200); }
                    finally
                    {
                        releaser.ExitActivity();
                        Interlocked.Decrement(ref inFlight);
                    }
                }
            });
        }
        var checker = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                releaser.TryRelease();
        });

        Task.WaitAll(workers);
        checker.Wait();

        Assert.Equal(0, releasesWhileBusy);
    }
}
