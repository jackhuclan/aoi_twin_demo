namespace AoiTwin.Core.Bus;

/// <summary>
/// 节流策略（PRD-AOI-TWIN-M2 FR-M2-01 / §5.1，T1.0 契约的 M2 增补）。
/// 轴数据 200ms 合并（保留最新）、状态/结果事件直推。
/// </summary>
/// <param name="MinInterval">
/// 合并窗口：窗口内的多条消息只送出一条。TimeSpan.Zero = 事件直推不过滤。
/// </param>
/// <param name="KeepLatest">
/// 窗口内保留最新值（轴场景，合并覆盖）；false = 逐条排队，按窗口间隔依次送达（顺序完整）。
/// </param>
public sealed record ThrottlePolicy(TimeSpan MinInterval, bool KeepLatest = true)
{
    /// <summary>事件直推策略（等价于不传 policy）。</summary>
    public static ThrottlePolicy PassThrough { get; } = new(TimeSpan.Zero);
}
