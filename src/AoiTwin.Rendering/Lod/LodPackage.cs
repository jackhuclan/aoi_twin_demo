using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Catalog;
using AoiTwin.Rendering.Scene;

namespace AoiTwin.Rendering.Lod;

/// <summary>运动分组：LOD2 远景档的合批单元（一个组 = 一个代理网格 = 一个 Transform 节点）。</summary>
public enum MotionGroup
{
    Static = 0,
    AxisX = 1,
    AxisY = 2,
    AxisZ = 3,
    Theta = 4,

    // M2 追加（只加不改）：机械手与载台各有独立位姿，不能与其他组共用一个 Transform
    Robot = 5,
    Stage = 6,
}

/// <summary>单个合批代理：把同组部件的 L2 几何合并成一个网格，远景一次 Draw 完成。</summary>
public sealed record LodProxyGroup(
    MotionGroup Group,
    IReadOnlyList<string> PartIds,
    MeshGeometry3D Geometry,
    Color Color,
    int TriangleCount,
    int MergedMeshCount)
{
    /// <summary>代理显示名（装配树节点 / 状态栏）。</summary>
    public string DisplayName => Group switch
    {
        MotionGroup.AxisX => "X 轴组件",
        MotionGroup.AxisY => "Y 轴组件",
        MotionGroup.AxisZ => "Z 轴组件",
        MotionGroup.Theta => "旋转台组件",
        MotionGroup.Robot => "机械手",
        MotionGroup.Stage => "载台组件",
        _ => "固定结构",
    };
}

/// <summary>
/// LOD2 合批（T1.6）：把远景档按运动组把各部件的几何合并成少量代理网格。
///
/// 为什么合批而不是逐件渲染：WPF 3D 每个 GeometryModel3D ≈ 一次 Draw Call，
/// 远景下逐件渲染的收益只有三角形数，而合批把 Draw Call 从部件数压到运动组数
/// （占位包 10 → 5，降 50%，满足 NFR「远档 Draw Call 较 LOD1 下降 ≥ 40%」）。
///
/// 点选不受影响：PartPicker 始终以 LOD1 全保真几何做射线求交（见 Interaction/PartPicker），
/// 代理只是渲染形态，所以「全部在册部件命中率 100%（含 LOD2）」仍然成立。
///
/// 分组规则：默认按台账 Axes 推导；随 Z 头升降的光学件（相机/光源）用显式覆盖表归入 Z 组，
/// M2 运动学驱动代理节点与逐件节点使用同一套平移语义。
/// </summary>
public static class LodMerger
{
    /// <summary>部件 → 运动组的显式覆盖（台账 Axes 之外的结构性随动关系）。</summary>
    private static readonly IReadOnlyDictionary<string, MotionGroup> GroupOverrides =
        new Dictionary<string, MotionGroup>(StringComparer.Ordinal)
        {
            // 光学件挂在 Z 行程上，随升降头运动
            ["CAMERA"] = MotionGroup.AxisZ,
            ["CAMERA_LINE_SCAN"] = MotionGroup.AxisZ,
            ["LIGHT_RING"] = MotionGroup.AxisZ,
            ["LIGHT_COAX"] = MotionGroup.AxisZ,
            // 晶圆随载台运动；传输轨道固定在机架上
            ["WAFER_CURRENT"] = MotionGroup.Stage,
            ["STAGE"] = MotionGroup.Stage,
            ["ROBOT"] = MotionGroup.Robot,
            ["PRE_ALIGNER"] = MotionGroup.Theta,
        };

    /// <summary>按运动组把远档场景合并成代理网格列表。</summary>
    public static IReadOnlyList<LodProxyGroup> Merge(LoadedScene farScene)
    {
        ArgumentNullException.ThrowIfNull(farScene);

        var merged = new List<LodProxyGroup>();

        foreach (var group in farScene.Parts.GroupBy(p => GroupOf(p.Entry)).OrderBy(g => (int)g.Key))
        {
            var parts = group.ToList();
            merged.Add(new LodProxyGroup(
                group.Key,
                [.. parts.Select(p => p.PartId)],
                MergeGeometry(parts),
                parts[0].Color,
                parts.Sum(p => p.TriangleCount),
                parts.Count));
        }

        return merged;
    }

    /// <summary>部件的运动组：显式覆盖优先，其余按台账 Axes 推导。</summary>
    public static MotionGroup GroupOf(PartCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (GroupOverrides.TryGetValue(entry.PartId, out var overridden))
        {
            return overridden;
        }

        foreach (var axis in entry.Axes)
        {
            return axis.ToUpperInvariant() switch
            {
                "X" => MotionGroup.AxisX,
                "Y" => MotionGroup.AxisY,
                "Z" => MotionGroup.AxisZ,
                "THETA" => MotionGroup.Theta,
                "R" => MotionGroup.Robot, // 机械手径向轴（无覆盖表时的兜底）
                _ => MotionGroup.Static,
            };
        }

        return MotionGroup.Static;
    }

    /// <summary>把同组部件的世界坐标几何拼成一个网格（索引统一偏移，结果 Freeze）。</summary>
    private static MeshGeometry3D MergeGeometry(IReadOnlyList<LoadedPart> parts)
    {
        var positions = new List<Point3D>();
        var normals = new List<Vector3D>();
        var indices = new List<int>();

        foreach (var part in parts)
        {
            var source = part.Geometry;
            var offset = positions.Count;

            positions.AddRange(source.Positions);

            if (source.Normals is { Count: > 0 })
            {
                normals.AddRange(source.Normals);
            }
            else
            {
                normals.AddRange(Enumerable.Repeat(default(Vector3D), source.Positions.Count));
            }

            foreach (var index in source.TriangleIndices)
            {
                indices.Add(offset + index);
            }
        }

        var merged = new MeshGeometry3D
        {
            Positions = new Point3DCollection(positions),
            Normals = new Vector3DCollection(normals),
            TriangleIndices = new Int32Collection(indices),
        };
        merged.Freeze();
        return merged;
    }
}

/// <summary>LOD 切换阈值（米）：NearDistance 内用 LOD1，超出 FarDistance 用 LOD2，中间是回滞带。</summary>
public sealed record LodThresholds(double NearDistance = 4.4, double Hysteresis = 0.6)
{
    public double FarDistance => NearDistance + Hysteresis;
}

/// <summary>
/// LOD 档位选择器（T1.6）：按相机到场景包围盒的距离切换 LOD1/LOD2。
///
/// 回滞（±Hysteresis）是防闪烁的关键：单阈值会在边界距离上反复切换；
/// 两个阈值之间保持当前档位，操作员缓慢平移时档位稳定。
/// </summary>
public sealed class LodLevelSelector(LodThresholds? thresholds = null)
{
    public LodThresholds Thresholds { get; } = thresholds ?? new LodThresholds();

    public LodLevel Current { get; private set; } = LodLevel.L1;

    /// <summary>评估一次距离；档位变化返回 true 并更新 <see cref="Current"/>。</summary>
    public bool Evaluate(double distanceToScene)
    {
        var previous = Current;

        if (Current == LodLevel.L1 && distanceToScene > Thresholds.FarDistance)
        {
            Current = LodLevel.L2;
        }
        else if (Current == LodLevel.L2 && distanceToScene < Thresholds.NearDistance)
        {
            Current = LodLevel.L1;
        }

        return Current != previous;
    }

    public void Reset(LodLevel level = LodLevel.L1) => Current = level;

    /// <summary>点到包围盒的最近距离（盒内为 0）。米制。</summary>
    public static double DistanceTo(Rect3D bounds, Point3D point)
    {
        if (bounds.IsEmpty)
        {
            return 0;
        }

        var dx = Math.Max(Math.Max(bounds.X - point.X, 0), point.X - (bounds.X + bounds.SizeX));
        var dy = Math.Max(Math.Max(bounds.Y - point.Y, 0), point.Y - (bounds.Y + bounds.SizeY));
        var dz = Math.Max(Math.Max(bounds.Z - point.Z, 0), point.Z - (bounds.Z + bounds.SizeZ));
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}

/// <summary>
/// 两档场景包（T1.6）：LOD1 全保真 + LOD2 合批代理一次加载。
/// 点选始终用 <see cref="PickParts"/>（LOD1 几何），渲染按当前档位二选一。
/// </summary>
public sealed record LodPackage(LoadedScene Near, LoadedScene Far, IReadOnlyList<LodProxyGroup> Proxies)
{
    /// <summary>点选与信息面板使用的全保真部件（LOD1）。</summary>
    public IReadOnlyList<LoadedPart> PickParts => Near.Parts;

    public int MeshCount(LodLevel level)
        => level == LodLevel.L1 ? Near.Parts.Count : Proxies.Count;

    /// <summary>整机包围盒（LOD1 几何并集，场景坐标：米、Z 向上）。</summary>
    public Rect3D SceneBounds { get; } = Union(Near.Parts.Select(p => p.Geometry.Bounds));

    /// <summary>并行加载两档并生成合批代理。</summary>
    public static async Task<LodPackage> LoadAsync(
        string catalogPath,
        string assetsRoot,
        IProgress<AssetLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // LOD1 带进度（部件数与 UI 进度条口径一致），LOD2 随后静默补载
        var near = await GlbAssetLoader
            .LoadAsync(catalogPath, assetsRoot, LodLevel.L1, progress, cancellationToken)
            .ConfigureAwait(false);
        var far = await GlbAssetLoader
            .LoadAsync(catalogPath, assetsRoot, LodLevel.L2, null, cancellationToken)
            .ConfigureAwait(false);
        var proxies = await Task.Run(() => LodMerger.Merge(far), cancellationToken).ConfigureAwait(false);

        return new LodPackage(near, far, proxies);
    }

    private static Rect3D Union(IEnumerable<Rect3D> bounds)
    {
        var result = Rect3D.Empty;
        foreach (var bound in bounds)
        {
            if (bound.IsEmpty)
            {
                continue;
            }

            result = result.IsEmpty ? bound : Rect3D.Union(result, bound);
        }

        return result;
    }
}
