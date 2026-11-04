using System.Diagnostics;
using System.Windows.Media.Media3D;
using AoiTwin.App.Services;
using AoiTwin.App.ViewModels;
using AoiTwin.Core.Bus;
using AoiTwin.Rendering.Dynamics;
using AoiTwin.ViewModel;
using Xunit;

namespace AoiTwin.App.Tests;

/// <summary>
/// 缺陷 2D/3D 双向联动（TA.14，FR-M2-07）：四条链路——
/// 列表选中 → 3D 高亮（≤100ms）；3D 点选 → 反查 DefectId → 列表定位 → 原图窗口；
/// 未装配 3D 端的降级；释放后无回调（订阅不泄漏）。
/// </summary>
public sealed class DefectLinkTests
{
    [Fact]
    public void List_selection_highlights_3d_within_100ms()
    {
        var (hub, list) = CreateList();
        using var link = new DefectLinkService(list);
        var pick = new FakePickSource();
        pick.Add(11, "Scratch");
        pick.Add(12, "Particle");
        link.Attach(pick);

        Publish(hub, "WFR-0001", 11, 12);

        var stopwatch = Stopwatch.StartNew();
        list.Selected = list.Rows[1];
        stopwatch.Stop();

        // 判据：2D 选中 → 3D 高亮 ≤ 100ms（纯内存寻址，无重建）
        Assert.True(stopwatch.Elapsed <= TimeSpan.FromMilliseconds(100), $"高亮耗时 {stopwatch.Elapsed.TotalMilliseconds}ms");
        Assert.Equal(12, pick.Highlighted);
        Assert.Equal(12, link.LastHighlightedId);
        Assert.True(link.LastHighlightElapsed <= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void Pick_in_3d_locates_row_and_opens_image()
    {
        var (hub, list) = CreateList();
        using var link = new DefectLinkService(list);
        var pick = new FakePickSource();
        pick.Add(11, "Scratch");
        pick.Add(12, "Particle");
        link.Attach(pick);
        Publish(hub, "WFR-0001", 11, 12);

        var world = new Point3D(0.1, 0.25, 0.59);
        pick.NextHit = new DefectIndexEntry(12, "Particle", 1, 0, 1, world);

        DefectRow? opened = null;
        link.ImageRequested += (_, row) => opened = row;

        Assert.True(link.FocusFrom3D(new Point3D(0.1, 0.25, 1.2), new Vector3D(0, 0, -1)));

        // 反查 → 列表定位（选中行与 DefectId 一致）→ 原图窗口
        Assert.Equal(12, link.LastPickEntry!.Value.DefectId);
        Assert.Equal(12, list.Selected!.DefectId);
        Assert.Equal(12, pick.Highlighted);
        Assert.Equal(1, link.ImageOpenCount);
        Assert.Equal(12L, link.LastImageDefectId);
        Assert.NotNull(opened);
        Assert.Equal(12, opened!.DefectId);
        Assert.Equal("Particle", opened.DefectClass);
    }

    [Fact]
    public void Without_3d_end_linkage_degrades_silently()
    {
        var (hub, list) = CreateList();
        using var link = new DefectLinkService(list);
        Publish(hub, "WFR-0001", 21);

        Assert.False(link.IsAttached);
        Assert.False(link.HighlightFrom2D(21));   // 3D 未装配：不抛异常、不误报
        Assert.False(link.FocusFrom3D(new Point3D(0, 0, 1), new Vector3D(0, 0, -1)));

        // 原图窗口不依赖 3D 端：列表有行即可打开
        Assert.True(link.OpenImage(21));
        Assert.Equal(1, link.ImageOpenCount);
        Assert.False(link.OpenImage(9999));       // 行不存在：不打开
    }

    [Fact]
    public void Dispose_detaches_list_subscription()
    {
        var (hub, list) = CreateList();
        var link = new DefectLinkService(list);
        var pick = new FakePickSource();
        pick.Add(31, "Residue");
        link.Attach(pick);
        Publish(hub, "WFR-0001", 31);

        link.Dispose();
        Assert.False(link.IsAttached);

        list.Selected = list.Rows[0];
        Assert.Equal(0, pick.HighlightCalls); // 释放后不再回调
    }

    [Theory]
    [InlineData(1.0, 18.0)]    // 下限截断（3µm 缺陷也看得见）
    [InlineData(10.0, 60.0)]   // 10µm ↔ 60px 基准
    [InlineData(1000.0, 110.0)] // 上限截断
    public void Image_marker_scales_with_defect_size(double sizeUm, double expectedDiameter)
    {
        var row = new DefectRow(7, "Scratch", 1.25, -3.5, sizeUm, 96);
        var image = new DefectImageViewModel(row, "WFR-0001");

        Assert.Equal(expectedDiameter, image.MarkerDiameter, 3);
        Assert.Equal($"({1.25:F2}, {-3.50:F2}) mm", image.PositionText);
        Assert.Equal($"{sizeUm:F1} µm", image.SizeText);
        Assert.Equal("缺陷 #7 · Scratch", image.Title);
    }

    [Fact]
    public void Image_view_model_falls_back_when_wafer_id_missing()
    {
        var row = new DefectRow(8, "Particle", 0, 0, 12, 100);
        Assert.Equal("—", new DefectImageViewModel(row, "").WaferId);
        Assert.Equal("WFR-9", new DefectImageViewModel(row, "WFR-9").WaferId);
    }

    private static (IRealtimeHub Hub, DefectListViewModel List) CreateList()
    {
        var hub = new RealtimeHub();
        var list = new DefectListViewModel(hub);
        return (hub, list);
    }

    private static void Publish(IRealtimeHub hub, string waferId, params long[] defectIds)
    {
        var defects = defectIds
            .Select((id, index) => new DefectRecord(id, index * 2.0, index * -1.0, 10, index == 0 ? "Scratch" : "Particle", 100))
            .ToList();
        hub.Publish(TwinTopic.Result, new WaferResult(
            waferId,
            new WaferMapSnapshot(2, 2, new byte[4]),
            defects,
            TimeSpan.FromSeconds(4.0),
            DateTimeOffset.UtcNow));
    }

    /// <summary>3D 端桩：索引由测试写入，拾取结果由 <see cref="NextHit"/> 决定（无视口依赖）。</summary>
    private sealed class FakePickSource : IDefectPickSource
    {
        private readonly Dictionary<long, DefectIndexEntry> _entries = [];

        public long? Highlighted { get; private set; }

        public int HighlightCalls { get; private set; }

        public DefectIndexEntry? NextHit { get; set; }

        public void Add(long defectId, string defectClass) =>
            _entries[defectId] = new DefectIndexEntry(defectId, defectClass, 0, 0, 0, new Point3D(0, 0, 0.59));

        public bool TryGetEntry(long defectId, out DefectIndexEntry entry) =>
            _entries.TryGetValue(defectId, out entry);

        public void Highlight(long defectId)
        {
            HighlightCalls++;
            if (_entries.ContainsKey(defectId))
            {
                Highlighted = defectId;
            }
        }

        public void HighlightAt(Point3D world)
        {
        }

        public void ClearHighlight() => Highlighted = null;

        public bool TryPickOnRay(
            Point3D origin,
            Vector3D direction,
            out DefectIndexEntry hit,
            double maxPerpendicular = DefectPointDriver.DefaultPickTolerance)
        {
            if (NextHit is { } next)
            {
                hit = next;
                return true;
            }

            hit = default;
            return false;
        }
    }
}
