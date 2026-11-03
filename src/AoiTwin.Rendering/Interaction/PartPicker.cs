using System.Windows.Media.Media3D;
using AoiTwin.Rendering.Scene;

namespace AoiTwin.Rendering.Interaction;

/// <summary>一次命中的结果：命中的 PartId、射线参数距离与交点。</summary>
public sealed record PartPick(string PartId, double Distance, Point3D Point);

/// <summary>参与拾取的部件条目（PartId + 几何 + 世界变换）。</summary>
public sealed record PickPart(string PartId, MeshGeometry3D Geometry, Matrix3D World)
{
    public static PickPart From(LoadedPart part) => new(
        part.PartId,
        part.Geometry,
        Matrix3D.Identity);
}

/// <summary>
/// 部件拾取器（T1.8）：对全部在册部件的 LOD1 几何做射线求交，取最近命中。
///
/// 关键决策：**点选始终以 LOD1 全保真几何求交**，与当前渲染档位无关——
/// LOD2 代理只是渲染形态（且不在拾取集合里），因此「全部在册部件命中率 100%（含 LOD2）」
/// 在任何档位下都成立，且不受运动组合批影响。M2 运动后传入部件节点的 <c>Motion.Value</c> 即可。
/// </summary>
public sealed class PartPicker
{
    /// <summary>可见性探针的六个入射方向（±X/±Y/±Z）。</summary>
    private static readonly Vector3D[] ProbeDirections =
    [
        new(1, 0, 0), new(-1, 0, 0),
        new(0, 1, 0), new(0, -1, 0),
        new(0, 0, 1), new(0, 0, -1),
    ];

    private readonly List<(PickPart Part, Point3D Centroid, double Radius)> _entries;

    public PartPicker(IEnumerable<PickPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        _entries = [];
        foreach (var part in parts)
        {
            var (centroid, radius) = ComputeBounds(part);
            _entries.Add((part, centroid, radius));
        }
    }

    public int PartCount => _entries.Count;

    /// <summary>射线拾取：返回最近命中的部件（无命中返回 null）。</summary>
    public PartPick? Pick(Point3D origin, Vector3D direction)
    {
        PartPick? best = null;

        foreach (var (part, _, _) in _entries)
        {
            if (RayMeshIntersector.Intersect(origin, direction, part.Geometry, part.World, out var distance, out var point)
                && (best is null || distance < best.Distance))
            {
                best = new PartPick(part.PartId, distance, point);
            }
        }

        return best;
    }

    /// <summary>
    /// 部件是否可被点选（可见性探针）：从部件包围球外的六个轴向各打一条指向质心的射线，
    /// 只要有一个方向的首个命中是它自己即判「可点选」。这是「命中率 100%」的程序化判据——
    /// 比单一视点更严格（包围死区的部件会被如实判为不可点选），也排除遮挡误报。
    /// </summary>
    public bool IsPickable(string partId)
    {
        var entry = _entries.FirstOrDefault(e => e.Part.PartId == partId);
        if (entry.Part is null)
        {
            return false;
        }

        foreach (var dir in ProbeDirections)
        {
            var origin = entry.Centroid + (dir * (entry.Radius + 0.25));
            var hit = Pick(origin, -dir);
            if (hit is not null && hit.PartId == partId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>列出可点选的部件（验收报告用）。</summary>
    public IReadOnlyList<string> FindPickable()
        => [.. _entries.Select(e => e.Part.PartId).Where(IsPickable)];

    private static (Point3D Centroid, double Radius) ComputeBounds(PickPart part)
    {
        var bounds = part.Geometry.Bounds;
        var centroid = new Point3D(
            bounds.X + (bounds.SizeX / 2),
            bounds.Y + (bounds.SizeY / 2),
            bounds.Z + (bounds.SizeZ / 2));

        var radius = Math.Max(Math.Max(bounds.SizeX, bounds.SizeY), bounds.SizeZ) / 2;
        if (!part.World.IsIdentity)
        {
            centroid = part.World.Transform(centroid);
        }

        return (centroid, radius);
    }
}
