using System;
using System.IO;
using System.Threading;
using AoiTwin.Rendering.Viewport;
using Xunit;

namespace AoiTwin.Regression.Tests;

/// <summary>
/// TA.17 回放回归套件（DoD §6：覆盖 FR-M2-03~07）。判据来自计划原文：
/// 帧数一致 / 末帧位姿接近期望 / 上传队列消费完毕。
/// </summary>
public sealed class TwinVisualRegressionTests
{
    [Fact]
    public void Replay_drives_motion_optics_wafer_and_defects_to_expected_state()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aoi-regression-" + Guid.NewGuid().ToString("N"));
        try
        {
            TwinVisualRegressionReport? report = null;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var viewport = new TwinViewport3D();
                    report = TwinVisualRegression.Run(
                        dir,
                        AoiTwin.App.ViewModels.AssetPackageLocator.LocateAssetsRoot(),
                        viewport);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    // STA 线程收尾必须关掉本线程的 Dispatcher：否则 WPF 的 MediaContext 计时器
                    // 会留在进程里，与后续（同进程）创建 Application 的冒烟用例互相拖死
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "回放回归超时");
            if (failure is not null)
            {
                throw new Xunit.Sdk.XunitException($"回放回归内部失败: {failure}");
            }

            Assert.NotNull(report);

            // ① 帧数一致：12 片 × 3 帧，全部走完
            Assert.Equal(TwinVisualRegression.RecordedFrameCount, report!.RecordedFrames);
            Assert.Equal(report.RecordedFrames, report.ReplayedFrames);

            // ② 末帧位姿接近期望：第 11 帧轴值 1_100_000 µm → 1.1 m
            Assert.Equal(TwinVisualRegression.ExpectedStageOffsetX, report.StageOffsetX, 6);

            // ③ 上传队列消费完毕：12 片全入队、全被渲染线程取走、无挤压
            Assert.Equal(TwinVisualRegression.WaferFrames, report.WaferEnqueued);
            Assert.Equal(TwinVisualRegression.WaferFrames, report.WaferTaken);
            Assert.Equal(0, report.WaferDropped);

            // 缺陷点云：整片追加不合并（12 × 2 = 24 点）
            Assert.Equal(TwinVisualRegression.WaferFrames * TwinVisualRegression.DefectsPerWafer, report.DefectPoints);

            // 光学变色：8 个通道各被点亮过一次，且末帧通道色在叠加层上
            Assert.Contains(TwinVisualRegression.Channel8Color, report.OverlayColors);
            Assert.Contains(TwinVisualRegression.LastFrameChannelColor, report.OverlayColors);
            Assert.True(report.OverlayColors.Count >= 8, $"叠加层颜色 {report.OverlayColors.Count} 个，少于 8 通道");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
