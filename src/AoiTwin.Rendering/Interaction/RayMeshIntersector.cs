using System.Windows.Media.Media3D;

namespace AoiTwin.Rendering.Interaction;

/// <summary>
/// 射线-网格求交（T1.8）：Möller–Trumbore 三角形求交的纯计算实现。
///
/// 为什么不用 WPF 内置的 VisualTreeHelper.HitTest：它要求目标在渲染树上且完成布局，
/// CI 无头环境不可复现；纯数学实现对 LOD2（代理不在渲染树）与程序化命中率断言
/// （PRD §7-4「全部在册部件命中率 100%」）都同样成立，且耗时确定可测（≤50ms 判据）。
/// </summary>
public static class RayMeshIntersector
{
    private const double Epsilon = 1e-10;

    /// <summary>
    /// 求射线与网格的最近交点。<paramref name="direction"/> 会被归一化；
    /// <paramref name="transform"/> 为网格的世界变换（M2 运动后的部件位置）。
    /// </summary>
    public static bool Intersect(
        Point3D origin,
        Vector3D direction,
        MeshGeometry3D mesh,
        Matrix3D transform,
        out double distance,
        out Point3D point)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        distance = double.MaxValue;
        point = default;

        var dir = direction;
        if (dir.LengthSquared < Epsilon || !double.IsFinite(dir.LengthSquared))
        {
            return false;
        }

        dir.Normalize();
        var hit = false;

        var positions = mesh.Positions;
        var indices = mesh.TriangleIndices;

        // 无索引几何按三顶点一三角形处理
        var triangleCount = indices.Count >= 3 ? indices.Count / 3 : positions.Count / 3;

        for (var t = 0; t < triangleCount; t++)
        {
            Point3D a;
            Point3D b;
            Point3D c;

            if (indices.Count >= 3)
            {
                a = positions[indices[(t * 3) + 0]];
                b = positions[indices[(t * 3) + 1]];
                c = positions[indices[(t * 3) + 2]];
            }
            else
            {
                a = positions[(t * 3) + 0];
                b = positions[(t * 3) + 1];
                c = positions[(t * 3) + 2];
            }

            if (!transform.IsIdentity)
            {
                a = transform.Transform(a);
                b = transform.Transform(b);
                c = transform.Transform(c);
            }

            if (IntersectTriangle(origin, dir, a, b, c, out var tHit) && tHit < distance)
            {
                distance = tHit;
                point = origin + (dir * tHit);
                hit = true;
            }
        }

        return hit;
    }

    /// <summary>Möller–Trumbore：返回沿射线的参数 t（> 0 才算命中）。</summary>
    private static bool IntersectTriangle(in Point3D origin, in Vector3D dir, in Point3D v0, in Point3D v1, in Point3D v2, out double t)
    {
        t = 0;

        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var p = Cross(dir, e2);
        var det = Dot(e1, p);

        // 平行或背面剔除关闭（双面材质都要能拾取）
        if (Math.Abs(det) < Epsilon)
        {
            return false;
        }

        var invDet = 1.0 / det;
        var s = origin - v0;
        var u = Dot(s, p) * invDet;
        if (u is < 0 or > 1)
        {
            return false;
        }

        var q = Cross(s, e1);
        var v = Dot(dir, q) * invDet;
        if (v is < 0 or > 1 || (u + v) > 1)
        {
            return false;
        }

        var distance = Dot(e2, q) * invDet;
        if (distance <= Epsilon)
        {
            return false;
        }

        t = distance;
        return true;
    }

    private static Vector3D Cross(in Vector3D a, in Vector3D b)
        => new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

    private static double Dot(in Vector3D a, in Vector3D b)
        => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
}
