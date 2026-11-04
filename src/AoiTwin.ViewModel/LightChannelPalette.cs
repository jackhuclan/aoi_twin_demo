using System.Collections.Immutable;
using System.Globalization;

namespace AoiTwin.ViewModel;

/// <summary>线性 RGB 颜色（字节通道，纯数据层不依赖 WindowsBase）。</summary>
public readonly record struct RgbColor(byte R, byte G, byte B);

/// <summary>
/// 光源通道 → 颜色映射表（FR-M2-04 / PRD §5.5，随配方发布，NFR-18 免编译可配）。
/// 键为 1 起始的通道号；未配置的通道由渲染层降级为中性色。
/// </summary>
public sealed class LightChannelPalette
{
    /// <summary>PRD §5.5 的默认八色（无配置文件时的兜底，保证首次运行即有完整视觉）。</summary>
    public static readonly ImmutableDictionary<int, RgbColor> Default = new Dictionary<int, RgbColor>
    {
        [1] = new(0xFF, 0xE9, 0xC4),
        [2] = new(0xC4, 0xE9, 0xFF),
        [3] = new(0xE9, 0xC4, 0xFF),
        [4] = new(0xC4, 0xFF, 0xE1),
        [5] = new(0xFF, 0xC4, 0xC4),
        [6] = new(0xF5, 0xF5, 0xC4),
        [7] = new(0xC4, 0xD6, 0xFF),
        [8] = new(0xD6, 0xFF, 0xC4),
    }.ToImmutableDictionary();

    /// <summary>PRD §5.5 的默认缺陷等级配色（未配置时的兜底）。</summary>
    public static readonly ImmutableDictionary<string, RgbColor> DefaultDefectClassColors =
        new Dictionary<string, RgbColor>(StringComparer.Ordinal)
        {
            ["Scratch"] = new(0xFF, 0x6B, 0x81),
            ["Particle"] = new(0xFF, 0x9F, 0x43),
            ["Residue"] = new(0xC5, 0x6C, 0xF0),
        }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>
    /// PRD §5.5 的默认状态色。M2 只用 Run/Idle/Alarm；<c>Warning</c>/<c>Critical</c>
    /// 为 M3 报警分级**预留占位**（键已存在，M3 直接取值即可，不需要改 schema）。
    /// </summary>
    public static readonly ImmutableDictionary<string, RgbColor> DefaultStateColors =
        new Dictionary<string, RgbColor>(StringComparer.OrdinalIgnoreCase)
        {
            ["Run"] = new(0x2E, 0xD5, 0x73),
            ["Idle"] = new(0x83, 0x95, 0xA7),
            ["Alarm"] = new(0xFF, 0x47, 0x57),
            ["Warning"] = new(0xF5, 0xA6, 0x23),
            ["Critical"] = new(0xC0, 0x39, 0x2B),
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public LightChannelPalette(
        IReadOnlyDictionary<int, RgbColor> channels,
        IReadOnlyDictionary<string, RgbColor>? defectClassColors = null,
        IReadOnlyDictionary<string, RgbColor>? stateColors = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        Channels = channels.ToImmutableDictionary();
        DefectClassColors = defectClassColors is null
            ? DefaultDefectClassColors
            : defectClassColors.ToImmutableDictionary(StringComparer.Ordinal);
        StateColors = stateColors is null
            ? DefaultStateColors
            : stateColors.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>通道号 → 颜色。</summary>
    public ImmutableDictionary<int, RgbColor> Channels { get; }

    /// <summary>缺陷等级 → 颜色（PRD §5.5 <c>defectClassColors</c>，NFR-18 免编译可配）。</summary>
    public ImmutableDictionary<string, RgbColor> DefectClassColors { get; }

    /// <summary>部件状态 → 颜色（PRD §5.5 <c>stateColors</c>；含 M3 预留的 Warning/Critical）。</summary>
    public ImmutableDictionary<string, RgbColor> StateColors { get; }

    public bool TryGetColor(int channel, out RgbColor color) => Channels.TryGetValue(channel, out color);

    /// <summary>取缺陷等级配色；未配置的等级返回 false（渲染层回落中性灰）。</summary>
    public bool TryGetDefectColor(string defectClass, out RgbColor color) =>
        DefectClassColors.TryGetValue(defectClass, out color);

    /// <summary>取状态色；未配置的状态返回 false。</summary>
    public bool TryGetStateColor(string state, out RgbColor color) =>
        StateColors.TryGetValue(state, out color);

    /// <summary>解析 "#RRGGBB"。</summary>
    public static RgbColor ParseHex(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        var span = hex.StartsWith('#') ? hex[1..] : hex;
        if (span.Length != 6)
        {
            throw new FormatException($"颜色须为 #RRGGBB：{hex}");
        }

        return new RgbColor(
            byte.Parse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(span[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
