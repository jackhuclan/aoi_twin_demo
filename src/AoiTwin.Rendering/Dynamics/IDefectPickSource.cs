using System.Windows.Media.Media3D;

namespace AoiTwin.Rendering.Dynamics;

/// <summary>
/// 缺陷点云的可寻址面（TA.14）：2D/3D 联动只经此契约访问 3D 端，
/// 上层（App 服务）不依赖 <see cref="DefectPointDriver"/> 的渲染细节——
/// 满足计划风险项 R-4「统一 DefectId 寻址服务，禁止各链路私连」。
/// </summary>
public interface IDefectPickSource
{
    /// <summary>按 DefectId 取索引项（2D→3D 高亮前校验存在性）。</summary>
    bool TryGetEntry(long defectId, out DefectIndexEntry entry);

    /// <summary>高亮指定缺陷（未知 DefectId 无操作）。</summary>
    void Highlight(long defectId);

    /// <summary>按世界坐标高亮（3D 点选已反查到条目时避免二次查表）。</summary>
    void HighlightAt(Point3D world);

    /// <summary>清除高亮。</summary>
    void ClearHighlight();

    /// <summary>在世界射线附近找最近缺陷点（3D→2D 反查）。</summary>
    bool TryPickOnRay(
        Point3D origin,
        Vector3D direction,
        out DefectIndexEntry hit,
        double maxPerpendicular = DefectPointDriver.DefaultPickTolerance);
}
