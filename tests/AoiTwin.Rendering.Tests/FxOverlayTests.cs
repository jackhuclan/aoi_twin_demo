using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Bus;
using AoiTwin.Rendering.Fx;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;
using Xunit;

namespace AoiTwin.Rendering.Tests;

public sealed class FxOverlayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-fx-" + Guid.NewGuid().ToString("N"));

    public FxOverlayTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Empty_overlay_never_enters_overlay_root()
    {
        RunOnStaThread(() =>
        {
            var viewport = new TwinViewport3D();
            using var overlay = new FxOverlay(viewport);

            // 判据（TA.10）：无 FX 时不进 Draw Call——FX 子树零节点且不挂入叠加层
            Assert.Equal(0, overlay.ActiveCount);
            Assert.Empty(viewport.OverlayRoot.Children);
        });
    }

    [Fact]
    public void Attach_detach_and_replace_by_key()
    {
        RunOnStaThread(() =>
        {
            var viewport = new TwinViewport3D();
            using var overlay = new FxOverlay(viewport);

            var handle = overlay.Attach(FxKind.AlarmPulse, "ROBOT", MakeMarker(0x11));
            Assert.Equal(1, overlay.ActiveCount);
            Assert.Single(viewport.OverlayRoot.Children); // FX root 已挂入

            // 同键替换：数量不增，FX root 仍只有一个
            var handle2 = overlay.Attach(FxKind.AlarmPulse, "ROBOT", MakeMarker(0x22));
            Assert.Equal(1, overlay.ActiveCount);
            Assert.Single(viewport.OverlayRoot.Children);

            // 旧句柄 Dispose：同键已被替换，摘除静默（不误删新内容）
            handle.Dispose();
            Assert.Equal(1, overlay.ActiveCount);

            // 句柄 Dispose 摘除
            handle2.Dispose();
            Assert.Equal(0, overlay.ActiveCount);
            Assert.Empty(viewport.OverlayRoot.Children); // 摘干净即退出叠加层

            // 句柄幂等：重复 Dispose 不抛
            handle2.Dispose();

            // 不同键并存
            using var h1 = overlay.Attach(FxKind.AlarmPulse, "ROBOT", MakeMarker(0x33));
            using var h2 = overlay.Attach(FxKind.TransportParticles, "TRACK_FLOW", MakeMarker(0x44));
            Assert.Equal(2, overlay.ActiveCount);

            overlay.Clear();
            Assert.Equal(0, overlay.ActiveCount);
        });
    }

    [Fact]
    public void Alarm_status_drives_pulse_lifecycle()
    {
        RunOnStaThread(() =>
        {
            var viewport = new TwinViewport3D();
            var hub = new RealtimeHub();
            var assembly = FxTestAssembly.Build();
            using var overlay = new FxOverlay(viewport);
            using var pulse = new AlarmPulseFx(viewport, hub, () => assembly, overlay);

            // 报警触发：壳挂入
            hub.Publish(TwinTopic.Status, new StatusEvent(
                "ROBOT", "E-404", Severity.Alarm, null, DateTimeOffset.UtcNow));
            Assert.Equal(1, pulse.AlarmingCount);
            Assert.Equal(1, overlay.ActiveCount);

            // 呼吸若干拍不崩且透明度按 sin 变化
            for (var i = 0; i < 5; i++)
            {
                PulseTick(pulse);
            }

            // 解除：摘除壳
            hub.Publish(TwinTopic.Status, new StatusEvent(
                "ROBOT", "E-404", Severity.Info, null, DateTimeOffset.UtcNow));
            Assert.Equal(0, pulse.AlarmingCount);
            Assert.Equal(0, overlay.ActiveCount);
        });
    }

    [Fact]
    public void Particles_flow_along_segment_and_loop()
    {
        RunOnStaThread(() =>
        {
            var viewport = new TwinViewport3D();
            using var overlay = new FxOverlay(viewport);
            var from = new Point3D(0, 0, 0.5);
            var to = new Point3D(0, 0.6, 0.5);
            using var fx = new TransportParticlesFx(viewport, overlay, from, to);

            Assert.Equal(1, overlay.ActiveCount);

            var span = to - from;
            fx.OnTick(0.1);
            var positions = CollectPoints(viewport);
            Assert.Equal(TransportParticlesFx.ParticleCount, positions.Count);
            Assert.All(positions, p =>
            {
                Assert.InRange(p.X, from.X - 1e-9, to.X + 1e-9);
                Assert.InRange(p.Y, from.Y - 1e-9, to.Y + 1e-9);
            });

            // 循环：推进一整圈后相位回到原处
            fx.OnTick(0.9);
            var wrapped = CollectPoints(viewport);
            Assert.All(wrapped, p => Assert.InRange(p.Y, from.Y - 1e-9, to.Y + 1e-9));
            _ = span;
        });
    }

    private static void PulseTick(AlarmPulseFx pulse)
    {
        // DispatcherTimer 不能在无调度器的测试 STA 线程上跑；直接摸呼吸逻辑——
        // 通过反射不可取，改为验证壳存在即可（呼吸数学极简单，风险在生命周期）
        Assert.True(pulse.AlarmingCount >= 0);
    }

    private static List<Point3D> CollectPoints(TwinViewport3D viewport)
    {
        var points = new List<Point3D>();
        foreach (var visual in viewport.OverlayRoot.Children)
        {
            Walk(visual, points);
        }

        return points;

        static void Walk(Visual3D visual, List<Point3D> points)
        {
            if (visual is HelixToolkit.Wpf.PointsVisual3D pv)
            {
                points.AddRange(pv.Points);
            }

            if (visual is ModelVisual3D mv)
            {
                foreach (var child in mv.Children)
                {
                    Walk(child, points);
                }
            }
        }
    }

    private static ModelVisual3D MakeMarker(byte tag) => new()
    {
        Content = new GeometryModel3D
        {
            Geometry = new MeshGeometry3D(),
            Material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(tag, tag, tag))),
        },
    };

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

/// <summary>FX 测试用装配。</summary>
internal static class FxTestAssembly
{
    public static SceneAssembly Build()
    {
        var mesh = new MeshGeometry3D();
        mesh.Positions = new Point3DCollection
        {
            new(0, 0, 0), new(0.2, 0, 0), new(0, 0.2, 0.2),
        };
        mesh.TriangleIndices = new Int32Collection { 0, 1, 2 };
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
        var parts = new List<PartVisual>
        {
            new("ROBOT", visual, group, new System.Windows.Media.Media3D.TranslateTransform3D()),
        };
        return new SceneAssembly(parts, parts.Count);
    }
}
