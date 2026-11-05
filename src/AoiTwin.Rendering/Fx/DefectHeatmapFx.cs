using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Rendering.Viewport;

namespace AoiTwin.Rendering.Fx;

/// <summary>热力层：同一密度档的单元合并成一个网格（一个 Draw Call）。</summary>
public sealed record HeatLayer(MeshGeometry3D Geometry, Color Color, int CellCount);

/// <summary>热力网格构建结果。</summary>
public sealed record HeatGrid(
    IReadOnlyList<HeatLayer> Layers,
    int CellCount,
    int NonEmptyCells,
    double MaxDensity,
    Rect3D Bounds);

/// <summary>
/// 缺陷密度热力晕染（TB.14，FR-M3-13）：把缺陷点按网格聚合，密度分档着色后叠在工件表面。
///
/// 为什么分档合并而不是每格一个模型：WPF 3D 一个 GeometryModel3D 就是一次 Draw Call，
/// 32×32 逐格渲染会瞬间加 1000+ Draw Call，把 M2 的帧预算吃光。
/// 按 8 档密度合并后，热力层固定 ≤ 8 次 Draw Call，且空网格完全不进树（按需渲染的延续）。
/// </summary>
public static class DefectHeatmapBuilder
{
    /// <summary>密度分档数（色阶数 = Draw Call 上限）。</summary>
    public const int DefaultBands = 8;

    /// <summary>
    /// 构建热力网格。<paramref name="area"/> 为工件表面包围盒（取 Z 最高面作为贴附面），
    /// 点坐标为场景坐标（米），<paramref name="weight"/> 可为缺陷面积/严重度权重。
    /// </summary>
    public static HeatGrid Build(
        IReadOnlyList<(double X, double Y, double Weight)> points,
        Rect3D area,
        int cells = 32,
        int bands = DefaultBands)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cells);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bands);

        if (points.Count == 0 || area.IsEmpty || area.SizeX <= 0 || area.SizeY <= 0)
        {
            return new HeatGrid([], 0, 0, 0, area);
        }

        var density = new double[cells, cells];
        var cellW = area.SizeX / cells;
        var cellH = area.SizeY / cells;
        double max = 0;
        var nonEmpty = 0;

        foreach (var (x, y, weight) in points)
        {
            var ix = (int)((x - area.X) / cellW);
            var iy = (int)((y - area.Y) / cellH);
            if (ix < 0 || ix >= cells || iy < 0 || iy >= cells)
            {
                continue; // 落在工件外的点不参与热力（例如误检/坐标系不一致）
            }

            var next = density[ix, iy] + Math.Max(weight, 0.0001);
            if (density[ix, iy] <= 0)
            {
                nonEmpty++;
            }

            density[ix, iy] = next;
            max = Math.Max(max, next);
        }

        if (max <= 0)
        {
            return new HeatGrid([], cells * cells, 0, 0, area);
        }

        // 分档：按密度归一化到 [0,bands)，0 档（空）不入层
        var z = area.Z + area.SizeZ + (Math.Max(area.SizeZ, 0.001) * 0.002); // 抬 0.2% 避免 Z-fighting
        var buckets = new List<(Point3D A, Point3D B, Point3D C, Point3D D)>[bands];
        for (var i = 0; i < bands; i++)
        {
            buckets[i] = [];
        }

        for (var ix = 0; ix < cells; ix++)
        {
            for (var iy = 0; iy < cells; iy++)
            {
                var d = density[ix, iy];
                if (d <= 0)
                {
                    continue;
                }

                var band = Math.Clamp((int)Math.Ceiling(d / max * bands) - 1, 0, bands - 1);
                var x0 = area.X + (ix * cellW);
                var y0 = area.Y + (iy * cellH);
                buckets[band].Add((
                    new Point3D(x0, y0, z),
                    new Point3D(x0 + cellW, y0, z),
                    new Point3D(x0 + cellW, y0 + cellH, z),
                    new Point3D(x0, y0 + cellH, z)));
            }
        }

        var layers = new List<HeatLayer>();
        for (var i = 0; i < bands; i++)
        {
            if (buckets[i].Count == 0)
            {
                continue;
            }

            var intensity = (i + 1) / (double)bands; // 0..1
            var color = ColorFromIntensity(intensity);
            layers.Add(new HeatLayer(BuildQuads(buckets[i]), color, buckets[i].Count));
        }

        return new HeatGrid(layers, cells * cells, nonEmpty, max, area);
    }

    /// <summary>密度强度 → 颜色：低密度青蓝，中密度黄，高密度红（与缺陷等级配色不冲突）。</summary>
    public static Color ColorFromIntensity(double intensity)
    {
        var t = Math.Clamp(intensity, 0, 1);
        var alpha = (byte)(60 + (t * 170));
        return t switch
        {
            < 0.33 => Color.FromArgb(alpha, 0x4C, 0xC9, 0xF0),
            < 0.66 => Color.FromArgb(alpha, 0xFF, 0xC5, 0x3D),
            _ => Color.FromArgb(alpha, 0xFF, 0x47, 0x57),
        };
    }

    private static MeshGeometry3D BuildQuads(IReadOnlyList<(Point3D A, Point3D B, Point3D C, Point3D D)> quads)
    {
        var positions = new Point3DCollection(quads.Count * 4);
        var indices = new Int32Collection(quads.Count * 6);

        foreach (var (a, b, c, d) in quads)
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

        var mesh = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        mesh.Freeze();
        return mesh;
    }
}

/// <summary>
/// 缺陷热力层（TB.14）：按部件挂载/摘除热力网格，走 M2 的 <see cref="FxOverlay"/>，
/// 关闭时整层从树中移除（不渲染、不进 Draw Call）。
/// </summary>
public sealed class DefectHeatmapFx : IDisposable
{
    private readonly FxOverlay _overlay;
    private readonly Dictionary<string, HeatGrid> _grids = new(StringComparer.Ordinal);
    private bool _disposed;

    public DefectHeatmapFx(FxOverlay overlay)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
    }

    public bool Enabled { get; private set; } = true;

    public int ActivePartCount => _grids.Count;

    /// <summary>最近一次的构建结果（诊断/测试）。</summary>
    public HeatGrid? LastGrid { get; private set; }

    /// <summary>为部件构建并显示热力层；点数或区域为空时等于隐藏。</summary>
    public HeatGrid Show(
        string partId,
        IReadOnlyList<(double X, double Y, double Weight)> points,
        Rect3D area,
        int cells = 32)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(partId);

        var grid = DefectHeatmapBuilder.Build(points, area, cells);
        LastGrid = grid;

        if (!Enabled || grid.Layers.Count == 0)
        {
            Hide(partId);
            return grid;
        }

        var group = new Model3DGroup();
        foreach (var layer in grid.Layers)
        {
            var material = new DiffuseMaterial(new SolidColorBrush(layer.Color));
            material.Freeze();
            group.Children.Add(new GeometryModel3D(layer.Geometry, material) { BackMaterial = material });
        }

        group.Freeze();
        _overlay.Attach(FxKind.DefectHeatmap, partId, new ModelVisual3D { Content = group });
        _grids[partId] = grid;
        return grid;
    }

    public void Hide(string partId)
    {
        _overlay.Detach(FxKind.DefectHeatmap, partId);
        _grids.Remove(partId);
    }

    /// <summary>总开关：关闭后立即摘除全部热力层，再开启需重新 Show。</summary>
    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (!enabled)
        {
            Clear();
        }
    }

    public void Clear()
    {
        foreach (var partId in _grids.Keys.ToArray())
        {
            Hide(partId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }
}
