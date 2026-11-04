using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Bus;
using AoiTwin.Rendering.Dynamics;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;
using AoiTwin.ViewModel;
using Xunit;

namespace AoiTwin.Rendering.Tests;

public sealed class DefectPointDriverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-defect-" + Guid.NewGuid().ToString("N"));

    public DefectPointDriverTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Appending_10k_points_creates_exactly_ceil_n_512_blocks_without_rebuild()
    {
        RunOnStaThread(() =>
        {
            var (driver, viewport, visual, _) = CreateDriver();
            try
            {
                driver.OnFrame();

                // 三个等级各追加若干点：块数按池独立计算
                const int scratchCount = 10_000;
                var defects = new List<DefectRecord>(scratchCount);
                for (var i = 0; i < scratchCount; i++)
                {
                    defects.Add(new DefectRecord(i + 1, (i % 150) - 75.0, (i * 7 % 150) - 75.0, 42, "Scratch", 128));
                }

                var stopwatch = Stopwatch.StartNew();
                visual.DefectBatches.Enqueue(defects);
                driver.OnFrame();
                stopwatch.Stop();

                Assert.Equal(scratchCount, driver.PointCount);
                Assert.Equal(scratchCount, driver.IndexedCount);

                // 判据：块数 = ⌈N/512⌉（单等级场景，总块数即该等级块数）
                var expectedBlocks = (scratchCount + DefectPointDriver.BlockCapacity - 1) / DefectPointDriver.BlockCapacity;
                Assert.Equal(expectedBlocks, CollectAllBlocks(viewport).Count);

                // O(增量) 探针：1 万点追加（含 20 个新块）应为毫秒级，远低于一帧预算
                Assert.True(stopwatch.ElapsedMilliseconds < 500, $"追加耗时 {stopwatch.ElapsedMilliseconds}ms");

                // 已有块零重建：再追加一点，第一块的 Points 引用不变（仅追加）
                var firstBlock = CollectAllBlocks(viewport)[0];
                var pointsRef = firstBlock.Points;
                visual.DefectBatches.Enqueue(
                    [new DefectRecord(99_999, 0, 0, 10, "Scratch", 64)]);
                driver.OnFrame();
                Assert.Same(pointsRef, CollectAllBlocks(viewport)[0].Points);
                Assert.Equal(scratchCount + 1, driver.PointCount);
            }
            finally
            {
                driver.Dispose();
            }
        });
    }

    [Fact]
    public void Classes_get_configured_colors_and_unknown_falls_back()
    {
        RunOnStaThread(() =>
        {
            var (driver, viewport, visual, _) = CreateDriver();
            try
            {
                driver.OnFrame();
                visual.DefectBatches.Enqueue(
                [
                    new DefectRecord(1, -10, -10, 8, "Scratch", 100),
                    new DefectRecord(2, 10, 10, 8, "Particle", 100),
                    new DefectRecord(3, 0, 20, 8, "Residue", 100),
                    new DefectRecord(4, -20, 0, 8, "Fiber", 100), // 未配置等级
                ]);
                driver.OnFrame();

                Assert.Equal(4, driver.PointCount);
                var allBlocks = CollectAllBlocks(viewport);
                Assert.Equal(4, allBlocks.Count); // 四个等级各一块

                // PRD §5.5 defectClassColors + 未配置等级降级灰
                Assert.Contains(allBlocks, b => b.Color == Color.FromRgb(0xFF, 0x6B, 0x81)); // Scratch
                Assert.Contains(allBlocks, b => b.Color == Color.FromRgb(0xFF, 0x9F, 0x43)); // Particle
                Assert.Contains(allBlocks, b => b.Color == Color.FromRgb(0xC5, 0x6C, 0xF0)); // Residue
                Assert.Contains(allBlocks, b => b.Color == Color.FromRgb(0xB0, 0xB0, 0xB0)); // Fiber
            }
            finally
            {
                driver.Dispose();
            }
        });
    }

    [Fact]
    public void Highlight_moves_wire_cube_to_defect_point()
    {
        RunOnStaThread(() =>
        {
            var (driver, viewport, visual, assembly) = CreateDriver();
            try
            {
                driver.OnFrame();
                const double defectX = 12.5, defectY = -30.0;
                visual.DefectBatches.Enqueue(
                    [new DefectRecord(7, defectX, defectY, 8, "Scratch", 128)]);
                driver.OnFrame();

                driver.Highlight(7);
                var cube = CollectHighlightEdges(viewport);
                Assert.NotEmpty(cube);

                // 高亮框中心 = 缺陷世界坐标（mm→m，晶圆顶面上方 1mm）
                var wafer = assembly.Find("WAFER_CURRENT")!.Geometry.Bounds;
                var expected = new Point3D(
                    wafer.X + wafer.SizeX / 2 + defectX * 1e-3,
                    wafer.Y + wafer.SizeY / 2 + defectY * 1e-3,
                    wafer.Z + wafer.SizeZ + 0.001);
                Assert.Equal(expected.X, cube[0].X + 0.004, 5);
                Assert.Equal(expected.Y, cube[0].Y + 0.004, 5);
                Assert.Equal(expected.Z, cube[0].Z + 0.004, 5);

                // 未知缺陷：不高亮
                driver.ClearHighlight();
                driver.Highlight(12345);
                Assert.Empty(CollectHighlightEdges(viewport));
            }
            finally
            {
                driver.Dispose();
            }
        });
    }

    [Fact]
    public void Dispose_clears_all_pools()
    {
        RunOnStaThread(() =>
        {
            var (driver, viewport, visual, _) = CreateDriver();
            driver.OnFrame();
            visual.DefectBatches.Enqueue(
            [
                new DefectRecord(1, 0, 0, 8, "Scratch", 100),
            ]);
            driver.OnFrame();
            Assert.Equal(1, driver.PointCount);
            driver.Dispose();
            Assert.Equal(0, viewport.OverlayRoot.Children.Count);
        });
    }

    [Fact]
    public void Index_records_pool_block_and_offset_for_every_defect()
    {
        RunOnStaThread(() =>
        {
            var (driver, _, visual, _) = CreateDriver();
            try
            {
                driver.OnFrame();
                var defects = new List<DefectRecord>(600);
                for (var i = 1; i <= 600; i++)
                {
                    defects.Add(new DefectRecord(i, i % 50, i % 37, 9, "Scratch", 100));
                }

                visual.DefectBatches.Enqueue(defects);
                driver.OnFrame();

                // 池内序号 → 块号/块内偏移（块按追加顺序填满，索引与几何不重复存储）
                Assert.True(driver.TryGetEntry(512, out var firstBlockLast));
                Assert.Equal(511, firstBlockLast.PoolIndex);
                Assert.Equal(0, firstBlockLast.BlockIndex);
                Assert.Equal(511, firstBlockLast.PointInBlock);

                Assert.True(driver.TryGetEntry(600, out var last));
                Assert.Equal(599, last.PoolIndex);
                Assert.Equal(1, last.BlockIndex);
                Assert.Equal(600 % DefectPointDriver.BlockCapacity - 1, last.PointInBlock);
                Assert.Equal("Scratch", last.DefectClass);

                Assert.False(driver.TryGetEntry(9_999, out _));
                Assert.Equal(600, driver.IndexedCount);
            }
            finally
            {
                driver.Dispose();
            }
        });
    }

    [Fact]
    public void Ray_pick_returns_nearest_defect_within_tolerance()
    {
        RunOnStaThread(() =>
        {
            var (driver, _, visual, assembly) = CreateDriver();
            try
            {
                driver.OnFrame();
                // 两处缺陷：A 在 (10,0)mm，B 在 (0,20)mm
                visual.DefectBatches.Enqueue(
                [
                    new DefectRecord(1, 10, 0, 8, "Scratch", 100),
                    new DefectRecord(2, 0, 20, 8, "Particle", 100),
                ]);
                driver.OnFrame();

                var wafer = assembly.Find("WAFER_CURRENT")!.Geometry.Bounds;
                var pointA = new Point3D(wafer.X + wafer.SizeX / 2 + 0.010, wafer.Y + wafer.SizeY / 2, wafer.Z + wafer.SizeZ + 0.001);

                // A：自上而下正对命中（垂距 0）
                Assert.True(driver.TryPickOnRay(
                    new Point3D(pointA.X, pointA.Y, pointA.Z + 0.5), new Vector3D(0, 0, -1), out var hitA));
                Assert.Equal(1, hitA.DefectId);
                Assert.Equal(pointA.X, hitA.World.X, 6);

                // B：射线偏移 3mm 仍命中（容差 6mm），且不误选 A
                var pointB = new Point3D(wafer.X + wafer.SizeX / 2, wafer.Y + wafer.SizeY / 2 + 0.020, pointA.Z);
                Assert.True(driver.TryPickOnRay(
                    new Point3D(pointB.X + 0.003, pointB.Y, pointB.Z + 0.5), new Vector3D(0, 0, -1), out var hitB));
                Assert.Equal(2, hitB.DefectId);

                // 全部超容差：不命中
                Assert.False(driver.TryPickOnRay(
                    new Point3D(pointA.X + 0.20, pointA.Y, pointA.Z + 0.5), new Vector3D(0, 0, -1), out _));

                // 射线背向（点在射线后方）：不命中
                Assert.False(driver.TryPickOnRay(
                    new Point3D(pointA.X, pointA.Y, pointA.Z - 0.5), new Vector3D(0, 0, -1), out _));

                // 容差可收紧：3mm 偏移在 1mm 容差下不命中
                Assert.False(driver.TryPickOnRay(
                    new Point3D(pointB.X + 0.003, pointB.Y, pointB.Z + 0.5), new Vector3D(0, 0, -1), out _, 0.001));
            }
            finally
            {
                driver.Dispose();
            }
        });
    }

    [Fact]
    public void Highlight_tracks_defect_id_and_clears_on_rebind()
    {
        RunOnStaThread(() =>
        {
            var (driver, viewport, visual, _) = CreateDriver();
            try
            {
                driver.OnFrame();
                visual.DefectBatches.Enqueue([new DefectRecord(42, 5, 5, 8, "Residue", 90)]);
                driver.OnFrame();

                driver.Highlight(42);
                Assert.Equal(42, driver.HighlightedDefectId);
                Assert.NotEmpty(CollectHighlightEdges(viewport));

                driver.ClearHighlight();
                Assert.Equal(0, driver.HighlightedDefectId);
                Assert.Empty(CollectHighlightEdges(viewport));
            }
            finally
            {
                driver.Dispose();
            }
        });
    }

    private static (DefectPointDriver Driver, TwinViewport3D Viewport, TwinVisualViewModel Visual, SceneAssembly Assembly)
        CreateDriver()
    {
        var viewport = new TwinViewport3D();
        var hub = new RealtimeHub();
        var visual = new TwinVisualViewModel(hub, motionPolicyProvider: () => ThrottlePolicy.PassThrough);
        var assembly = BuildAssembly();
        var driver = new DefectPointDriver(viewport, visual, () => assembly);
        return (driver, viewport, visual, assembly);
    }

    /// <summary>收集全部点块（层次：OverlayRoot → 驱动容器 → 池容器 → 块，递归查找）。</summary>
    private static List<HelixToolkit.Wpf.PointsVisual3D> CollectAllBlocks(TwinViewport3D viewport)
    {
        var blocks = new List<HelixToolkit.Wpf.PointsVisual3D>();
        foreach (var visual in viewport.OverlayRoot.Children)
        {
            Walk(visual, blocks);
        }

        return blocks;

        static void Walk(Visual3D visual, List<HelixToolkit.Wpf.PointsVisual3D> blocks)
        {
            if (visual is HelixToolkit.Wpf.PointsVisual3D block)
            {
                blocks.Add(block);
            }

            if (visual is ModelVisual3D mv)
            {
                foreach (var child in mv.Children)
                {
                    Walk(child, blocks);
                }
            }
        }
    }

    private static List<Point3D> CollectHighlightEdges(TwinViewport3D viewport)
    {
        // 高亮线框是 OverlayRoot 的直接子节点（ModelVisual3D）下的 LinesVisual3D
        var edges = new List<Point3D>();
        foreach (var visual in viewport.OverlayRoot.Children.OfType<ModelVisual3D>())
        {
            foreach (var child in visual.Children)
            {
                if (child is HelixToolkit.Wpf.LinesVisual3D line)
                {
                    edges.AddRange(line.Points);
                }
            }
        }

        return edges;
    }

    private static SceneAssembly BuildAssembly()
    {
        var visuals = new List<PartVisual>
        {
            MakePart("WAFER_CURRENT", new Point3D(0, 0.10, 0.585), new Size3D(0.30, 0.30, 0.004)),
        };
        return new SceneAssembly(visuals, visuals.Count);
    }

    private static PartVisual MakePart(string partId, Point3D center, Size3D size)
    {
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        var hx = size.X / 2;
        var hy = size.Y / 2;
        var hz = size.Z / 2;
        Span<(Point3D a, Point3D b, Point3D c, Point3D d)> faces = stackalloc (Point3D, Point3D, Point3D, Point3D)[]
        {
            (new(center.X - hx, center.Y - hy, center.Z - hz), new(center.X + hx, center.Y - hy, center.Z - hz),
                new(center.X + hx, center.Y + hy, center.Z - hz), new(center.X - hx, center.Y + hy, center.Z - hz)),
            (new(center.X + hx, center.Y - hy, center.Z + hz), new(center.X - hx, center.Y - hy, center.Z + hz),
                new(center.X - hx, center.Y + hy, center.Z + hz), new(center.X + hx, center.Y + hy, center.Z + hz)),
        };
        foreach (var (a, b, c, d) in faces)
        {
            var baseIndex = positions.Count;
            positions.Add(a);
            positions.Add(b);
            positions.Add(c);
            positions.Add(d);
            indices.Add(baseIndex);
            indices.Add(baseIndex + 1);
            indices.Add(baseIndex + 2);
            indices.Add(baseIndex);
            indices.Add(baseIndex + 2);
            indices.Add(baseIndex + 3);
        }

        mesh.Positions = positions;
        mesh.TriangleIndices = indices;
        mesh.Freeze();

        var geometryModel = new GeometryModel3D
        {
            Geometry = mesh,
            Material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60))),
        };
        geometryModel.Freeze();
        var group = new Model3DGroup();
        group.Children.Add(geometryModel);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA 线程超时");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA 线程内断言失败: {failure}");
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
