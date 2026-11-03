using AoiTwin.Core.Bus;

namespace AoiTwin.Adapters;

/// <summary>
/// 光源控制器传输抽象。TODO(M1 现场, PRD 风险②)：RS-485/网口协议按光源供应商文档补齐；
/// 文档到位前以本接口 + Mock 占位。
/// </summary>
public interface ILightTransport : IDisposable
{
    void Connect(CancellationToken cancellationToken);

    /// <summary>读取通道亮度（0–100%）；通道关闭返回 0。</summary>
    int ReadChannelBrightness(int channel, CancellationToken cancellationToken);

    int ChannelCount { get; }
}

/// <summary>
/// 光源适配器 → Optics Topic：轮询 8 通道亮度（LightRing 8CH，PRD §5.6 台账）。
/// Value 语义冻结：亮度百分比 0–100（不做归一化，渲染层自行映射）。
/// </summary>
public sealed class LightSourceAdapter : AdapterBase
{
    private readonly ILightTransport _transport;
    private readonly TimeSpan _pollInterval;

    public LightSourceAdapter(ILightTransport transport, TimeSpan? pollInterval = null)
        : base("LIGHT")
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    }

    protected override void ConnectCore(CancellationToken cancellationToken)
        => _transport.Connect(cancellationToken);

    protected override void ReceiveCore(CancellationToken cancellationToken)
    {
        for (var ch = 1; ch <= _transport.ChannelCount; ch++)
        {
            var brightness = _transport.ReadChannelBrightness(ch, cancellationToken);
            Emit(TwinTopic.Optics, new OpticsEvent("LIGHT_RING", OpticsKind.LightBrightness, ch, brightness, DateTimeOffset.UtcNow));
        }

        cancellationToken.WaitHandle.WaitOne(_pollInterval);
    }
}
