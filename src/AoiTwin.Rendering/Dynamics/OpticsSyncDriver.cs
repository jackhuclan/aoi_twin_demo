using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;
using AoiTwin.ViewModel;

namespace AoiTwin.Rendering.Dynamics;

/// <summary>
/// 光学状态驱动（TA.5，FR-M2-04）：把 <see cref="TwinVisualViewModel"/> 里的光学状态快照
/// 按帧写进可视元素——事件直推 + 帧内应用，端到端延迟结构性 ≤ 1 帧（≈16ms，远小于 100ms 判据）。
///
/// 两种可视化均为「叠加视觉」挂 <see cref="TwinViewport3D.OverlayRoot"/>，不改装配材质：
/// 装配材质在 M1 已 Freeze（描边/LOD 共享），叠加层随驱动器生命周期挂摘（Dispose 摘除）。
/// - LIGHT_RING：程序化 8 段环（每通道一段独立未冻结 DiffuseMaterial），
///   参数由装配几何包围盒推导；通道值 0 = 熄灭灰，值 v = 配置色 × v；
/// - LIGHT_COAX：包围盒发光壳（EmissiveMaterial），亮度 → 透明度与色温（0 = 完全隐藏）。
///
/// 占位资产行为说明：真实光源 GLB 带分区网格后（M3），LIGHT_RING 换为逐区改材质。
/// </summary>
public sealed class OpticsSyncDriver : IDisposable
{
    /// <summary>环形光源部件（台账冻结件名）。</summary>
    public const string RingPartId = "LIGHT_RING";

    /// <summary>同轴光源部件。</summary>
    public const string CoaxPartId = "LIGHT_COAX";

    /// <summary>通道熄灭时的底色（暗灰，保留环轮廓）。</summary>
    private static readonly Color OffColor = Color.FromRgb(0x2A, 0x2A, 0x2A);

    /// <summary>未配置通道的降级色。</summary>
    private static readonly RgbColor FallbackColor = new(0xE8, 0xE8, 0xE8);

    private readonly TwinViewport3D _viewport;
    private readonly TwinVisualViewModel _visualViewModel;
    private readonly Func<LightChannelPaletteFileSource?> _paletteSourceProvider;
    private readonly Func<SceneAssembly?> _assemblyProvider;
    private readonly ModelVisual3D _visualRoot = new();
    private bool _visualAttached;
    private SceneAssembly? _boundAssembly;
    private SegmentVisual[] _ringSegments = [];
    private SegmentVisual[] _coaxShells = [];
    private bool _disposed;

    public OpticsSyncDriver(
        TwinViewport3D viewport,
        TwinVisualViewModel visualViewModel,
        Func<LightChannelPaletteFileSource?> paletteSourceProvider,
        Func<SceneAssembly?> assemblyProvider)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(visualViewModel);
        ArgumentNullException.ThrowIfNull(paletteSourceProvider);
        ArgumentNullException.ThrowIfNull(assemblyProvider);

        _viewport = viewport;
        _visualViewModel = visualViewModel;
        _paletteSourceProvider = paletteSourceProvider;
        _assemblyProvider = assemblyProvider;

        viewport.FrameRendered += OnViewportFrameRendered;
    }

    private void OnViewportFrameRendered(object? sender, TimeSpan e) => OnFrame();

    /// <summary>叠加视觉数（环段 + 发光壳；测试/诊断用）。</summary>
    public int VisualCount => _ringSegments.Length + _coaxShells.Length;

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

        if (_ringSegments.Length == 0 && _coaxShells.Length == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (_ringSegments.Length > 0)
        {
            // 无配置源/文件缺失 → PRD §5.5 默认八色（与 LightChannelPaletteFileSource 的兜底一致）
            var palette = _paletteSourceProvider()?.Channels ?? LightChannelPalette.Default;
            var ring = (OpticsPartViewModel)_visualViewModel.GetOpticsPart(RingPartId);
            var state = ring.ReadLatest();
            for (var i = 0; i < _ringSegments.Length; i++)
            {
                var value = i < state.ChannelValues.Length ? Math.Clamp(state.ChannelValues[i], 0, 1) : 0;
                var rgb = palette is not null && palette.TryGetValue(i + 1, out var c) ? c : FallbackColor;
                _ringSegments[i].SetColor(Blend(OffColor, rgb, value));
            }
        }

        if (_coaxShells.Length > 0)
        {
            var coax = (OpticsPartViewModel)_visualViewModel.GetOpticsPart(CoaxPartId);
            var brightness = Math.Clamp(coax.ReadLatest().Brightness, 0, 1);
            foreach (var shell in _coaxShells)
            {
                shell.SetEmissive(brightness);
            }
        }
    }

    /// <summary>熄灭底色 → 配置色按强度混合（v=0 全暗、v=1 纯配置色）。</summary>
    private static Color Blend(Color off, RgbColor on, double v) => Color.FromRgb(
        (byte)(off.R + ((on.R - off.R) * v)),
        (byte)(off.G + ((on.G - off.G) * v)),
        (byte)(off.B + ((on.B - off.B) * v)));

    private void Rebind(SceneAssembly? assembly)
    {
        if (_boundAssembly is not null)
        {
            _visualRoot.Children.Clear(); // 换档重建：摘除旧叠加视觉
        }

        _boundAssembly = assembly;
        var segments = Array.Empty<SegmentVisual>();
        var shells = Array.Empty<SegmentVisual>();

        if (assembly is not null)
        {
            var ring = assembly.Find(RingPartId);
            if (ring is not null)
            {
                segments = BuildRingSegments(ring);
            }

            var coax = assembly.Find(CoaxPartId);
            if (coax is not null)
            {
                shells = BuildCoaxShell(coax);
            }
        }

        _ringSegments = segments;
        _coaxShells = shells;
        if (_visualRoot.Children.Count > 0 && !_visualAttached)
        {
            _viewport.OverlayRoot.Children.Add(_visualRoot); // 有视觉才挂入
            _visualAttached = true;
        }
        else if (_visualRoot.Children.Count == 0 && _visualAttached)
        {
            _viewport.OverlayRoot.Children.Remove(_visualRoot);
            _visualAttached = false;
        }
    }

    /// <summary>8 段环：共享单位盒几何 + 各段独立矩阵与材质。</summary>
    private SegmentVisual[] BuildRingSegments(PartVisual part)
    {
        var bounds = part.Geometry.Bounds;
        const int channelCount = 8;
        var thickness = Math.Max(bounds.SizeX, bounds.SizeY) * 0.09;
        var radius = bounds.SizeX / 2 - thickness;
        var height = Math.Max(bounds.SizeZ * 0.7, 0.008);
        var topZ = bounds.Z + bounds.SizeZ;
        var center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, topZ + thickness);

        var unitBox = CreateUnitBox();
        var segments = new SegmentVisual[channelCount];
        for (var i = 0; i < channelCount; i++)
        {
            var angle = 2 * Math.PI * (i + 0.5) / channelCount; // 段中心角，覆盖整周
            var cx = center.X + radius * Math.Cos(angle);
            var cy = center.Y + radius * Math.Sin(angle);
            var arcLength = 2 * Math.PI * radius / channelCount * 0.8;

            var m = new Matrix3D();
            m.Translate(new Vector3D(-0.5, -0.5, -0.5));
            m.Scale(new Vector3D(arcLength, thickness, height));
            m.Rotate(new Quaternion(new Vector3D(0, 0, 1), angle * 180 / Math.PI));
            m.Translate(new Vector3D(cx, cy, center.Z));

            var brush = new SolidColorBrush(OffColor);
            var model = new GeometryModel3D
            {
                Geometry = unitBox,
                Material = new DiffuseMaterial(brush),
                Transform = new MatrixTransform3D(m),
            };
            var visual = new ModelVisual3D { Content = model };
            _visualRoot.Children.Add(visual);
            segments[i] = new SegmentVisual(brush, emissive: null);
        }

        return segments;
    }

    /// <summary>同轴光源发光壳：包围盒略缩，EmissiveMaterial 亮度→色值。</summary>
    private SegmentVisual[] BuildCoaxShell(PartVisual part)
    {
        var bounds = part.Geometry.Bounds;
        var unitBox = CreateUnitBox();
        var shells = new List<SegmentVisual>(4);

        foreach (var mesh in part.Geometry.Children.OfType<GeometryModel3D>())
        {
            var b = mesh.Bounds;
            var m = new Matrix3D();
            m.Translate(new Vector3D(-0.5, -0.5, -0.5));
            m.Scale(new Vector3D(b.SizeX * 0.98, b.SizeY * 0.98, b.SizeZ * 0.98));
            m.Translate(new Vector3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2));

            var brush = new SolidColorBrush(Color.FromArgb(0, 0xFF, 0xE9, 0xC4));
            var model = new GeometryModel3D
            {
                Geometry = unitBox,
                Material = new EmissiveMaterial(brush),
                Transform = new MatrixTransform3D(m),
            };
            _visualRoot.Children.Add(new ModelVisual3D { Content = model });
            shells.Add(new SegmentVisual(null, brush));
        }

        return shells.ToArray();
    }

    /// <summary>单位盒（中心原点、边长 1，Freeze 共享；段差异全靠矩阵）。</summary>
    private static MeshGeometry3D CreateUnitBox()
    {
        const double h = 0.5;
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection();
        var indices = new Int32Collection();

        // 六个面，每面四顶点两三角（法线由 WPF 屏背面剔除，双面渲染不必要）
        Span<(Point3D a, Point3D b, Point3D c, Point3D d)> faces = stackalloc (Point3D, Point3D, Point3D, Point3D)[]
        {
            (new(-h, -h, -h), new(h, -h, -h), new(h, h, -h), new(-h, h, -h)),       // 前 -Z
            (new(h, -h, h), new(-h, -h, h), new(-h, h, h), new(h, h, h)),           // 后 +Z
            (new(-h, -h, h), new(-h, -h, -h), new(-h, h, -h), new(-h, h, h)),       // 左 -X
            (new(h, -h, -h), new(h, -h, h), new(h, h, h), new(h, h, -h)),           // 右 +X
            (new(-h, h, -h), new(h, h, -h), new(h, h, h), new(-h, h, h)),           // 上 +Y
            (new(-h, -h, h), new(h, -h, h), new(h, -h, -h), new(-h, -h, -h)),       // 下 -Y
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewport.FrameRendered -= OnViewportFrameRendered;
        _visualRoot.Children.Clear();
        _viewport.OverlayRoot.Children.Remove(_visualRoot);

        _ringSegments = [];
        _coaxShells = [];
    }

    /// <summary>单个可变材质段：环段用 diffuse 色刷，发光壳用 emissive 色刷。</summary>
    private sealed class SegmentVisual(SolidColorBrush? diffuse, SolidColorBrush? emissive)
    {
        public void SetColor(Color color)
        {
            if (diffuse is not null && diffuse.Color != color)
            {
                diffuse.Color = color; // 未冻结刷子，属性写即失效重绘
            }
        }

        public void SetEmissive(double brightness)
        {
            if (emissive is null)
            {
                return;
            }

            var alpha = (byte)Math.Round(200 * brightness);
            var color = Color.FromArgb(alpha, 0xFF, 0xE9, 0xC4);
            if (emissive.Color != color)
            {
                emissive.Color = color;
            }
        }
    }
}
