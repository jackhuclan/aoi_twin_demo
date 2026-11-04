using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Bus;
using AoiTwin.Rendering.Dynamics;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;
using AoiTwin.ViewModel;
using Xunit;

namespace AoiTwin.Rendering.Tests;

/// <summary>
/// 装载状态驱动（TA.16，FR-M2-13）：Track 晶圆占位显隐、Cassette 槽位点亮、
/// 1s 节流与换档重建——判据均来自计划原文。
/// </summary>
public sealed class HandlingSyncDriverTests
{
    [Fact]
    public void Wafer_proxy_attaches_only_when_present()
    {
        RunOnStaThread(() =>
        {
            var hub = new RealtimeHub();
            var state = new HandlingStateViewModel(hub);
            var viewport = new TwinViewport3D();
            var assembly = BuildAssembly();
            var clock = new TestClock();
            using var driver = new HandlingSyncDriver(viewport, state, () => assembly, clock.Read);
            driver.OnFrame();

            Assert.False(driver.WaferVisible);
            Assert.Equal(driver.SlotCount, DriverRoot(viewport).Children.Count);

            hub.Publish(TwinTopic.Status, new StatusEvent(
                "TRACK", "WAFER_IN", Severity.Info,
                new Dictionary<string, string> { ["WaferPresent"] = "1" }, DateTimeOffset.UtcNow));
            clock.Advance(TimeSpan.FromSeconds(1.1));
            driver.OnFrame();

            Assert.True(driver.WaferVisible);
            Assert.Equal(driver.SlotCount + 1, DriverRoot(viewport).Children.Count);

            // 空片信号：占位壳立即退出叠加层（无片时零 Draw Call 开销）
            hub.Publish(TwinTopic.Status, new StatusEvent(
                "TRACK", "WAFER_OUT", Severity.Info,
                new Dictionary<string, string> { ["WaferPresent"] = "0" }, DateTimeOffset.UtcNow));
            clock.Advance(TimeSpan.FromSeconds(1.1));
            driver.OnFrame();

            Assert.False(driver.WaferVisible);
            Assert.Equal(driver.SlotCount, DriverRoot(viewport).Children.Count);
        });
    }

    [Fact]
    public void State_changes_apply_at_most_once_per_second()
    {
        RunOnStaThread(() =>
        {
            var hub = new RealtimeHub();
            var state = new HandlingStateViewModel(hub);
            var viewport = new TwinViewport3D();
            var assembly = BuildAssembly();
            var clock = new TestClock();
            using var driver = new HandlingSyncDriver(viewport, state, () => assembly, clock.Read);

            // 首帧：初始灯态无条件应用一次（开机不等 1s）
            driver.OnFrame();
            Assert.Equal(1, driver.ApplyCount);
            Assert.Equal(0, driver.LitSlotCount);

            PublishCassette(hub, 5);
            clock.Advance(TimeSpan.FromMilliseconds(400));
            driver.OnFrame();
            Assert.Equal(1, driver.ApplyCount); // 窗口内：不应用
            Assert.Equal(0, driver.LitSlotCount);

            clock.Advance(TimeSpan.FromMilliseconds(700)); // 距上次应用 1.1s
            driver.OnFrame();
            Assert.Equal(2, driver.ApplyCount);
            Assert.Equal(5, driver.LitSlotCount);

            PublishCassette(hub, 9);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            driver.OnFrame();
            Assert.Equal(2, driver.ApplyCount); // 仍在节流窗口内
            Assert.Equal(5, driver.LitSlotCount);

            clock.Advance(TimeSpan.FromMilliseconds(700));
            driver.OnFrame();
            Assert.Equal(3, driver.ApplyCount);
            Assert.Equal(9, driver.LitSlotCount);
        });
    }

    [Fact]
    public void Cassette_slots_light_up_to_count_and_clamp_to_capacity()
    {
        RunOnStaThread(() =>
        {
            var hub = new RealtimeHub();
            var state = new HandlingStateViewModel(hub);
            var viewport = new TwinViewport3D();
            var clock = new TestClock();
            using var driver = new HandlingSyncDriver(viewport, state, BuildAssembly, clock.Read);

            driver.OnFrame();
            Assert.Equal(HandlingStateViewModel.DefaultCassetteCapacity, driver.SlotCount); // PRD：25 槽

            PublishCassette(hub, 18);
            clock.Advance(TimeSpan.FromSeconds(1.1));
            driver.OnFrame();
            Assert.Equal(18, driver.LitSlotCount);

            // 超容量由 VM 截断到 capacity，驱动器灯数与之同步
            PublishCassette(hub, 99);
            clock.Advance(TimeSpan.FromSeconds(1.1));
            driver.OnFrame();
            Assert.Equal(25, driver.LitSlotCount);
            Assert.Equal(25, state.CassetteCount);

            PublishCassette(hub, 0);
            clock.Advance(TimeSpan.FromSeconds(1.1));
            driver.OnFrame();
            Assert.Equal(0, driver.LitSlotCount);
        });
    }

    [Fact]
    public void Rebind_keeps_slot_count_after_lod_swap()
    {
        RunOnStaThread(() =>
        {
            var hub = new RealtimeHub();
            var state = new HandlingStateViewModel(hub);
            var viewport = new TwinViewport3D();
            var current = BuildAssembly();
            var clock = new TestClock();
            using var driver = new HandlingSyncDriver(viewport, state, () => current, clock.Read);

            driver.OnFrame();
            PublishCassette(hub, 3);
            clock.Advance(TimeSpan.FromSeconds(1.1));
            driver.OnFrame();
            Assert.Equal(3, driver.LitSlotCount);

            // LOD 换档 = 新装配实例：槽位在新几何上重建，灯态在下一帧重新应用
            current = BuildAssembly();
            driver.OnFrame();
            Assert.Equal(HandlingStateViewModel.DefaultCassetteCapacity, driver.SlotCount);
            driver.OnFrame();
            Assert.Equal(3, driver.LitSlotCount);
        });
    }

    private static void PublishCassette(IRealtimeHub hub, int count) =>
        hub.Publish(TwinTopic.Status, new StatusEvent(
            "LOAD_PORT", "CASSETTE", Severity.Info,
            new Dictionary<string, string>
            {
                ["CassetteCount"] = count.ToString(),
                ["CassetteCapacity"] = HandlingStateViewModel.DefaultCassetteCapacity.ToString(),
            },
            DateTimeOffset.UtcNow));

    /// <summary>驱动器挂载的叠加根（OverlayRoot 下唯一的 ModelVisual3D）。</summary>
    private static ModelVisual3D DriverRoot(TwinViewport3D viewport) =>
        viewport.OverlayRoot.Children.OfType<ModelVisual3D>().Single();

    private static SceneAssembly BuildAssembly()
    {
        var visuals = new List<PartVisual>
        {
            MakePart("TRACK", new Point3D(0.40, -0.62, 0.10), new Size3D(0.30, 0.10, 0.02)),
            MakePart("LOAD_PORT", new Point3D(0.60, -0.20, 0.35), new Size3D(0.06, 0.20, 0.25)),
        };
        return new SceneAssembly(visuals, visuals.Count);
    }

    private static PartVisual MakePart(string partId, Point3D center, Size3D size)
    {
        var mesh = new MeshGeometry3D
        {
            Positions =
            [
                new(center.X - size.X / 2, center.Y - size.Y / 2, center.Z),
                new(center.X + size.X / 2, center.Y - size.Y / 2, center.Z),
                new(center.X + size.X / 2, center.Y + size.Y / 2, center.Z),
                new(center.X - size.X / 2, center.Y + size.Y / 2, center.Z),
            ],
            TriangleIndices = [0, 1, 2, 0, 2, 3],
        };
        mesh.Freeze();

        var model = new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(Colors.Gray)));
        model.Freeze();
        var group = new Model3DGroup();
        group.Children.Add(model);
        var visual = new ModelVisual3D { Content = group };
        return new PartVisual(partId, visual, group, new TranslateTransform3D());
    }

    private sealed class TestClock
    {
        private TimeSpan _now;

        public TimeSpan Read() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
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
