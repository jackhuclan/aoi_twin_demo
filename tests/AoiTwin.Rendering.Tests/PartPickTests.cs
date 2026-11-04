using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.AssetTools;
using AoiTwin.Rendering.Interaction;
using AoiTwin.Rendering.Lod;
using Xunit;

namespace AoiTwin.Rendering.Tests;

/// <summary>
/// T1.8 验收：点选命中率（PRD §7-4：全部在册部件 100%，含 LOD2 档位）、
/// 拾取响应时间（≤ 50ms）、反向壳描边的结构断言。
/// </summary>
public sealed class PartPickTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-pick-" + Guid.NewGuid().ToString("N")[..8]);

    public PartPickTests() => PlaceholderAssets.Generate(_root);

    private string CatalogPath => Path.Combine(_root, PlaceholderAssets.CatalogFileName);

    private static PartPicker PickerFor(LodPackage package)
        => new(package.PickParts.Select(PickPart.From));

    [Fact]
    public async Task Every_catalogued_part_is_pickable_with_100_percent_hit_rate()
    {
        var package = await LodPackage.LoadAsync(CatalogPath, _root);
        var picker = PickerFor(package);

        Assert.Equal(16, picker.PartCount);

        // 可见性探针（六轴向外入射、首个命中必须是自己）：这是「命中率 100%」的程序化判据。
        // 点选集合固定用 LOD1 几何（PickParts），与渲染档位无关，因此 LOD2 档位下同样成立。
        var pickable = picker.FindPickable();

        Assert.Equal(
            package.PickParts.Select(p => p.PartId).OrderBy(x => x, StringComparer.Ordinal),
            pickable.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Ray_from_default_camera_hits_first_visible_part()
    {
        var package = await LodPackage.LoadAsync(CatalogPath, _root);
        var picker = PickerFor(package);

        // 从默认等轴测相机位置向整机中心打射线：应命中最外侧的部件（机架/龙门之一），
        // 且命中的必须是台账内的 PartId——不允许命中未知几何或空结果
        var origin = new Point3D(3.1, -3.8, 2.5);
        var direction = new Point3D(0, 0, 0.7) - origin;
        var hit = picker.Pick(origin, direction);

        Assert.NotNull(hit);
        Assert.Contains(hit!.PartId, package.PickParts.Select(p => p.PartId));
        Assert.True(hit.Distance > 0);
    }

    [Fact]
    public async Task Picking_a_part_from_its_own_direction_returns_it()
    {
        var package = await LodPackage.LoadAsync(CatalogPath, _root);
        var picker = PickerFor(package);

        // 对每个可点选部件：沿六个轴向外侧入射，至少有一个方向的首个命中是它自己
        // （与 IsPickable 的探针判据一致；固定 -Y 会误判——部件不一定朝 -Y 可见）
        foreach (var part in package.PickParts)
        {
            if (!picker.IsPickable(part.PartId))
            {
                continue;
            }

            var bounds = part.Geometry.Bounds;
            var centroid = new Point3D(
                bounds.X + (bounds.SizeX / 2),
                bounds.Y + (bounds.SizeY / 2),
                bounds.Z + (bounds.SizeZ / 2));
            var radius = Math.Max(Math.Max(bounds.SizeX, bounds.SizeY), bounds.SizeZ) / 2;

            var directions = new[]
            {
                new Vector3D(1, 0, 0), new Vector3D(-1, 0, 0),
                new Vector3D(0, 1, 0), new Vector3D(0, -1, 0),
                new Vector3D(0, 0, 1), new Vector3D(0, 0, -1),
            };

            var found = directions.Any(dir =>
            {
                var origin = centroid + (dir * (radius + 0.25));
                return picker.Pick(origin, -dir) is { } hit && hit.PartId == part.PartId;
            });

            Assert.True(found, $"{part.PartId} 判定可点选，但六个方向都拾取不到它");
        }
    }

    [Fact]
    public async Task Pick_responds_within_50ms_budget()
    {
        var package = await LodPackage.LoadAsync(CatalogPath, _root);
        var picker = PickerFor(package);

        // NFR：点选响应 ≤ 50ms（含最坏情况：未命中需要遍历全部三角形）
        var stopwatch = Stopwatch.StartNew();
        var hit = picker.Pick(new Point3D(3.1, -3.8, 2.5), new Vector3D(-1, 1, -0.5));
        stopwatch.Stop();

        _ = hit;
        Assert.True(stopwatch.ElapsedMilliseconds <= 50, $"点选耗时 {stopwatch.ElapsedMilliseconds}ms 超出 50ms 判据");
    }

    [Fact]
    public void Nearest_hit_wins_when_parts_overlap_along_the_ray()
    {
        // 两个沿 Z 叠放的盒体：射线从上往下，必须命中最上面的一个
        var picker = new PartPicker(
        [
            new PickPart("TOP", Box(0, 0, 1.0), Matrix3D.Identity),
            new PickPart("BOTTOM", Box(0, 0, 0.5), Matrix3D.Identity),
        ]);

        var hit = picker.Pick(new Point3D(0, 0, 3), new Vector3D(0, 0, -1));

        Assert.Equal("TOP", hit!.PartId);
    }

    [Fact]
    public void Degenerate_rays_never_hit()
    {
        var picker = new PartPicker([new PickPart("BOX", Box(0, 0, 0.5), Matrix3D.Identity)]);

        Assert.Null(picker.Pick(new Point3D(0, 0, 3), new Vector3D(0, 0, 0)));           // 零方向
        Assert.Null(picker.Pick(new Point3D(0, 0, 3), new Vector3D(0, 0, 1)));           // 背向
        Assert.Null(picker.Pick(new Point3D(0, 0, 3), new Vector3D(1, 0, 0)));           // 擦边错过
    }

    [Fact]
    public void Motion_transform_is_respected_by_the_picker()
    {
        // M2 语义：部件被运动变换平移后，射线命中也跟随移动
        var offset = new TranslateTransform3D(2, 0, 0).Value;
        var picker = new PartPicker([new PickPart("MOVED", Box(0, 0, 0.5), offset)]);

        Assert.Equal("MOVED", picker.Pick(new Point3D(2, 0, 3), new Vector3D(0, 0, -1))!.PartId);
        Assert.Null(picker.Pick(new Point3D(0, 0, 3), new Vector3D(0, 0, -1)));
    }

    [Fact]
    public void Outline_is_a_backface_only_hull_that_clears()
    {
        var overlay = new ModelVisual3D();
        var fx = new SelectionOutlineFx(overlay);

        Assert.False(fx.IsShowing);

        var box = Box(0, 0, 0.5);
        fx.Show(box);
        Assert.True(fx.IsShowing);

        var model = Assert.IsType<GeometryModel3D>(((ModelVisual3D)overlay.Children[0]).Content);
        Assert.Null(model.Material); // 正面不绘制——反向壳的关键
        Assert.NotNull(model.BackMaterial);

        // 壳比本体大一圈（描边余量），且锚定在包围盒中心
        var transform = Assert.IsType<MatrixTransform3D>(model.Transform);
        var scaled = transform.Matrix.Transform(new Point3D(0.5, 0.5, 1.0));
        Assert.True(scaled.X > 0.5 && scaled.Z > 1.0);

        fx.Show(null);
        Assert.False(fx.IsShowing);
        Assert.Empty(overlay.Children);

        fx.Show(box);
        fx.Clear();
        Assert.False(fx.IsShowing);
        Assert.Empty(overlay.Children);
    }

    /// <summary>单位边长的立方体网格（中心在 (x,y,z)，12 三角形）。</summary>
    private static MeshGeometry3D Box(double x, double y, double z)
    {
        var mesh = new MeshGeometry3D();
        const double h = 0.5;

        Point3D P(double px, double py, double pz) => new(x + px, y + py, z + pz);

        Point3DCollection positions =
        [
            P(-h, -h, -h), P(h, -h, -h), P(h, h, -h), P(-h, h, -h),
            P(-h, -h, h), P(h, -h, h), P(h, h, h), P(-h, h, h),
        ];
        Int32Collection indices =
        [
            0, 2, 1, 0, 3, 2, // 前
            4, 5, 6, 4, 6, 7, // 后
            0, 1, 5, 0, 5, 4, // 下
            3, 6, 2, 3, 7, 6, // 上
            0, 4, 7, 0, 7, 3, // 左
            1, 2, 6, 1, 6, 5, // 右
        ];

        var frozen = new MeshGeometry3D
        {
            Positions = positions,
            TriangleIndices = indices,
        };
        frozen.Freeze();
        return frozen;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
