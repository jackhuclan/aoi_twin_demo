using AoiTwin.Core.Bus;

namespace AoiTwin.Adapters;

/// <summary>
/// PLC 传输抽象。TODO(M1 现场, PRD 风险②)：Modbus TCP 寄存器地址表以现场协议文档为准，
/// 文档到位前以本接口 + Mock 实现（AoiTwin.Adapters.Mock）占位，解析/换算逻辑不受影响。
/// </summary>
public interface IPlcTransport : IDisposable
{
    void Connect(CancellationToken cancellationToken);

    /// <summary>读取保持寄存器块（16bit 字），按地址升序返回。</summary>
    short[] ReadBlock(int startAddress, int length, CancellationToken cancellationToken);
}

/// <summary>PLC 轴寄存器映射：一个运动轴占 4 个寄存器（位置 i32 + 速度 i32，原始值 = µm）。</summary>
public sealed record PlcAxisMapping(string PartId, string AxisName, int Address);

/// <summary>
/// PLC 适配器 → Motion Topic：轮询轴寄存器块，原始 µm → mm 单位换算（PRD §4.2 单位换算约定）。
/// </summary>
public sealed class PlcAdapter : AdapterBase
{
    private readonly IPlcTransport _transport;
    private readonly IReadOnlyList<PlcAxisMapping> _axes;
    private readonly TimeSpan _pollInterval;

    public PlcAdapter(IPlcTransport transport, IReadOnlyList<PlcAxisMapping> axes, TimeSpan? pollInterval = null)
        : base("PLC")
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _axes = axes is { Count: > 0 } ? axes : throw new ArgumentException("轴映射不能为空", nameof(axes));
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200); // 与 M2 轴节流基线一致
    }

    protected override void ConnectCore(CancellationToken cancellationToken)
        => _transport.Connect(cancellationToken);

    protected override void ReceiveCore(CancellationToken cancellationToken)
    {
        foreach (var axis in _axes)
        {
            var block = _transport.ReadBlock(axis.Address, 4, cancellationToken);
            Emit(TwinTopic.Motion, ParseAxis(axis, block, DateTimeOffset.UtcNow));
        }

        cancellationToken.WaitHandle.WaitOne(_pollInterval);
    }

    /// <summary>原始寄存器 → 轴快照：i32 µm → double mm（换算规则冻结，Mock/现场共用）。</summary>
    public static AxisSnapshot ParseAxis(PlcAxisMapping mapping, short[] block, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.Length < 4)
        {
            throw new ArgumentException("轴寄存器块必须为 4 个字", nameof(block));
        }

        var positionRaw = ((int)block[0] << 16) | (ushort)block[1];
        var velocityRaw = ((int)block[2] << 16) | (ushort)block[3];
        return new AxisSnapshot(
            mapping.PartId,
            mapping.AxisName,
            positionRaw / 1000.0,
            velocityRaw / 1000.0,
            timestamp);
    }
}
