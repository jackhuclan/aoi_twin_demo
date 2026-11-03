using System.Windows;
using System.Windows.Media.Media3D;

namespace AoiTwin.Rendering.Scene;

/// <summary>
/// PartId 附加属性（T1.4/T1.8）：把台账的 PartId 绑到 3D 场景节点上，
/// 使 Raycaster 点选、运动学驱动（M2）、选中描边都能从命中结果直接反查部件台账。
///
/// 约定：**每个 Mesh（<see cref="GeometryModel3D"/>）都必须带 PartId**（PRD §7-4 命中率 100% 的基础），
/// 装配缺失由 UI 冒烟测试与 T1.8 的台账遍历断言共同兜底。
/// </summary>
public static class PartTag
{
    public static readonly DependencyProperty PartIdProperty = DependencyProperty.RegisterAttached(
        "PartId",
        typeof(string),
        typeof(PartTag),
        new PropertyMetadata(null));

    public static void SetPartId(DependencyObject element, string? value)
        => element.SetValue(PartIdProperty, value);

    public static string? GetPartId(DependencyObject element)
        => (string?)element.GetValue(PartIdProperty);

    /// <summary>
    /// 遍历场景树，取出所有带 PartId 的 Mesh（未打标的 Mesh 不入结果，便于检出装配遗漏）。
    /// </summary>
    public static IReadOnlyList<(string PartId, GeometryModel3D Mesh)> CollectTagged(ModelVisual3D root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var found = new List<(string, GeometryModel3D)>();
        var pending = new Stack<Visual3D>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var visual = pending.Pop();

            if (visual is ModelVisual3D { Content: not null } node)
            {
                CollectModels(node.Content, found);
            }

            if (visual is ModelVisual3D withChildren)
            {
                foreach (var child in withChildren.Children)
                {
                    pending.Push(child);
                }
            }
        }

        return found;
    }

    private static void CollectModels(Model3D model, List<(string, GeometryModel3D)> sink)
    {
        switch (model)
        {
            case GeometryModel3D mesh when GetPartId(mesh) is { } id:
                sink.Add((id, mesh));
                break;
            case Model3DGroup group:
                foreach (var child in group.Children)
                {
                    CollectModels(child, sink);
                }

                break;
        }
    }
}
