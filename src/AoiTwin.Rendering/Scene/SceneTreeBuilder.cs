using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AoiTwin.Rendering.Scene;

/// <summary>
/// 单个部件的可视单元：<see cref="Visual"/> 是装配树里的节点（携带运动变换），
/// <see cref="Geometry"/> 是带 PartId 标记的模型组。
/// M2 运动学只需改写 <see cref="Motion"/> 的 OffsetX/Y/Z，不必重建场景。
/// </summary>
public sealed record PartVisual(
    string PartId,
    ModelVisual3D Visual,
    Model3DGroup Geometry,
    TranslateTransform3D Motion);

/// <summary>装配结果（供 UI 状态栏与测试断言）。</summary>
public sealed record SceneAssembly(IReadOnlyList<PartVisual> Parts, int MeshCount)
{
    public PartVisual? Find(string partId)
        => Parts.FirstOrDefault(p => string.Equals(p.PartId, partId, StringComparison.Ordinal));
}

/// <summary>
/// 场景树装配（T1.4）：把 <see cref="LoadedScene"/> 挂成 WPF 可视树，并给每个 Mesh 打 PartId 标记。
///
/// 结构约定（M2 依赖）：SceneRoot → 部件节点（ModelVisual3D，Transform = 运动变换）→ Model3DGroup → GeometryModel3D(PartId)。
/// 各部件的几何已按装配态世界坐标烘焙，故初始变换为零；M2 只在部件节点上叠加运动量。
/// </summary>
public static class SceneTreeBuilder
{
    /// <summary>把场景装配到 <paramref name="root"/> 下（先清空原有内容）。必须在 UI 线程调用。</summary>
    public static SceneAssembly Build(LoadedScene scene, ModelVisual3D root)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(root);

        Clear(root);

        var visuals = new List<PartVisual>(scene.Parts.Count);
        var meshCount = 0;

        foreach (var part in scene.Parts)
        {
            var material = CreateMaterial(part.Color);
            var mesh = new GeometryModel3D(part.Geometry, material)
            {
                BackMaterial = material, // LOD2 简化后可能露出内壁，避免暗面
            };
            PartTag.SetPartId(mesh, part.PartId);

            var group = new Model3DGroup();
            group.Children.Add(mesh);
            meshCount++;

            var motion = new TranslateTransform3D();
            var visual = new ModelVisual3D
            {
                Content = group,
                Transform = motion,
            };

            root.Children.Add(visual);
            visuals.Add(new PartVisual(part.PartId, visual, group, motion));
        }

        return new SceneAssembly(visuals, meshCount);
    }

    /// <summary>清空装配树（卸载场景 / 重新加载前调用）。</summary>
    public static void Clear(ModelVisual3D root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.Children.Clear();
        root.Content = null;
    }

    /// <summary>
    /// M1 统一 Blinn-Phong 级材质（docs/asset-spec.md §2）：漫反射 + 微弱高光，
    /// 让暗色产线背景下金属件与塑料件仍能看出转折面。
    /// </summary>
    private static Material CreateMaterial(Color color)
    {
        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
        group.Children.Add(new SpecularMaterial(
            new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 28));
        group.Freeze();
        return group;
    }
}
