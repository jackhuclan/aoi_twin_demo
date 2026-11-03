using System.Diagnostics;
using System.IO;
using System.Windows.Media.Media3D;
using AoiTwin.AssetTools;
using AoiTwin.Rendering.Scene;
using Xunit;

namespace AoiTwin.Rendering.Tests;

/// <summary>
/// T1.4 验收：GLB 后台预加载 + 场景树静态挂载。
/// 资产用 <see cref="PlaceholderAssets"/> 现场生成到临时目录——同一份生成器也产出仓库内的
/// assets/model 占位包，测试与真实资产走完全相同的加载路径。
/// </summary>
public sealed class SceneLoadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-scene-" + Guid.NewGuid().ToString("N")[..8]);

    public SceneLoadTests() => PlaceholderAssets.Generate(_root);

    private string CatalogPath => Path.Combine(_root, PlaceholderAssets.CatalogFileName);

    [Fact]
    public async Task Loads_every_part_declared_in_the_catalog()
    {
        var scene = await GlbAssetLoader.LoadAsync(CatalogPath, _root);

        Assert.Equal(10, scene.Parts.Count);
        Assert.Equal(
            new[] { "BASE_FRAME", "GATE_AXIS_X", "GATE_AXIS_Y", "HEAD_AXIS_Z", "CAMERA_UNIT",
                "LIGHT_RING", "WAFER_STAGE", "LOAD_PORT", "SAFETY_DOOR", "SIGNAL_TOWER" },
            scene.Parts.Select(p => p.PartId));
        Assert.All(scene.Parts, part =>
        {
            Assert.True(part.TriangleCount > 0, $"{part.PartId} 无三角面");
            Assert.True(part.Geometry.Positions.Count > 0);
            Assert.Equal(part.TriangleCount * 3, part.Geometry.TriangleIndices.Count);
            Assert.True(part.Geometry.IsFrozen, $"{part.PartId} 几何未 Freeze（后台线程产出的前提）");
        });
        Assert.Equal(LodLevel.L1, scene.Lod);
        Assert.True(scene.TotalTriangles >= 10 * 12);
    }

    [Fact]
    public async Task Geometry_carries_baked_world_coordinates()
    {
        // 装配态世界坐标烘焙：各部件包围盒不重叠才是正确拼装（Z 向上，机架在底部）
        var scene = await GlbAssetLoader.LoadAsync(CatalogPath, _root);

        var frame = scene.Find("BASE_FRAME")!;
        Assert.Equal(-0.8, frame.Geometry.Bounds.X, 2);
        Assert.Equal(0, frame.Geometry.Bounds.Z, 2);
        // 机架本体 0.5m + 龙门立柱到 1.42m
        Assert.Equal(1.42, frame.Geometry.Bounds.SizeZ, 2);

        var head = scene.Find("HEAD_AXIS_Z")!;
        Assert.True(head.Geometry.Bounds.Z > frame.Geometry.Bounds.Z,
            $"Z 轴头应在机架上方: head.Z={head.Geometry.Bounds.Z:F2}, frame.Z={frame.Geometry.Bounds.Z:F2}");
    }

    [Fact]
    public async Task Progress_reports_each_part_and_ends_at_full()
    {
        // 用同步 IProgress 避免 Progress<T> 的投递乱序（并行加载下 Completed 计数本身已是原子递增）
        var recorder = new RecordingProgress();

        var scene = await GlbAssetLoader.LoadAsync(CatalogPath, _root, progress: recorder);

        var reports = recorder.Items;
        Assert.Equal(0, reports[0].Completed);
        Assert.Equal(scene.Parts.Count, reports.Max(r => r.Completed));
        Assert.Equal(100, reports.Max(r => r.Percent));
        Assert.All(reports, r => Assert.Equal(scene.Parts.Count, r.Total));

        // 每个部件都恰好回报一次（首个 0 进度之外）
        var partReports = reports.Skip(1).ToList();
        Assert.Equal(scene.Parts.Count, partReports.Count);
        Assert.Equal(
            scene.Parts.Select(p => p.PartId).OrderBy(x => x, StringComparer.Ordinal),
            partReports.Select(r => r.CurrentPartId!).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task L2_mesh_is_lighter_than_L1_and_keeps_all_parts()
    {
        var l1 = await GlbAssetLoader.LoadAsync(CatalogPath, _root, LodLevel.L1);
        var l2 = await GlbAssetLoader.LoadAsync(CatalogPath, _root, LodLevel.L2);

        Assert.Equal(l1.Parts.Count, l2.Parts.Count); // 运动部件在 LOD2 必须保留（asset-spec §2）
        Assert.True(l2.TotalTriangles < l1.TotalTriangles,
            $"L2 应更轻: L1={l1.TotalTriangles}, L2={l2.TotalTriangles}");
    }

    [Fact]
    public async Task Missing_part_file_reports_the_part_id()
    {
        File.Delete(Path.Combine(_root, "model", "light_ring_l1.glb"));

        var ex = await Assert.ThrowsAsync<SceneAssetException>(
            () => GlbAssetLoader.LoadAsync(CatalogPath, _root));

        Assert.Contains("LIGHT_RING", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_catalog_reports_readable_error()
    {
        var ex = await Assert.ThrowsAsync<SceneAssetException>(
            () => GlbAssetLoader.LoadAsync(Path.Combine(_root, "nope.json"), _root));

        Assert.Contains("部件台账不存在", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Corrupt_glb_is_reported_with_file_name()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "model", "camera_unit_l1.glb"), "not a glb");

        var ex = await Assert.ThrowsAsync<SceneAssetException>(
            () => GlbAssetLoader.LoadAsync(CatalogPath, _root));

        Assert.Contains("CAMERA_UNIT", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_of_placeholder_package_is_fast_enough_for_startup()
    {
        // T1.4 验证：加载 ≤ 3s（含台账解析与 10 个 GLB 并行解析）
        var scene = await GlbAssetLoader.LoadAsync(CatalogPath, _root);

        Assert.True(scene.LoadElapsed < TimeSpan.FromSeconds(3),
            $"加载耗时 {scene.LoadElapsed.TotalMilliseconds:F0}ms 超 3s 预算");
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GlbAssetLoader.LoadAsync(CatalogPath, _root, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Scene_tree_tags_every_mesh_with_its_part_id()
    {
        var scene = await GlbAssetLoader.LoadAsync(CatalogPath, _root);

        RunOnStaThread(() =>
        {
            var root = new ModelVisual3D();
            var assembly = SceneTreeBuilder.Build(scene, root);

            Assert.Equal(scene.Parts.Count, assembly.MeshCount);
            Assert.Equal(scene.Parts.Count, root.Children.Count);

            var tagged = PartTag.CollectTagged(root);
            Assert.Equal(scene.Parts.Count, tagged.Count);
            Assert.Equal(
                scene.Parts.Select(p => p.PartId).OrderBy(x => x, StringComparer.Ordinal),
                tagged.Select(t => t.PartId).OrderBy(x => x, StringComparer.Ordinal));

            // M2 依赖：每个部件节点都有独立的平移变换（初始为零）
            Assert.All(assembly.Parts, part =>
            {
                Assert.Equal(0, part.Motion.OffsetX);
                Assert.NotNull(part.Visual.Transform);
            });

            // 重新装配必须清空旧树，避免重复叠加
            SceneTreeBuilder.Build(scene, root);
            Assert.Equal(scene.Parts.Count, root.Children.Count);
        });
    }

    private sealed class RecordingProgress : IProgress<AssetLoadProgress>
    {
        private readonly List<AssetLoadProgress> _items = [];

        public IReadOnlyList<AssetLoadProgress> Items
        {
            get
            {
                lock (_items)
                {
                    return _items.ToList();
                }
            }
        }

        public void Report(AssetLoadProgress value)
        {
            lock (_items)
            {
                _items.Add(value);
            }
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA 线程超时");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA 线程内断言失败: {failure}");
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响结论
        }
    }
}
