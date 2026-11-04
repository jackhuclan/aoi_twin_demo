using AoiTwin.Core.Bus;

namespace AoiTwin.ViewModel;

/// <summary>
/// 运动位姿换算源（TA.2 定义、TA.3 实现）：轴快照 → 部件位姿。
/// OnAxis 在数据线程（Hub 订阅回调）调用；GetPose 在渲染线程调用——实现方内部
/// 自行做线程安全（建议与 MotionPartViewModel 同构的不可变快照模式）。
/// </summary>
public interface IMotionPoseSource
{
    /// <summary>收到轴快照（数据线程）。实现方按 PartId+Axis 累积并换算位姿。</summary>
    void OnAxis(AxisSnapshot snapshot);

    /// <summary>取部件当前位姿（渲染线程）。无该部件或尚无数据返回 null。</summary>
    PoseSnapshot? GetPose(string partId, DateTimeOffset now);
}
