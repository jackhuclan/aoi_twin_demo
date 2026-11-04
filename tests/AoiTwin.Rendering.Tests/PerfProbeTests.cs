using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Kinematics;
using AoiTwin.Rendering.Dynamics;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;
using AoiTwin.ViewModel;
using Xunit;
using Xunit.Abstractions;

namespace AoiTwin.Rendering.Tests;

/// <summary>
/// 性能探针（TA.19，NFR-11/12/13）：把 PRD 的性能判据变成可重复执行的断言，
/// 实测数字经 <see cref="ITestOutputHelper"/> 输出，直接引用到 <c>docs/m2-perf-report.md</c>。
///
/// 口径：
/// - 分配量用 <see cref="GC.GetAllocatedBytesForCurrentThread"/>（精确到字节、不受其它线程干扰），
///   判据是**严格 0**——NFR-13 原文「渲染帧内禁止堆分配」，放宽成「小于某个值」就等于没约束；
/// - 耗时类判据取「墙钟耗时」而不是平均值，因为它就是用户能感知到的那个延迟；
///   真机帧率（NFR-11/NFR-17）必须有真实 GPU 与产线数据流，离线只能给出 CPU 侧成本。
/// </summary>
public sealed class PerfProbeTests
{
    private readonly ITestOutputHelper _output;

    public PerfProbeTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// NFR-13 的两层口径：
    /// ① 自有链路（位姿合成 / 插值 / 标定换算 / 视图模型刷新）**严格 0 字节**——
    ///    这条是必须守住的，任何回归都是我们自己的代码在热路径上分配；
    /// ② 整帧驱动含 WPF 依赖属性写入，<see cref="TranslateTransform3D"/> 的三个 OffsetX/Y/Z
    ///    各自把 double 装箱进 <c>DependencyPropertyChangedEventArgs</c>（24 B/分量），
    ///    72 B/驱动件/帧是框架固定成本（4.3 KB/s @60fps），应用层无法消除。
    /// </summary>
    [Fact]
    public void Motion_hot_path_is_allocation_free()
    {
        RunOnStaThread(() =>
        {
            var hub = new RealtimeHub();
            var engine = new MotionPoseEngine(DemoMapper());
            using var visual = new TwinVisualViewModel(hub, engine);
            visual.GetMotionPart("STAGE");

            // 两个样本把「现在」夹在中间：每帧都走插值合成（最重的那条路径），
            // 而不是命中保持段缓存——后者零分配是显然的，前者才是 NFR-13 的考点
            var now = DateTimeOffset.UtcNow;
            engine.OnAxis(new AxisSnapshot("STAGE", "X", 100_000, 0, now.AddSeconds(-2)));
            engine.OnAxis(new AxisSnapshot("STAGE", "X", 300_000, 0, now.AddSeconds(2)));

            var viewport = new TwinViewport3D();
            var assembly = BuildAssembly();
            using var driver = new MotionSyncDriver(viewport, visual, () => assembly, static id => id);
            driver.OnFrame(); // 绑定 + JIT 预热

            Assert.True(driver.BindingCount > 0, "运动绑定表为空，探针无效");

            const int frames = 120; // 2s @60fps

            // ① 自有链路：位姿引擎合成 + 视图模型刷新
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < frames; i++)
            {
                visual.RefreshMotion(DateTimeOffset.UtcNow);
            }

            var pipeline = GC.GetAllocatedBytesForCurrentThread() - before;
            _output.WriteLine($"位姿合成 + VM 刷新 ×{frames}: {pipeline} B");

            // ② 整帧驱动：叠加 WPF 依赖属性写入
            before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < frames; i++)
            {
                driver.OnFrame();
            }

            var frame = GC.GetAllocatedBytesForCurrentThread() - before;
            var perFrame = frame / (double)frames;
            _output.WriteLine($"整帧 ×{frames}: {frame} B（{perFrame:F1} B/帧 = 3 × 24 B WPF 装箱 + 自有链路 0 B）");

            Assert.Equal(0, pipeline);
            Assert.True(perFrame <= 96, $"整帧分配 {perFrame:F1} B/帧，超过框架固定成本上限（NFR-13）");
            Assert.True(visual.GetMotionPart("STAGE").ReadLatest().Matrix[3] > 0.1, "插值未生效，探针未覆盖合成路径");
        });
    }

    [Fact]
    public void Idle_texture_and_point_frames_are_allocation_free()
    {
        RunOnStaThread(() =>
        {
            var hub = new RealtimeHub();
            using var visual = new TwinVisualViewModel(hub);
            var viewport = new TwinViewport3D();
            var assembly = BuildAssembly();
            using var mapDriver = new WaferMapDriver(viewport, visual, () => assembly);
            using var pointDriver = new DefectPointDriver(viewport, visual, () => assembly);
            mapDriver.OnFrame();
            pointDriver.OnFrame();

            // 先落一片数据（纹理与点云都进入稳态），之后空帧不得再有分配
            hub.Publish(TwinTopic.Result, MakeResult(dies: 64, defectCount: 32));
            mapDriver.OnFrame();
            pointDriver.OnFrame();
            Assert.Equal(32, pointDriver.PointCount);
            Assert.True(mapDriver.RefreshCount > 0);

            const int frames = 120;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < frames; i++)
            {
                mapDriver.OnFrame();
                pointDriver.OnFrame();
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            _output.WriteLine($"空帧 ×{frames}（纹理 + 点云）: 分配 {allocated} B");

            Assert.Equal(0, allocated);
        });
    }

    [Fact]
    public void Wafer_map_full_refresh_cost_is_measured()
    {
        RunOnStaThread(() =>
        {
            var hub = new RealtimeHub();
            using var visual = new TwinVisualViewModel(hub);
            var viewport = new TwinViewport3D();
            var assembly = BuildAssembly();
            using var driver = new WaferMapDriver(viewport, visual, () => assembly);
            driver.OnFrame();

            // 300×300 = 9 万 die（12 吋片在 100µm die 下的量级）
            const int dies = 90_000;
            hub.Publish(TwinTopic.Result, MakeResult(300 * 300, defectCount: 0));

            var stopwatch = Stopwatch.StartNew();
            driver.OnFrame();
            stopwatch.Stop();

            _output.WriteLine($"整片刷新 {dies} die: {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
            Assert.Equal(1, driver.RefreshCount);
            Assert.True(
                stopwatch.ElapsedMilliseconds < 100,
                $"整片刷新耗时 {stopwatch.Elapsed.TotalMilliseconds:F1}ms 超过 100ms（NFR-11 掉帧判据）");
        });
    }

    [Fact]
    public void Axis_visual_follow_latency_within_300ms()
    {
        RunOnStaThread(() =>
        {
            var hub = new RealtimeHub();
            var engine = new MotionPoseEngine(DemoMapper());
            using var visual = new TwinVisualViewModel(hub, engine);
            var part = (MotionPartViewModel)visual.GetMotionPart("STAGE");

            var viewport = new TwinViewport3D();
            var assembly = BuildAssembly();
            using var driver = new MotionSyncDriver(viewport, visual, () => assembly, static id => id);
            driver.OnFrame();

            // 轴快照先到（数据线程），渲染帧按 60fps 泵
            var stopwatch = Stopwatch.StartNew();
            hub.Publish(TwinTopic.Motion, new AxisSnapshot("STAGE", "X", 250_000, 0, DateTimeOffset.UtcNow));

            var deadline = TimeSpan.FromMilliseconds(300);
            while (part.ReadLatest().Matrix[3] == 0 && stopwatch.Elapsed < deadline)
            {
                driver.OnFrame();
                Thread.Sleep(5);
            }

            stopwatch.Stop();
            _output.WriteLine($"轴跟随延迟（Publish → 位姿可见）: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");

            Assert.True(part.ReadLatest().Matrix[3] > 0, "轴位移未在 300ms 内反映到视图模型");
            Assert.True(
                stopwatch.Elapsed <= deadline,
                $"轴跟随耗时 {stopwatch.Elapsed.TotalMilliseconds:F1}ms 超过 300ms（NFR-12）");
        });
    }

    [Fact]
    public void Status_event_visibility_latency_within_150ms()
    {
        RunOnStaThread(() =>
        {
            var hub = new RealtimeHub();
            using var visual = new TwinVisualViewModel(hub);

            var stopwatch = Stopwatch.StartNew();
            hub.Publish(TwinTopic.Status, new StatusEvent(
                "LIGHT_COAX", "BRIGHTNESS", Severity.Info,
                new Dictionary<string, string> { ["Value"] = "0.8" }, DateTimeOffset.UtcNow));

            var deadline = TimeSpan.FromMilliseconds(150);
            while (!visual.TryGetStatus("LIGHT_COAX", out _) && stopwatch.Elapsed < deadline)
            {
                Thread.Sleep(1);
            }

            stopwatch.Stop();
            _output.WriteLine($"事件延迟（Publish → VM 可读）: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");

            Assert.True(visual.TryGetStatus("LIGHT_COAX", out _), "状态事件未在 150ms 内到达视图模型");
        });
    }

    [Fact]
    public void Defect_pick_and_highlight_within_100ms()
    {
        RunOnStaThread(() =>
        {
            var hub = new RealtimeHub();
            using var visual = new TwinVisualViewModel(hub);
            var viewport = new TwinViewport3D();
            var assembly = BuildAssembly();
            using var driver = new DefectPointDriver(viewport, visual, () => assembly);
            driver.OnFrame();

            // 1 万点（PRD 缺陷密度上限的量级）：索引 1 万条后反查 + 高亮仍须 ≤100ms
            hub.Publish(TwinTopic.Result, MakeResult(dies: 4, defectCount: 10_000));
            driver.OnFrame();
            Assert.Equal(10_000, driver.IndexedCount);

            Assert.True(driver.TryGetEntry(9_999, out var entry), "索引缺失，探针无效");

            var stopwatch = Stopwatch.StartNew();
            var picked = driver.TryPickOnRay(
                new Point3D(entry.World.X, entry.World.Y, entry.World.Z + 0.4),
                new Vector3D(0, 0, -1),
                out var hit);
            stopwatch.Stop();

            var highlight = Stopwatch.StartNew();
            driver.Highlight(hit.DefectId);
            highlight.Stop();

            _output.WriteLine($"射线反查（1 万点）: {stopwatch.Elapsed.TotalMilliseconds:F2} ms；" +
                              $"高亮寻址: {highlight.Elapsed.TotalMilliseconds:F3} ms");

            Assert.True(picked, "射线未命中缺陷点");
            Assert.Equal(9_999, hit.DefectId);
            Assert.Equal(9_999, driver.HighlightedDefectId);
            Assert.True(
                stopwatch.Elapsed <= TimeSpan.FromMilliseconds(100),
                $"2D→3D 高亮耗时 {stopwatch.Elapsed.TotalMilliseconds:F1}ms 超过 100ms（NFR-12）");
        });
    }

    [Fact]
    public void Defect_batch_append_cost_is_measured()
    {
        RunOnStaThread(() =>
        {
            var hub = new RealtimeHub();
            using var visual = new TwinVisualViewModel(hub);
            var viewport = new TwinViewport3D();
            var assembly = BuildAssembly();
            using var driver = new DefectPointDriver(viewport, visual, () => assembly);
            driver.OnFrame();

            // 1 万点 = 20 个 512 点块：判据是「分块批量提交」而非逐点 Add（后者 O(N²)）
            hub.Publish(TwinTopic.Result, MakeResult(dies: 4, defectCount: 10_000));

            var before = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            driver.OnFrame();
            stopwatch.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            var dropped = stopwatch.Elapsed.TotalMilliseconds / (1000.0 / 60);
            _output.WriteLine(
                $"1 万点追加: {stopwatch.Elapsed.TotalMilliseconds:F1} ms（≈ 掉 {dropped:F1} 帧），" +
                $"一次性分配 {allocated / 1024.0:F1} KB");
            Assert.Equal(10_000, driver.PointCount);

            // 判据取 M1 NFR-02 的 UI 单次阻塞上限（100ms），而不是 16.67ms 单帧预算：
            // 整片结果到达是**离散事件**（每片一次），PRD NFR-11 允许整片刷新引起掉帧（< 5fps）
            Assert.True(
                stopwatch.ElapsedMilliseconds < 100,
                $"1 万点追加耗时 {stopwatch.ElapsedMilliseconds}ms 超过 100ms（UI 阻塞上限）");
        });
    }

    private static WaferResult MakeResult(int dies, int defectCount)
    {
        var sizeX = (int)Math.Sqrt(dies);
        var sizeY = Math.Max(1, dies / sizeX);
        var map = new byte[sizeX * sizeY];
        for (var i = 0; i < map.Length; i++)
        {
            map[i] = (byte)(i % 17 == 0 ? 2 : 1);
        }

        var defects = new List<DefectRecord>(defectCount);
        var random = new Random(20261104);
        for (var i = 0; i < defectCount; i++)
        {
            defects.Add(new DefectRecord(
                i + 1,
                (random.NextDouble() - 0.5) * 140,
                (random.NextDouble() - 0.5) * 140,
                12,
                i % 3 == 0 ? "Scratch" : i % 3 == 1 ? "Particle" : "Residue",
                100));
        }

        return new WaferResult("W-2026-1104", new WaferMapSnapshot(sizeX, sizeY, map), defects,
            TimeSpan.FromMilliseconds(1200), DateTimeOffset.UtcNow);
    }

    private static TeachCoordinateMapper DemoMapper() => new(
    [
        new AxisCalibration("STAGE", "X", "X", 1e-6, 1, 0),
        new AxisCalibration("STAGE", "Y", "Y", 1e-6, 1, 0),
        new AxisCalibration("STAGE", "Z", "Z", 1e-6, 1, 0),
    ]);

    private static SceneAssembly BuildAssembly()
    {
        var visuals = new List<PartVisual>
        {
            MakePart("STAGE", new Point3D(0, 0, 0.30), new Size3D(0.36, 0.36, 0.05)),
            MakePart("WAFER_CURRENT", new Point3D(0, 0, 0.34), new Size3D(0.30, 0.30, 0.004)),
        };
        return new SceneAssembly(visuals, visuals.Count);
    }

    private static PartVisual MakePart(string partId, Point3D center, Size3D size)
    {
        var hx = size.X / 2;
        var hy = size.Y / 2;
        var hz = size.Z / 2;
        var mesh = new MeshGeometry3D
        {
            Positions =
            [
                new(center.X - hx, center.Y - hy, center.Z - hz),
                new(center.X + hx, center.Y - hy, center.Z - hz),
                new(center.X + hx, center.Y + hy, center.Z - hz),
                new(center.X - hx, center.Y + hy, center.Z - hz),
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA 线程超时");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA 线程内断言失败: {failure}");
        }
    }
}
