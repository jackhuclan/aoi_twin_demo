namespace AoiTwin.Core.Persistence;

/// <summary>存储治理参数（TB.2）。</summary>
/// <param name="RetentionDays">保留天数；早于 now - RetentionDays 的分区被清理。</param>
/// <param name="MinFreeBytes">磁盘水位下限：低于此值进入应急清理（保留最近 <paramref name="EmergencyKeepDays"/> 天）。</param>
/// <param name="EmergencyKeepDays">应急清理时无论如何保留的最近天数（防止把当天数据删光）。</param>
public sealed record StorageGovernanceOptions(
    int RetentionDays = 30,
    long MinFreeBytes = 5L * 1024 * 1024 * 1024,
    int EmergencyKeepDays = 1);

/// <summary>一轮治理的结果（报告与健康页展示）。</summary>
public sealed record StorageReport(
    DateTimeOffset RunAt,
    int PartitionsBefore,
    int PartitionsAfter,
    long DeletedBytes,
    long TotalBytes,
    long FreeBytes,
    bool WatermarkOk,
    IReadOnlyList<string> Actions)
{
    public bool Healthy => WatermarkOk;
}

/// <summary>
/// 存储治理（TB.2）：保留天数 + 自动清理 + 磁盘水位保护 + 增长估算。
///
/// 两条策略边界：
/// 1) **常规只删过期分区**（早于保留天数），不做「按大小删最新」——历史数据是回测资产，宁可告警不可误删；
/// 2) **水位是硬约束**：低于 <see cref="StorageGovernanceOptions.MinFreeBytes"/> 时进入应急清理，
///    仍保留最近 N 天，并返回 <c>WatermarkOk=false</c> 让上层停录/告警（PRD §「磁盘水位」）。
/// </summary>
public sealed class StorageGovernor
{
    private readonly ITimeSeriesStore _store;
    private readonly StorageGovernanceOptions _options;
    private readonly Func<string, long> _freeBytesProvider;

    /// <param name="freeBytesProvider">可用空间探测（默认取存储所在盘；测试可注入）。</param>
    public StorageGovernor(
        ITimeSeriesStore store,
        StorageGovernanceOptions? options = null,
        Func<string, long>? freeBytesProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new StorageGovernanceOptions();
        _freeBytesProvider = freeBytesProvider ?? FreeBytesOf;
    }

    /// <summary>执行一轮治理。</summary>
    public StorageReport RunOnce(DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.RetentionDays);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.EmergencyKeepDays);

        var actions = new List<string>();
        var before = _store.Partitions();
        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-_options.RetentionDays);
        var deleted = _store.Prune(cutoff);
        var afterRetention = _store.Partitions();

        if (deleted > 0)
        {
            actions.Add($"按保留期（{_options.RetentionDays} 天）清理 {before.Count - afterRetention.Count} 个分区，释放 {deleted} 字节");
        }

        var free = _freeBytesProvider(_store.RootPath);
        var watermarkOk = free >= _options.MinFreeBytes;

        if (!watermarkOk)
        {
            actions.Add($"磁盘水位不足（可用 {free} B < 下限 {_options.MinFreeBytes} B），进入应急清理");
            var keepFrom = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-_options.EmergencyKeepDays);
            var emergencyDeleted = _store.Prune(keepFrom);
            deleted += emergencyDeleted;
            free = _freeBytesProvider(_store.RootPath);
            watermarkOk = free >= _options.MinFreeBytes;
            actions.Add(emergencyDeleted > 0
                ? $"应急清理释放 {emergencyDeleted} 字节；水位{(watermarkOk ? "已恢复" : "仍不足，需停录并人工介入")}"
                : "无可清理分区；需停录并人工介入");
        }

        var after = _store.Partitions();
        return new StorageReport(
            now,
            before.Count,
            after.Count,
            deleted,
            after.Sum(p => p.Bytes),
            free,
            watermarkOk,
            actions);
    }

    /// <summary>未来 <paramref name="days"/> 天的存储增长估算（按现有分区日均字节外推，NFR-21）。</summary>
    public long EstimateGrowthBytes(int days, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);
        var partitions = _store.Partitions();
        if (partitions.Count == 0)
        {
            return 0;
        }

        var average = partitions.Average(p => (double)p.Bytes);
        return (long)(average * days);
    }

    private static long FreeBytesOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return long.MaxValue; // 探测失败不误判为水位不足
        }
    }
}
