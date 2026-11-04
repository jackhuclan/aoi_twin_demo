using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Bus;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;

namespace AoiTwin.Rendering.Fx;

/// <summary>
/// 报警脉冲特效（TA.11，FR-M2-12）：部件进入 <c>Severity.Alarm</c> 时在其包围盒上
/// 叠加发光壳，材质透明度 0→1 呼吸（DispatcherTimer 16ms 直改属性——不用 Storyboard，
/// 解除时可立即摘除，无动画排空延迟）；恢复非 Alarm 自动解除。
/// 壳体几何随装配重绑（报警瞬间正处 LOD 换档也正确重建）。
/// </summary>
public sealed class AlarmPulseFx : IDisposable
{
    /// <summary>呼吸周期（毫秒）：报警视觉约 0.8s 一个呼吸，够醒目不打扰。</summary>
    public const int PulsePeriodMs = 800;

    private static readonly Color PulseColor = Color.FromRgb(0xFF, 0x47, 0x57);

    private readonly TwinViewport3D _viewport;
    private readonly IRealtimeHub _hub;
    private readonly Func<SceneAssembly?> _assemblyProvider;
    private readonly FxOverlay _overlay;
    private readonly DispatcherTimer _timer;
    private readonly IDisposable _subscription;
    private readonly HashSet<string> _alarmingParts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModelVisual3D> _shells = new(StringComparer.Ordinal);
    private readonly List<SolidColorBrush> _pulseBrushes = [];
    private long _tick;
    private bool _disposed;

    public AlarmPulseFx(TwinViewport3D viewport, IRealtimeHub hub, Func<SceneAssembly?> assemblyProvider, FxOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(assemblyProvider);
        ArgumentNullException.ThrowIfNull(overlay);

        _viewport = viewport;
        _hub = hub;
        _assemblyProvider = assemblyProvider;
        _overlay = overlay;
        _subscription = hub.Subscribe<StatusEvent>(TwinTopic.Status, OnStatus);

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += (_, _) => OnTick();
    }

    /// <summary>当前报警部件数（诊断用）。</summary>
    public int AlarmingCount => _alarmingParts.Count;

    private void OnStatus(StatusEvent status)
    {
        if (status.Severity == Severity.Alarm)
        {
            if (_alarmingParts.Add(status.PartId))
            {
                StartPulse(status.PartId);
            }
        }
        else if (_alarmingParts.Remove(status.PartId))
        {
            StopPulse(status.PartId);
        }
    }

    private void StartPulse(string partId)
    {
        RebuildShell(partId);
        if (_shells.Count > 0)
        {
            _timer.Start();
        }
    }

    private void StopPulse(string partId)
    {
        _overlay.Detach(FxKind.AlarmPulse, partId);
        _shells.Remove(partId);
        if (_shells.Count == 0)
        {
            _timer.Stop();
        }
    }

    /// <summary>发光壳：部件包围盒略放大 + EmissiveMaterial（呼吸透明度）。</summary>
    private void RebuildShell(string partId)
    {
        _overlay.Detach(FxKind.AlarmPulse, partId);
        _shells.Remove(partId);

        if (_assemblyProvider()?.Find(partId) is not { } part)
        {
            return; // 装配未就绪：不建壳；换档重绑时若有报警再补
        }

        var shell = BuildShell(part.Geometry.Bounds);
        _overlay.Attach(FxKind.AlarmPulse, partId, shell);
        _shells[partId] = shell;
    }

    private ModelVisual3D BuildShell(Rect3D bounds)
    {
        var brush = new SolidColorBrush(Color.FromArgb(0, PulseColor.R, PulseColor.G, PulseColor.B));
        _pulseBrushes.Add(brush); // 呼吸只改这些刷子；壳摘除后残留刷子由 GC 回收

        var m = new Matrix3D();
        m.Translate(new Vector3D(-0.5, -0.5, -0.5));
        m.Scale(new Vector3D(bounds.SizeX * 1.04, bounds.SizeY * 1.04, bounds.SizeZ * 1.04));
        m.Translate(new Vector3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2));

        var model = new GeometryModel3D
        {
            Geometry = UnitCube.Shared,
            Material = new EmissiveMaterial(brush),
            Transform = new MatrixTransform3D(m),
        };
        return new ModelVisual3D { Content = model };
    }

    private void OnTick()
    {
        _tick++;
        // 呼吸：sin 半波透明度 0..170
        var phase = 2 * Math.PI * (_tick * 16 % PulsePeriodMs) / PulsePeriodMs;
        var alpha = (byte)Math.Max(0, Math.Sin(phase) * 170);
        foreach (var brush in _pulseBrushes)
        {
            brush.Color = Color.FromArgb(alpha, PulseColor.R, PulseColor.G, PulseColor.B);
        }
    }

    /// <summary>LOD 换档后重建报警壳（装配实例变化时由视图层调用）。</summary>
    public void Refresh()
    {
        foreach (var partId in _alarmingParts)
        {
            RebuildShell(partId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscription.Dispose();
        _timer.Stop();
        foreach (var partId in _alarmingParts.ToArray())
        {
            StopPulse(partId);
        }

        _pulseBrushes.Clear();
    }

    /// <summary>共享单位盒（中心原点、边长 1；壳差异全靠矩阵）。</summary>
    private static class UnitCube
    {
        public static readonly MeshGeometry3D Shared = Create();

        private static MeshGeometry3D Create()
        {
            const double h = 0.5;
            var mesh = new MeshGeometry3D();
            var positions = new Point3DCollection();
            var indices = new Int32Collection();
            Span<(Point3D a, Point3D b, Point3D c, Point3D d)> faces = stackalloc (Point3D, Point3D, Point3D, Point3D)[]
            {
                (new(-h, -h, -h), new(h, -h, -h), new(h, h, -h), new(-h, h, -h)),
                (new(h, -h, h), new(-h, -h, h), new(-h, h, h), new(h, h, h)),
                (new(-h, -h, h), new(-h, -h, -h), new(-h, h, -h), new(-h, h, h)),
                (new(h, -h, -h), new(h, -h, h), new(h, h, h), new(h, h, -h)),
                (new(-h, h, -h), new(h, h, -h), new(h, h, h), new(-h, h, h)),
                (new(-h, -h, h), new(h, -h, h), new(h, -h, -h), new(-h, -h, -h)),
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
            return mesh;
        }
    }
}
