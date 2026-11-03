using SharpGLTF.Schema2;

namespace AoiTwin.AssetTools.Glb;

/// <summary>GLB 基本信息检查（加载 / 面数统计），基于 SharpGLTF Schema2。</summary>
public static class GlbInspector
{
    /// <summary>统计 GLB 全部 Mesh 的三角面数；无法加载抛出 <see cref="InvalidDataException"/>。</summary>
    public static int CountFaces(string glbPath)
    {
        ModelRoot root;
        try
        {
            root = ModelRoot.Load(glbPath);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"GLB 无法加载: {ex.Message}", ex);
        }

        try
        {
            return root.LogicalMeshes
                .Sum(m => m.Primitives.Sum(p => p.IndexAccessor?.Count ?? 0) / 3);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"GLB 结构异常（访问器缺失）: {ex.Message}", ex);
        }
    }
}
