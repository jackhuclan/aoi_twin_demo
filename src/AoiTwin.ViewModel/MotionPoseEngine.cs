using System.Collections.Concurrent;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Kinematics;

namespace AoiTwin.ViewModel;

/// <summary>
/// 运动位姿引擎（TA.4，FR-M2-03）：IMotionPoseSource 的完整实现——
/// 轴快照（数据线程）→ <see cref="AxisInterpolator"/> 环形缓冲 → Sample(now) 线性插值 →
/// <see cref="TeachCoordinateMapper"/> 换算 → 合成部件位姿快照（渲染线程）。
///
/// 性能：部件在「保持段」（所有轴都晚于最新样本/早于最旧样本）时直接复用上次合成的快照，
/// 只有插值区间内的帧才重新分配矩阵——16 部件 × 60fps 下典型分配率 < 1KB/s。
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
        public PoseSnapshot? CachedPose;
        public int CachedSampleSum;

        /// <summary>缓存快照是否在保持段合成——只有保持段的结果才可跨时刻复用（区间内插值随时间变化）。</summary>
        public bool CachedIsHold;
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
    }

    /// <inheritdoc />
    public PoseSnapshot? GetPose(string partId, DateTimeOffset now)
    {
        if (!_parts.TryGetValue(partId, out var part))
        {
            return null; // 该部件尚无任何轴数据
        }

        var sampleSum = 0;
        var holding = true;
        foreach (var axis in part.Axes.Values)
        {
            sampleSum += axis.SampleCount;
            holding &= axis.IsHolding(now);
        }

        // 保持段合成的快照可跨时刻复用（结果恒定，零分配热路径）；
        // 区间内合成的快照随时间变化，即使此刻恰好处于保持段也必须先重算一次
        if (part.CachedPose is not null && part.CachedIsHold && holding && sampleSum == part.CachedSampleSum)
        {
            return part.CachedPose;
        }

        double dx = 0, dy = 0, dz = 0;
        foreach (var (axisName, interpolator) in part.Axes)
        {
            var (ax, ay, az) = _mapper.Map(partId, axisName, interpolator.Sample(now));
            dx += ax;
            dy += ay;
            dz += az;
        }

        var pose = PoseSnapshot.Translation(dx, dy, dz, now);
        part.CachedPose = pose;
        part.CachedSampleSum = sampleSum;
        part.CachedIsHold = holding;
        return pose;
    }

    /// <summary>引擎已见过的部件号（诊断/测试用）。</summary>
    public IReadOnlyCollection<string> KnownPartIds => _parts.Keys.ToArray();
}
