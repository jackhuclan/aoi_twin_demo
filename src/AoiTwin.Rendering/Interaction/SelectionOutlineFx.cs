using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AoiTwin.Rendering.Interaction;

/// <summary>
/// 选中描边（T1.8）：对选中部件叠加一圈高亮轮廓。
///
/// WPF 3D 没有 stencil buffer，采用经典的「反向壳（inverted hull）」等效实现：
/// 复用选中部件的几何，整体放大一小圈（包围盒中心为缩放锚点），只保留背面材质——
/// 正面被放大壳遮住的部分实际不绘制，背面又被部件本体挡住，
/// 只有轮廓边缘一圈露出来，视觉上等价于 stencil 轮廓描边。
/// </summary>
public sealed class SelectionOutlineFx
{
    /// <summary>描边颜色（高亮青蓝，产线暗色主题下醒目）。</summary>
    public static readonly Color OutlineColor = Color.FromRgb(0x4F, 0xC3, 0xF7);

    /// <summary>描边宽度（米）：以最短包围盒边为基准的放大余量。</summary>
    private const double OutlineMarginMeters = 0.006;

    private readonly ModelVisual3D _overlayRoot;
    private ModelVisual3D? _current;

    public SelectionOutlineFx(ModelVisual3D overlayRoot)
    {
        ArgumentNullException.ThrowIfNull(overlayRoot);
        _overlayRoot = overlayRoot;
    }

    public bool IsShowing => _current is not null;

    /// <summary>
    /// 在叠加层显示部件描边。<paramref name="world"/> 为部件的世界变换（M2 运动后描边跟随）。
    /// 重复调用 = 换选中对象；传 null 几何则清除。
    /// </summary>
    public void Show(MeshGeometry3D? geometry, Matrix3D world = default)
    {
        Clear();

        if (geometry is null || geometry.Positions.Count == 0)
        {
            return;
        }

        var bounds = geometry.Bounds;
        var center = new Point3D(
            bounds.X + (bounds.SizeX / 2),
            bounds.Y + (bounds.SizeY / 2),
            bounds.Z + (bounds.SizeZ / 2));

        var shortest = Math.Max(Math.Min(Math.Min(bounds.SizeX, bounds.SizeY), bounds.SizeZ), 1e-4);
        var scale = 1.0 + ((2.0 * OutlineMarginMeters) / shortest);

        // 放大 + 世界变换的组合（先缩放后平移，与场景树语义一致）
        var transform = new Matrix3D();
        if (!world.IsIdentity)
        {
            transform = world;
        }

        transform.Scale(new Vector3D(scale, scale, scale));
        transform.OffsetX += (center.X * (1 - scale));
        transform.OffsetY += (center.Y * (1 - scale));
        transform.OffsetZ += (center.Z * (1 - scale));

        var outline = new GeometryModel3D(geometry, null)
        {
            // 只画背面：正面不可见，背面被本体遮挡，仅轮廓边缘露出——经典反向壳描边
            BackMaterial = new EmissiveMaterial(new SolidColorBrush(OutlineColor)),
            Transform = new MatrixTransform3D(transform),
        };
        outline.Freeze();

        _current = new ModelVisual3D { Content = outline };
        _overlayRoot.Children.Add(_current);
    }

    /// <summary>清除描边。</summary>
    public void Clear()
    {
        if (_current is not { } current)
        {
            return;
        }

        _overlayRoot.Children.Remove(current);
        _current = null;
    }
}
