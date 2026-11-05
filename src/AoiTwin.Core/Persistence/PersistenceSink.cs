using System.Collections.Concurrent;
using AoiTwin.Core.Analytics;
using AoiTwin.Core.Bus;

namespace AoiTwin.Core.Persistence;

/// <summary>持久化 Sink 参数（TB.0）。</summary>
/// <param name="RootPath">存储根目录。</param>
/// <param name="DeviceId">设备号（报告与导出脱敏用）。</param>
/// <param name="BatchSize">单批条数上限：达到即刷盘（不等定时器）。</param>
/// <param name="FlushInterval">定时刷盘间隔（PRD 要求 ≤ 5s）。</param>
/// <param name="RetryWindow">失败批次的重试窗口（PRD 要求 ≤ 30min），超窗丢弃并计数。</param>
/// <param name="MaxBufferRecords">内存缓冲上限；超限丢弃最旧并计数（保护实时链路的最后一道闸）。</param>
public sealed record PersistenceOptions(
    string RootPath,
    string DeviceId = "AOI-01",
    int BatchSize = 5000,
    TimeSpan? FlushInterval = null,
    TimeSpan? RetryWindow = null,
    int MaxBufferRecords = 500_000)
{
    public TimeSpan EffectiveFlushInterval => FlushInterval ?? TimeSpan.FromSeconds(5);

    public TimeSpan EffectiveRetryWindow => RetryWindow ?? TimeSpan.FromMinutes(30);
}

/// <summary>Sink 运行统计（健康页与长稳报告读取）。</summary>
public sealed record PersistenceStats(
    long Enqueued,
    long Written,
    long Pending,
    long DroppedByBuffer,
    long DroppedAfterRetry,
    long FailedBatches,
    long RetriedBatches,
    string? LastError)
{
    public bool Healthy => FailedBatches == 0 && DroppedByBuffer == 0 && DroppedAfterRetry == 0;
}

/// <summary>
/// 持久化 Sink（TB.0，NFR-22 设计红线）：Hub 的普通订阅方，把四类消息 + 预警事件
/// 落入时序库（五张表：motion / status / optics / result / alert）。
///
/// 三条不可妥协的约束（均有测试锁定）：
/// 1) **绝不阻塞实时链路**：消息回调里只做入队，不做 IO；写失败只进重试队列并计数。
/// 2) **异常不扩散**：任何写入异常都在 Sink 内部消化，Hub 的发布方线程永远看不到。
/// 3) **可观测**：丢弃与失败都进 <see cref="Stats"/>，长稳报告据此判「数据完整性」而非只看进程活着。
///
/// 降级语义：缓冲超上限时丢最旧（生产端比分析端重要）；重试超窗口时丢弃并计入
/// <see cref="PersistenceStats.DroppedAfterRetry"/>——宁可丢历史，不可拖慢实时。
/// </summary>
public sealed class PersistenceSink : IDisposable
{
    private readonly ITimeSeriesStore _store;
    private readonly IRealtimeHub _hub;
    private readonly PersistenceOptions _options;
    private readonly TimeProvider _clock;
    private readonly ConcurrentQueue<TimeSeriesRecord> _buffer = new();
    private readonly ConcurrentQueue<FailedBatch> _retry = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Timer _timer;
    private readonly object _flushGate = new();

    private long _enqueued;
    private long _written;
    private long _droppedByBuffer;
    private long _droppedAfterRetry;
    private long _failedBatches;
    private long _retriedBatches;
    private string? _lastError;
    private int _bufferCount;
    private bool _disposed;

    public PersistenceSink(ITimeSeriesStore store, IRealtimeHub hub, PersistenceOptions options, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;

        _subscriptions.Add(hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, m => Enqueue(TwinTopic.Motion, m)));
        _subscriptions.Add(hub.Subscribe<StatusEvent>(TwinTopic.Status, m => Enqueue(TwinTopic.Status, m)));
        _subscriptions.Add(hub.Subscribe<OpticsEvent>(TwinTopic.Optics, m => Enqueue(TwinTopic.Optics, m)));
        _subscriptions.Add(hub.Subscribe<WaferResult>(TwinTopic.Result, m => Enqueue(TwinTopic.Result, m)));
        _subscriptions.Add(hub.Subscribe<AlertEvent>(TwinTopic.Alert, m => Enqueue(TwinTopic.Alert, m)));

        _timer = new Timer(_ => SafeFlush(), null, _options.EffectiveFlushInterval, _options.EffectiveFlushInterval);
    }

    /// <summary>当前统计快照。</summary>
    public PersistenceStats Stats => new(
        Interlocked.Read(ref _enqueued),
        Interlocked.Read(ref _written),
        Volatile.Read(ref _bufferCount),
        Interlocked.Read(ref _droppedByBuffer),
        Interlocked.Read(ref _droppedAfterRetry),
        Interlocked.Read(ref _failedBatches),
        Interlocked.Read(ref _retriedBatches),
        Volatile.Read(ref _lastError));

    /// <summary>入队（回调路径：不做 IO，不抛异常）。</summary>
    private void Enqueue(TwinTopic topic, ITwinMessage message)
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            var record = TimeSeriesRecord.FromMessage(topic, message);
            _buffer.Enqueue(record);
            Interlocked.Increment(ref _enqueued);
            var pending = Interlocked.Increment(ref _bufferCount);

            if (pending > _options.MaxBufferRecords)
            {
                // 缓冲超限：丢最旧，保最新（实时链路优先）
                if (_buffer.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _bufferCount);
                    Interlocked.Increment(ref _droppedByBuffer);
                }
            }

            if (pending >= _options.BatchSize)
            {
                // 达到批上限立即刷；在 Timer 线程做，不在发布方线程做
                _timer.Change(TimeSpan.Zero, _options.EffectiveFlushInterval);
            }
        }
        catch (Exception ex)
        {
            // 连入队都失败（构造记录时序列化异常）：计数，不向 Hub 抛
            Interlocked.Increment(ref _failedBatches);
            Volatile.Write(ref _lastError, ex.Message);
        }
    }

    /// <summary>强制刷盘（关闭前、长稳巡检、测试）。返回本次写出的条数。</summary>
    public int Flush()
    {
        lock (_flushGate)
        {
            var written = RetryPending();

            while (true)
            {
                var batch = DrainBatch();
                if (batch.Count == 0)
                {
                    break;
                }

                written += WriteBatch(batch);
            }

            return written;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();

        try
        {
            Flush();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastError, ex.Message);
        }
    }

    private void SafeFlush()
    {
        try
        {
            Flush();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedBatches);
            Volatile.Write(ref _lastError, ex.Message);
        }
    }

    private List<TimeSeriesRecord> DrainBatch()
    {
        var batch = new List<TimeSeriesRecord>(Math.Min(_options.BatchSize, 1024));
        while (batch.Count < _options.BatchSize && _buffer.TryDequeue(out var record))
        {
            Interlocked.Decrement(ref _bufferCount);
            batch.Add(record);
        }

        return batch;
    }

    private int WriteBatch(List<TimeSeriesRecord> batch)
    {
        try
        {
            _store.Append(batch);
            Interlocked.Add(ref _written, batch.Count);
            return batch.Count;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedBatches);
            Volatile.Write(ref _lastError, ex.Message);
            _retry.Enqueue(new FailedBatch(batch, _clock.GetUtcNow()));
            return 0;
        }
    }

    private int RetryPending()
    {
        var written = 0;
        var deadline = _options.EffectiveRetryWindow;
        var now = _clock.GetUtcNow();
        var remaining = new List<FailedBatch>();

        while (_retry.TryDequeue(out var failed))
        {
            if (now - failed.FirstFailedAt > deadline)
            {
                Interlocked.Add(ref _droppedAfterRetry, failed.Records.Count);
                continue;
            }

            try
            {
                _store.Append(failed.Records);
                Interlocked.Add(ref _written, failed.Records.Count);
                Interlocked.Increment(ref _retriedBatches);
                written += failed.Records.Count;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedBatches);
                Volatile.Write(ref _lastError, ex.Message);
                remaining.Add(failed);
            }
        }

        foreach (var failed in remaining)
        {
            _retry.Enqueue(failed);
        }

        return written;
    }

    private sealed record FailedBatch(IReadOnlyList<TimeSeriesRecord> Records, DateTimeOffset FirstFailedAt);
}
