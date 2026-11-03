namespace AoiTwin.Core.Bus;

/// <summary>
/// 统一 Topic 分类（PRD-AOI-TWIN-M1 §5.1）。
/// 契约冻结（T1.0）：M2/M3 只允许追加枚举值（M3 追加 Alert），不得修改或重命名既有值。
/// </summary>
public enum TwinTopic
{
    Motion,
    Status,
    Optics,
    Result,
}
