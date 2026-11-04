namespace AoiTwin.Core.Bus;

/// <summary>
/// 进程内实时数据总线（PRD-AOI-TWIN-M1 §5.3）。
/// M1 订阅方：录制器、回放引擎（反向 Publish）、监控面板；
/// M2 扩展节流订阅重载（ThrottlePolicy，见 PRD-AOI-TWIN-M2 §5.1）——本契约冻结后只增不改。
/// 线程模型：Publish/Subscribe 均可在数据线程调用，订阅方回调在发布方线程执行。
/// </summary>
public interface IRealtimeHub
{
    /// <summary>发布消息到指定 Topic。发布方线程内同步分发；任一订阅方异常被隔离，不影响其他订阅方。</summary>
    void Publish<T>(TwinTopic topic, T message) where T : ITwinMessage;

    /// <summary>订阅指定 Topic 的指定消息类型；返回 IDisposable，释放即取消订阅。</summary>
    IDisposable Subscribe<T>(TwinTopic topic, Action<T> handler) where T : ITwinMessage;

    /// <summary>
    /// 节流订阅（PRD-AOI-TWIN-M2 §5.1）：policy 为 null 或 MinInterval ≤ 0 时等价直推。
    /// 合并窗口内的多条消息只送出一条（KeepLatest=true 保留最新 / false 逐条排队）；
    /// 窗口期送达发生在节流定时器线程，不再占用发布方线程。
    /// </summary>
    IDisposable Subscribe<T>(TwinTopic topic, Action<T> handler, ThrottlePolicy? policy) where T : ITwinMessage;

    /// <summary>
    /// 活策略节流订阅（NFR-18 热加载）：每条消息到达时重新查询当前策略，
    /// 配置热加载后无需重新订阅即生效。查询返回 null = 直推。
    /// </summary>
    IDisposable Subscribe<T>(TwinTopic topic, Action<T> handler, Func<ThrottlePolicy?> policyProvider)
        where T : ITwinMessage;
}
