namespace AoiTwin.Core.Recording;

/// <summary>
/// 帧索引条目（回放引擎 T3.1）：一帧在文件中的位置与元数据。
/// Offset 指向帧头起始；TimestampMs 为帧头权威时间戳（回放时钟基准）。
/// </summary>
public readonly record struct FrameIndexEntry(
    long Offset,
    long TimestampMs,
    Bus.TwinTopic Topic,
    byte TypeId,
    int PayloadLength)
{
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);
}
