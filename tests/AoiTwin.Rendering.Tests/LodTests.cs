using System.IO;
using System.Windows;
using System.Windows.Media.Media3D;
using AoiTwin.AssetTools;
using AoiTwin.Rendering.Lod;
using AoiTwin.Rendering.Scene;
using Xunit;

namespace AoiTwin.Rendering.Tests;

/// <summary>
/// T1.6 验收：LOD 两档切换——合批代理、Draw Call 下降判据（≥40%）、距离阈值回滞防闪烁。
/// </summary>
public sealed class LodTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-lod-" + Guid.NewGuid().ToString("N")[..8]);

    public LodTests() => PlaceholderAssets.Generate(_root);

    private string CatalogPath => Path.Combine(_root, PlaceholderAssets.CatalogFileName);

    [Fact]
    public async Task Package_loads_both_levels_and_merges_proxies_by_motion_group()
    {
        var package = await LodPackage.LoadAsync(CatalogPath, _root);

        Assert.Equal(10, package.Near.Parts.Count);
        Assert.Equal(10, package.Far.Parts.Count);
        Assert.Same(package.Near.Parts, package.PickParts);

        // 分组：固定结构 / X / Y / Z（含相机与光源随动）/ 旋转台
        Assert.Equal(
            new[] { MotionGroup.Static, MotionGroup.AxisX, MotionGroup.AxisY, MotionGroup.AxisZ, MotionGroup.Theta },
            package.Proxies.Select(p => p.Group));

        var staticProxy = package.Proxies[0];
        Assert.Equal(
            new[] { "BASE_FRAME", "LOAD_PORT", "SAFETY_DOOR", "SIGNAL_TOWER" }.OrderBy(x => x),
            staticProxy.PartIds.OrderBy(x => x));

        var zProxy = package.Proxies[3];
        Assert.Equal(
            new[] { "HEAD_AXIS_Z", "CAMERA_UNIT", "LIGHT_RING" }.OrderBy(x => x),
            zProxy.PartIds.OrderBy(x => x));
    }

    [Fact]
    public async Task Far_level_reduces_draw_calls_by_at_least_40_percent()
    {
        // NFR（PRD FR-M1-05）：远景档 Draw Call 较 LOD1 下降 ≥ 40%
        var package = await LodPackage.LoadAsync(CatalogPath, _root);

        var nearMeshes = package.MeshCount(LodLevel.L1);
        var farMeshes = package.MeshCount(LodLevel.L2);
        var reduction = 1 - (farMeshes / (double)nearMeshes);

        Assert.Equal(10, nearMeshes);
        Assert.Equal(5, farMeshes);
        Assert.True(reduction >= 0.40, $"Draw Call 仅下降 {reduction:P0}（{nearMeshes}→{farMeshes}）");
    }

    [Fact]
    public async Task Proxy_geometry_preserves_world_coordinates_and_freezes()
    {
        var package = await LodPackage.LoadAsync(CatalogPath, _root);

        Assert.All(package.Proxies, proxy =>
        {
            Assert.True(proxy.TriangleCount > 0);
            Assert.Equal(proxy.TriangleCount * 3, proxy.Geometry.TriangleIndices.Count);
            Assert.True(proxy.Geometry.IsFrozen, $"{proxy.DisplayName} 几何未 Freeze");
            Assert.Equal(proxy.PartIds.Count, proxy.MergedMeshCount);
        });

        // 合批后的包围盒应与远档原部件并集一致（世界坐标不被破坏）
        var zProxy = package.Proxies[3];
        var expected = Rect3D.Empty;
        foreach (var partId in zProxy.PartIds)
        {
            expected = Rect3D.Union(expected, package.Far.Find(partId)!.Geometry.Bounds);
        }

        var actual = zProxy.Geometry.Bounds;
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.SizeX, actual.SizeX, 6);
        Assert.Equal(expected.SizeZ, actual.SizeZ, 6);
    }

    [Fact]
    public void Selector_switches_with_hysteresis_and_never_oscillates_in_the_dead_zone()
    {
        var selector = new LodLevelSelector(new LodThresholds(NearDistance: 4.4, Hysteresis: 0.6));

        Assert.Equal(LodLevel.L1, selector.Current);

        // 回滞带内（4.4–5.0）保持 LOD1
        Assert.False(selector.Evaluate(4.5));
        Assert.False(selector.Evaluate(4.9));
        Assert.Equal(LodLevel.L1, selector.Current);

        // 越过远阈值 → LOD2
        Assert.True(selector.Evaluate(5.2));
        Assert.Equal(LodLevel.L2, selector.Current);

        // 回滞带内保持 LOD2（单阈值会在边界反复切换导致闪烁，回滞就是为了消灭它）
        Assert.False(selector.Evaluate(4.8));
        Assert.False(selector.Evaluate(4.5));
        Assert.Equal(LodLevel.L2, selector.Current);

        // 拉回近阈值内 → LOD1
        Assert.True(selector.Evaluate(4.0));
        Assert.Equal(LodLevel.L1, selector.Current);
    }

    [Fact]
    public void Distance_to_bounds_is_zero_inside_and_exact_outside()
    {
        var bounds = new Rect3D(new Point3D(-1, -1, 0), new Size3D(2, 2, 2));

        Assert.Equal(0, LodLevelSelector.DistanceTo(bounds, new Point3D(0, 0, 1)), 9);
        Assert.Equal(0, LodLevelSelector.DistanceTo(bounds, new Point3D(0, 0, 2)), 9); // 贴面视为盒内
        // 各轴向外的欧氏距离
        Assert.Equal(1, LodLevelSelector.DistanceTo(bounds, new Point3D(0, 0, 3)), 9);
        Assert.Equal(Math.Sqrt(1 + 0.25), LodLevelSelector.DistanceTo(bounds, new Point3D(-2, 0, 2.5)), 9);
    }

    [Fact]
    public async Task Scene_tree_builder_mounts_proxies_with_group_motion_transforms()
    {
        var package = await LodPackage.LoadAsync(CatalogPath, _root);
        var root = new ModelVisual3D();

        var assembly = SceneTreeBuilder.BuildProxies(package.Proxies, root);

        Assert.Equal(5, assembly.MeshCount);
        Assert.Equal(5, root.Children.Count);
        Assert.Empty(PartTag.CollectTagged(root)); // 代理不打 PartId：点选走 LOD1

        // 每个代理节点都带平移变换（M2 运动学的驱动点）
        Assert.All(assembly.Parts, part => Assert.IsType<TranslateTransform3D>(part.Motion));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
