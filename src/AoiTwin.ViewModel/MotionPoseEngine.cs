using System.Collections.Concurrent;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Kinematics;

namespace AoiTwin.ViewModel;

/// <summary>
/// 运动位姿引擎（TA.4，FR-M2-03）：IMotionPoseSource 的完整实现——
/// 轴快照（数据线程）→ <see cref="AxisInterpolator"/> 环形缓冲 → Sample(now) 线性插值 →
/// <see cref="TeachCoordinateMapper"/> 换算 → 合成部件位姿快照（渲染线程）。
///
/// 性能：部件在「保持段」（所有轴都晚于最新样本/早于最旧样本）时直接复用上次合成的快照；
/// 插值区间内走**每部件双缓冲**——两块预分配的 16 元素缓冲轮换写入，写满才换引用，
/// 渲染线程读到的永远是完整旧值或完整新值，帧内零堆分配（NFR-13）。
/// 旋转轴（THETA/R）在占位资产期按标定 Direction=NONE 处理（零位移）；
/// 旋转可视化待真实 GLB 轴心资产就绪后启用（见 PLAN-AOI-TWIN-M2 §8 遗留）。
/// </summary>
public sealed class MotionPoseEngine : IMotionPoseSource
{
    private readonly TeachCoordinateMapper _mapper;
    private readonly ConcurrentDictionary<string, PartState> _parts = new(StringComparer.Ordinal);

    private sealed class PartState
    {
        public readonly ConcurrentDictionary<string, AxisInterpolator> Axes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 轴表快照（NFR-13）：ConcurrentDictionary 的枚举器会复制一份节点数组（每次约 64B），
        /// 60fps 热路径上不能每帧枚举它。轴集合只在首个样本到达时增长（冷路径），
        /// 这里缓存 <see cref="KeyValuePair{TKey,TValue}"/> 数组、长度不一致才重建。
        /// </summary>
        public KeyValuePair<string, AxisInterpolator>[] AxisArray = [];

        /// <summary>双缓冲：渲染线程可能正拿着一块的快照，只能改写另一块。</summary>
        public readonly double[] BufferA = CreateIdentityBuffer();
        public readonly double[] BufferB = CreateIdentityBuffer();
        public readonly PoseSnapshot SnapshotA;
        public readonly PoseSnapshot SnapshotB;

        public bool UseA = true;
        public PoseSnapshot? CachedPose;
        public int CachedSampleSum;

        /// <summary>缓存快照是否在保持段合成——只有保持段的结果才可跨时刻复用（区间内插值随时间变化）。</summary>
        public bool CachedIsHold;

        public PartState()
        {
            SnapshotA = PoseSnapshot.FromOwnedBuffer(BufferA, DateTimeOffset.MinValue);
            SnapshotB = PoseSnapshot.FromOwnedBuffer(BufferB, DateTimeOffset.MinValue);
        }

        private static double[] CreateIdentityBuffer() =>
            [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
    }

    public MotionPoseEngine(TeachCoordinateMapper? mapper = null)
    {
        _mapper = mapper ?? TeachCoordinateMapper.Empty;
    }

    /// <inheritdoc />
    public void OnAxis(AxisSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var part = _parts.GetOrAdd(snapshot.PartId, static _ => new PartState());
        var axis = part.Axes.GetOrAdd(snapshot.AxisName, static _ => new AxisInterpolator());
        axis.AddSample(snapshot.Timestamp, snapshot.Position); // 倒退时间戳由插值器丢弃

        if (part.AxisArray.Length != part.Axes.Count)
        {
            part.AxisArray = [.. part.Axes]; // 仅新轴出现时重建（渲染帧内只读）
        }
    }

    /// <inheritdoc />
    public PoseSnapshot? GetPose(string partId, DateTimeOffset now)
    {
        if (!_parts.TryGetValue(partId, out var part))
        {
            return null; // 该部件尚无任何轴数据
        }

        // 直接枚举 ConcurrentDictionary（GetEnumerator 是 struct，不分配）；
        // 走 .Values 会每次复制一份 List —— 60fps 热路径上这就是 NFR-13 禁止的分配
        var axes = part.AxisArray;
        var sampleSum = 0;
        var holding = true;
        foreach (var pair in axes)
        {
            sampleSum += pair.Value.SampleCount;
            holding &= pair.Value.IsHolding(now);
        }

        // 保持段合成的快照可跨时刻复用（结果恒定，零分配热路径）；
        // 区间内合成的快照随时间变化，即使此刻恰好处于保持段也必须先重算一次
        if (part.CachedPose is not null && part.CachedIsHold && holding && sampleSum == part.CachedSampleSum)
        {
            return part.CachedPose;
        }

        double dx = 0, dy = 0, dz = 0;
        foreach (var pair in axes)
        {
            var (ax, ay, az) = _mapper.Map(partId, pair.Key, pair.Value.Sample(now));
            dx += ax;
            dy += ay;
            dz += az;
        }

        var pose = Compose(part, dx, dy, dz, now);
        part.CachedPose = pose;
        part.CachedSampleSum = sampleSum;
        part.CachedIsHold = holding;
        return pose;
    }

    /// <summary>
    /// 双缓冲合成（NFR-13 零分配）：改写未被读者持有的那块缓冲，写完才换引用。
    /// 矩阵是纯平移，这里直接写 16 个元素而不是走 <see cref="PoseSnapshot.Translation"/>（后者会新分配数组）。
    /// </summary>
    private static PoseSnapshot Compose(PartState part, double dx, double dy, double dz, DateTimeOffset now)
    {
        var useA = !part.UseA;
        part.UseA = useA;

        var buffer = useA ? part.BufferA : part.BufferB;
        buffer[3] = dx;
        buffer[7] = dy;
        buffer[11] = dz;

        var snapshot = useA ? part.SnapshotA : part.SnapshotB;
        snapshot.Timestamp = now;
        return snapshot;
    }

    /// <summary>引擎已见过的部件号（诊断/测试用）。</summary>
    public IReadOnlyCollection<string> KnownPartIds => _parts.Keys.ToArray();
}
