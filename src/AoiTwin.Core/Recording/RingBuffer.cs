namespace AoiTwin.Core.Recording;

/// <summary>
/// 有界 FIFO 环形缓冲（T2.2）：解耦发布线程与磁盘 IO。
/// 策略：满时 TryEnqueue 返回 false（调用方阻塞重试），保证"丢帧率 0"语义；
/// 线程安全，支持多生产者/多消费者。
/// </summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private readonly object _gate = new();
    private int _head;
    private int _count;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "容量必须为正数");
        }

        Capacity = capacity;
        _items = new T[capacity];
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public bool TryEnqueue(T item)
    {
        lock (_gate)
        {
            if (_count == Capacity)
            {
                return false;
            }

            _items[(_head + _count) % Capacity] = item;
            _count++;
            Monitor.Pulse(_gate);
            return true;
        }
    }

    /// <summary>阻塞取出；缓冲空时等待。取消时抛 OperationCanceledException。</summary>
    public T Take(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            while (_count == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(200));
            }

            return DequeueCore();
        }
    }

    public bool TryTake(out T item)
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                item = default!;
                return false;
            }

            item = DequeueCore();
            return true;
        }
    }

    private T DequeueCore()
    {
        var item = _items[_head];
        _items[_head] = default!;
        _head = (_head + 1) % Capacity;
        _count--;
        Monitor.Pulse(_gate);
        return item;
    }
}
