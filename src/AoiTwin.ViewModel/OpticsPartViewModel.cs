using System.Threading;

namespace AoiTwin.ViewModel;

/// <summary>
/// 光学部件状态快照（FR-M2-04）：通道值与亮度为原始数值，
/// 通道→颜色的映射在渲染/配置层完成（NFR-18 颜色映射表可配置，不进 VM）。
/// </summary>
/// <param name="ChannelValues">各通道强度（0..1），下标即通道号。</param>
/// <param name="Brightness">同轴光源亮度（0..1），自发光强度映射用。</param>
/// <param name="Timestamp">状态时间戳。</param>
public sealed record OpticsStateSnapshot(double[] ChannelValues, double Brightness, DateTimeOffset Timestamp);

/// <summary>光学部件状态视图模型。</summary>
public interface IOpticsPartViewModel
{
    string PartId { get; }

    /// <summary>读取最新光学状态（渲染线程调用；无更新时返回上次值）。</summary>
    OpticsStateSnapshot ReadLatest();
}

/// <summary>光学部件状态双缓冲（与 MotionPartViewModel 同构：不可变快照 + 引用换入）。</summary>
public sealed class OpticsPartViewModel : IOpticsPartViewModel
{
    private readonly int _channelCount;
    private OpticsStateSnapshot _snapshot;

    public OpticsPartViewModel(string partId, int channelCount = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partId);
        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount), "通道数必须为正");
        }

        PartId = partId;
        _channelCount = channelCount;
        _snapshot = new OpticsStateSnapshot(new double[channelCount], 0, DateTimeOffset.MinValue);
    }

    public string PartId { get; }

    public OpticsStateSnapshot ReadLatest() => Volatile.Read(ref _snapshot);

    /// <summary>数据线程调用：更新单通道值，生成新快照换入。</summary>
    public void UpdateChannel(int channel, double value, DateTimeOffset timestamp)
    {
        if (channel < 0 || channel >= _channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        var current = Volatile.Read(ref _snapshot);
        var values = (double[])current.ChannelValues.Clone();
        values[channel] = value;
        Volatile.Write(ref _snapshot, new OpticsStateSnapshot(values, current.Brightness, timestamp));
    }

    /// <summary>数据线程调用：更新亮度，生成新快照换入。</summary>
    public void UpdateBrightness(double brightness, DateTimeOffset timestamp)
    {
        var current = Volatile.Read(ref _snapshot);
        Volatile.Write(ref _snapshot, new OpticsStateSnapshot(current.ChannelValues, brightness, timestamp));
    }
}
