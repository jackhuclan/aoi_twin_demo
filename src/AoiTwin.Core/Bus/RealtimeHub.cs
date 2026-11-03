using System.Collections.Immutable;

namespace AoiTwin.Core.Bus;

/// <summary>
/// RealtimeHub 实现（PRD-AOI-TWIN-M1 FR-M1-10 / T2.1）：
/// 进程内发布订阅总线，四类 Topic（Motion/Status/Optics/Result）。
///
/// 线程模型（PRD §5.7）：Publish/Subscribe 均可在数据线程调用；
/// 订阅方回调在发布方线程同步执行——订阅方不得阻塞（M2 起由节流层保证）。
/// 可靠性（NFR-05）：任一订阅方异常被隔离，不影响发布方与其他订阅方。
/// </summary>
public sealed class RealtimeHub : IRealtimeHub
{
    private readonly object _gate = new();
    private ImmutableList<Subscription> _subscriptions = [];

    private sealed record Subscription(TwinTopic Topic, Type MessageType, Action<ITwinMessage> Invoke);

    public void Publish<T>(TwinTopic topic, T message)
        where T : ITwinMessage
    {
        ArgumentNullException.ThrowIfNull(message);

        Subscription[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _subscriptions];
        }

        foreach (var sub in snapshot)
        {
            if (sub.Topic != topic || !sub.MessageType.IsInstanceOfType(message))
            {
                continue;
            }

            try
            {
                sub.Invoke(message);
            }
            catch
            {
                // 订阅方异常必须被隔离（NFR-05）：不向发布方传播，不中断其他订阅方。
            }
        }
    }

    public IDisposable Subscribe<T>(TwinTopic topic, Action<T> handler)
        where T : ITwinMessage
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(topic, typeof(T), m => handler((T)m));
        lock (_gate)
        {
            _subscriptions = _subscriptions.Add(subscription);
        }

        return new Unsubscriber(this, subscription);
    }

    private sealed class Unsubscriber(RealtimeHub hub, Subscription subscription) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (hub._gate)
            {
                hub._subscriptions = hub._subscriptions.Remove(subscription);
            }
        }
    }
}
