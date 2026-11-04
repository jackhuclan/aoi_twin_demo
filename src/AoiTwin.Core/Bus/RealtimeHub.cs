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

    public IDisposable Subscribe<T>(TwinTopic topic, Action<T> handler, ThrottlePolicy? policy)
        where T : ITwinMessage
    {
        if (policy is null)
        {
            return Subscribe(topic, handler);
        }

        return Subscribe(topic, handler, (Func<ThrottlePolicy?>)(() => policy));
    }

    public IDisposable Subscribe<T>(TwinTopic topic, Action<T> handler, Func<ThrottlePolicy?> policyProvider)
        where T : ITwinMessage
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(policyProvider);

        // 静态直推策略（null / Zero）退化为 M1 原生订阅，零额外开销
        var initial = policyProvider();
        if (initial is null || initial.MinInterval <= TimeSpan.Zero)
        {
            return Subscribe(topic, handler);
        }

        var sink = new ThrottledSink<T>(handler, policyProvider);
        var subscription = new Subscription(topic, typeof(T), m => sink.OnNext((T)m));
        lock (_gate)
        {
            _subscriptions = _subscriptions.Add(subscription);
        }

        return new Unsubscriber(this, subscription, sink);
    }

    /// <summary>
    /// 合并窗口节流槽（TA.1）：窗口内 KeepLatest=true 只留最新一条、false 逐条排队；
    /// 窗口到期由一次性 Timer 在线程池线程送达。策略每次到达时重新查询（支持热加载）。
    /// </summary>
    private sealed class ThrottledSink<T> : IDisposable
        where T : ITwinMessage
    {
        private readonly object _gate = new();
        private readonly Action<T> _handler;
        private readonly Func<ThrottlePolicy?> _policyProvider;
        private readonly Queue<T> _backlog = new();
        private readonly Dictionary<string, T> _latestByKey = new(StringComparer.Ordinal);
        private T? _latest;
        private Timer? _timer;
        private bool _disposed;

        public ThrottledSink(Action<T> handler, Func<ThrottlePolicy?> policyProvider)
        {
            _handler = handler;
            _policyProvider = policyProvider;
        }

        public void OnNext(T message)
        {
            var policy = _policyProvider();
            if (policy is null || policy.MinInterval <= TimeSpan.Zero)
            {
                _handler(message);
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (policy.MergeKey is not null)
                {
                    // 按键合并：每键独立保留最新，窗口到期逐键各送一条
                    var key = policy.MergeKey(message) ?? string.Empty;
                    _latestByKey[key] = message;
                }
                else if (policy.KeepLatest)
                {
                    _latest = message;
                }
                else
                {
                    _backlog.Enqueue(message);
                }

                _timer ??= new Timer(Flush, null, policy.MinInterval, Timeout.InfiniteTimeSpan);
            }
        }

        private void Flush(object? state)
        {
            List<T>? batch = null;
            T? deliver;
            bool reschedule;
            TimeSpan interval = Timeout.InfiniteTimeSpan;

            lock (_gate)
            {
                var fired = _timer;
                _timer = null;
                fired?.Dispose();

                if (_latestByKey.Count > 0)
                {
                    batch = [.. _latestByKey.Values];
                    _latestByKey.Clear();
                    deliver = default;
                }
                else
                {
                    deliver = _latest;
                    _latest = default;
                    if (deliver is null && _backlog.Count > 0)
                    {
                        deliver = _backlog.Dequeue();
                    }
                }

                // KeepLatest=false 时若队列仍有积压，按剩余策略间隔继续排空
                reschedule = _backlog.Count > 0;
                if (reschedule)
                {
                    interval = _policyProvider()?.MinInterval ?? Timeout.InfiniteTimeSpan;
                    if (interval <= TimeSpan.Zero)
                    {
                        interval = TimeSpan.FromMilliseconds(1);
                    }
                }
            }

            if (batch is not null)
            {
                foreach (var message in batch)
                {
                    try
                    {
                        _handler(message);
                    }
                    catch
                    {
                        // 与直推路径一致：订阅方异常被隔离（NFR-05）。
                    }
                }
            }
            else if (deliver is not null)
            {
                try
                {
                    _handler(deliver);
                }
                catch
                {
                    // 与直推路径一致：订阅方异常被隔离（NFR-05）。
                }
            }

            if (reschedule)
            {
                lock (_gate)
                {
                    if (!_disposed && _timer is null)
                    {
                        _timer = new Timer(Flush, null, interval, Timeout.InfiniteTimeSpan);
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
                _latest = default;
                _backlog.Clear();
            }
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly RealtimeHub _hub;
        private readonly Subscription _subscription;
        private readonly IDisposable? _sink;
        private int _disposed;

        public Unsubscriber(RealtimeHub hub, Subscription subscription, IDisposable? sink = null)
        {
            _hub = hub;
            _subscription = subscription;
            _sink = sink;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _sink?.Dispose();
            lock (_hub._gate)
            {
                _hub._subscriptions = _hub._subscriptions.Remove(_subscription);
            }
        }
    }
}
