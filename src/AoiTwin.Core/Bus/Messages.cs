using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AoiTwin.Core.Bus;

public enum Severity
{
    Info,
    Warning,
    Alarm,
}

public enum OpticsKind
{
    LightChannel,
    LightBrightness,
    CameraParam,
    ScanProgress,
}

internal static class MessageGuard
{
    public static string RequirePartId(string? value, [CallerArgumentExpression("value")] string? param = null)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("PartId 不能为空", param)
            : value;

    public static string Require(string? value, [CallerArgumentExpression("value")] string? param = null)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("字段不能为空", param)
            : value;
}

/// <summary>轴快照：运动部件单个轴的瞬时位姿（Motion Topic）。</summary>
public sealed record AxisSnapshot(
    string PartId,
    string AxisName,
    double Position,
    double Velocity,
    DateTimeOffset Timestamp) : ITwinMessage
{
    public string PartId { get; init; } = MessageGuard.RequirePartId(PartId);
    public string AxisName { get; init; } = MessageGuard.Require(AxisName);
}

/// <summary>状态事件：装载状态、晶圆占位、报警等（Status Topic，事件直推不过滤）。</summary>
public sealed record StatusEvent(
    string PartId,
    string EventCode,
    Severity Severity,
    IReadOnlyDictionary<string, string>? Payload,
    DateTimeOffset Timestamp) : ITwinMessage
{
    /// <summary>Payload 允许传入 null，归一化为空字典（回放/反序列化容错）。</summary>
    public IReadOnlyDictionary<string, string> Payload { get; init; } =
        Payload ?? new Dictionary<string, string>();

    public string PartId { get; init; } = MessageGuard.RequirePartId(PartId);
    public string EventCode { get; init; } = MessageGuard.Require(EventCode);
}

/// <summary>
/// 光学事件：光源通道/亮度、相机参数、扫描进度（Optics Topic）。
/// 契约说明：PRD §5.2 原文为 DeviceId，与 ITwinMessage.PartId 语义重合（光学部件本身即台账部件），
/// 统一为 PartId——这是 T1.0 契约冻结时对 PRD 的一处收敛，已在冻结评审记录。
/// </summary>
public sealed record OpticsEvent(
    string PartId,
    OpticsKind Kind,
    int Channel,
    double Value,
    DateTimeOffset Timestamp) : ITwinMessage
{
    public string PartId { get; init; } = MessageGuard.RequirePartId(PartId);
}

/// <summary>缺陷记录（Result Topic 内嵌）。</summary>
public sealed record DefectRecord(
    long DefectId,
    double X,
    double Y,
    double SizeUm,
    string DefectClass,
    int GrayLevel)
{
    public string DefectClass { get; init; } = MessageGuard.Require(DefectClass);
}

/// <summary>Wafer Map 快照：DieMap 为按行存储的二值/灰度 die 图（序列化为 Base64）。</summary>
public sealed record WaferMapSnapshot(int SizeX, int SizeY, byte[] DieMap)
{
    [JsonPropertyName("sizeX")]
    public int SizeX { get; init; } = SizeX > 0
        ? SizeX
        : throw new ArgumentOutOfRangeException(nameof(SizeX), "必须为正数");

    [JsonPropertyName("sizeY")]
    public int SizeY { get; init; } = SizeY > 0
        ? SizeY
        : throw new ArgumentOutOfRangeException(nameof(SizeY), "必须为正数");

    [JsonPropertyName("dieMap")]
    public byte[] DieMap { get; init; } =
        DieMap ?? throw new ArgumentNullException(nameof(DieMap));
}

/// <summary>检测结果：整片推送（Result Topic，按片整片不过滤）。</summary>
public sealed record WaferResult(
    string WaferId,
    WaferMapSnapshot Map,
    IReadOnlyList<DefectRecord>? Defects,
    TimeSpan Tact,
    DateTimeOffset Timestamp) : ITwinMessage
{
    /// <summary>晶圆数据由 Wafer_Current 节点承载，PartId 固定（PRD §5.6 台账约定）。</summary>
    [JsonIgnore]
    public string PartId => "WAFER_CURRENT";

    /// <summary>Defects 允许传入 null，归一化为空列表。</summary>
    public IReadOnlyList<DefectRecord> Defects { get; init; } = Defects ?? [];

    public string WaferId { get; init; } = MessageGuard.Require(WaferId);
}
