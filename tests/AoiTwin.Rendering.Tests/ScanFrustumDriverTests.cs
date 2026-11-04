using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Bus;
using AoiTwin.Rendering.Dynamics;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;
using Xunit;

namespace AoiTwin.Rendering.Tests;

public sealed class ScanFrustumDriverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-frustum-" + Guid.NewGuid().ToString("N"));

    public ScanFrustumDriverTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Frustum_covers_wafer_scan_area()
    {
        RunOnStaThread(() =>
        {
            var (driver, viewport, hub, assembly) = CreateDriver();
            try
            {
                driver.OnFrame();

                // 视锥四条棱线 + 扫描面矩形 + 进度线 = 3 个 LinesVisual3D
                Assert.Equal(3, driver.VisualCount);

                // 覆盖判据：扫描面矩形四角落在晶圆顶面外接矩形上（与 Wafer 尺寸对齐）
                var lines = CollectLines(viewport);
                var wafer = assembly.Find("WAFER_CURRENT")!.Geometry.Bounds;
                var topZ = wafer.Z + wafer.SizeZ + 0.002;
                var minX = wafer.X;
                var maxX = wafer.X + wafer.SizeX;
                var minY = wafer.Y;
                var maxY = wafer.Y + wafer.SizeY;

                var outline = lines[1];
                Assert.Contains(new Point3D(minX, minY, topZ), outline);
                Assert.Contains(new Point3D(maxX, maxY, topZ), outline);

                // 棱线顶点在镜头下沿（占位相机 X 居中）
                var apex = lines[0][0];
                Assert.Equal(wafer.X + wafer.SizeX / 2, apex.X, 4); // 占位相机 X 居中
            }
            finally
            {
                driver.Dispose();
            }
        });
    }

    [Fact]
    public void Progress_event_moves_line_every_publish()
    {
        RunOnStaThread(() =>
        {
            var (driver, viewport, hub, assembly) = CreateDriver();
            try
            {
                driver.OnFrame();
                Assert.Equal(0, driver.Progress);

                // 事件直推（Mock 2Hz ≈ 500ms 一条）：进度线沿 +Y 推进
                hub.Publish(TwinTopic.Optics,
                    new OpticsEvent("CAMERA_LINE_SCAN", OpticsKind.ScanProgress, 0, 0.25, DateTimeOffset.UtcNow));
                driver.OnFrame();
                Assert.Equal(0.25, driver.Progress);

                var lines = CollectLines(viewport);
                var progressLine = lines[2];
                var wafer = assembly.Find("WAFER_CURRENT")!.Geometry.Bounds;
                var topZ = wafer.Z + wafer.SizeZ + 0.002;
                var expectedY = wafer.Y + 0.25 * wafer.SizeY;
                Assert.Equal(expectedY, progressLine[0].Y, 5);
                Assert.Equal(topZ, progressLine[0].Z, 5);

                hub.Publish(TwinTopic.Optics,
                    new OpticsEvent("CAMERA_LINE_SCAN", OpticsKind.ScanProgress, 0, 1.0, DateTimeOffset.UtcNow));
                driver.OnFrame();
                Assert.Equal(1.0, driver.Progress);
                Assert.Equal(wafer.Y + wafer.SizeY, CollectLines(viewport)[2][0].Y, 5); // 终点 = 面远端

                // 值域钳制：超界值不产生越界几何
                hub.Publish(TwinTopic.Optics,
                    new OpticsEvent("CAMERA_LINE_SCAN", OpticsKind.ScanProgress, 0, 7.0, DateTimeOffset.UtcNow));
                driver.OnFrame();
                Assert.Equal(1.0, driver.Progress);
            }
            finally
            {
                driver.Dispose();
            }
        });
    }

    [Fact]
    public void Missing_parts_or_dispose_leave_no_visuals()
    {
        RunOnStaThread(() =>
        {
            var (driver, viewport, hub, _) = CreateDriver(withParts: false);
            try
            {
                driver.OnFrame();
                Assert.Equal(0, driver.VisualCount); // 无相机/晶圆部件：不建视觉
                Assert.Equal(0, viewport.OverlayRoot.Children.Count);
            }
            finally
            {
                driver.Dispose();
            }

            // 正常装配：挂入 → Dispose 摘除
            var (driver2, viewport2, hub2, _) = CreateDriver();
            driver2.OnFrame();
            Assert.Equal(3, driver2.VisualCount);
            driver2.Dispose();
            Assert.Equal(0, viewport2.OverlayRoot.Children.Count);
        });
    }

    private static (ScanFrustumDriver Driver, TwinViewport3D Viewport, IRealtimeHub Hub, SceneAssembly Assembly)
        CreateDriver(bool withParts = true)
    {
        var viewport = new TwinViewport3D();
        var hub = new RealtimeHub();
        var assembly = withParts
            ? BuildAssembly()
            : new SceneAssembly([], 0);
        var driver = new ScanFrustumDriver(viewport, hub, () => assembly);
        return (driver, viewport, hub, assembly);
    }

    private static List<List<Point3D>> CollectLines(TwinViewport3D viewport)
    {
        var result = new List<List<Point3D>>();
        var root = viewport.OverlayRoot;
        foreach (var visual in root.Children)
        {
            Walk(visual, result);
        }

        return result;

        static void Walk(Visual3D visual, List<List<Point3D>> result)
        {
            // LinesVisual3D 本身是 Visual3D（非 Model3D），直接按类型判
            if (visual is HelixToolkit.Wpf.LinesVisual3D lines)
            {
                result.Add(lines.Points.ToList());
            }

            if (visual is ModelVisual3D mv)
            {
                foreach (var child in mv.Children)
                {
                    Walk(child, result);
                }
            }
        }
    }

    /// <summary>手工装配：线扫相机（含镜头下沿）+ 在制晶圆，bounds 与占位资产一致。</summary>
    private static SceneAssembly BuildAssembly()
    {
        var visuals = new List<PartVisual>
        {
            MakePart("CAMERA_LINE_SCAN", new Point3D(0, 0.02, 0.86), new Size3D(0.34, 0.07, 0.14)),
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
            (new(center.X - hx, center.Y - hy, center.Z + hz), new(center.X - hx, center.Y - hy, center.Z - hz),
                new(center.X - hx, center.Y + hy, center.Z - hz), new(center.X - hx, center.Y + hy, center.Z + hz)),
            (new(center.X + hx, center.Y - hy, center.Z - hz), new(center.X + hx, center.Y - hy, center.Z + hz),
                new(center.X + hx, center.Y + hy, center.Z + hz), new(center.X + hx, center.Y + hy, center.Z - hz)),
            (new(center.X - hx, center.Y + hy, center.Z - hz), new(center.X + hx, center.Y + hy, center.Z - hz),
                new(center.X + hx, center.Y + hy, center.Z + hz), new(center.X - hx, center.Y + hy, center.Z + hz)),
            (new(center.X - hx, center.Y - hy, center.Z + hz), new(center.X + hx, center.Y - hy, center.Z + hz),
                new(center.X + hx, center.Y - hy, center.Z - hz), new(center.X - hx, center.Y - hy, center.Z - hz)),
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
