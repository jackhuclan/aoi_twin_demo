namespace AoiTwin.Core.Persistence;

/// <summary>单个分区的元信息（存储治理与健康页展示用）。</summary>
public sealed record TimeSeriesPartition(DateOnly Date, string FilePath, long Bytes);

/// <summary>
/// 时序存储抽象（TB.0）：M3 的全部历史能力（回灌、健康档案、预测回测、导出）只依赖本接口。
///
/// 默认实现是 <see cref="FileTimeSeriesStore"/>（按天分区的 JSONL，零外部依赖）。
/// 若现场改用 SQLite/TimescaleDB，只需另写一个实现并在 DI 中替换——上层（Sink、预测器、回放）
/// 不改一行，这正是 PRD NFR-22「分析层故障不影响实时链路」与「存储可替换」的落地方式。
///
/// 线程模型：Append 可被单写者线程调用；Query 可并发调用（每次独立打开文件句柄）。
/// </summary>
public interface ITimeSeriesStore
{
    /// <summary>存储根目录（报告与健康页展示）。</summary>
    string RootPath { get; }

    /// <summary>批量追加。实现应保证「要么整批写入、要么抛异常」的批次语义，便于 Sink 重试。</summary>
    void Append(IReadOnlyList<TimeSeriesRecord> records);

    /// <summary>按条件惰性查询（按时间升序）。调用方可用 LINQ 在其上做聚合。</summary>
    IEnumerable<TimeSeriesRecord> Query(TimeSeriesQuery query);

    /// <summary>现存分区（升序）。</summary>
    IReadOnlyList<TimeSeriesPartition> Partitions();

    /// <summary>指定区间的落盘字节数估算（NFR-21 存储增长估算）。</summary>
    long EstimateBytes(DateTimeOffset from, DateTimeOffset to);

    /// <summary>删除早于 <paramref name="before"/> 的分区，返回删除字节数。</summary>
    long Prune(DateOnly before);
}
