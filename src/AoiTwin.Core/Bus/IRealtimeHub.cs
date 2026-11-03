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
}
