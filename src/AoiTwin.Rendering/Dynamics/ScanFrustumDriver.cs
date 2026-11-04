using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Bus;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;
using HelixToolkit.Wpf;

namespace AoiTwin.Rendering.Dynamics;

/// <summary>
/// 线扫视野锥与扫描进度（TA.6，FR-M2-05）：
/// - 视锥：从 CAMERA_LINE_SCAN 镜头下沿到扫描面（WAFER_CURRENT 顶面外接矩形）的四条棱线 +
///   扫描面矩形轮廓，预建一次只随装配重绑（LOD 换档）；
/// - 进度线：扫描面内沿 Y 的横线，随 <c>OpticsEvent(ScanProgress)</c>（Mock 2Hz ≈ 500ms）推进。
///
/// 全部为 LinesVisual3D 线框——不进实体渲染管线，无 FX 时也只是几条线段的开销；
/// 扫描进度不进状态双缓冲（TA.2 契约），驱动器直接订阅 Optics Topic 过滤部件。
/// </summary>
public sealed class ScanFrustumDriver : IDisposable
{
    /// <summary>视锥顶点部件（台账冻结件名）。</summary>
    public const string CameraPartId = "CAMERA_LINE_SCAN";

    /// <summary>扫描面部件（在制晶圆顶面）。</summary>
    public const string WaferPartId = "WAFER_CURRENT";

    private static readonly Color FrustumColor = Color.FromRgb(0x4F, 0xC3, 0xF7);
    private static readonly Color ProgressColor = Color.FromRgb(0xFF, 0xC1, 0x07);

    private readonly TwinViewport3D _viewport;
    private readonly IRealtimeHub _hub;
    private readonly Func<SceneAssembly?> _assemblyProvider;
    private readonly IDisposable _subscription;
    private readonly ModelVisual3D _visualRoot = new();
    private LinesVisual3D? _progressLine;
    private Rect3D _scanArea;
    private double _progress;
    private SceneAssembly? _boundAssembly;
    private bool _disposed;

    public ScanFrustumDriver(TwinViewport3D viewport, IRealtimeHub hub, Func<SceneAssembly?> assemblyProvider)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(assemblyProvider);

        _viewport = viewport;
        _hub = hub;
        _assemblyProvider = assemblyProvider;
        _subscription = hub.Subscribe<OpticsEvent>(TwinTopic.Optics, OnOptics);
        viewport.FrameRendered += OnViewportFrameRendered;
    }

    /// <summary>当前扫描进度（0..1；测试/诊断用）。</summary>
    public double Progress => _progress;

    /// <summary>叠加线段数（视锥 8 点对 + 进度线 2 点 = 3 个 LinesVisual3D；诊断用）。</summary>
    public int VisualCount => _visualRoot.Children.Count;

    private void OnOptics(OpticsEvent optics)
    {
        if (optics.PartId == CameraPartId && optics.Kind == OpticsKind.ScanProgress)
        {
            _progress = Math.Clamp(optics.Value, 0, 1);
        }
    }

    private void OnViewportFrameRendered(object? sender, TimeSpan e) => OnFrame();

    /// <summary>单帧驱动（公开供无头测试直接调用）。</summary>
    public void OnFrame()
    {
        if (_disposed)
        {
            return;
        }

        var assembly = _assemblyProvider();
        if (!ReferenceEquals(assembly, _boundAssembly))
        {
            Rebind(assembly);
        }

        if (_progressLine is not null && _scanArea.SizeX > 0)
        {
            UpdateProgressLine();
        }
    }

    private void Rebind(SceneAssembly? assembly)
    {
        _boundAssembly = assembly;
        _visualRoot.Children.Clear();
        _progressLine = null;

        if (assembly?.Find(CameraPartId) is not { } camera || assembly.Find(WaferPartId) is not { } wafer)
        {
            _viewport.OverlayRoot.Children.Remove(_visualRoot);
            return;
        }

        var lens = camera.Geometry.Bounds;
        var aperture = new Point3D(lens.X + lens.SizeX / 2, lens.Y + lens.SizeY / 2, lens.Z); // 镜头下沿

        var w = wafer.Geometry.Bounds;
        var z = w.Z + w.SizeZ + 0.002; // 扫描面：晶圆顶面外 2mm
        _scanArea = new Rect3D(w.X, w.Y, z, w.SizeX, w.SizeY, 0.001);
        var minX = _scanArea.X;
        var maxX = _scanArea.X + _scanArea.SizeX;
        var minY = _scanArea.Y;
        var maxY = _scanArea.Y + _scanArea.SizeY;

        // 四条棱线：镜头下沿 → 扫描面四角
        _visualRoot.Children.Add(new LinesVisual3D
        {
            Points = new Point3DCollection
            {
                aperture, new(minX, minY, z),
                aperture, new(maxX, minY, z),
                aperture, new(maxX, maxY, z),
                aperture, new(minX, maxY, z),
            },
            Color = FrustumColor,
            Thickness = 1.4,
        });

        // 扫描面矩形轮廓（点对语义：闭合轮廓显式回起点）
        _visualRoot.Children.Add(new LinesVisual3D
        {
            Points = new Point3DCollection
            {
                new(minX, minY, z), new(maxX, minY, z),
                new(maxX, minY, z), new(maxX, maxY, z),
                new(maxX, maxY, z), new(minX, maxY, z),
                new(minX, maxY, z), new(minX, minY, z),
            },
            Color = FrustumColor,
            Thickness = 1.0,
        });

        _progressLine = new LinesVisual3D
        {
            Color = ProgressColor,
            Thickness = 2.4,
        };
        _visualRoot.Children.Add(_progressLine);

        if (_viewport.OverlayRoot.Children.IndexOf(_visualRoot) < 0)
        {
            _viewport.OverlayRoot.Children.Add(_visualRoot);
        }
    }

    /// <summary>进度线：沿 +Y 从扫描面起点推进到终点。</summary>
    private void UpdateProgressLine()
    {
        var y = _scanArea.Y + _progress * _scanArea.SizeY;
        var x = _scanArea.X;
        var maxX = _scanArea.X + _scanArea.SizeX;
        var z = _scanArea.Z;
        var points = new Point3DCollection
        {
            new(x, y, z),
            new(maxX, y, z),
        };
        _progressLine!.Points = points;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscription.Dispose();
        _viewport.FrameRendered -= OnViewportFrameRendered;
        _visualRoot.Children.Clear();
        _viewport.OverlayRoot.Children.Remove(_visualRoot);
    }
}
