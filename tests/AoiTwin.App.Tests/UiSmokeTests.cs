using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using AoiTwin.Rendering.Lod;
using System.Windows.Threading;
using AoiTwin.App.ViewModels;
using AoiTwin.App.Views;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Recording;
using AoiTwin.Rendering.Camera;
using AoiTwin.Rendering.Scene;
using Xunit;

namespace AoiTwin.App.Tests;

/// <summary>
/// UI 装载冒烟测试（T3.2/T3.3）：XAML 的 StaticResource / 模板 / 绑定错误只在运行时暴露，
/// 编译期无法发现。此处以 STA 线程真实构造应用资源与全部视图并强制布局，
/// 把「界面一打开就崩」这类问题挡在 CI 里（渲染效果本身仍由人工验证清单覆盖）。
/// </summary>
public class UiSmokeTests
{
    private static readonly DateTimeOffset Base = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void App_resources_and_all_views_load_without_xaml_errors()
    {
        Exception? failure = null;
        var dir = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var thread = new Thread(() =>
        {
            try
            {
                var app = new global::AoiTwin.App.App();
                app.InitializeComponent(); // 合并 Themes/Dark.xaml

                var hub = new RealtimeHub();
                var replay = new ReplayViewModel(hub);
                var recordings = new RecordingManagerViewModel(_ => false);
                using var monitor = new CollectionMonitorViewModel(hub);

                // 采集页（T2.5）：启动四路 Mock → 真实计数/速率/健康状态经绑定刷新
                Assert.False(monitor.IsRunning);
                Assert.Equal(4, monitor.Sources.Count);
                Assert.Equal(4, monitor.Topics.Count);
                // 等待条件必须是「每一路都产出」：CAMERA 有 100ms 限频，Connected 先于首条消息，
                // 用总帧数兜底会在 CI 慢速调度下让个别源 0 计数（0.1.14 CI 假失败教训）
                monitor.StartCommand.Execute(null);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!monitor.IsRunning
                    || !monitor.Sources.All(s => s.IsConnected && s.MessageCount > 0))
                {
                    Thread.Sleep(50);
                    monitor.Refresh();
                    Assert.True(sw.ElapsedMilliseconds < 20000, "Mock 采集未在 20s 内让四路全部产出");
                }

                Assert.Equal("采集中", monitor.RunStateText);
                Assert.All(monitor.Sources, s => Assert.True(s.MessageCount > 0, $"{s.Name} 无计数"));
                Assert.All(monitor.Sources, s => Assert.Equal("已连接", s.HealthText));
                Assert.True(monitor.DiskPercent is >= 0 and <= 100);
                monitor.StopCommand.Execute(null);
                Assert.Equal("已停止", monitor.RunStateText);

                // 用真实录制文件驱动一次加载（批次列表/时间轴/详情区全部走绑定）
                var file = BuildRecording(dir);
                replay.LoadFile(file);
                Assert.True(replay.HasSession, replay.StatusText);
                Assert.Equal(3, replay.Batches.Count);
                Assert.Contains("WAFER-001", replay.CurrentBatchText);
                Assert.True(replay.TotalSeconds > 0);

                // 3D 场景页（T1.3/T1.4）：视口 + 真实资产包装配
                var twin = new TwinSceneViewModel(
                    AssetPackageLocator.LocateCatalog(),
                    AssetPackageLocator.LocateAssetsRoot(),
                    new CameraBookmarkService(Path.Combine(dir, "bookmarks.json")));
                var twinView = new TwinView { DataContext = twin };
                ForceLayout(twinView);

                var viewport = twinView.TwinViewport;
                Assert.NotNull(viewport.Viewport.Camera);
                Assert.False(viewport.Probe.IsRunning); // Loaded 未触发前不订阅渲染循环
                Assert.True(viewport.IsFrameProbeVisible);
                Assert.False(viewport.HasLongFrame);

                // 装配：加载完成后场景树挂到视口，每个 Mesh 都带 PartId（T1.8 点选的前提）
                var loadTask = twin.LoadAsync(viewport.SceneRoot);
                PumpUntilCompleted(loadTask);
                Assert.False(twin.HasError, twin.DetailText);
                Assert.True(twin.HasScene);
                Assert.Equal(10, twin.Package!.Near.Parts.Count);
                Assert.Equal(10, viewport.SceneRoot.Children.Count);
                Assert.Equal(10, PartTag.CollectTagged(viewport.SceneRoot).Count);
                Assert.Contains("已装配", twin.StatusText);
                Assert.Equal(100, twin.ProgressPercent);
                ForceLayout(twinView);

                // LOD 两档（T1.6）：远档合批代理 + 距离切换 + 锁定模式
                Assert.Equal(LodLevel.L1, twin.ActiveLod);
                Assert.Equal("LOD1 中模 · 自动", twin.LodText);
                twin.SetLodMode(TwinLodMode.Far);
                Assert.Equal(LodLevel.L2, twin.ActiveLod);
                Assert.Equal(5, viewport.SceneRoot.Children.Count);
                Assert.Contains("LOD2", twin.LodText);
                twin.SetLodMode(TwinLodMode.Near);
                Assert.Equal(10, viewport.SceneRoot.Children.Count);

                // 自动档：拉远相机越过远阈值后切 LOD2，拉回后恢复 LOD1
                // （VM 内部 250ms 节流，连续两次调用需间隔一个评估周期）
                twin.SetLodMode(TwinLodMode.Auto);
                twin.OnCameraMoved(new Point3D(30, -30, 20));
                Assert.Equal(LodLevel.L2, twin.ActiveLod);
                Assert.Equal(5, viewport.SceneRoot.Children.Count);
                Thread.Sleep(300);
                twin.OnCameraMoved(new Point3D(0.5, -0.5, 1.0));
                Assert.Equal(LodLevel.L1, twin.ActiveLod);
                Assert.Equal(10, viewport.SceneRoot.Children.Count);
                ForceLayout(twinView);

                // 点选与台账信息区（T1.8）：列表 10 项、选中详情、清除选中
                Assert.Equal(10, twin.PartSummaries.Count);
                twin.SelectPart("WAFER_STAGE");
                Assert.Equal("WAFER_STAGE", twin.SelectedPartId);
                Assert.True(twin.SelectedPartLines.Count >= 5, "选中详情行数不足");
                Assert.Contains("STAGE", twin.SelectionHint);
                twin.SelectPart(null);
                Assert.Null(twin.SelectedPartId);
                Assert.Empty(twin.SelectedPartLines);
                twin.SelectPart("不存在"); // 未知 PartId 等价于清除
                Assert.Null(twin.SelectedPartId);
                ForceLayout(twinView);

                // 布局切换（T4.1 FR-M1-15）：沉浸全屏 ≤1s，3D 场景不受影响（不重建视口）
                var layoutWatch = System.Diagnostics.Stopwatch.StartNew();
                twin.ToggleImmersiveCommand.Execute(null);
                ForceLayout(twinView);
                layoutWatch.Stop();
                Assert.True(layoutWatch.ElapsedMilliseconds <= 1000, $"布局切换 {layoutWatch.ElapsedMilliseconds}ms 超出 1s");
                Assert.True(twin.IsImmersive);
                Assert.Equal("退出全屏", twin.ImmersiveToggleText);
                Assert.Equal(10, viewport.SceneRoot.Children.Count); // 3D 场景未被重建

                twin.ToggleImmersiveCommand.Execute(null);
                ForceLayout(twinView);
                Assert.False(twin.IsImmersive);
                Assert.Equal(10, viewport.SceneRoot.Children.Count);
                ForceLayout(twinView);

                // 视角书签（T1.5）：4 预置 + 保存/持久化/删除，切换时长硬顶 800ms
                Assert.Equal(4, twin.Bookmarks.Count);
                Assert.All(twin.Bookmarks, b => Assert.True(b.IsPreset));

                var target = new CameraPose(new Point3D(0.6, -2.6, 1.6), new Point3D(0, 0, 0.8));
                var duration = viewport.AnimateTo(target);
                Assert.True(duration.TotalMilliseconds <= 800, $"过渡 {duration.TotalMilliseconds}ms 超出判据");
                Assert.True(viewport.CameraAnimator.IsRunning);
                viewport.CameraAnimator.Cancel(); // 无窗口环境不推帧，取消即可

                twin.NewBookmarkName = "作业视角";
                Assert.True(twin.SaveBookmark(target), twin.BookmarkHint);
                Assert.Equal(5, twin.Bookmarks.Count);
                Assert.Contains(twin.Bookmarks, b => !b.IsPreset && b.Name == "作业视角");
                Assert.True(File.Exists(Path.Combine(dir, "bookmarks.json")));
                Assert.True(twin.DeleteSelectedBookmark(), twin.BookmarkHint);
                Assert.Equal(4, twin.Bookmarks.Count);

                var replayView = new ReplayView { DataContext = replay };
                ForceLayout(replayView);

                var managerView = new RecordingManagerView { DataContext = recordings };
                ForceLayout(managerView);

                var monitorView = new CollectionMonitorView { DataContext = monitor };
                ForceLayout(monitorView);

                var main = new MainViewModel(twin, monitor, replay, recordings);
                var window = new global::AoiTwin.App.MainWindow(main);
                Assert.Same(main, window.DataContext);

                // 3D 场景页为主页签（页序见 MainViewModel）
                Assert.Equal(0, main.SelectedTabIndex);

                window.Close();
                replay.Dispose();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "UI 冒烟测试超时");

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }

        Assert.Null(failure);
    }

    /// <summary>
    /// 在 STA 线程上把异步加载跑到完成：ViewModel 的 await 会封送回 Dispatcher，
    /// 直接 Wait 会死锁，必须推帧泵。
    /// </summary>
    private static void PumpUntilCompleted(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void ForceLayout(FrameworkElement element)
    {
        element.Measure(new Size(1400, 820));
        element.Arrange(new Rect(0, 0, 1400, 820));
        element.UpdateLayout();
    }

    /// <summary>3 片晶圆的最小录制（每片 2 轴帧 + 1 结果帧）。</summary>
    private static string BuildRecording(string dir)
    {
        using var writer = new RecordingWriter(new RecordingOptions
        {
            Directory = dir,
            DeviceId = "AOI-01",
            ShiftId = "S1",
            DiskUsedRatioProvider = () => 0.1,
        });

        for (var b = 0; b < 3; b++)
        {
            for (var k = 0; k < 2; k++)
            {
                writer.Write(TwinTopic.Motion, new AxisSnapshot(
                    "ROBOT", "R", b * 10 + k, 0.5, Base.AddMilliseconds(b * 1000 + k * 100)));
            }

            writer.Write(TwinTopic.Result, new WaferResult(
                $"WAFER-{b + 1:D3}",
                new WaferMapSnapshot(2, 2, [255, 0, 128, 64]),
                [new DefectRecord(1, 1.5, 2.5, 3.0, "PARTICLE", 128)],
                TimeSpan.FromSeconds(8.4),
                Base.AddMilliseconds(b * 1000 + 500)));
        }

        return Assert.Single(Directory.GetFiles(dir, "*.aotrec"));
    }
}
