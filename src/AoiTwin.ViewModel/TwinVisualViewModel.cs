using System.Collections.Concurrent;
using AoiTwin.Core.Bus;

namespace AoiTwin.ViewModel;

/// <summary>
/// 3D 视觉视图模型聚合（TA.2，FR-M2-02）：
/// Hub 四类 Topic → 各部件双缓冲 VM / 上传队列。绝不持有 UI 对象、不关心线程调度——
/// 数据线程只写不可变快照，渲染线程只读，双方零共享可变状态。
///
/// 订阅与线程：Motion 走节流策略（默认 200ms、按键「PartId|AxisName」合并保留最新，
/// 避免逐轴消息被全局合并丢轴）；Status/Optics/Result 直推。
/// 异常边界（NFR-05）：任一消息处理异常被捕获计数并触发 HandlerError，不向发布方传播。
/// </summary>
public sealed class TwinVisualViewModel : IDisposable
{
    public static TimeSpan DefaultMotionWindow { get; } = TimeSpan.FromMilliseconds(200);

    private readonly IRealtimeHub _hub;
    private readonly IMotionPoseSource? _motionSource;
    private readonly ConcurrentDictionary<string, MotionPartViewModel> _motionParts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OpticsPartViewModel> _opticsParts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StatusEvent> _latestStatus = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, double>> _axisPositions = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];
    private long _handlerErrors;
    private bool _disposed;

    /// <summary>轴名 → 兜底平移方向的映射（TA.3 接入 TeachCoordinateMapper 前的恒等换算）。</summary>
    private static readonly IReadOnlyDictionary<string, (double X, double Y, double Z)> AxisDirections =
        new Dictionary<string, (double, double, double)>(StringComparer.OrdinalIgnoreCase)
        {
            ["X"] = (1, 0, 0),
            ["Y"] = (0, 1, 0),
            ["Z"] = (0, 0, 1),
            ["R"] = (1, 0, 0), // 机械手回转轴暂按 X 向平移占位，TA.3 换旋转矩阵
        };

    public TwinVisualViewModel(
        IRealtimeHub hub,
        IMotionPoseSource? motionSource = null,
        Func<ThrottlePolicy?>? motionPolicyProvider = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
        _motionSource = motionSource;

        // Motion：节流（默认 200ms 按键合并）；TA.3 换算源优先，否则恒等平移兜底
        _subscriptions.Add(hub.Subscribe<AxisSnapshot>(
            TwinTopic.Motion,
            Guarded<AxisSnapshot>(TwinTopic.Motion, OnMotion),
            motionPolicyProvider ?? DefaultMotionPolicyProvider));

        _subscriptions.Add(hub.Subscribe<StatusEvent>(TwinTopic.Status, Guarded<StatusEvent>(TwinTopic.Status, OnStatus)));
        _subscriptions.Add(hub.Subscribe<OpticsEvent>(TwinTopic.Optics, Guarded<OpticsEvent>(TwinTopic.Optics, OnOptics)));
        _subscriptions.Add(hub.Subscribe<WaferResult>(TwinTopic.Result, Guarded<WaferResult>(TwinTopic.Result, OnResult)));
    }

    private static readonly Func<ThrottlePolicy?> DefaultMotionPolicyProvider = () => DefaultMotionPolicy;

    /// <summary>默认轴策略：200ms 窗口、按键（PartId|AxisName）保留各自最新。</summary>
    public static ThrottlePolicy DefaultMotionPolicy { get; } = new(DefaultMotionWindow)
    {
        MergeKey = m => m switch
        {
            AxisSnapshot axis => $"{axis.PartId}|{axis.AxisName}",
            { } other => other.PartId,
            _ => string.Empty,
        },
    };

    public IWaferDataViewModel WaferQueue { get; } = new WaferUploadQueue();

    /// <summary>
    /// 缺陷批队列（TA.8）：与 WaferQueue 同源双发（同一 OnResult 内先入纹理队列再入缺陷队列，
    /// 顺序一致），渲染层独立消费——纹理驱动与缺陷驱动各取所需，互不抢占。
    /// </summary>
    public ConcurrentQueue<IReadOnlyList<DefectRecord>> DefectBatches { get; } = new();

    /// <summary>消息处理异常计数（>0 即降态：对应部件可能停留在旧值，渲染不中断）。</summary>
    public long HandlerErrorCount => Interlocked.Read(ref _handlerErrors);

    /// <summary>降级标志：任何处理异常即视为降态，供状态栏提示。</summary>
    public bool IsDegraded => HandlerErrorCount > 0;

    public event Action<TwinTopic, Exception>? HandlerError;

    /// <summary>取运动部件 VM（渲染装配时调用；数据尚未到达也先建好占位）。</summary>
    public IMotionPartViewModel GetMotionPart(string partId) =>
        _motionParts.GetOrAdd(partId, static id => new MotionPartViewModel(id));

    /// <summary>取光学部件 VM。</summary>
    public IOpticsPartViewModel GetOpticsPart(string partId) =>
        _opticsParts.GetOrAdd(partId, static id => new OpticsPartViewModel(id));

    /// <summary>取部件最新状态事件（钻取面板 FR-M2-08 用）；无记录返回 false。</summary>
    public bool TryGetStatus(string partId, out StatusEvent status) => _latestStatus.TryGetValue(partId, out status!);

    /// <summary>
    /// 渲染帧回调（渲染线程，60fps）：把换算源的最新位姿刷进各部件双缓冲。
    /// 无换算源时刷新无操作（兜底恒等换算在 OnMotion 内直接完成）。
    /// </summary>
    public void RefreshMotion(DateTimeOffset now)
    {
        if (_motionSource is null)
        {
            return;
        }

        foreach (var part in _motionParts.Values)
        {
            var pose = _motionSource.GetPose(part.PartId, now);
            if (pose is not null)
            {
                ((MotionPartViewModel)part).Update(pose);
            }
        }
    }

    private void OnMotion(AxisSnapshot snapshot)
    {
        _motionSource?.OnAxis(snapshot);
        if (_motionSource is not null)
        {
            return; // 位姿由换算源产生，经 RefreshMotion 刷入
        }

        // 兜底恒等换算：按部件累积各轴位置，任一轴更新都基于全量轴位重新合成平移，
        // 避免逐轴快照互相覆盖（Z 轴更新不该清掉 X 轴的位移）
        var axes = _axisPositions.GetOrAdd(snapshot.PartId, static _ => new(StringComparer.OrdinalIgnoreCase));
        axes[snapshot.AxisName] = snapshot.Position;

        double x = 0, y = 0, z = 0;
        foreach (var (axisName, position) in axes)
        {
            if (AxisDirections.TryGetValue(axisName, out var dir))
            {
                x += dir.X * position;
                y += dir.Y * position;
                z += dir.Z * position;
            }
        }

        var pose = PoseSnapshot.Translation(x, y, z, snapshot.Timestamp);
        ((MotionPartViewModel)GetMotionPart(snapshot.PartId)).Update(pose);
    }

    private void OnStatus(StatusEvent status) => _latestStatus[status.PartId] = status;

    private void OnOptics(OpticsEvent optics)
    {
        var part = (OpticsPartViewModel)GetOpticsPart(optics.PartId);
        switch (optics.Kind)
        {
            case OpticsKind.LightChannel:
                // 通道号契约：OpticsEvent.Channel 为 1 起始（与 PRD §5.5 配置键一致），
                // 内部快照数组 0 起始——此处做唯一一次换算，驱动器两侧均按各自约定直读
                if (optics.Channel >= 1)
                {
                    part.UpdateChannel(optics.Channel - 1, optics.Value, optics.Timestamp);
                }

                break;
            case OpticsKind.LightBrightness:
                part.UpdateBrightness(optics.Value, optics.Timestamp);
                break;
            // CameraParam / ScanProgress 由 FR-M2-05 视野锥层消费，不进状态双缓冲
        }
    }

    private void OnResult(WaferResult result)
    {
        WaferQueue.Enqueue(result);
        DefectBatches.Enqueue(result.Defects); // 同源双发（见 DefectBatches 注释）
    }

    private Action<T> Guarded<T>(TwinTopic topic, Action<T> inner)
        where T : ITwinMessage
        => message =>
        {
            try
            {
                inner(message);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _handlerErrors);
                HandlerError?.Invoke(topic, ex);
            }
        };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }
}
