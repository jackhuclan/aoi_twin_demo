using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Bus;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;
using AoiTwin.ViewModel;
using HelixToolkit.Wpf;

namespace AoiTwin.Rendering.Dynamics;

/// <summary>
/// 缺陷点云增量驱动（TA.8，FR-M2-07）：
/// - 消费 <see cref="TwinVisualViewModel.DefectBatches"/>，按缺陷等级分池；
///   每池内固定 512 点/块，块满新开一个 <see cref="PointsVisual3D"/>——追加是 O(1)
///   摊销（只 Add 点或新开块），已有块零重建（1 万点追加的判据）；
/// - 等级 → 颜色取 PRD §5.5 defectClassColors（默认三色，未配置等级回落灰）；
/// - <see cref="Highlight"/>：8 顶点线框小方块跟随目标缺陷（2D/3D 联动的 3D 端，TA.14 接线）。
///
/// 坐标契约：DefectRecord.X/Y 为晶圆平面坐标（毫米，晶圆中心为原点），渲染时 ×1e-3 映射到米；
/// 点位于晶圆顶面上方 1mm，视觉点径固定 3mm（SizeUm 只入索引，不驱动渲染尺寸）。
///
/// 索引契约（TA.14）：<see cref="DefectIndexEntry"/> 记录 DefectId → 池内序号/块号/块内偏移/世界坐标。
/// 池内块按追加顺序填满，故「池内序号 / 512」必然等于块号、「池内序号 % 512」即为块内偏移——
/// 索引与几何不重复存储，块内点批量替换后仍然成立。
/// </summary>
public sealed class DefectPointDriver : IDefectPickSource, IDisposable
{

    /// <summary>每块最大点数（判据：块数 = ⌈N/512⌉）。</summary>
    public const int BlockCapacity = 512;

    /// <summary>未配置缺陷等级的降级色。</summary>
    private static readonly Color FallbackColor = Color.FromRgb(0xB0, 0xB0, 0xB0);

    /// <summary>PRD §5.5 默认缺陷配色。</summary>
    private static readonly IReadOnlyDictionary<string, Color> DefaultClassColors =
        new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["Scratch"] = Color.FromRgb(0xFF, 0x6B, 0x81),
            ["Particle"] = Color.FromRgb(0xFF, 0x9F, 0x43),
            ["Residue"] = Color.FromRgb(0xC5, 0x6C, 0xF0),
        };

    private readonly TwinViewport3D _viewport;
    private readonly TwinVisualViewModel _visualViewModel;
    private readonly Func<SceneAssembly?> _assemblyProvider;
    private readonly ModelVisual3D _visualRoot = new();
    private readonly Dictionary<string, DefectPool> _pools = new(StringComparer.Ordinal);
    private readonly Dictionary<long, DefectIndexEntry> _index = new();
    private readonly ModelVisual3D _highlightVisual = new();
    private Rect3D _waferBounds;
    private SceneAssembly? _boundAssembly;
    private bool _disposed;

    public DefectPointDriver(TwinViewport3D viewport, TwinVisualViewModel visualViewModel, Func<SceneAssembly?> assemblyProvider)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(visualViewModel);
        ArgumentNullException.ThrowIfNull(assemblyProvider);

        _viewport = viewport;
        _visualViewModel = visualViewModel;
        _assemblyProvider = assemblyProvider;
        viewport.FrameRendered += OnViewportFrameRendered;
    }

    /// <summary>当前点总数（所有池合计，诊断用）。</summary>
    public int PointCount => _pools.Values.Sum(p => p.PointCount);

    /// <summary>已索引缺陷数（Highlight 可寻址范围）。</summary>
    public int IndexedCount => _index.Count;

    /// <summary>当前高亮的 DefectId（0 = 无高亮；用于 3D→2D 反查后的一致性校验）。</summary>
    public long HighlightedDefectId { get; private set; }

    /// <summary>2D/3D 双向联动的默认拾取容差（米）：4mm 线框 + 3mm 点径 + 手抖余量。</summary>
    public const double DefaultPickTolerance = 0.006;

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

        while (_visualViewModel.DefectBatches.TryDequeue(out var batch))
        {
            Append(batch);
        }
    }

    /// <summary>
    /// 高亮缺陷（3D 端面片）：小线框方块移到缺陷点上方；defectId 未知则隐藏。
    /// TA.14 的 2D→3D 高亮即调本方法。
    /// </summary>
    public void Highlight(long defectId)
    {
        if (!_index.TryGetValue(defectId, out var entry))
        {
            return;
        }

        HighlightedDefectId = defectId;
        HighlightAt(entry.World);
    }

    /// <summary>按索引项高亮（已反查到条目时避免二次查表）。</summary>
    public void HighlightAt(Point3D world)
    {
        const double half = 0.004; // 8mm 线框
        _highlightVisual.Children.Clear(); // 只换几何，不动 HighlightedDefectId（由 Highlight 维护）
        _highlightVisual.Children.Add(new LinesVisual3D
        {
            // 立方体 12 条边（点对语义）
            Points = BoxEdges(world.X - half, world.Y - half, world.Z - half, half * 2),
            Color = Colors.White,
            Thickness = 2.0,
        });
    }

    /// <summary>清除高亮。</summary>
    public void ClearHighlight()
    {
        HighlightedDefectId = 0;
        _highlightVisual.Children.Clear();
    }

    /// <summary>按 DefectId 取索引项（2D→3D 高亮前校验存在性）。</summary>
    public bool TryGetEntry(long defectId, out DefectIndexEntry entry) =>
        _index.TryGetValue(defectId, out entry);

    /// <summary>
    /// 3D→2D 反查（TA.14）：在世界射线附近找最近的一个缺陷点。
    /// 判定 = 点到射线垂距 ≤ <paramref name="maxPerpendicular"/>，多个候选取垂距最小者
    /// （垂距相同取沿视线更近者）。索引为空或全超容差返回 false。
    /// </summary>
    public bool TryPickOnRay(
        Point3D origin,
        Vector3D direction,
        out DefectIndexEntry hit,
        double maxPerpendicular = DefaultPickTolerance)
    {
        hit = default;
        if (_index.Count == 0)
        {
            return false;
        }

        var dir = direction;
        if (dir.LengthSquared < 1e-12)
        {
            return false;
        }

        dir.Normalize();

        var found = false;
        var bestPerp = double.MaxValue;
        var bestAlong = double.MaxValue;

        foreach (var entry in _index.Values)
        {
            var v = entry.World - origin;
            var along = Vector3D.DotProduct(v, dir);
            if (along < 0)
            {
                continue; // 射线背后
            }

            var perp = (v - dir * along).Length;
            if (perp > maxPerpendicular)
            {
                continue;
            }

            if (perp < bestPerp || (Math.Abs(perp - bestPerp) < 1e-9 && along < bestAlong))
            {
                bestPerp = perp;
                bestAlong = along;
                hit = entry;
                found = true;
            }
        }

        return found;
    }

    private void Rebind(SceneAssembly? assembly)
    {
        _boundAssembly = assembly;
        foreach (var pool in _pools.Values)
        {
            pool.Detach(_visualRoot);
        }

        _pools.Clear();
        _index.Clear();
        ClearHighlight();

        if (assembly?.Find(WaferMapDriver.WaferPartId) is not { } wafer)
        {
            _viewport.OverlayRoot.Children.Remove(_visualRoot);
            _viewport.OverlayRoot.Children.Remove(_highlightVisual);
            return;
        }

        _waferBounds = wafer.Geometry.Bounds;
        if (_viewport.OverlayRoot.Children.IndexOf(_visualRoot) < 0)
        {
            _viewport.OverlayRoot.Children.Add(_visualRoot);
            _viewport.OverlayRoot.Children.Add(_highlightVisual);
        }
    }

    /// <summary>一批缺陷追加进池（O(批量)：整块一次性赋 Points，避免逐点变更通知触发重建）。</summary>
    private void Append(IReadOnlyList<DefectRecord> defects)
    {
        foreach (var defect in defects)
        {
            if (!_pools.TryGetValue(defect.DefectClass, out var pool))
            {
                pool = new DefectPool(ResolveColor(defect.DefectClass));
                pool.Attach(_visualRoot);
                _pools[defect.DefectClass] = pool;
            }

            var point = ToWorld(defect, _waferBounds);
            var ordinal = pool.PointCount; // 缓冲前即为池内序号
            _index[defect.DefectId] = new DefectIndexEntry(
                defect.DefectId,
                defect.DefectClass,
                ordinal,
                ordinal / BlockCapacity,
                ordinal % BlockCapacity,
                point);
            pool.Buffer(point);
        }

        foreach (var pool in _pools.Values)
        {
            pool.Flush();
        }
    }

    private static Color ResolveColor(string defectClass) =>
        DefaultClassColors.TryGetValue(defectClass, out var color) ? color : FallbackColor;

    /// <summary>缺陷坐标 → 世界坐标（mm → m，晶圆顶面上方 1mm）。</summary>
    private static Point3D ToWorld(DefectRecord defect, Rect3D wafer) => new(
        wafer.X + wafer.SizeX / 2 + defect.X * 1e-3,
        wafer.Y + wafer.SizeY / 2 + defect.Y * 1e-3,
        wafer.Z + wafer.SizeZ + 0.001);

    /// <summary>立方体 12 条边（点对）。</summary>
    private static Point3DCollection BoxEdges(double x, double y, double z, double size)
    {
        Point3D P(double dx, double dy, double dz) => new(x + dx, y + dy, z + dz);
        var a = P(0, 0, 0);
        var b = P(size, 0, 0);
        var c = P(size, size, 0);
        var d = P(0, size, 0);
        var e = P(0, 0, size);
        var f = P(size, 0, size);
        var g = P(size, size, size);
        var h = P(0, size, size);
        return new Point3DCollection
        {
            a, b, b, c, c, d, d, a, // 底
            e, f, f, g, g, h, h, e, // 顶
            a, e, b, f, c, g, d, h, // 竖
        };
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
        _highlightVisual.Children.Clear();
        _viewport.OverlayRoot.Children.Remove(_visualRoot);
        _viewport.OverlayRoot.Children.Remove(_highlightVisual);
    }

    /// <summary>
    /// 单等级缺陷池：批次缓冲批量落块。块内点一次性赋 <c>Points</c>——
    /// 逐点 Add 会触发 Helix 每次变更都重建块几何，1 万点退化为 O(N²)。
    /// </summary>
    private sealed class DefectPool
    {
        private readonly Color _color;
        private readonly ModelVisual3D _root = new();
        private readonly List<PointsVisual3D> _blocks = [];
        private readonly List<Point3D> _tail = []; // 未满块的缓冲

        public DefectPool(Color color)
        {
            _color = color;
        }

        public int PointCount { get; private set; }

        public void Buffer(Point3D point)
        {
            _tail.Add(point);
            PointCount++;

            if (_tail.Count == BlockCapacity)
            {
                SealTail();
            }
        }

        /// <summary>批次收尾：把尾部缓冲刷进（或更新）当前尾块。</summary>
        public void Flush()
        {
            if (_tail.Count == 0)
            {
                return;
            }

            if (_blocks.Count == 0 || _blocks[^1].Points.Count == BlockCapacity)
            {
                NewBlock();
                return;
            }

            // 更新尾块的 Points（整体替换，O(尾部)；块容量固定 ≤512，摊销 O(1)）
            _blocks[^1].Points = new Point3DCollection(_tail);
        }

        private void SealTail()
        {
            if (_blocks.Count == 0 || _blocks[^1].Points.Count == BlockCapacity)
            {
                NewBlock();
                return;
            }

            _blocks[^1].Points = new Point3DCollection(_tail);
        }

        private void NewBlock()
        {
            var block = new PointsVisual3D
            {
                Color = _color,
                Size = 3, // 视觉点径（屏幕像素），与缺陷物理尺寸解耦
                Points = new Point3DCollection(_tail),
            };
            _blocks.Add(block);
            _root.Children.Add(block);
            _tail.Clear();
        }

        public void Attach(ModelVisual3D parent) => parent.Children.Add(_root);

        public void Detach(ModelVisual3D parent)
        {
            parent.Children.Remove(_root);
            _root.Children.Clear();
            _blocks.Clear();
            _tail.Clear();
            PointCount = 0;
        }
    }
}
