using AoiTwin.Core.Analytics;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Recording;

namespace AoiTwin.Core.Persistence;

/// <summary>回灌结果（TB.1）。</summary>
public sealed record RecImportResult(
    string FilePath,
    long FramesScanned,
    long Imported,
    long Skipped,
    IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// 录制文件回灌工具（TB.1）：把 <c>.aotrec</c> 转成时序记录，补齐 Sink 部署之前的历史。
///
/// 为什么必须存在：M3 的预测模型要求「提前量 ≥ 7 天」的回测证据，而 Sink 只有在 M2 窗口 B
/// 之后才开始沉淀数据。现场已有的 M1 班次录制是唯一的真实历史来源，没有回灌就只能拿 Mock 数据回测。
///
/// 与文件回放的差异：回灌是**离线批处理**（不按时间节奏推送到总线），产物是时序库中的历史分区；
/// 回灌后的历史回放走 <see cref="TimeSeriesReplayEngine"/>，两者共享同一套记录格式与解析路径。
/// </summary>
public static class RecImporter
{
    public const int DefaultBatchSize = 5000;

    /// <summary>把单个录制文件导入时序库。逐帧 CRC 由 <see cref="RecordingReader.Inspect"/> 校验，坏帧跳过并记入结果。</summary>
    public static RecImportResult Import(string filePath, ITimeSeriesStore store, int batchSize = DefaultBatchSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var batch = new List<TimeSeriesRecord>(batchSize);
        long scanned = 0;
        long skipped = 0;

        void Flush()
        {
            if (batch.Count == 0)
            {
                return;
            }

            store.Append(batch);
            batch.Clear();
        }

        var info = RecordingReader.Inspect(filePath, message =>
        {
            scanned++;
            var topic = TopicOf(message);
            if (topic is null)
            {
                skipped++;
                return;
            }

            batch.Add(TimeSeriesRecord.FromMessage(topic.Value, message));
            if (batch.Count >= batchSize)
            {
                Flush();
            }
        });

        Flush();

        return new RecImportResult(filePath, scanned, scanned - skipped, skipped, info.Errors);
    }

    /// <summary>消息类型 → Topic（回灌时 Topic 不从文件读，按消息类型唯一映射）。</summary>
    public static TwinTopic? TopicOf(ITwinMessage message) => message switch
    {
        AxisSnapshot => TwinTopic.Motion,
        StatusEvent => TwinTopic.Status,
        OpticsEvent => TwinTopic.Optics,
        WaferResult => TwinTopic.Result,
        AlertEvent => TwinTopic.Alert,
        _ => null,
    };
}
