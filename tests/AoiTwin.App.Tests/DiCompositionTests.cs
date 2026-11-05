using System;
using System.IO;
using System.Threading;
using AoiTwin.App;
using AoiTwin.App.Services;
using AoiTwin.App.ViewModels;
using AoiTwin.Core.Bus;
using AoiTwin.ViewModel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AoiTwin.App.Tests;

/// <summary>
/// DI 装配一致性（TA.20 回归门禁）。
///
/// 背景：M2 的实现一度是「DI 注册了位姿引擎与配色，但 <see cref="TwinSceneViewModel"/> 走无参构造
/// 又自建了一套 hub + 恒等标定引擎」——界面能开、单测全绿，真机上数据却到不了 3D。
/// 这类「假接线」只能靠**解析真实容器**来拦：断言场景 VM 用的就是容器里的那个 hub，
/// 并且轴数据经容器 hub 发布后能按 µm→m 标定出现在视图模型上。
/// </summary>
public sealed class DiCompositionTests
{
    [Fact]
    public void Scene_view_model_uses_container_hub_and_calibrated_engine()
    {
        RunOnStaThread(() =>
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAoiTwinApp(); // 与 App.OnStartup 同一份注册表
            using var provider = services.BuildServiceProvider();

            var hub = provider.GetRequiredService<IRealtimeHub>();
            var twin = provider.GetRequiredService<TwinSceneViewModel>();

            // ① 没有自建总线：否则适配器/回放发布的数据永远到不了 3D
            Assert.Same(hub, twin.VisualHub);

            // ② 引擎与标定接上了：250 000 µm → 0.25 m（恒等降级会得到 250 000）
            twin.Visual.GetMotionPart("STAGE");
            hub.Publish(TwinTopic.Motion, new AxisSnapshot("STAGE", "X", 250_000, 0, DateTimeOffset.UtcNow));

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (twin.Visual.GetMotionPart("STAGE").ReadLatest().Matrix[3] == 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
                twin.Visual.RefreshMotion(DateTimeOffset.UtcNow);
            }

            var pose = twin.Visual.GetMotionPart("STAGE").ReadLatest();
            Assert.InRange(pose.Matrix[3], 0.24, 0.26);
        });
    }

    [Fact]
    public void Visual_view_model_gets_motion_source_from_container()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAoiTwinApp();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IMotionPoseSource>());

        var visual = provider.GetRequiredService<TwinVisualViewModel>();
        var engine = provider.GetRequiredService<IMotionPoseSource>();

        // 同一个引擎实例：视觉 VM 里订阅的轴数据要能驱动容器的引擎
        engine.OnAxis(new AxisSnapshot("STAGE", "X", 500_000, 0, DateTimeOffset.UtcNow.AddSeconds(-1)));
        var pose = engine.GetPose("STAGE", DateTimeOffset.UtcNow);
        Assert.NotNull(pose);
        Assert.InRange(pose!.Matrix[3], 0.49, 0.51);

        // 视觉 VM 的刷新走的是同一个引擎（否则这里的位移不会出现）
        var part = visual.GetMotionPart("STAGE");
        visual.RefreshMotion(DateTimeOffset.UtcNow);
        Assert.InRange(part.ReadLatest().Matrix[3], 0.49, 0.51);
    }

    [Fact]
    public void Health_page_subscribes_to_container_hub()
    {
        RunOnStaThread(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"di-health-{Guid.NewGuid():N}");
            try
            {
                var services = new ServiceCollection();
                services.AddLogging();
                // 数据落到临时目录：测试不得写用户真实时序库
                services.AddAoiTwinApp(new AnalyticsRuntimeOptions(
                    StoreRoot: Path.Combine(dir, "timeseries"),
                    ExportDirectory: Path.Combine(dir, "exports"),
                    OptionsFilePath: null,
                    AutoStart: false));

                using var provider = services.BuildServiceProvider();

                var hub = provider.GetRequiredService<IRealtimeHub>();
                var health = provider.GetRequiredService<HealthAnalyticsViewModel>();

                // 发到容器 hub 上的一条轴数据，必须出现在健康页的档案里
                // （否则就是又一个「界面能开、单测全绿、真机没数据」的假接线）
                hub.Publish(TwinTopic.Motion, new AxisSnapshot("STAGE", "X", 12_000, 0, DateTimeOffset.UtcNow));
                hub.Publish(TwinTopic.Status, new StatusEvent("STAGE", "E1042", Severity.Alarm, null, DateTimeOffset.UtcNow));

                health.Refresh();
                var row = Assert.Single(health.Parts);
                Assert.Equal("STAGE", row.PartId);
                Assert.True(row.Score < 100, "报警扣分未生效：评分链路可能没读到档案");

                // 落盘也走容器里的同一个 Sink：刷一次再加一条，条数应增加
                Assert.True(provider.GetRequiredService<AnalyticsRuntime>().Flush() >= 2);
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
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA 线程超时");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA 线程内断言失败: {failure}");
        }
    }
}
