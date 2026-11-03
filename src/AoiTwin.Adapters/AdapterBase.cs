using AoiTwin.Core.Bus;

namespace AoiTwin.Adapters;

/// <summary>适配器健康状态（采集监控面板 T2.5 展示）。</summary>
public enum AdapterHealth
{
    /// <summary>未启动/已停止。</summary>
    Stopped,

    /// <summary>首次连接中。</summary>
    Connecting,

    /// <summary>已连接，数据接收中。</summary>
    Connected,

    /// <summary>连接断开，按指数退避重连中。</summary>
    Reconnecting,
}

/// <summary>
/// 数据源适配器基类（PRD-AOI-TWIN-M1 §4.2，T2.3）。
/// 职责：连接生命周期、断线指数退避重连（1s→30s 封顶）、健康状态机、订阅方异常隔离。
/// 数据模型：同步拉取（ReceiveCore）——M1 数据率低且可测；回调型（push）重载属 M2 扩展，契约只增不改。
/// 隔离规则：单个适配器的连接失败/接收异常不终止循环（NFR-05 单源故障不影响其他通道）；
///           消息订阅方回调异常被吞掉并继续（对应 RealtimeHub 订阅侧同样隔离）。
/// </summary>
public abstract class AdapterBase : IDisposable
{
    private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 16, 30];

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _started;
    private bool _disposed;

    protected AdapterBase(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }

    public AdapterHealth Health { get; private set; } = AdapterHealth.Stopped;

    /// <summary>健康状态变化事件（状态机全迁移由 T2.3 测试覆盖）。</summary>
    public event EventHandler<AdapterHealth>? HealthChanged;

    /// <summary>解析后的统一消息出口。订阅方异常被隔离，不影响适配器循环。</summary>
    public event Action<TwinTopic, ITwinMessage>? MessageEmitted;

    /// <summary>启动适配器（幂等：重复调用忽略）。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        SetHealth(AdapterHealth.Connecting);
        _loop = Task.Run(() => RunLoop(_cts.Token));
    }

    /// <summary>停止适配器并退出重连循环（幂等）。</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // 循环内异常已在 RunLoop 捕获，此处仅为 Wait 包装
        }

        SetHealth(AdapterHealth.Stopped);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>建立连接；失败抛异常由基类进入退避重连。</summary>
    protected abstract void ConnectCore(CancellationToken cancellationToken);

    /// <summary>
    /// 接收一次数据并自行调用 <see cref="Emit"/> 发布解析后的消息。
    /// 无数据时直接返回；连接故障时抛异常，由基类断线重连。
    /// </summary>
    protected abstract void ReceiveCore(CancellationToken cancellationToken);

    /// <summary>重连等待（虚方法供测试注入，避免真实等待）。</summary>
    protected virtual Task DelayAsync(int seconds, CancellationToken cancellationToken)
        => Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);

    /// <summary>发布解析后的消息（topic 由各适配器按 1:1 映射自行指定）。</summary>
    protected void Emit(TwinTopic topic, ITwinMessage message)
    {
        var handlers = MessageEmitted;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<TwinTopic, ITwinMessage> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(topic, message);
            }
            catch
            {
                // 订阅方异常隔离（NFR-05）：吞掉并继续分发
            }
        }
    }

    private async Task RunLoop(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ConnectCore(ct);
                SetHealth(AdapterHealth.Connected);
                attempt = 0;
                while (!ct.IsCancellationRequested)
                {
                    ReceiveCore(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // 连接失败/接收异常：退避重连，不终止循环（NFR-05）
                SetHealth(AdapterHealth.Reconnecting);
                var seconds = BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
                attempt++;
                try
                {
                    await DelayAsync(seconds, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                SetHealth(AdapterHealth.Connecting);
            }
        }
    }

    private void SetHealth(AdapterHealth health)
    {
        if (Health == health)
        {
            return;
        }

        Health = health;
        HealthChanged?.Invoke(this, health);
    }
}
