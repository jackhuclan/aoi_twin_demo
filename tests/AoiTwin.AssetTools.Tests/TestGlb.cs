using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using System.Numerics;

namespace AoiTwin.AssetTools.Tests;

/// <summary>测试夹具：用 SharpGLTF 现场生成最小 GLB，避免二进制资源入库。</summary>
internal static class TestGlb
{
    public static string WriteTrianglesGlb(string dir, string fileName, int triangleCount)
    {
        var mesh = new MeshBuilder<VertexPosition, VertexEmpty, VertexEmpty>("m");
        var prim = mesh.UsePrimitive(new MaterialBuilder("default"));

        for (var i = 0; i < triangleCount; i++)
        {
            var o = i * 0.01f;
            prim.AddTriangle(
                new VertexPosition(new Vector3(0f + o, 0f, 0f)),
                new VertexPosition(new Vector3(1f + o, 0f, 0f)),
                new VertexPosition(new Vector3(0f, 1f, 0f)));
        }

        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, new NodeBuilder("node_" + fileName));
        var model = scene.ToGltf2();

        var path = Path.Combine(dir, fileName);
        model.SaveGLB(path);
        return path;
    }
}
