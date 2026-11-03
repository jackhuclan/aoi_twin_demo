using System.IO;
using System.Windows;
using System.Windows.Controls;
using AoiTwin.App.ViewModels;
using AoiTwin.App.Views;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Recording;
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
                monitor.StartCommand.Execute(null);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!monitor.IsRunning
                    || !monitor.Sources.All(s => s.IsConnected)
                    || monitor.Topics.Sum(t => t.Count) < 6)
                {
                    Thread.Sleep(50);
                    monitor.Refresh();
                    Assert.True(sw.ElapsedMilliseconds < 20000, "Mock 采集未在 20s 内产出数据");
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

                // 强制布局：模板应用 + 绑定求值 + 资源查找
                var twinView = new TwinView();
                ForceLayout(twinView);

                // 3D 视口（T1.3）：相机就位、场景挂载点已挂入视口、探针未在无窗口环境下启动
                var viewport = twinView.TwinViewport;
                Assert.NotNull(viewport.Viewport.Camera);
                Assert.False(viewport.Probe.IsRunning); // Loaded 未触发前不订阅渲染循环
                Assert.True(viewport.IsFrameProbeVisible);
                Assert.False(viewport.HasLongFrame);

                var replayView = new ReplayView { DataContext = replay };
                ForceLayout(replayView);

                var managerView = new RecordingManagerView { DataContext = recordings };
                ForceLayout(managerView);

                var monitorView = new CollectionMonitorView { DataContext = monitor };
                ForceLayout(monitorView);

                var main = new MainViewModel(monitor, replay, recordings);
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
