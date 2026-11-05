using System.Globalization;
using System.Text;

namespace AoiTwin.Core.Persistence;

/// <summary>
/// 按天分区的 JSONL 时序存储（TB.0 默认实现）。
///
/// 文件布局：<c>&lt;root&gt;/&lt;yyyyMMdd&gt;.jsonl</c>（UTC 天粒度，一行一条记录）。
/// 选 JSONL 而不是 SQLite 的理由：
/// 1) 零外部依赖与原生库，CI/离线机不需要额外安装；
/// 2) 事故现场可直接用记事本/日志工具查看，不需要客户端；
/// 3) 按天分区天然支持「删过期数据 = 删文件」，不会出现 SQLite 需要 VACUUM 的空间回收问题。
/// 代价是区间查询要顺序扫描——30 天量级由分区裁剪 + 行级过滤承担，NFR-21 的实测口径见
/// <c>docs/m3-perf-report.md</c>；需要更高查询性能时按 <see cref="ITimeSeriesStore"/> 换实现即可。
///
/// 分区按 UTC 切分：现场时区调整不会把同一天的数据劈成两段，跨时区分析也不会错位。
/// </summary>
public sealed class FileTimeSeriesStore : ITimeSeriesStore
{
    private const string Extension = ".jsonl";

    private readonly object _writeGate = new();

    public FileTimeSeriesStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public void Append(IReadOnlyList<TimeSeriesRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        lock (_writeGate)
        {
            foreach (var group in records.GroupBy(r => r.PartitionDate).OrderBy(g => g.Key))
            {
                var path = PathFor(group.Key);
                var payload = new StringBuilder(group.Count() * 160);
                foreach (var record in group)
                {
                    payload.Append(record.ToLine()).Append('\n');
                }

                // 单次追加：同一天的一批要么整体写入，要么抛异常（Sink 依此整批重试）
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(payload.ToString());
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
        }
    }

    public IEnumerable<TimeSeriesRecord> Query(TimeSeriesQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var fromDate = DateOnly.FromDateTime(query.From.UtcDateTime);
        var toDate = DateOnly.FromDateTime(query.To.UtcDateTime);

        foreach (var partition in Partitions().Where(p => p.Date >= fromDate && p.Date <= toDate))
        {
            foreach (var record in ReadPartition(partition.FilePath))
            {
                if (query.Matches(record))
                {
                    yield return record;
                }
            }
        }
    }

    public IReadOnlyList<TimeSeriesPartition> Partitions()
    {
        if (!Directory.Exists(RootPath))
        {
            return [];
        }

        var list = new List<TimeSeriesPartition>();
        foreach (var file in Directory.EnumerateFiles(RootPath, "*" + Extension, SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!DateOnly.TryParseExact(name, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue; // 非分区文件（现场手工放的说明文件等）不参与治理
            }

            list.Add(new TimeSeriesPartition(date, file, new FileInfo(file).Length));
        }

        list.Sort((a, b) => a.Date.CompareTo(b.Date));
        return list;
    }

    public long EstimateBytes(DateTimeOffset from, DateTimeOffset to)
    {
        var fromDate = DateOnly.FromDateTime(from.UtcDateTime);
        var toDate = DateOnly.FromDateTime(to.UtcDateTime);
        return Partitions()
            .Where(p => p.Date >= fromDate && p.Date <= toDate)
            .Sum(p => p.Bytes);
    }

    public long Prune(DateOnly before)
    {
        long removed = 0;
        foreach (var partition in Partitions().Where(p => p.Date < before))
        {
            try
            {
                var size = partition.Bytes;
                File.Delete(partition.FilePath);
                removed += size;
            }
            catch (IOException)
            {
                // 文件被外部工具占用：保留，下次治理再删（不使整轮治理失败）
            }
        }

        return removed;
    }

    /// <summary>某个 UTC 日的分区文件路径（也用于测试与现场排查）。</summary>
    public string PathFor(DateOnly date)
        => Path.Combine(RootPath, date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + Extension);

    private static IEnumerable<TimeSeriesRecord> ReadPartition(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException)
        {
            yield break; // 写入方独占瞬间：跳过该分区，不让查询抛出
        }

        using (stream)
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false))
        {
            while (reader.ReadLine() is { } line)
            {
                var record = TimeSeriesRecord.TryParseLine(line);
                if (record is not null)
                {
                    yield return record;
                }
            }
        }
    }
}
