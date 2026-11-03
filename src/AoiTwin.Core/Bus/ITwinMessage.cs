namespace AoiTwin.Core.Bus;

/// <summary>
/// 总线统一消息契约（PRD-AOI-TWIN-M1 §5.1）。
/// 所有消息必须携带 PartId（与部件台账一一对应）与设备侧时间戳；
/// 回放场景下 Timestamp 由回放引擎统一偏移为回放时钟。
/// </summary>
public interface ITwinMessage
{
    /// <summary>部件台账 ID；设备级消息为 "EQUIPMENT"，晶圆结果消息为 "WAFER_CURRENT"。</summary>
    string PartId { get; }

    /// <summary>设备侧时间戳。</summary>
    DateTimeOffset Timestamp { get; }
}
