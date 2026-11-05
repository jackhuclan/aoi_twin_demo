using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Catalog;
using AoiTwin.Rendering.Fx;
using AoiTwin.Rendering.Lod;
using AoiTwin.Rendering.Overlay;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Upload;
using Xunit;

namespace AoiTwin.Analytics.Tests;

/// <summary>
/// S3 渲染终调验收（TB.11–TB.15）：静态合批 A/B、上传预算、热力网格、世界→屏幕投影、LOD0 选择器。
/// 全部走纯几何/纯数学路径，不需要开窗或 GPU。
/// </summary>
public sealed class RenderingIncrementTests
{
    [Fact]
    public void Static_batcher_reduces_draw_calls_and_preserves_triangles()
    {
        var scene = BuildScene(
            ("STAGE", Colors.OrangeRed, "Z"),
            ("WAFER_CURRENT", Colors.OrangeRed, null),
            ("ROBOT", Colors.SteelBlue, "R"),
            ("FRAME", Colors.Gray, null));

        var batched = StaticBatcher.Build(scene);

        Assert.Equal(4, batched.DrawCallsBefore);
        Assert.Equal(3, batched.DrawCallsAfter);
        Assert.True(batched.DrawCallReduction > 0);
        Assert.True(batched.TrianglesPreserved);
        Assert.Equal(4, batched.TrianglesAfter);
    }

    [Fact]
    public void LOD_selector_offers_L0_only_when_asset_is_available()
    {
        var twoTier = new LodLevelSelector();
        Assert.False(twoTier.SupportsClose);
        Assert.False(twoTier.Evaluate(1.0)); // 近距离但无 L0 资产：仍 L1
        Assert.Equal(LodLevel.L1, twoTier.Current);

        var threeTier = new LodLevelSelector(supportsClose: true);
        Assert.True(threeTier.Evaluate(1.0));
        Assert.Equal(LodLevel.L0, threeTier.Current);
        Assert.False(threeTier.Evaluate(1.7)); // L0 回滞带内保持
        Assert.Equal(LodLevel.L0, threeTier.Current);
        Assert.True(threeTier.Evaluate(2.5)); // 退出特写回 L1
        Assert.Equal(LodLevel.L1, threeTier.Current);
    }

    [Fact]
    public void Upload_gate_respects_frame_budget_and_covers_same_key()
    {
        var gate = new UploadBudgetGate(100);
        gate.Enqueue(new UploadItem("a", UploadPriority.Normal, 60));
        gate.Enqueue(new UploadItem("b", UploadPriority.Normal, 30));
        gate.Enqueue(new UploadItem("c", UploadPriority.Normal, 20));

        var frame = gate.Drain();

        Assert.True(frame.Bytes <= 100);
        Assert.Equal(2, frame.Items.Count);
        Assert.True(frame.BudgetExhausted);
        Assert.Equal(1, frame.Remaining);

        // 同 Key 覆盖：连续换片只保留最新一张
        gate.Enqueue(new UploadItem("map", UploadPriority.Normal, 10));
        gate.Enqueue(new UploadItem("map", UploadPriority.Normal, 12));
        Assert.Equal(2, gate.PendingCount); // 上一帧剩下的 a + 新的 map
    }

    [Fact]
    public void Upload_gate_gives_oversized_item_its_own_frame()
    {
        var gate = new UploadBudgetGate(100);
        gate.Enqueue(new UploadItem("huge", UploadPriority.Critical, 250));
        gate.Enqueue(new UploadItem("small", UploadPriority.Background, 10));

        var frame = gate.Drain();

        Assert.Single(frame.Items);
        Assert.Equal("huge", frame.Items[0].Key);
        Assert.True(frame.Remaining >= 1);
    }

    [Fact]
    public void Defect_heatmap_aggregates_density_into_bounded_layers()
    {
        var area = new Rect3D(0, 0, 0, 10, 10, 1);
        var points = new List<(double X, double Y, double Weight)>();
        for (var i = 0; i < 100; i++)
        {
            points.Add((5, 5, 1));
        }

        for (var i = 0; i < 10; i++)
        {
            points.Add((1, 1, 0.5));
        }

        var grid = DefectHeatmapBuilder.Build(points, area, cells: 10);

        Assert.InRange(grid.Layers.Count, 1, DefectHeatmapBuilder.DefaultBands);
        Assert.Equal(2, grid.NonEmptyCells);
        Assert.True(grid.MaxDensity >= 100);
        Assert.True(grid.Layers.Sum(l => l.CellCount) <= 100);

        var empty = DefectHeatmapBuilder.Build([], area);
        Assert.Empty(empty.Layers);
    }

    [Fact]
    public void World_to_screen_projects_center_above_and_behind_camera()
    {
        var matrix = WorldToScreenProjector.FromCamera(
            new Point3D(0, 0, -10),
            new Vector3D(0, 0, 1),
            new Vector3D(0, 1, 0),
            fovYDegrees: 60,
            aspect: 16.0 / 9.0);

        var center = WorldToScreenProjector.Project(matrix, new Point3D(0, 0, 0), 800, 450);
        Assert.NotNull(center);
        Assert.Equal(400, center!.Value.X, 3);
        Assert.Equal(225, center.Value.Y, 3);

        var above = WorldToScreenProjector.Project(matrix, new Point3D(0, 1, 0), 800, 450);
        Assert.NotNull(above);
        Assert.True(above!.Value.Y < center.Value.Y);

        var behind = WorldToScreenProjector.Project(matrix, new Point3D(0, 0, -20), 800, 450);
        Assert.Null(behind);
    }

    [Fact]
    public void Alert_state_fx_maps_severity_to_state_colours()
    {
        Assert.Equal(AlertStateFx.WarningColor, AlertStateFx.ColorFor(Core.Bus.Severity.Warning));
        Assert.Equal(AlertStateFx.CriticalColor, AlertStateFx.ColorFor(Core.Bus.Severity.Alarm));
        Assert.Null(AlertStateFx.ColorFor(Core.Bus.Severity.Info));
    }

    private static LoadedScene BuildScene(params (string PartId, Color Color, string? Axis)[] specs)
    {
        var parts = specs.Select(spec =>
        {
            var mesh = new MeshGeometry3D
            {
                Positions = [new Point3D(0, 0, 0), new Point3D(1, 0, 0), new Point3D(0, 1, 0)],
                TriangleIndices = [0, 1, 2],
            };
            mesh.Freeze();

            var entry = new PartCatalogEntry(spec.PartId, spec.PartId, "MOTION", "placeholder", null, spec.Axis is null ? [] : [spec.Axis]);
            return new LoadedPart(spec.PartId, entry, mesh, spec.Color, 1);
        }).ToList();

        return new LoadedScene("catalog.json", ".", LodLevel.L1, parts, TimeSpan.Zero);
    }
}
