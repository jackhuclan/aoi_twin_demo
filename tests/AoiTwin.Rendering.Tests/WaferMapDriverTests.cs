using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Bus;
using AoiTwin.Rendering.Dynamics;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;
using AoiTwin.ViewModel;
using Xunit;

namespace AoiTwin.Rendering.Tests;

public sealed class WaferMapDriverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-wafermap-" + Guid.NewGuid().ToString("N"));

    public WaferMapDriverTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Binary_diemap_paints_expected_die_colors()
    {
        RunOnStaThread(() =>
        {
            var (driver, viewport, visual, _) = CreateDriver();
            try
            {
                driver.OnFrame();
                Assert.Equal(0, driver.RefreshCount); // 无片数据：不刷
                Assert.NotNull(GetDisc(viewport)); // 圆片视觉已挂入

                // 4×4 二值图：0 未测 / 1 OK / 2 NG 交替（初版 1×1 占位纹理被替换为实际尺寸）
                visual.WaferQueue.Enqueue(MakeResult(
                    "W-BIN", 4, 4, die => (byte)(die % 3)));
                driver.OnFrame();

                Assert.Equal((4, 4), driver.TextureSize);
                Assert.Equal(1, driver.RefreshCount);
                var bitmap = GetBitmap(viewport); // 刷新后再取：纹理已从 1×1 占位重建
                AssertPixel(bitmap!, 0, Color.FromRgb(0x1E, 0x2A, 0x32)); // 未测
                AssertPixel(bitmap!, 1, Color.FromRgb(0x2E, 0xD5, 0x73)); // OK
                AssertPixel(bitmap!, 2, Color.FromRgb(0xFF, 0x47, 0x57)); // NG
            }
            finally
            {
                driver.Dispose();
            }
        });
    }

    [Fact]
    public void Gray_scale_diemap_maps_gradient()
    {
        RunOnStaThread(() =>
        {
            var (driver, viewport, visual, _) = CreateDriver();
            try
            {
                driver.OnFrame();
                visual.WaferQueue.Enqueue(MakeResult("W-GRAY", 2, 1, die => (byte)(3 + die * 126))); // 3 / 129
                driver.OnFrame();

                var bitmap = GetBitmap(viewport)!;
                var ok = Color.FromRgb(0x2E, 0xD5, 0x73);
                var ng = Color.FromRgb(0xFF, 0x47, 0x57);
                // 值 3 → OK 端点；值 129 → 渐变 t=(129-3)/252=0.5
                AssertPixel(bitmap, 0, ok);
                AssertPixel(bitmap, 1, Color.FromRgb(
                    (byte)((ok.R + ng.R) / 2), (byte)((ok.G + ng.G) / 2), (byte)((ok.B + ng.B) / 2)));
            }
            finally
            {
                driver.Dispose();
            }
        });
    }

    [Fact]
    public void Whole_map_refresh_never_rebuilds_geometry()
    {
        RunOnStaThread(() =>
        {
            var (driver, viewport, visual, assembly) = CreateDriver();
            try
            {
                driver.OnFrame();
                var waferGeometry = assembly.Find("WAFER_CURRENT")!.Geometry.Bounds;
                var disc = GetDisc(viewport)!;
                var geometryRef = disc.Geometry;
                var materialRef = disc.Material;

                // 连续多片整片刷新（NFR-11 判据的机制面：只 WritePixels）
                for (var i = 0; i < 5; i++)
                {
                    visual.WaferQueue.Enqueue(MakeResult($"W-{i}", 8, 8, _ => (byte)(i % 3)));
                }

                driver.OnFrame();

                Assert.Equal(5, driver.RefreshCount);
                Assert.Equal((8, 8), driver.TextureSize);
                Assert.Same(geometryRef, disc.Geometry); // 几何引用不变（判据明文）
                Assert.Same(materialRef, disc.Material); // 材质对象不变，只换 ImageSource

                // 尺寸变化（换配方）：重建的是纹理缓冲，几何仍不动
                visual.WaferQueue.Enqueue(MakeResult("W-NEW", 12, 6, _ => 1));
                driver.OnFrame();
                Assert.Equal((12, 6), driver.TextureSize);
                Assert.Same(geometryRef, disc.Geometry);
                _ = waferGeometry;
            }
            finally
            {
                driver.Dispose();
            }
        });
    }

    [Fact]
    public void Dispose_removes_disc_visual()
    {
        RunOnStaThread(() =>
        {
            var (driver, viewport, _, _) = CreateDriver();
            driver.OnFrame();
            Assert.Equal(1, viewport.OverlayRoot.Children.Count);
            driver.Dispose();
            Assert.Equal(0, viewport.OverlayRoot.Children.Count);
        });
    }

    private static (WaferMapDriver Driver, TwinViewport3D Viewport, TwinVisualViewModel Visual, SceneAssembly Assembly)
        CreateDriver()
    {
        var viewport = new TwinViewport3D();
        var hub = new RealtimeHub();
        var visual = new TwinVisualViewModel(hub, motionPolicyProvider: () => ThrottlePolicy.PassThrough);
        var assembly = BuildAssembly();
        var driver = new WaferMapDriver(viewport, visual, () => assembly);
        return (driver, viewport, visual, assembly);
    }

    private static WaferResult MakeResult(string waferId, int sizeX, int sizeY, Func<int, byte> fill) => new(
        waferId,
        new WaferMapSnapshot(sizeX, sizeY, Enumerable.Range(0, sizeX * sizeY).Select(fill).ToArray()),
        null,
        TimeSpan.FromMilliseconds(1200),
        DateTimeOffset.UtcNow);

    private static WriteableBitmap? GetBitmap(TwinViewport3D viewport)
    {
        var disc = GetDisc(viewport);
        return disc?.Material is DiffuseMaterial { Brush: ImageBrush { ImageSource: WriteableBitmap bitmap } }
            ? bitmap
            : null;
    }

    private static GeometryModel3D? GetDisc(TwinViewport3D viewport)
    {
        foreach (var visual in viewport.OverlayRoot.Children)
        {
            if (FindDisc(visual) is { } gm)
            {
                return gm;
            }
        }

        return null;
    }

    private static GeometryModel3D? FindDisc(Visual3D visual)
    {
        if (visual is ModelVisual3D { Content: GeometryModel3D gm })
        {
            return gm;
        }

        if (visual is ModelVisual3D mv)
        {
            foreach (var child in mv.Children)
            {
                if (FindDisc(child) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static void AssertPixel(WriteableBitmap bitmap, int index, Color expected)
    {
        var buffer = new byte[4];
        bitmap.CopyPixels(new Int32Rect(index, 0, 1, 1), buffer, 4, 0);
        var actual = Color.FromRgb(buffer[2], buffer[1], buffer[0]); // Bgra32
        Assert.Equal(expected, actual);
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
