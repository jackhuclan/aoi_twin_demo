using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using SharpGLTF.Schema2;

namespace AoiTwin.Rendering.Scene;

/// <summary>
/// GLB → WPF 几何（T1.4）。用 SharpGLTF 直接解析，不走 HelixToolkit 的导入器——
/// 3.1.2 的 <c>ModelImporter</c> 不支持 glTF，且我们需要完全控制「每个 Mesh 携带 PartId」的装配约定。
///
/// 线程约定（PRD §5.7）：本类所有方法都是纯 CPU + Freezable 构建，**可在后台线程调用**；
/// 产出的 <see cref="MeshGeometry3D"/> 已 Freeze，交给 UI 线程挂载时零成本。
/// </summary>
public static class GlbMeshReader
{
    /// <summary>材质缺失或读取失败时的兜底色（中性钢灰，保证暗场景下可辨识轮廓）。</summary>
    private static readonly Color FallbackColor = Color.FromRgb(0x8A, 0x96, 0xA8);

    public static MeshGeometry3D Read(string glbPath)
        => Read(glbPath, out _);

    /// <summary>
    /// 读取 GLB 并合并为一个 <see cref="MeshGeometry3D"/>（多 primitive / 多节点合并，索引重映射）；
    /// 同时解析首个可用材质基色。文件损坏抛 <see cref="SceneAssetException"/>。
    /// </summary>
    public static MeshGeometry3D Read(string glbPath, out Color color)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(glbPath);
        if (!File.Exists(glbPath))
        {
            throw new SceneAssetException($"GLB 不存在: {glbPath}");
        }

        ModelRoot model;
        try
        {
            model = ModelRoot.Load(glbPath);
        }
        catch (Exception ex)
        {
            throw new SceneAssetException($"GLB 无法加载: {Path.GetFileName(glbPath)}（{ex.Message}）", ex);
        }

        var positions = new Point3DCollection();
        var normals = new Vector3DCollection();
        var indices = new Int32Collection();
        color = FallbackColor;
        var colorResolved = false;

        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is null)
            {
                continue;
            }

            var world = node.WorldMatrix;
            foreach (var primitive in node.Mesh.Primitives)
            {
                var accessor = primitive.GetVertexAccessor("POSITION")
                    ?? throw new SceneAssetException($"GLB 缺少 POSITION 属性: {Path.GetFileName(glbPath)}");
                var localPositions = accessor.AsVector3Array();
                var localNormals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();

                var baseIndex = positions.Count;
                for (var i = 0; i < localPositions.Count; i++)
                {
                    var p = Vector3.Transform(localPositions[i], world);
                    positions.Add(new Point3D(p.X, p.Y, p.Z));

                    if (localNormals is not null && i < localNormals.Count)
                    {
                        // 法线只做旋转/缩放，不平移
                        var n = Vector3.Normalize(Vector3.TransformNormal(localNormals[i], world));
                        normals.Add(new Vector3D(n.X, n.Y, n.Z));
                    }
                    else
                    {
                        normals.Add(new Vector3D(0, 0, 1));
                    }
                }

                // GetTriangleIndices 直接给出「三角形三元组」，非索引几何也已按顺序展开
                var triangles = primitive.GetTriangleIndices();
                foreach (var (a, b, c) in triangles)
                {
                    indices.Add(baseIndex + a);
                    indices.Add(baseIndex + b);
                    indices.Add(baseIndex + c);
                }

                if (!colorResolved)
                {
                    color = ReadBaseColor(primitive.Material) ?? FallbackColor;
                    colorResolved = true;
                }
            }
        }

        if (positions.Count == 0 || indices.Count == 0)
        {
            throw new SceneAssetException($"GLB 不含三角面: {Path.GetFileName(glbPath)}");
        }

        var geometry = new MeshGeometry3D
        {
            Positions = positions,
            Normals = normals,
            TriangleIndices = indices,
        };
        geometry.Freeze();
        return geometry;
    }

    /// <summary>材质基色（PBR BaseColor 因子）；无材质或非 PBR 材质返回 null。</summary>
    private static Color? ReadBaseColor(SharpGLTF.Schema2.Material? material)
    {
        var parameters = material?.FindChannel("BaseColor")?.Parameters;
        if (parameters is null || parameters.Count == 0)
        {
            return null;
        }

        if (parameters[0].Value is not Vector4 rgba)
        {
            return null;
        }

        return Color.FromRgb(ToByte(rgba.X), ToByte(rgba.Y), ToByte(rgba.Z));

        static byte ToByte(float value) => (byte)Math.Clamp((int)Math.Round(value * 255f), 0, 255);
    }
}
