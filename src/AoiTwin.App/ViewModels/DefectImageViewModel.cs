using AoiTwin.ViewModel;

namespace AoiTwin.App.ViewModels;

/// <summary>
/// 缺陷原图窗口视图模型（TA.14，FR-M2-07）：把 <see cref="DefectRow"/> 摊平成显示字段。
/// 靶心直径按尺寸等比（10µm ↔ 60px，18~110px 截断）——M2 无相机档案，用尺寸关系代替原图。
/// </summary>
public sealed class DefectImageViewModel
{
    private const double PixelsPer10Um = 60.0;
    private const double MinDiameter = 18.0;
    private const double MaxDiameter = 110.0;

    public DefectImageViewModel(DefectRow row, string waferId)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        WaferId = string.IsNullOrWhiteSpace(waferId) ? "—" : waferId;
    }

    public DefectRow Row { get; }

    public string WaferId { get; }

    public long DefectId => Row.DefectId;

    public string DefectClass => Row.DefectClass;

    public string Title => Row.Title;

    public int GrayLevel => Row.GrayLevel;

    /// <summary>缺陷尺寸（µm）。</summary>
    public string SizeText => $"{Row.SizeUm:F1} µm";

    /// <summary>晶圆平面坐标（mm）。</summary>
    public string PositionText => $"({Row.X:F2}, {Row.Y:F2}) mm";

    /// <summary>靶心直径（像素，随缺陷尺寸等比）。</summary>
    public double MarkerDiameter =>
        Math.Clamp(Row.SizeUm / 10.0 * PixelsPer10Um, MinDiameter, MaxDiameter);
}
