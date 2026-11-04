using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;
using AoiTwin.ViewModel;

namespace AoiTwin.Rendering.Dynamics;

/// <summary>
/// 装载状态驱动（TA.16，FR-M2-13）：Track 晶圆占位 + LoadPort Cassette 槽位点亮。
///
/// 结构约定（与 TA.5–TA.8 一致）：全部走 <see cref="TwinViewport3D.OverlayRoot"/> 叠加视觉，
/// 装配材质零侵入；晶圆占位壳按需 attach/detach（无片时完全退出 Draw Call），
/// 25 个槽位共享一份冻结几何、只换各自材质引用（一次装配，之后只改灯）。
///
/// 节流（计划判据 1s）：状态变化记脏，帧循环内最多每 <see cref="ApplyInterval"/> 应用一次；
/// 首次状态立即应用（避免开机 1s 空窗）。
/// </summary>
public sealed class HandlingSyncDriver : IDisposable
{
    /// <summary>状态应用周期（计划判据：1s 节流）。</summary>
    public static readonly TimeSpan ApplyInterval = TimeSpan.FromSeconds(1);

    private readonly TwinViewport3D _viewport;
    private readonly HandlingStateViewModel _state;
    private readonly Func<SceneAssembly?> _assemblyProvider;
    private readonly Func<TimeSpan> _clock;
    private const int DiscSegments = 32;
    private readonly ModelVisual3D _visualRoot = new();
    private readonly ModelVisual3D _waferVisual = new();
    private readonly List<GeometryModel3D> _slots = [];
    private MeshGeometry3D? _slotMesh;
    private SceneAssembly? _boundAssembly;
    private TimeSpan _lastApply = TimeSpan.MinValue;
    private int _appliedCassetteCount = -1;
    private bool _appliedWaferPresent;
    private bool _waferAttached;
    private bool _disposed;

    /// <param name="clock">时间源（缺省 <see cref="Environment.TickCount64"/>；测试可注入手动时钟）。</param>
    public HandlingSyncDriver(
        TwinViewport3D viewport,
        HandlingStateViewModel state,
        Func<SceneAssembly?> assemblyProvider,
        Func<TimeSpan>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(assemblyProvider);

        _viewport = viewport;
        _state = state;
        _assemblyProvider = assemblyProvider;
        _clock = clock ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
        viewport.FrameRendered += OnViewportFrameRendered;
    }

    /// <summary>已应用的晶圆占位状态（诊断/测试）。</summary>
    public bool WaferVisible => _waferAttached;

    /// <summary>已点亮的槽位数（诊断/测试）。</summary>
    public int LitSlotCount => _slots.Count(s => IsLit(s));

    /// <summary>槽位总数（= 几何装配结果）。</summary>
    public int SlotCount => _slots.Count;

    /// <summary>状态实际应用次数（1s 节流后的计数）。</summary>
    public int ApplyCount { get; private set; }

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
            _lastApply = TimeSpan.MinValue; // 换装配后重新应用一次（新几何需要初始灯态）
        }

        if (!IsDirty())
        {
            return;
        }

        var now = _clock();
        if (_lastApply != TimeSpan.MinValue && now - _lastApply < ApplyInterval)
        {
            return;
        }

        Apply(now);
    }

    private bool IsDirty() =>
        _state.WaferOnTrack != _appliedWaferPresent || _state.CassetteCount != _appliedCassetteCount;

    private void Apply(TimeSpan now)
    {
        _appliedWaferPresent = _state.WaferOnTrack;
        _appliedCassetteCount = _state.CassetteCount;
        _lastApply = now;
        ApplyCount++;

        SetWaferVisible(_state.WaferOnTrack);
        SetLitSlots(_state.CassetteCount);
    }

    private void SetWaferVisible(bool visible)
    {
        if (visible == _waferAttached)
        {
            return;
        }

        _waferAttached = visible;
        if (visible)
        {
            if (!_visualRoot.Children.Contains(_waferVisual))
            {
                _visualRoot.Children.Add(_waferVisual);
            }
        }
        else
        {
            _visualRoot.Children.Remove(_waferVisual);
        }
    }

    private void SetLitSlots(int litCount)
    {
        // 槽位自下而上装载：0..litCount-1 点亮
        for (var i = 0; i < _slots.Count; i++)
        {
            var lit = i < litCount;
            var material = lit ? LitMaterial : EmptyMaterial;
            if (!ReferenceEquals(_slots[i].Material, material))
            {
                _slots[i].Material = material;
            }
        }
    }

    private static readonly Material LitMaterial = Freeze(new DiffuseMaterial(
        new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xA6))));

    private static readonly Material EmptyMaterial = Freeze(new DiffuseMaterial(
        new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x46))));

    private static Material Freeze(Material material)
    {
        material.Freeze();
        return material;
    }

    private static bool IsLit(GeometryModel3D model) =>
        ReferenceEquals(model.Material, LitMaterial);

    private void Rebind(SceneAssembly? assembly)
    {
        _boundAssembly = assembly;
        _slots.Clear();
        _slotMesh = null;
        _visualRoot.Children.Clear();
        _waferVisual.Content = null;
        _waferAttached = false;
        _appliedCassetteCount = -1;
        _appliedWaferPresent = false;

        if (assembly is null)
        {
            _viewport.OverlayRoot.Children.Remove(_visualRoot);
            return;
        }

        if (_viewport.OverlayRoot.Children.IndexOf(_visualRoot) < 0)
        {
            _viewport.OverlayRoot.Children.Add(_visualRoot);
        }

        if (assembly.Find(HandlingStateViewModel.TrackPartId) is { } track)
        {
            BuildWaferProxy(track.Geometry.Bounds);
        }

        if (assembly.Find(HandlingStateViewModel.LoadPortPartId) is { } loadPort)
        {
            BuildCassetteSlots(loadPort.Geometry.Bounds);
        }
    }

    /// <summary>晶圆占位：轨道顶面一枚薄圆盘（直径取轨道短边的 55%）。</summary>
    private void BuildWaferProxy(Rect3D bounds)
    {
        var radius = Math.Min(bounds.SizeX, bounds.SizeY) * 0.55;
        var center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ + 0.0015);
        var mesh = BuildDisc(center, radius, DiscSegments);
        mesh.Freeze();

        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x9F, 0xB6, 0xD6)));
        material.Freeze();
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        model.Freeze();

        var group = new Model3DGroup();
        group.Children.Add(model);
        group.Freeze();
        _waferVisual.Content = group;
    }

    /// <summary>Cassette 槽位：上下料口正面竖排 25 条薄片，共享一份冻结几何。</summary>
    private void BuildCassetteSlots(Rect3D bounds)
    {
        var capacity = Math.Clamp(_state.CassetteCapacity, 1, 25);
        var pitch = bounds.SizeZ / capacity;
        _slotMesh = BuildBox(
            new Point3D(bounds.X + bounds.SizeX + 0.004, bounds.Y + bounds.SizeY / 2, bounds.Z + pitch / 2),
            0.008,
            bounds.SizeY * 0.8,
            Math.Max(pitch * 0.55, 0.001));
        _slotMesh.Freeze();

        for (var i = 0; i < capacity; i++)
        {
            // 不冻结：灯态切换要改 Material 引用（冻结后不可变）
            var model = new GeometryModel3D(_slotMesh, EmptyMaterial)
            {
                Transform = new TranslateTransform3D(0, 0, pitch * i),
            };
            _slots.Add(model);
            _visualRoot.Children.Add(new ModelVisual3D { Content = model });
        }
    }

    /// <summary>圆盘网格（三角扇，法线朝上；背面由 BackMaterial 兜住）。</summary>
    private static MeshGeometry3D BuildDisc(Point3D center, double radius, int segments)
    {
        var positions = new Point3DCollection(segments + 1) { center };
        var indices = new Int32Collection();
        for (var i = 0; i < segments; i++)
        {
            var angle = 2 * Math.PI * i / segments;
            positions.Add(new Point3D(
                center.X + radius * Math.Cos(angle),
                center.Y + radius * Math.Sin(angle),
                center.Z));
        }

        for (var i = 0; i < segments; i++)
        {
            indices.Add(0);
            indices.Add(1 + i);
            indices.Add(1 + (i + 1) % segments);
        }

        return new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
    }

    /// <summary>长方体网格（12 三角面）。</summary>
    private static MeshGeometry3D BuildBox(Point3D center, double sizeX, double sizeY, double sizeZ)
    {
        var hx = sizeX / 2;
        var hy = sizeY / 2;
        var hz = sizeZ / 2;
        Point3D P(int sx, int sy, int sz) => new(center.X + sx * hx, center.Y + sy * hy, center.Z + sz * hz);

        var positions = new Point3DCollection
        {
            P(-1, -1, -1), P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1), // 0-3 下
            P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1),     // 4-7 上
        };
        var indices = new Int32Collection
        {
            0, 2, 1, 0, 3, 2, // 下
            4, 5, 6, 4, 6, 7, // 上
            0, 1, 5, 0, 5, 4, // 前
            2, 3, 7, 2, 7, 6, // 后
            1, 2, 6, 1, 6, 5, // 右
            3, 0, 4, 3, 4, 7, // 左
        };

        return new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewport.FrameRendered -= OnViewportFrameRendered;
        _slots.Clear();
        _visualRoot.Children.Clear();
        _waferVisual.Content = null;
        _viewport.OverlayRoot.Children.Remove(_visualRoot);
    }
}
