using AoiTwin.Core.Bus;

namespace AoiTwin.Adapters;

/// <summary>
/// 结果队列传输抽象：AOI 判定结果由产线系统写入队列（MES/共享目录/消息队列，按现场方案定）。
/// TODO(M1 现场, PRD 风险②)：接入介质未定，文档到位前以本接口 + Mock 占位。
/// </summary>
public interface IResultQueueTransport : IDisposable
{
    void Connect(CancellationToken cancellationToken);

    /// <summary>非阻塞取一条判定结果；队列空返回 false。</summary>
    bool TryTake(out ResultEnvelope envelope);
}

/// <summary>判定结果原始封装：像素坐标/灰度由本适配器换算为物理单位。</summary>
public sealed record ResultEnvelope(
    string WaferId,
    int SizeX,
    int SizeY,
    byte[] DieMap,
    IReadOnlyList<RawDefect> Defects,
    long TactMs,
    DateTimeOffset CapturedAt);

/// <summary>原始缺陷记录（像素单位）。</summary>
public sealed record RawDefect(long DefectId, int Px, int Py, double SizePx, string DefectClass, int GrayLevel);

/// <summary>
/// 结果队列适配器 → Result Topic：像素 → µm（按标定 PixelSizeUm），节拍 ms → TimeSpan。
/// 换算规则冻结：X = Px × PixelSizeUm，Wafer Map DieMap 原样透传（裸字节，见 PRD §5.5）。
/// </summary>
public sealed class ResultQueueAdapter : AdapterBase
{
    private readonly IResultQueueTransport _transport;
    private readonly double _pixelSizeUm;
    private readonly TimeSpan _pollInterval;

    public ResultQueueAdapter(IResultQueueTransport transport, double pixelSizeUm, TimeSpan? pollInterval = null)
        : base("RESULT_QUEUE")
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (pixelSizeUm is <= 0 or double.NaN)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelSizeUm), "标定像素尺寸必须为正数");
        }

        _pixelSizeUm = pixelSizeUm;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
    }

    protected override void ConnectCore(CancellationToken cancellationToken)
        => _transport.Connect(cancellationToken);

    protected override void ReceiveCore(CancellationToken cancellationToken)
    {
        while (_transport.TryTake(out var envelope))
        {
            Emit(TwinTopic.Result, Parse(envelope, _pixelSizeUm));
        }

        cancellationToken.WaitHandle.WaitOne(_pollInterval);
    }

    public static WaferResult Parse(ResultEnvelope envelope, double pixelSizeUm)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var defects = envelope.Defects
            .Select(d => new DefectRecord(
                d.DefectId,
                d.Px * pixelSizeUm,
                d.Py * pixelSizeUm,
                d.SizePx * pixelSizeUm,
                d.DefectClass,
                d.GrayLevel))
            .ToList();

        return new WaferResult(
            envelope.WaferId,
            new WaferMapSnapshot(envelope.SizeX, envelope.SizeY, envelope.DieMap),
            defects,
            TimeSpan.FromMilliseconds(envelope.TactMs),
            envelope.CapturedAt);
    }
}
