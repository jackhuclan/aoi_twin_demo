using System.Windows.Media.Media3D;

namespace AoiTwin.Rendering.Dynamics;

/// <summary>
/// 缺陷索引项（TA.14 双向联动的寻址单元）：DefectId 是 2D 列表与 3D 点云共用的唯一主键。
/// </summary>
/// <param name="DefectId">缺陷唯一标识。</param>
/// <param name="DefectClass">缺陷等级（决定所属池与配色）。</param>
/// <param name="PoolIndex">该缺陷在其等级池内的追加序号（0 起始）。</param>
/// <param name="BlockIndex">所在块的池内序号（= PoolIndex / BlockCapacity）。</param>
/// <param name="PointInBlock">块内偏移（= PoolIndex % BlockCapacity）。</param>
/// <param name="World">世界坐标（晶圆顶面上方 1mm）。</param>
public readonly record struct DefectIndexEntry(
    long DefectId,
    string DefectClass,
    int PoolIndex,
    int BlockIndex,
    int PointInBlock,
    Point3D World);
