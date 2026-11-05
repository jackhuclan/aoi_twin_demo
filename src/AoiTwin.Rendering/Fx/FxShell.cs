using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AoiTwin.Rendering.Fx;

/// <summary>
/// 特效外壳几何（M3）：单位立方体 + 按包围盒缩放的发光壳。
/// 与 AlarmPulseFx 内部实现同构，但抽出来供预警状态/热力等新增 FX 复用——
/// 不回头改 M2 的报警脉冲（避免已验收的视觉与测试发生漂移）。
/// </summary>
internal static class FxShell
{
    /// <summary>共享单位盒（中心原点、边长 1）。</summary>
    public static MeshGeometry3D UnitCube { get; } = CreateCube();

    /// <summary>按包围盒生成略放大的发光壳；<paramref name="brush"/> 用于后续改透明度（呼吸）。</summary>
    public static ModelVisual3D Create(Rect3D bounds, Color color, double scale, byte initialAlpha, out SolidColorBrush brush)
    {
        brush = new SolidColorBrush(Color.FromArgb(initialAlpha, color.R, color.G, color.B));

        var sx = Math.Max(bounds.SizeX, 1e-4) * scale;
        var sy = Math.Max(bounds.SizeY, 1e-4) * scale;
        var sz = Math.Max(bounds.SizeZ, 1e-4) * scale;

        var matrix = new Matrix3D();
        matrix.Translate(new Vector3D(-0.5, -0.5, -0.5));
        matrix.Scale(new Vector3D(sx, sy, sz));
        matrix.Translate(new Vector3D(bounds.X + (bounds.SizeX / 2), bounds.Y + (bounds.SizeY / 2), bounds.Z + (bounds.SizeZ / 2)));

        var model = new GeometryModel3D
        {
            Geometry = UnitCube,
            Material = new EmissiveMaterial(brush),
            Transform = new MatrixTransform3D(matrix),
        };

        return new ModelVisual3D { Content = model };
    }

    private static MeshGeometry3D CreateCube()
    {
        const double h = 0.5;
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        Span<(Point3D A, Point3D B, Point3D C, Point3D D)> faces =
        [
            (new(-h, -h, -h), new(h, -h, -h), new(h, h, -h), new(-h, h, -h)),
            (new(h, -h, h), new(-h, -h, h), new(-h, h, h), new(h, h, h)),
            (new(-h, -h, h), new(-h, -h, -h), new(-h, h, -h), new(-h, h, h)),
            (new(h, -h, -h), new(h, -h, h), new(h, h, h), new(h, h, -h)),
            (new(-h, h, -h), new(h, h, -h), new(h, h, h), new(-h, h, h)),
            (new(-h, -h, h), new(h, -h, h), new(h, -h, -h), new(-h, -h, -h)),
        ];

        foreach (var (a, b, c, d) in faces)
        {
            var baseIndex = positions.Count;
            positions.Add(a);
            positions.Add(b);
            positions.Add(c);
            positions.Add(d);
            indices.Add(baseIndex);
            indices.Add(baseIndex + 1);
            indices.Add(baseIndex + 2);
            indices.Add(baseIndex);
            indices.Add(baseIndex + 2);
            indices.Add(baseIndex + 3);
        }

        mesh.Positions = positions;
        mesh.TriangleIndices = indices;
        mesh.Freeze();
        return mesh;
    }
}
