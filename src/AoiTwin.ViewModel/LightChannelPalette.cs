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

    public LightChannelPalette(IReadOnlyDictionary<int, RgbColor> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        Channels = channels.ToImmutableDictionary();
    }

    /// <summary>通道号 → 颜色。</summary>
    public ImmutableDictionary<int, RgbColor> Channels { get; }

    public bool TryGetColor(int channel, out RgbColor color) => Channels.TryGetValue(channel, out color);

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
