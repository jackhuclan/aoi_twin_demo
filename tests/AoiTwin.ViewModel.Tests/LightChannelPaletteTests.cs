using System.IO;
using System.Text;
using AoiTwin.ViewModel;
using Xunit;

namespace AoiTwin.ViewModel.Tests;

public sealed class LightChannelPaletteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-palette-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public LightChannelPaletteTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "light-channel-colors.json");
    }

    [Fact]
    public void Default_palette_matches_prd_5_5()
    {
        // PRD §5.5 冻结的八色，逐通道核对
        Assert.Equal(8, LightChannelPalette.Default.Count);
        Assert.Equal(new RgbColor(0xFF, 0xE9, 0xC4), LightChannelPalette.Default[1]);
        Assert.Equal(new RgbColor(0xC4, 0xE9, 0xFF), LightChannelPalette.Default[2]);
        Assert.Equal(new RgbColor(0xE9, 0xC4, 0xFF), LightChannelPalette.Default[3]);
        Assert.Equal(new RgbColor(0xC4, 0xFF, 0xE1), LightChannelPalette.Default[4]);
        Assert.Equal(new RgbColor(0xFF, 0xC4, 0xC4), LightChannelPalette.Default[5]);
        Assert.Equal(new RgbColor(0xF5, 0xF5, 0xC4), LightChannelPalette.Default[6]);
        Assert.Equal(new RgbColor(0xC4, 0xD6, 0xFF), LightChannelPalette.Default[7]);
        Assert.Equal(new RgbColor(0xD6, 0xFF, 0xC4), LightChannelPalette.Default[8]);
    }

    [Fact]
    public void Loads_channels_from_file()
    {
        File.WriteAllText(_path, """
            { "lightChannelColors": { "1": "#112233", "4": "#445566" } }
            """, Encoding.UTF8);

        using var source = new LightChannelPaletteFileSource(_path);

        Assert.Null(source.LastError);
        Assert.Equal(2, source.Channels.Count);
        Assert.Equal(new RgbColor(0x11, 0x22, 0x33), source.Channels[1]);
        Assert.Equal(new RgbColor(0x44, 0x55, 0x66), source.Channels[4]);
    }

    [Fact]
    public void Missing_file_falls_back_to_default_without_error()
    {
        using var source = new LightChannelPaletteFileSource(_path); // 文件不存在

        Assert.Null(source.LastError);
        Assert.Equal(LightChannelPalette.Default, source.Channels);
    }

    [Fact]
    public void Loads_defect_and_state_colors_with_m3_reserved_keys()
    {
        File.WriteAllText(_path, """
            {
              "lightChannelColors": { "1": "#112233" },
              "defectClassColors": { "Scratch": "#AABBCC" },
              "stateColors": { "Run": "#00FF00", "Warning": "#FFAA00", "Critical": "#AA0000" }
            }
            """, Encoding.UTF8);

        using var source = new LightChannelPaletteFileSource(_path);
        var palette = source.Palette;

        Assert.Null(source.LastError);
        Assert.Equal(new RgbColor(0xAA, 0xBB, 0xCC), palette.DefectClassColors["Scratch"]);
        // 未配置的缺陷等级不凭空补默认值：调用方按需回落（渲染层再兜中性灰）
        Assert.False(palette.TryGetDefectColor("Particle", out _));
        Assert.Equal(new RgbColor(0xFF, 0xAA, 0x00), palette.StateColors["Warning"]);
        Assert.Equal(new RgbColor(0xAA, 0x00, 0x00), palette.StateColors["Critical"]); // M3 报警分级预留键
    }

    [Fact]
    public void Omitted_optional_tables_fall_back_to_prd_defaults()
    {
        File.WriteAllText(_path, """
            { "lightChannelColors": { "1": "#112233" } }
            """, Encoding.UTF8);

        using var source = new LightChannelPaletteFileSource(_path);

        // 只写了通道色也要能跑：缺陷色与状态色取 PRD §5.5 默认
        Assert.Equal(
            new RgbColor(0xFF, 0x6B, 0x81),
            source.Palette.DefectClassColors["Scratch"]);
        Assert.Equal(
            new RgbColor(0xFF, 0x47, 0x57),
            source.Palette.StateColors["Alarm"]);
    }

    [Fact]
    public void Default_state_colors_cover_m3_alarm_grades()
    {
        // 键必须存在：M3 加报警分级时不应改动 schema（契约冻结项）
        Assert.True(LightChannelPalette.DefaultStateColors.ContainsKey("Warning"));
        Assert.True(LightChannelPalette.DefaultStateColors.ContainsKey("Critical"));
        Assert.True(LightChannelPalette.DefaultStateColors.ContainsKey("Run"));
        Assert.True(LightChannelPalette.DefaultStateColors.ContainsKey("Idle"));
        Assert.True(LightChannelPalette.DefaultStateColors.ContainsKey("Alarm"));
    }

    [Fact]
    public void Hot_reload_picks_up_new_values()
    {
        File.WriteAllText(_path, """{ "lightChannelColors": { "1": "#112233" } }""", Encoding.UTF8);
        using var source = new LightChannelPaletteFileSource(_path);
        Assert.Equal(new RgbColor(0x11, 0x22, 0x33), source.Channels[1]);

        // 编辑器原子替换后重读：新值生效（NFR-18 免编译可配）
        File.WriteAllText(_path, """{ "lightChannelColors": { "1": "#AABBCC", "2": "#DDEEFF" } }""", Encoding.UTF8);
        source.Reload();

        Assert.Equal(new RgbColor(0xAA, 0xBB, 0xCC), source.Channels[1]);
        Assert.Equal(new RgbColor(0xDD, 0xEE, 0xFF), source.Channels[2]);
    }

    [Fact]
    public void Corrupt_config_keeps_old_palette_and_records_error()
    {
        File.WriteAllText(_path, """{ "lightChannelColors": { "1": "#112233" } }""", Encoding.UTF8);
        using var source = new LightChannelPaletteFileSource(_path);

        File.WriteAllText(_path, "{ not valid json", Encoding.UTF8);
        source.Reload();

        Assert.NotNull(source.LastError);
        Assert.Equal(new RgbColor(0x11, 0x22, 0x33), source.Channels[1]); // 坏配置保旧值
    }

    [Fact]
    public void ParseHex_rejects_malformed_input()
    {
        Assert.Throws<FormatException>(() => LightChannelPalette.ParseHex("#12345"));
        Assert.Throws<FormatException>(() => LightChannelPalette.ParseHex("zz1234"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
