using System.Collections.Concurrent;
using AoiTwin.Core.Bus;

namespace AoiTwin.ViewModel;

/// <summary>
/// Wafer 数据 GPU 上传队列（FR-M2-06/07）：数据线程整片入队，渲染线程逐片取出
/// 做 Texture 替换 / 缺陷点云增量——上传动作发生在渲染线程，天然无共享可变状态。
/// </summary>
public interface IWaferDataViewModel
{
    /// <summary>当前积压片数。</summary>
    int Count { get; }

    long EnqueuedTotal { get; }

    long TakenTotal { get; }

    /// <summary>队列满被挤掉的片数（降级计数：渲染线程长时间不消费时丢最旧保最新）。</summary>
    long DroppedTotal { get; }

    void Enqueue(WaferResult result);

    bool TryTake(out WaferResult result);
}

/// <summary>
/// 有界 Wafer 上传队列（TA.2）：容量满时挤掉最旧一片并计数（不丢最新片的语义下最小损失）。
/// ConcurrentQueue + 计数器保证不重；「不丢」由容量内的完整性与 DroppedTotal 透明化共同保证。
/// </summary>
public sealed class WaferUploadQueue : IWaferDataViewModel
{
    public const int DefaultCapacity = 8;

    private readonly ConcurrentQueue<WaferResult> _queue = new();
    private readonly int _capacity;
    private long _enqueued;
    private long _taken;
    private long _dropped;

    public WaferUploadQueue(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "容量必须为正");
        }

        _capacity = capacity;
    }

    public int Count => _queue.Count;

    public long EnqueuedTotal => Interlocked.Read(ref _enqueued);

    public long TakenTotal => Interlocked.Read(ref _taken);

    public long DroppedTotal => Interlocked.Read(ref _dropped);

    public void Enqueue(WaferResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Interlocked.Increment(ref _enqueued);
        _queue.Enqueue(result);

        // 超容量挤掉最旧：单生产者（Result 直推）下单循环即可
        while (_queue.Count > _capacity)
        {
            if (_queue.TryDequeue(out _))
            {
                Interlocked.Increment(ref _dropped);
            }
            else
            {
                break;
            }
        }
    }

    public bool TryTake(out WaferResult result)
    {
        if (_queue.TryDequeue(out var item))
        {
            Interlocked.Increment(ref _taken);
            result = item;
            return true;
        }

        result = null!;
        return false;
    }
}
