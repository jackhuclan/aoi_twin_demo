namespace AoiTwin.Core.Kinematics;

/// <summary>
/// 单轴样本插值器（TA.3，FR-M2-03）：2–5Hz 轴快照 → 60fps 渲染帧线性插值。
/// 环形样本缓冲（默认 32 个样本）；时间戳倒退的样本直接丢弃（防回跳）。
/// 线程模型：AddSample 数据线程、Sample 渲染线程，内部 lock（纳秒级临界区）。
/// </summary>
public sealed class AxisInterpolator(int capacity = 32)
{
    private readonly object _gate = new();
    private readonly (DateTimeOffset Time, double Position)[] _ring = new (DateTimeOffset, double)[capacity];
    private int _head;   // 最旧样本下标
    private int _count;

    /// <summary>加入样本；时间戳早于等于最新样本时丢弃（防回跳：设备时间抖动不得让位姿倒退）。</summary>
    public void AddSample(DateTimeOffset time, double position)
    {
        lock (_gate)
        {
            if (_count > 0)
            {
                var last = _ring[(_head + _count - 1) % _ring.Length];
                if (time <= last.Time)
                {
                    return; // 倒退/重复时间戳：丢弃
                }
            }

            var tail = (_head + _count) % _ring.Length;
            _ring[tail] = (time, position);
            if (_count < _ring.Length)
            {
                _count++;
            }
            else
            {
                _head = (_head + 1) % _ring.Length; // 缓冲满：淘汰最旧
            }
        }
    }

    /// <summary>
    /// 取指定时刻的插值位置：
    /// t ≤ 首样本 → 首样本值（hold）；t ≥ 末样本 → 末样本值（hold）；
    /// 区间内 → 相邻两样本线性插值。命中样本时刻时返回该样本精确值（端点严格相等）。
    /// </summary>
    public double Sample(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return 0; // 无数据：设备零位
            }

            var first = _ring[_head];
            if (now <= first.Time)
            {
                return first.Position;
            }

            var last = _ring[(_head + _count - 1) % _ring.Length];
            if (now >= last.Time)
            {
                return last.Position;
            }

            // 区间内：二分找右邻样本
            int lo = 0, hi = _count - 1;
            while (hi - lo > 1)
            {
                var mid = (lo + hi) / 2;
                if (_ring[(_head + mid) % _ring.Length].Time <= now)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            var a = _ring[(_head + lo) % _ring.Length];
            var b = _ring[(_head + hi) % _ring.Length];
            var fraction = (now - a.Time) / (b.Time - a.Time);
            return a.Position + (b.Position - a.Position) * fraction;
        }
    }

    /// <summary>当前样本数（诊断用）。</summary>
    public int SampleCount
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }
}
