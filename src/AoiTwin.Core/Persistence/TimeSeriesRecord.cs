using System.Text;
using System.Text.Json;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Recording;
using AoiTwin.Core.Serialization;

namespace AoiTwin.Core.Persistence;

/// <summary>
/// 时序记录（TB.0）：总线消息落盘后的统一形态——五张「表」共用一条记录结构，
/// 以 <see cref="TwinTopic"/> + <see cref="TypeId"/> 区分，避免为每类消息各写一套读写代码。
///
/// 为什么 Payload 保留原始 JSON 文本而不是强类型字段：
/// 1) 查询时按需反序列化（历史回灌只解析真正回放的消息），存储层不承担契约解析职责；
/// 2) 与 .aotrec 的 payload 语义完全一致（同一个 <see cref="TwinJson"/> 选项、同一个 TypeId），
///    回灌与文件回放走同一套 <see cref="RecordingReader.DeserializeFrame"/> 解析路径，不会出现两套格式解释。
/// </summary>
public sealed record TimeSeriesRecord(
    DateTimeOffset Timestamp,
    TwinTopic Topic,
    byte TypeId,
    string PartId,
    string PayloadJson)
{
    /// <summary>分区日期（UTC，天粒度）。跨天边界按 UTC 切分，避免现场时区变更导致分区错乱。</summary>
    public DateOnly PartitionDate => DateOnly.FromDateTime(Timestamp.UtcDateTime);

    /// <summary>由总线消息构造记录（TypeId 复用录制容器的登记表）。</summary>
    public static TimeSeriesRecord FromMessage(TwinTopic topic, ITwinMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var typeId = RecFileFormat.TypeIdOf(message);
        var payload = JsonSerializer.Serialize(message, message.GetType(), TwinJson.Options);
        return new TimeSeriesRecord(message.Timestamp, topic, typeId, message.PartId, payload);
    }

    /// <summary>反序列化为总线消息（与 .aotrec 帧解码同一条路径）。</summary>
    public ITwinMessage ToMessage()
        => RecordingReader.DeserializeFrame(TypeId, Encoding.UTF8.GetBytes(PayloadJson));

    /// <summary>写入 JSONL 的单行（不换行）。</summary>
    public string ToLine()
    {
        var ts = Timestamp.ToUniversalTime().ToString("O");
        return $"{{\"ts\":\"{ts}\",\"topic\":{(int)Topic},\"type\":{TypeId},\"partId\":{JsonSerializer.Serialize(PartId)},\"payload\":{PayloadJson}}}";
    }

    /// <summary>从 JSONL 单行解析；格式非法返回 null（坏行跳过，不使整个分区不可读）。</summary>
    public static TimeSeriesRecord? TryParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ts", out var tsEl)
                || !root.TryGetProperty("topic", out var topicEl)
                || !root.TryGetProperty("type", out var typeEl)
                || !root.TryGetProperty("partId", out var partEl)
                || !root.TryGetProperty("payload", out var payloadEl))
            {
                return null;
            }

            if (!DateTimeOffset.TryParse(tsEl.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            {
                return null;
            }

            var topic = (TwinTopic)topicEl.GetInt32();
            if (!Enum.IsDefined(topic))
            {
                return null;
            }

            var partId = partEl.GetString();
            if (string.IsNullOrWhiteSpace(partId))
            {
                return null;
            }

            return new TimeSeriesRecord(ts, topic, (byte)typeEl.GetInt32(), partId, payloadEl.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// 时序查询条件（TB.2/TB.3）：时间区间为半开区间 [From, To)，Topic/PartId 为空表示不限。
/// 用普通类而非 record：区间合法性在构造时一次校验，避免坏区间流到存储层才发现。
/// </summary>
public sealed class TimeSeriesQuery
{
    public TimeSeriesQuery(DateTimeOffset from, DateTimeOffset to, TwinTopic? topic = null, string? partId = null)
    {
        if (to <= from)
        {
            throw new ArgumentOutOfRangeException(nameof(to), "查询区间必须为非空（To > From）");
        }

        From = from;
        To = to;
        Topic = topic;
        PartId = partId;
    }

    public DateTimeOffset From { get; }

    public DateTimeOffset To { get; }

    public TwinTopic? Topic { get; }

    public string? PartId { get; }

    public bool Matches(TimeSeriesRecord record)
        => record.Timestamp >= From
           && record.Timestamp < To
           && (Topic is null || record.Topic == Topic)
           && (PartId is null || string.Equals(record.PartId, PartId, StringComparison.Ordinal));
}
