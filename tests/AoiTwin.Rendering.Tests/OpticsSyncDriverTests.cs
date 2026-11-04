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

public sealed class OpticsSyncDriverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-optics-" + Guid.NewGuid().ToString("N"));

    public OpticsSyncDriverTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Channel_events_repaint_ring_segments_with_configured_colors()
    {
        RunOnStaThread(() =>
        {
            var viewport = new TwinViewport3D(); // UserControl，非 IDisposable
            var hub = new RealtimeHub();
            using var visual = new TwinVisualViewModel(hub, motionPolicyProvider: () => ThrottlePolicy.PassThrough);
            var assembly = BuildAssembly(); // 驱动器按引用比对触发重绑，须保证实例稳定
            using var driver = new OpticsSyncDriver(
                viewport,
                visual,
                () => null, // 无配置文件：默认八色
                () => assembly);

            // 初始帧：全部熄灭（暗灰）
            driver.OnFrame();
            var segments = CollectSegmentBrushes(viewport);
            Assert.Equal(8, segments.Count);
            Assert.All(segments, b => Assert.Equal(Color.FromRgb(0x2A, 0x2A, 0x2A), b.Color));

            // 通道 3 满强度 → 该段变为 PRD §5.5 的 #E9C4FF（事件直推 + 帧内应用，≤100ms 结构性成立）
            hub.Publish(TwinTopic.Optics,
                new OpticsEvent("LIGHT_RING", OpticsKind.LightChannel, 3, 1.0, DateTimeOffset.UtcNow));
            driver.OnFrame();

            Assert.Equal(Color.FromRgb(0xE9, 0xC4, 0xFF), segments[2].Color);

            // 其余 7 段保持熄灭
            for (var i = 0; i < 8; i++)
            {
                if (i != 2)
                {
                    Assert.Equal(Color.FromRgb(0x2A, 0x2A, 0x2A), segments[i].Color);
                }
            }

            // 半强度：底色与配置色的中点
            hub.Publish(TwinTopic.Optics,
                new OpticsEvent("LIGHT_RING", OpticsKind.LightChannel, 3, 0.5, DateTimeOffset.UtcNow));
            driver.OnFrame();
            Assert.Equal(Color.FromRgb(0x89, 0x77, 0x94), segments[2].Color); // (0x2A+0xE9)/2, (0x2A+0xC4)/2, (0x2A+0xFF)/2
        });
    }

    [Fact]
    public void Custom_palette_file_drives_segment_colors()
    {
        RunOnStaThread(() =>
        {
            var palettePath = Path.Combine(_root, "light-channel-colors.json");
            File.WriteAllText(palettePath, """{ "lightChannelColors": { "1": "#010203", "2": "#040506" } }""");

            var viewport = new TwinViewport3D(); // UserControl，非 IDisposable
            var hub = new RealtimeHub();
            using var visual = new TwinVisualViewModel(hub, motionPolicyProvider: () => ThrottlePolicy.PassThrough);
            using var paletteSource = new LightChannelPaletteFileSource(palettePath);
            var assembly = BuildAssembly();
            using var driver = new OpticsSyncDriver(viewport, visual, () => paletteSource, () => assembly);

            hub.Publish(TwinTopic.Optics,
                new OpticsEvent("LIGHT_RING", OpticsKind.LightChannel, 1, 1.0, DateTimeOffset.UtcNow));
            hub.Publish(TwinTopic.Optics,
                new OpticsEvent("LIGHT_RING", OpticsKind.LightChannel, 2, 1.0, DateTimeOffset.UtcNow));
            driver.OnFrame();

            var segments = CollectSegmentBrushes(viewport);
            Assert.Equal(Color.FromRgb(0x01, 0x02, 0x03), segments[0].Color); // 通道 1 → 段 0
            Assert.Equal(Color.FromRgb(0x04, 0x05, 0x06), segments[1].Color); // 通道 2 → 段 1
        });
    }

    [Fact]
    public void Brightness_drives_coax_emissive_and_zero_hides_it()
    {
        RunOnStaThread(() =>
        {
            var viewport = new TwinViewport3D(); // UserControl，非 IDisposable
            var hub = new RealtimeHub();
            using var visual = new TwinVisualViewModel(hub, motionPolicyProvider: () => ThrottlePolicy.PassThrough);
            var assembly = BuildAssembly();
            using var driver = new OpticsSyncDriver(viewport, visual, () => null, () => assembly);

            driver.OnFrame();
            var shells = CollectShellBrushes(viewport);
            Assert.NotEmpty(shells);
            Assert.Equal(0, shells[0].Color.A); // 初始亮度 0 = 完全隐藏

            hub.Publish(TwinTopic.Optics,
                new OpticsEvent("LIGHT_COAX", OpticsKind.LightBrightness, 0, 0.6, DateTimeOffset.UtcNow));
            driver.OnFrame();

            Assert.Equal(120, shells[0].Color.A); // 200 × 0.6

            hub.Publish(TwinTopic.Optics,
                new OpticsEvent("LIGHT_COAX", OpticsKind.LightBrightness, 0, 0.0, DateTimeOffset.UtcNow));
            driver.OnFrame();
            Assert.Equal(0, shells[0].Color.A);
        });
    }

    [Fact]
    public void Dispose_removes_overlay_visuals()
    {
        RunOnStaThread(() =>
        {
            var viewport = new TwinViewport3D();
            var hub = new RealtimeHub();
            using var visual = new TwinVisualViewModel(hub, motionPolicyProvider: () => ThrottlePolicy.PassThrough);
            var driver = new OpticsSyncDriver(viewport, visual, () => null, () => BuildAssembly());
            driver.OnFrame();
            Assert.True(driver.VisualCount > 0);
            Assert.Contains(viewport.OverlayRoot.Children.Cast<Visual3D>(), v => true); // 已挂入

            driver.Dispose();
            Assert.DoesNotContain(viewport.OverlayRoot.Children.Cast<Visual3D>(), v => true); // 摘除
        });
    }

    private static List<SolidColorBrush> CollectSegmentBrushes(TwinViewport3D viewport)
    {
        var brushes = new List<SolidColorBrush>();
        foreach (var model in EnumerateModels(viewport.OverlayRoot))
        {
            if (model.Material is DiffuseMaterial { Brush: SolidColorBrush brush })
            {
                brushes.Add(brush);
            }
        }

        // 环段按通道序（构建顺序即通道 1..8）
        return brushes;
    }

    private static List<SolidColorBrush> CollectShellBrushes(TwinViewport3D viewport)
    {
        var brushes = new List<SolidColorBrush>();
        foreach (var model in EnumerateModels(viewport.OverlayRoot))
        {
            if (model.Material is EmissiveMaterial { Brush: SolidColorBrush brush })
            {
                brushes.Add(brush);
            }
        }

        return brushes;
    }

    private static IEnumerable<GeometryModel3D> EnumerateModels(Visual3D visual)
    {
        if (visual is ModelVisual3D { Content: Model3D content })
        {
            foreach (var model in Flatten(content))
            {
                yield return model;
            }
        }

        if (visual is ModelVisual3D mv)
        {
            foreach (var child in mv.Children)
            {
                foreach (var model in EnumerateModels(child))
                {
                    yield return model;
                }
            }
        }
    }

    private static IEnumerable<GeometryModel3D> Flatten(Model3D model)
    {
        if (model is GeometryModel3D gm)
        {
            yield return gm;
        }
        else if (model is Model3DGroup group)
        {
            foreach (var child in group.Children)
            {
                foreach (var inner in Flatten(child))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>手工装配：LIGHT_RING 环体 + LIGHT_COAX 盒（bounds 与占位资产一致量级）。</summary>
    private static SceneAssembly BuildAssembly()
    {
        var visuals = new List<PartVisual>
        {
            MakePart("LIGHT_RING", center: new Point3D(0, -0.35, 0.66), size: new Size3D(0.308, 0.308, 0.028)),
            MakePart("LIGHT_COAX", center: new Point3D(0, -0.35, 0.55), size: new Size3D(0.10, 0.10, 0.08)),
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
        return new PartVisual(partId, visual, group, new System.Windows.Media.Media3D.TranslateTransform3D());
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
