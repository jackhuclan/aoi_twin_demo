using AoiTwin.Core.Bus;

namespace AoiTwin.Core.Analytics;

/// <summary>预警种类（TB.5）。<see cref="Custom"/> 供插件新增模型使用，无需改枚举。</summary>
public enum AlertKind
{
    /// <summary>健康度评分跌入预警区间。</summary>
    HealthScore = 0,

    /// <summary>光源衰减预测（TB.7）。</summary>
    LightDecay = 1,

    /// <summary>轴磨损预警（TB.8）。</summary>
    AxisWear = 2,

    /// <summary>节拍劣化（TB.9）。</summary>
    TactDegradation = 3,

    /// <summary>插件自定义（TB.17 零改动扩展演示用）。</summary>
    Custom = 99,
}

/// <summary>
/// 预警证据因子（TB.6/TB.10）：每条预警都必须带「为什么」。
/// 这是 PRD 对「预测误报引发产线不信任」的核心对策——操作员看到黄色/红色时，
/// 能立刻看到是哪个量、超了多少、置信度多少，而不是一个无法解释的分数。
/// </summary>
public sealed record AlertFactor(string Name, double Value, string Unit, string? Note = null);

/// <summary>
/// 预警事件（TB.5/TB.10）：作为 <see cref="TwinTopic.Alert"/> 消息回流总线，
/// 与 PLC 报警走同一条事件通道；渲染层把它当作普通状态事件消费，
/// 分析与渲染之间因此没有直接引用（M2 冻结契约只追加，不改渲染主循环）。
/// </summary>
public sealed record AlertEvent(
    string PartId,
    AlertKind Kind,
    Severity Severity,
    double Score,
    string Summary,
    IReadOnlyList<AlertFactor>? Factors,
    int? HorizonDays,
    double? Confidence,
    DateTimeOffset Timestamp) : ITwinMessage
{
    public string PartId { get; init; } = string.IsNullOrWhiteSpace(PartId)
        ? throw new ArgumentException("PartId 不能为空", nameof(PartId))
        : PartId;

    public string Summary { get; init; } = string.IsNullOrWhiteSpace(Summary)
        ? throw new ArgumentException("Summary 不能为空", nameof(Summary))
        : Summary;

    public IReadOnlyList<AlertFactor> Factors { get; init; } = Factors ?? [];

    /// <summary>
    /// 同一部件同一类预警的稳定键（去重与状态升级用）。
    /// 注意：不含时间戳——否则调度器每 5 分钟都会被视为「新预警」而重复推 3D 提示。
    /// </summary>
    public string DedupeKey => $"{PartId}|{Kind}";
}

/// <summary>预测器输出（TB.5）：与 <see cref="AlertEvent"/> 同构但不含时间戳，由调度器统一打时间。</summary>
public sealed record AlertFinding(
    string PartId,
    AlertKind Kind,
    Severity Severity,
    double Score,
    string Summary,
    IReadOnlyList<AlertFactor> Factors,
    int? HorizonDays = null,
    double? Confidence = null);
