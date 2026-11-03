using AoiTwin.Core.Bus;

namespace AoiTwin.Adapters;

/// <summary>
/// 相机 SDK 传输抽象。TODO(M1 现场, PRD 风险②)：真实 SDK（Halcon/海康/大恒）回调接线方式
/// 以相机选型为准；文档到位前以本接口 + Mock 占位，事件模型不受影响。
/// </summary>
public interface ICameraSdkTransport : IDisposable
{
    void Connect(CancellationToken cancellationToken);

    /// <summary>非阻塞取一条 SDK 事件（相机参数变更/扫描进度）；无事件返回 false。</summary>
    bool TryDequeueEvent(out RawCameraEvent raw);
}

public enum RawCameraEventKind
{
    CameraParam,
    ScanProgress,
}

/// <summary>相机 SDK 原始事件。</summary>
public sealed record RawCameraEvent(RawCameraEventKind Kind, int Channel, double Value, DateTimeOffset Timestamp);

/// <summary>
/// 相机 SDK 适配器 → Optics Topic（CameraParam/ScanProgress 两类事件直推）。
/// </summary>
public sealed class CameraSdkAdapter : AdapterBase
{
    private readonly ICameraSdkTransport _transport;
    private readonly TimeSpan _pollInterval;

    public CameraSdkAdapter(ICameraSdkTransport transport, TimeSpan? pollInterval = null)
        : base("CAMERA")
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(50);
    }

    protected override void ConnectCore(CancellationToken cancellationToken)
        => _transport.Connect(cancellationToken);

    protected override void ReceiveCore(CancellationToken cancellationToken)
    {
        while (_transport.TryDequeueEvent(out var raw))
        {
            Emit(TwinTopic.Optics, Parse(raw));
        }

        cancellationToken.WaitHandle.WaitOne(_pollInterval);
    }

    public static OpticsEvent Parse(RawCameraEvent raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return new OpticsEvent(
            "CAMERA",
            raw.Kind == RawCameraEventKind.CameraParam ? OpticsKind.CameraParam : OpticsKind.ScanProgress,
            raw.Channel,
            raw.Value,
            raw.Timestamp);
    }
}
