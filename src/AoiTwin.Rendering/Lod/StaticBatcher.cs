using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Rendering.Scene;

namespace AoiTwin.Rendering.Lod;

/// <summary>合批组：同运动组 + 同颜色的部件合并为一个网格。</summary>
public sealed record BatchedGroup(
    MotionGroup Group,
    Color Color,
    MeshGeometry3D Geometry,
    int TriangleCount,
    int SourceMeshCount,
    IReadOnlyList<string> PartIds);

/// <summary>静态合批结果（含 A/B 口径）。</summary>
public sealed record BatchedScene(
    IReadOnlyList<BatchedGroup> Groups,
    int DrawCallsBefore,
    int TrianglesBefore,
    int TrianglesAfter)
{
    public int DrawCallsAfter => Groups.Count;

    /// <summary>Draw Call 下降比例（NFR-20 判据：LOD2 档较 M2 下降 ≥ 40%）。</summary>
    public double DrawCallReduction => DrawCallsBefore == 0
        ? 0
        : 1.0 - ((double)DrawCallsAfter / DrawCallsBefore);

    /// <summary>三角面数是否守恒（合批不得增减几何）。</summary>
    public bool TrianglesPreserved => TrianglesBefore == TrianglesAfter;
}

/// <summary>
/// 静态合批（TB.12，NFR-20）：把外壳/轨道/环境等「同运动组且同色」的部件合并成一个网格。
///
/// 合并单元是 (运动组, 颜色) 而不是"整个场景"：运动组保证合批后的节点仍能整体随动
/// （与 M2 的 LOD2 代理同一套平移语义），颜色保证材质不丢失——WPF 的 GeometryModel3D
/// 只有一个材质，跨颜色合并会把所有部件涂成同色，这是很多合批实现踩过的坑。
///
/// 接入方式：结果可直接交给 SceneTreeBuilder 的挂载路径（每组一个 GeometryModel3D +
/// 一个按运动组绑定的 Transform）。本类只做几何合并与统计，不碰可视树，便于单测 A/B。
/// </summary>
public static class StaticBatcher
{
    public static BatchedScene Build(LoadedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var groups = new List<BatchedGroup>();
        var before = scene.Parts.Count;

        foreach (var group in scene.Parts
                     .GroupBy(p => (Group: LodMerger.GroupOf(p.Entry), p.Color))
                     .OrderBy(g => (int)g.Key.Group)
                     .ThenBy(g => g.Key.Color.ToString(), StringComparer.Ordinal))
        {
            var parts = group.ToList();
            var merged = Merge(parts.Select(p => p.Geometry).ToList());
            groups.Add(new BatchedGroup(
                group.Key.Group,
                group.Key.Color,
                merged,
                parts.Sum(p => p.TriangleCount),
                parts.Count,
                parts.Select(p => p.PartId).ToList()));
        }

        return new BatchedScene(groups, before, scene.TotalTriangles, groups.Sum(g => g.TriangleCount));
    }

    /// <summary>合并多个网格（位置/法线/纹理坐标/索引），保留 UV 与法线，避免光照与贴图错乱。</summary>
    public static MeshGeometry3D Merge(IReadOnlyList<MeshGeometry3D> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);

        var positions = new Point3DCollection();
        var normals = new Vector3DCollection();
        var uvs = new PointCollection();
        var indices = new Int32Collection();
        var hasNormals = true;
        var hasUvs = true;

        foreach (var mesh in meshes)
        {
            if (mesh is null)
            {
                continue;
            }

            var baseIndex = positions.Count;
            foreach (var position in mesh.Positions)
            {
                positions.Add(position);
            }

            if (hasNormals && mesh.Normals is { Count: > 0 } meshNormals && meshNormals.Count == mesh.Positions.Count)
            {
                foreach (var normal in meshNormals)
                {
                    normals.Add(normal);
                }
            }
            else
            {
                hasNormals = false;
            }

            if (hasUvs && mesh.TextureCoordinates is { Count: > 0 } meshUvs && meshUvs.Count == mesh.Positions.Count)
            {
                foreach (var uv in meshUvs)
                {
                    uvs.Add(uv);
                }
            }
            else
            {
                hasUvs = false;
            }

            foreach (var index in mesh.TriangleIndices)
            {
                indices.Add(baseIndex + index);
            }
        }

        var result = new MeshGeometry3D
        {
            Positions = positions,
            TriangleIndices = indices,
        };

        if (hasNormals && normals.Count == positions.Count)
        {
            result.Normals = normals;
        }

        if (hasUvs && uvs.Count == positions.Count)
        {
            result.TextureCoordinates = uvs;
        }

        result.Freeze();
        return result;
    }
}
