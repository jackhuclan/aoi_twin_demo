using System.Buffers.Binary;
using System.Text;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Recording;

namespace AoiTwin.Core.Replay;

/// <summary>
/// 晶圆批次（T3.2）：一片晶圆的检测过程在录制文件中的区间与摘要。
/// 区间语义——**上一片结果之后 → 本片结果帧（含）**：从批次首帧开始回放，
/// 依次重演该片的轴运动/光学事件，片尾收到该片的 WaferResult。
/// 这样「按片复查」看到的是一整片完整的检测过程，而不是结果之后的空档。
/// </summary>
public sealed record WaferBatch(
    int Ordinal,
    string WaferId,
    int FirstFrameIndex,
    TimeSpan StartElapsed,
    int FrameCount,
    int DefectCount,
    TimeSpan Tact)
{
    /// <summary>批次结束（相对文件首帧的原始时间线位置；由索引构建时补齐）。</summary>
    public TimeSpan EndElapsed { get; init; }

    /// <summary>本片结果帧在文件中的帧号（批次末帧）。</summary>
    public int ResultFrameIndex { get; init; }

    public TimeSpan Duration => EndElapsed - StartElapsed;

    /// <summary>列表显示用短标签：`#003  WAFER-20261103-003`。</summary>
    public string Display => $"#{Ordinal:D3}  {WaferId}";

    /// <summary>缺陷/节拍摘要：`缺陷 12 · 节拍 8.4s`。</summary>
    public string Summary => $"缺陷 {DefectCount} · 节拍 {Tact.TotalSeconds:F1}s";
}

/// <summary>
/// 批次索引（T3.2）：扫描录制文件的有效帧前缀，抽取全部 WaferResult 帧构建批次表，
/// 并提供「当前帧 → 所属批次」的反查（时间轴定位与批次跳转用）。
///
/// 设计说明：批次表只依赖帧索引 + WaferResult 帧头偏移，不加载整片 die 图，
/// 因此整班次文件（十几万帧）构建索引的内存与耗时都在 O(批次数) 量级。
/// </summary>
public sealed class WaferBatchIndex
{
    private static readonly WaferBatchIndex EmptyIndex = new([]);

    private readonly IReadOnlyList<WaferBatch> _batches;

    private WaferBatchIndex(IReadOnlyList<WaferBatch> batches) => _batches = batches;

    public static WaferBatchIndex Empty => EmptyIndex;

    /// <summary>按时间顺序排列的批次表。</summary>
    public IReadOnlyList<WaferBatch> Batches => _batches;

    public int Count => _batches.Count;

    /// <summary>
    /// 从录制文件构建批次索引。index 可传入已构建的帧索引以避免重复扫描。
    /// 文件无 WaferResult 帧时返回空索引（不是错误：纯运动数据录制同样合法）。
    /// </summary>
    public static WaferBatchIndex Build(string filePath, IReadOnlyList<FrameIndexEntry>? index = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        index ??= RecordingReader.BuildIndex(filePath);
        if (index.Count == 0)
        {
            return EmptyIndex;
        }

        var startMs = index[0].TimestampMs;
        var found = new List<(int FrameIndex, long TimestampMs, string WaferId, int Defects, TimeSpan Tact)>();

        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            for (var i = 0; i < index.Count; i++)
            {
                var entry = index[i];
                if (entry.TypeId != RecFileFormat.MessageType.WaferResult)
                {
                    continue;
                }

                stream.Seek(entry.Offset + RecFileFormat.FrameHeaderSize, SeekOrigin.Begin);
                var payload = reader.ReadBytes(entry.PayloadLength);
                if (payload.Length != entry.PayloadLength)
                {
                    // 帧尾被截断（理论上 BuildIndex 已过滤，此处兜底）
                    break;
                }

                if (RecordingReader.DeserializeFrame(entry.TypeId, payload) is WaferResult wafer)
                {
                    found.Add((FrameIndex: i, entry.TimestampMs, wafer.WaferId, wafer.Defects.Count, wafer.Tact));
                }
            }
        }

        if (found.Count == 0)
        {
            return EmptyIndex;
        }

        var batches = new List<WaferBatch>(found.Count);
        for (var i = 0; i < found.Count; i++)
        {
            var cur = found[i];
            var firstFrame = i == 0 ? 0 : found[i - 1].FrameIndex + 1;
            var endFrameExclusive = cur.FrameIndex + 1;
            var endElapsed = i + 1 < found.Count
                ? TimeSpan.FromMilliseconds(index[cur.FrameIndex + 1].TimestampMs - startMs)
                : TimeSpan.FromMilliseconds(index[^1].TimestampMs - startMs);

            batches.Add(new WaferBatch(
                Ordinal: i + 1,
                WaferId: cur.WaferId,
                FirstFrameIndex: firstFrame,
                StartElapsed: TimeSpan.FromMilliseconds(index[firstFrame].TimestampMs - startMs),
                FrameCount: endFrameExclusive - firstFrame,
                DefectCount: cur.Defects,
                Tact: cur.Tact)
            {
                EndElapsed = endElapsed,
                ResultFrameIndex = cur.FrameIndex,
            });
        }

        return new WaferBatchIndex(batches);
    }

    /// <summary>当前帧所属批次下标；首批次之前返回 -1（用于「上一批次」跳转与时间轴高亮）。</summary>
    public int IndexOfFrame(int frameIndex)
    {
        if (_batches.Count == 0 || frameIndex < _batches[0].FirstFrameIndex)
        {
            return -1;
        }

        int lo = 0, hi = _batches.Count - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (_batches[mid].FirstFrameIndex <= frameIndex)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ans;
    }

    /// <summary>当前帧所属批次；不在任何批次内返回 null。</summary>
    public WaferBatch? BatchAt(int frameIndex)
    {
        var i = IndexOfFrame(frameIndex);
        return i < 0 ? null : _batches[i];
    }

    /// <summary>按序号（1 起）取批次，越界返回 null。</summary>
    public WaferBatch? ByOrdinal(int ordinal)
        => ordinal >= 1 && ordinal <= _batches.Count ? _batches[ordinal - 1] : null;

    /// <summary>按原始时间线位置取「该时刻已开始的最新批次」。</summary>
    public WaferBatch? BatchAtElapsed(TimeSpan elapsed)
    {
        WaferBatch? hit = null;
        foreach (var b in _batches)
        {
            if (b.StartElapsed <= elapsed)
            {
                hit = b;
            }
            else
            {
                break;
            }
        }

        return hit;
    }
}
