using AoiTwin.Core.Bus;

namespace AoiTwin.Core.Replay;

/// <summary>回放进度快照（UI 绑定用，不可变）。</summary>
public sealed record ReplayProgress(
    ReplayState State,
    int FrameIndex,
    int FrameCount,
    TimeSpan Position,
    TimeSpan Total,
    WaferBatch? Batch,
    TwinTopic? LastTopic,
    byte? LastTypeId);

/// <summary>
/// 回放会话（T3.2）：把 <see cref="ReplayEngine"/>（时钟/变速/Seek）与
/// <see cref="WaferBatchIndex"/>（批次定位）组合成一个 UI 友好的门面，
/// 统一输出进度快照。所有成员线程安全，进度回调在回放线程触发，由 UI 层负责封送。
///
/// 分层理由：视图模型只做「命令 → 会话调用」与「进度 → 属性」的机械转换，
/// 变速/跳转/批次定位等行为全部在此层被单元测试覆盖（UI 层不可测）。
/// </summary>
public sealed class ReplaySession : IDisposable
{
    private readonly ReplayEngine _engine;
    private readonly WaferBatchIndex _batches;
    private readonly string _filePath;
    private bool _disposed;

    public ReplaySession(string filePath, IRealtimeHub hub)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _engine = new ReplayEngine(filePath, hub ?? throw new ArgumentNullException(nameof(hub)));
        _batches = WaferBatchIndex.Build(filePath, _engine.Index);
        _engine.FrameReplayed += OnFrameReplayed;
        _engine.Completed += OnCompleted;
    }

    public string FilePath => _filePath;

    public IReadOnlyList<WaferBatch> Batches => _batches.Batches;

    public int FrameCount => _engine.FrameCount;

    public TimeSpan TotalDuration => _engine.TotalDuration;

    public ReplayState State => _engine.State;

    public int CurrentFrameIndex => _engine.CurrentFrameIndex;

    /// <summary>变速因子 0.5×–8×，即时生效。</summary>
    public double Speed
    {
        get => _engine.Speed;
        set => _engine.Speed = value;
    }

    /// <summary>当前帧所属批次（首批次之前为 null）。</summary>
    public WaferBatch? CurrentBatch => _batches.BatchAt(_engine.CurrentFrameIndex);

    /// <summary>每帧回放后触发（回放线程）；批量高帧率场景由 UI 层节流。</summary>
    public event Action<ReplayProgress>? Progress;

    /// <summary>自然播完触发（Pause/Dispose 不触发）。</summary>
    public event Action? Completed;

    public void Play() => _engine.Play();

    public void Pause() => _engine.Pause();

    /// <summary>播放/暂停切换，返回切换后的状态。已播完时从起点重播（UI 上「播放」按钮不应因播完而报错）。</summary>
    public ReplayState TogglePlayPause()
    {
        if (_engine.State == ReplayState.Playing)
        {
            _engine.Pause();
        }
        else if (_engine.State == ReplayState.Completed)
        {
            _engine.SeekToFrame(0);
            _engine.Play();
        }
        else
        {
            _engine.Play();
        }

        RaiseProgress(null, null);
        return _engine.State;
    }

    public void Seek(TimeSpan elapsed)
    {
        _engine.Seek(elapsed);
        RaiseProgress(null, null);
    }

    /// <summary>跳到指定批次首帧：跳转后的第一条消息即该批次的 WaferResult（T3.2 验收点）。</summary>
    public void SeekToBatch(WaferBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        _engine.SeekToFrame(batch.FirstFrameIndex);
        RaiseProgress(null, null);
    }

    /// <summary>跳到相对当前位置的第 delta 个批次（-1 上一批 / +1 下一批），越界不动。</summary>
    public WaferBatch? SeekRelativeBatch(int delta)
    {
        var current = CurrentBatch;
        var ordinal = (current?.Ordinal ?? 0) + delta;
        var target = _batches.ByOrdinal(ordinal);
        if (target is not null)
        {
            SeekToBatch(target);
        }

        return target;
    }

    public ReplayProgress Snapshot(TwinTopic? lastTopic = null, byte? lastTypeId = null) => new(
        _engine.State,
        _engine.CurrentFrameIndex,
        _engine.FrameCount,
        _engine.CurrentElapsed,
        _engine.TotalDuration,
        _batches.BatchAt(_engine.CurrentFrameIndex),
        lastTopic,
        lastTypeId);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.FrameReplayed -= OnFrameReplayed;
        _engine.Completed -= OnCompleted;
        _engine.Dispose();
    }

    private void OnFrameReplayed(ReplayFrameEventArgs e) => RaiseProgress(e.Topic, e.TypeId);

    private void OnCompleted() => Completed?.Invoke();

    private void RaiseProgress(TwinTopic? topic, byte? typeId) => Progress?.Invoke(Snapshot(topic, typeId));
}
