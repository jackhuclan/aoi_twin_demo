using System.Numerics;
using System.Text;
using System.Text.Json;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace AoiTwin.AssetTools;

/// <summary>
/// 占位资产包生成（T1.4 的「首版 GLB」）。外部美术资产未到位前，用程序化箱体拼出 AOI 整机中模
/// （L1/L2 两档），并同步输出台账 <c>part-catalog.json</c>——让 GLB 加载、LOD 切换、点选链路在
/// 真实资产接入前就能端到端验证，且默认通过 T1.2 资产校验。
///
/// 坐标约定（docs/asset-spec.md §2）：米制、Z 向上、原点 = 安装基准面中心、X 右 Y 前。
/// 每个部件一个 GLB，几何以**装配态世界坐标**烘焙，部件之间不需要变换节点即可直接拼装；
/// L2 只做几何简化（运动部件全部保留），满足 §2 的 LOD2 约束。
/// </summary>
public static class PlaceholderAssets
{
    public const string CatalogFileName = "part-catalog.json";
    public const string ModelDirName = "model";
    public const string AssetVersion = "0.1.0";
    public const string MachineId = "AOI-01";

    /// <summary>箱体（中心 + 尺寸，米）。</summary>
    private readonly record struct Box(Vector3 Center, float SizeX, float SizeY, float SizeZ);

    /// <summary>占位部件定义：台账字段 + L1/L2 两档几何 + 材质基色。</summary>
    private sealed record PartSpec(
        string PartId,
        string Name,
        string Module,
        string Model,
        string[] Axes,
        byte R,
        byte G,
        byte B,
        Box[] Lod1,
        Box[] Lod2)
    {
        /// <summary>GLB 文件名主体（docs/asset-spec.md §2：partId 的 snake_case）。</summary>
        public string Snake => PartId.ToLowerInvariant();
    }

    /// <summary>
    /// 生成整套占位资产包（幂等，重复执行覆盖同名文件）。返回台账路径。
    /// </summary>
    public static string Generate(string assetsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);

        var modelDir = Path.Combine(assetsRoot, ModelDirName);
        Directory.CreateDirectory(modelDir);

        var parts = Specs();
        var catalogParts = new List<CatalogPartJson>(parts.Count);

        foreach (var part in parts)
        {
            var l1 = $"{ModelDirName}/{part.Snake}_l1.glb";
            var l2 = $"{ModelDirName}/{part.Snake}_l2.glb";
            WriteGlb(Path.Combine(assetsRoot, l1), part.PartId, part.Lod1, part);
            WriteGlb(Path.Combine(assetsRoot, l2), part.PartId, part.Lod2, part);

            catalogParts.Add(new CatalogPartJson(
                part.PartId, part.Name, part.Module, part.Model,
                new CatalogLodJson(l1, l2), part.Axes));
        }

        var document = new CatalogJson(AssetVersion, MachineId, catalogParts);
        var catalogPath = Path.Combine(assetsRoot, CatalogFileName);
        File.WriteAllText(
            catalogPath,
            JsonSerializer.Serialize(document, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }),
            new UTF8Encoding(false));

        return catalogPath;
    }

    /// <summary>
    /// 整机部件构成（与真实 AOI 结构对应，PartId 同时是 PLC 侧部件台账与 Raycaster 点选的唯一键）。
    /// </summary>
    private static List<PartSpec> Specs()
    {
        var specs = new List<PartSpec>
        {
            new("BASE_FRAME", "整机机架", "FRAME", "AOI-M1", [], 96, 106, 120,
                Lod1:
                [
                    new(new Vector3(0, 0, 0.25f), 1.60f, 1.20f, 0.50f),
                    new(new Vector3(-0.72f, -0.35f, 0.96f), 0.12f, 0.12f, 0.92f),
                    new(new Vector3(0.72f, -0.35f, 0.96f), 0.12f, 0.12f, 0.92f),
                ],
                Lod2:
                [
                    new(new Vector3(0, 0, 0.25f), 1.60f, 1.20f, 0.50f),
                    new(new Vector3(-0.72f, -0.35f, 0.96f), 0.12f, 0.12f, 0.92f),
                    new(new Vector3(0.72f, -0.35f, 0.96f), 0.12f, 0.12f, 0.92f),
                ]),

            new("GATE_AXIS_X", "龙门 X 轴", "MOTION", "AOI-M1", ["X"], 132, 145, 165,
                Lod1: [new(new Vector3(0, -0.35f, 1.42f), 1.55f, 0.14f, 0.14f)],
                Lod2: [new(new Vector3(0, -0.35f, 1.42f), 1.55f, 0.14f, 0.14f)]),

            new("GATE_AXIS_Y", "横梁 Y 轴", "MOTION", "AOI-M1", ["Y"], 132, 145, 165,
                Lod1: [new(new Vector3(0, -0.15f, 1.29f), 0.13f, 0.95f, 0.12f)],
                Lod2: [new(new Vector3(0, -0.15f, 1.29f), 0.13f, 0.95f, 0.12f)]),

            new("HEAD_AXIS_Z", "Z 轴升降头", "MOTION", "AOI-M1", ["Z"], 150, 162, 182,
                Lod1: [new(new Vector3(0, -0.35f, 1.12f), 0.14f, 0.14f, 0.50f)],
                Lod2: [new(new Vector3(0, -0.35f, 1.12f), 0.14f, 0.14f, 0.50f)]),

            new("CAMERA_UNIT", "检测相机单元", "OPTICS", "AOI-M1", [], 58, 66, 82,
                Lod1: [new(new Vector3(0, -0.35f, 0.80f), 0.18f, 0.18f, 0.16f)],
                Lod2: [new(new Vector3(0, -0.35f, 0.80f), 0.18f, 0.18f, 0.16f)]),

            // 环形光源：L1 用 12 段拼出圆环，L2 简化为 4 段方环（保留类环形轮廓，避免材质/形状跳变）
            new("LIGHT_RING", "环形光源", "OPTICS", "AOI-M1", [], 240, 214, 120,
                Lod1: Ring(center: new Vector3(0, -0.35f, 0.66f), radius: 0.14f, segments: 12, thickness: 0.028f),
                Lod2: Ring(center: new Vector3(0, -0.35f, 0.66f), radius: 0.14f, segments: 4, thickness: 0.030f)),

            new("WAFER_STAGE", "晶圆载台", "STAGE", "AOI-M1", ["THETA"], 108, 118, 132,
                Lod1: [new(new Vector3(0, 0.10f, 0.54f), 0.52f, 0.52f, 0.08f)],
                Lod2: [new(new Vector3(0, 0.10f, 0.54f), 0.52f, 0.52f, 0.08f)]),

            new("LOAD_PORT", "上下料口", "HANDLING", "AOI-M1", [], 86, 96, 112,
                Lod1:
                [
                    new(new Vector3(0.40f, -0.635f, 0.30f), 0.50f, 0.08f, 0.32f),
                    new(new Vector3(0.40f, -0.645f, 0.13f), 0.50f, 0.10f, 0.05f),
                ],
                Lod2: [new(new Vector3(0.40f, -0.635f, 0.30f), 0.50f, 0.08f, 0.36f)]),

            new("SAFETY_DOOR", "前安全门", "FRAME", "AOI-M1", [], 74, 88, 108,
                Lod1:
                [
                    new(new Vector3(-0.42f, -0.625f, 0.25f), 0.62f, 0.03f, 0.50f),
                    new(new Vector3(-0.30f, -0.645f, 0.28f), 0.04f, 0.03f, 0.12f),
                ],
                Lod2: [new(new Vector3(-0.42f, -0.625f, 0.25f), 0.62f, 0.03f, 0.50f)]),

            new("SIGNAL_TOWER", "三色警示灯", "STATUS", "AOI-M1", [], 108, 190, 132,
                Lod1:
                [
                    new(new Vector3(0.72f, 0.45f, 0.52f), 0.09f, 0.09f, 0.04f),
                    new(new Vector3(0.72f, 0.45f, 0.58f), 0.07f, 0.07f, 0.07f),
                    new(new Vector3(0.72f, 0.45f, 0.66f), 0.07f, 0.07f, 0.07f),
                ],
                Lod2: [new(new Vector3(0.72f, 0.45f, 0.60f), 0.07f, 0.07f, 0.16f)]),
        };

        return specs;
    }

    /// <summary>绕 Z 轴均匀分布的小方块，拼出环形光源轮廓。</summary>
    private static Box[] Ring(Vector3 center, float radius, int segments, float thickness)
    {
        var boxes = new Box[segments];
        for (var i = 0; i < segments; i++)
        {
            var angle = 2 * Math.PI * i / segments;
            var x = center.X + (float)(radius * Math.Cos(angle));
            var y = center.Y + (float)(radius * Math.Sin(angle));
            boxes[i] = new Box(new Vector3(x, y, center.Z), thickness, thickness, thickness);
        }

        return boxes;
    }

    private static void WriteGlb(string path, string partId, IReadOnlyList<Box> boxes, PartSpec spec)
    {
        var material = new MaterialBuilder($"{partId}_MAT")
            .WithMetallicRoughnessShader()
            .WithBaseColor(new Vector4(spec.R / 255f, spec.G / 255f, spec.B / 255f, 1f));
        material.DoubleSided = true;

        var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>(partId);
        foreach (var box in boxes)
        {
            AddBox(mesh, material, box);
        }

        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        scene.ToGltf2().SaveGLB(path);
    }

    /// <summary>
    /// 把箱体的 6 个面（每面 2 个三角形、法线朝外）写入网格。
    /// 注意：<c>UsePrimitive</c> 的返回类型不是公开类型，故只能用 var 承载，无法作为参数类型传递。
    /// </summary>
    private static void AddBox(
        MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> mesh, MaterialBuilder material, Box box)
    {
        var primitive = mesh.UsePrimitive(material);

        var hx = box.SizeX / 2;
        var hy = box.SizeY / 2;
        var hz = box.SizeZ / 2;
        var c = box.Center;

        var min = new Vector3(c.X - hx, c.Y - hy, c.Z - hz);
        var max = new Vector3(c.X + hx, c.Y + hy, c.Z + hz);

        // 8 个角点（右手系，Z 向上）
        var p000 = new Vector3(min.X, min.Y, min.Z);
        var p100 = new Vector3(max.X, min.Y, min.Z);
        var p110 = new Vector3(max.X, max.Y, min.Z);
        var p010 = new Vector3(min.X, max.Y, min.Z);
        var p001 = new Vector3(min.X, min.Y, max.Z);
        var p101 = new Vector3(max.X, min.Y, max.Z);
        var p111 = new Vector3(max.X, max.Y, max.Z);
        var p011 = new Vector3(min.X, max.Y, max.Z);

        // 逆时针为正面（glTF 约定）
        Quad(p000, p010, p110, p100, new Vector3(0, 0, -1));
        Quad(p001, p101, p111, p011, new Vector3(0, 0, 1));
        Quad(p000, p100, p101, p001, new Vector3(0, -1, 0));
        Quad(p010, p011, p111, p110, new Vector3(0, 1, 0));
        Quad(p000, p001, p011, p010, new Vector3(-1, 0, 0));
        Quad(p100, p110, p111, p101, new Vector3(1, 0, 0));

        void Quad(Vector3 a, Vector3 b, Vector3 c2, Vector3 d, Vector3 normal)
        {
            var va = Vertex(a, normal);
            var vb = Vertex(b, normal);
            var vc = Vertex(c2, normal);
            var vd = Vertex(d, normal);
            primitive.AddTriangle(va, vb, vc);
            primitive.AddTriangle(va, vc, vd);
        }
    }

    private static VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> Vertex(Vector3 position, Vector3 normal)
        => new(new VertexPositionNormal(position, normal));

    private sealed record CatalogJson(string assetVersion, string machine, IReadOnlyList<CatalogPartJson> parts);

    private sealed record CatalogPartJson(
        string partId, string name, string module, string model, CatalogLodJson lod, IReadOnlyList<string> axes);

    private sealed record CatalogLodJson(string l1, string l2);
}
