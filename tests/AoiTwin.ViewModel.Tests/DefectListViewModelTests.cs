using AoiTwin.Core.Bus;
using AoiTwin.ViewModel;
using Xunit;

namespace AoiTwin.ViewModel.Tests;

/// <summary>
/// 2D 缺陷列表（TA.14）：整片替换、滑窗定位、选中事件——
/// 3D→2D 反查必须能在任意缺陷上「滚窗并选中」，否则联动后半程断链。
/// </summary>
public sealed class DefectListViewModelTests
{
    [Fact]
    public void Result_replaces_rows_and_exposes_caption()
    {
        var hub = new RealtimeHub();
        using var list = new DefectListViewModel(hub);
        var changed = new List<string?>();
        list.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Publish(hub, "WFR-0001", Row(1, "Scratch"), Row(2, "Particle"), Row(3, "Residue"));

        Assert.Equal(3, list.Rows.Count);
        Assert.Equal(3, list.TotalCount);
        Assert.Equal("WFR-0001", list.WaferId);
        Assert.Contains("WFR-0001", list.Caption);
        Assert.Contains("3", list.Caption);
        Assert.Contains(nameof(DefectListViewModel.Caption), changed);

        // 第二片整片替换：旧行不残留
        Publish(hub, "WFR-0002", Row(9, "Scratch"));
        Assert.Single(list.Rows);
        Assert.Equal(9, list.Rows[0].DefectId);
        Assert.Equal("WFR-0002", list.WaferId);
    }

    [Fact]
    public void Selection_raises_change_once_per_distinct_row()
    {
        var hub = new RealtimeHub();
        using var list = new DefectListViewModel(hub);
        var events = new List<long>();
        list.SelectionChanged += (_, row) => events.Add(row.DefectId);

        Publish(hub, "WFR-0001", Row(7, "Scratch"), Row(8, "Scratch"));

        list.Selected = list.Rows[0];
        Assert.Equal(7, list.Selected!.DefectId);
        Assert.Single(events);

        list.Selected = list.Rows[0]; // 同一行：不重复触发
        Assert.Single(events);

        list.Selected = list.Rows[1];
        Assert.Equal(new List<long> { 7, 8 }, events);
    }

    [Fact]
    public void SelectById_locates_row_inside_current_window()
    {
        var hub = new RealtimeHub();
        using var list = new DefectListViewModel(hub);
        Publish(hub, "WFR-0001", Row(11, "Scratch"), Row(12, "Scratch"), Row(13, "Scratch"));

        Assert.True(list.SelectById(12));
        Assert.Equal(12, list.Selected!.DefectId);
        Assert.Equal("Scratch", list.Selected.DefectClass);
        Assert.True(list.TryGetRow(12, out var row));
        Assert.Equal(12, row!.DefectId);
    }

    [Fact]
    public void SelectById_rolls_window_when_target_outside_capacity()
    {
        var hub = new RealtimeHub();
        using var list = new DefectListViewModel(hub);
        var defects = new List<DefectRecord>(2300);
        for (var i = 1; i <= 2300; i++)
        {
            defects.Add(new DefectRecord(i, i * 0.1, -i * 1e-3, 10, "Scratch", 100));
        }

        hub.Publish(TwinTopic.Result, new WaferResult(
            "WFR-BIG", new WaferMapSnapshot(2, 2, new byte[4]), defects, TimeSpan.FromSeconds(4.2), DateTimeOffset.UtcNow));

        // 列表容量恒定（≤ MaxRows），窗口从 0 开始
        Assert.Equal(DefectListViewModel.MaxRows, list.Rows.Count);
        Assert.Equal(2300, list.TotalCount);
        Assert.Equal(0, list.WindowStart);
        Assert.False(list.TryGetRow(2200, out _));

        // 定位窗口外侧缺陷：滚窗 + 选中，容量不变
        Assert.True(list.SelectById(2200));
        Assert.Equal(2200, list.Selected!.DefectId);
        Assert.True(list.WindowStart > 0);
        Assert.Equal(DefectListViewModel.MaxRows, list.Rows.Count);
        Assert.True(list.TryGetRow(2200, out var row));
        Assert.Equal(2200, row!.DefectId);

        // 窗口滚动后原来的首行已不在列表（不会无限增长）
        Assert.False(list.TryGetRow(1, out _));
    }

    [Fact]
    public void Unknown_ids_are_rejected_without_touching_selection()
    {
        var hub = new RealtimeHub();
        using var list = new DefectListViewModel(hub);
        Publish(hub, "WFR-0001", Row(1, "Scratch"));
        list.Selected = list.Rows[0];

        Assert.False(list.SelectById(424242));
        Assert.False(list.TryGetRow(424242, out _));
        Assert.Equal(1, list.Selected!.DefectId); // 选中不被错误清除

        // 新一片到达：旧选中失效（避免指向已下线的晶圆）
        Publish(hub, "WFR-0002", Row(2, "Scratch"));
        Assert.Null(list.Selected);
        Assert.False(list.TryGetRow(1, out _));
    }

    [Fact]
    public void Empty_result_yields_waiting_caption()
    {
        var hub = new RealtimeHub();
        using var list = new DefectListViewModel(hub);
        Assert.Equal("等待首片结果…", list.Caption);

        Publish(hub, "WFR-EMPTY");
        Assert.Empty(list.Rows);
        Assert.Equal(0, list.TotalCount);
        Assert.Contains("WFR-EMPTY", list.Caption);
    }

    private static DefectRecord Row(long id, string defectClass) =>
        new(id, id * 1.5, -id * 0.5, 12, defectClass, 128);

    private static void Publish(IRealtimeHub hub, string waferId, params DefectRecord[] defects) =>
        hub.Publish(TwinTopic.Result, new WaferResult(
            waferId,
            new WaferMapSnapshot(2, 2, new byte[4]),
            defects,
            TimeSpan.FromSeconds(4.0),
            DateTimeOffset.UtcNow));
}
