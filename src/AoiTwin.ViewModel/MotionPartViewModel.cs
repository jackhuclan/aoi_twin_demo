using System.Threading;

namespace AoiTwin.ViewModel;

/// <summary>运动部件的 3D 位姿视图模型（渲染线程唯一的位姿读取口）。</summary>
public interface IMotionPartViewModel
{
    string PartId { get; }

    /// <summary>读取最新位姿快照（渲染线程调用，60fps 频率；无更新时返回上次值）。</summary>
    PoseSnapshot ReadLatest();
}

/// <summary>
/// 运动部件位姿双缓冲（TA.2）：数据线程 Update 整引用换入，渲染线程 ReadLatest 读引用。
/// Interlocked/Volatile 保证引用读写原子，快照对象不可变——并发读写无撕裂、无锁。
/// </summary>
public sealed class MotionPartViewModel : IMotionPartViewModel
{
    private PoseSnapshot _snapshot = PoseSnapshot.Identity;

    public MotionPartViewModel(string partId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partId);
        PartId = partId;
    }

    public string PartId { get; }

    /// <summary>写入新位姿（数据线程调用；构造快照在锁外完成，这里只换引用）。</summary>
    public void Update(PoseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _snapshot, snapshot);
    }

    public PoseSnapshot ReadLatest() => Volatile.Read(ref _snapshot);
}
