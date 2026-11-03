using AoiTwin.Rendering.Diagnostics;
using Xunit;

namespace AoiTwin.Rendering.Tests;

/// <summary>
/// T1.3 帧时间采样器：NFR-01（60fps 基线）与 NFR-02（无 &gt;100ms 长帧）的判据都建立在这套统计上，
/// 因此它的口径必须可单测——实际渲染帧率由探针在真机上验证，纯计算部分不能靠肉眼。
/// </summary>
public class FrameTimeSamplerTests
{
    private const double Frame60 = 1000.0 / 60; // 16.67ms

    [Fact]
    public void Empty_sampler_reports_no_samples()
    {
        var sampler = new FrameTimeSampler();

        var stats = sampler.Sample();

        Assert.False(stats.HasSamples);
        Assert.Equal(FrameTimeStats.Empty, stats);
        Assert.Equal(0, stats.AverageFps);
    }

    [Fact]
    public void Constant_60fps_intervals_report_60fps()
    {
        var sampler = new FrameTimeSampler(windowSize: 120);

        for (var i = 0; i < 120; i++)
        {
            sampler.RecordInterval(Frame60);
        }

        var stats = sampler.Sample();

        Assert.True(stats.HasSamples);
        Assert.Equal(120, stats.FrameCount);
        Assert.InRange(stats.AverageFps, 59.5, 60.5);
        Assert.InRange(stats.AverageFrameMs, 16.6, 16.7);
        Assert.InRange(stats.P95FrameMs, 16.6, 16.7);
        Assert.InRange(stats.MaxFrameMs, 16.6, 16.7);
    }

    [Fact]
    public void Single_long_frame_shows_in_max_but_not_p95()
    {
        var sampler = new FrameTimeSampler(windowSize: 100);

        for (var i = 0; i < 99; i++)
        {
            sampler.RecordInterval(Frame60);
        }

        sampler.RecordInterval(120); // 一次 120ms 卡顿（模拟 GLB 解析/GC）

        var stats = sampler.Sample();

        Assert.Equal(120, stats.MaxFrameMs, 3);
        Assert.InRange(stats.P95FrameMs, 16.6, 16.7); // 1% 的极端值不污染 P95
        Assert.InRange(stats.AverageFps, 55, 60);
    }

    [Fact]
    public void Rolling_window_drops_stale_slow_frames()
    {
        var sampler = new FrameTimeSampler(windowSize: 10);

        for (var i = 0; i < 10; i++)
        {
            sampler.RecordInterval(100); // 卡顿期：10fps
        }

        Assert.InRange(sampler.Sample().AverageFps, 9.9, 10.1);

        for (var i = 0; i < 10; i++)
        {
            sampler.RecordInterval(Frame60); // 恢复 60fps
        }

        var stats = sampler.Sample();

        Assert.InRange(stats.AverageFps, 59.5, 60.5);
        Assert.InRange(stats.MaxFrameMs, 16.6, 16.7);
        Assert.Equal(20, stats.FrameCount); // 累计计数不因窗口回绕而丢失
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_intervals_are_ignored(double milliseconds)
    {
        var sampler = new FrameTimeSampler();

        sampler.RecordInterval(milliseconds);
        sampler.RecordInterval(Frame60);

        var stats = sampler.Sample();
        Assert.Equal(1, stats.FrameCount);
        Assert.InRange(stats.AverageFrameMs, 16.6, 16.7);
    }

    [Fact]
    public void Reset_clears_window_and_counters()
    {
        var sampler = new FrameTimeSampler(windowSize: 4);
        sampler.RecordInterval(Frame60);
        sampler.RecordInterval(Frame60);

        sampler.Reset();

        var stats = sampler.Sample();
        Assert.False(stats.HasSamples);
        Assert.Equal(0, stats.FrameCount);
    }

    [Fact]
    public void Timestamp_recording_skips_first_frame_as_baseline()
    {
        var sampler = new FrameTimeSampler();
        var ticksPerFrame = (long)(System.Diagnostics.Stopwatch.Frequency / 60.0);

        sampler.RecordTimestamp(1000); // 基线，不产生样本
        Assert.Equal(0, sampler.Sample().FrameCount);

        sampler.RecordTimestamp(1000 + ticksPerFrame);
        var stats = sampler.Sample();

        Assert.Equal(1, stats.FrameCount);
        Assert.InRange(stats.AverageFrameMs, 16.0, 17.5);
    }

    [Fact]
    public void Window_size_below_two_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameTimeSampler(windowSize: 1));
    }
}
