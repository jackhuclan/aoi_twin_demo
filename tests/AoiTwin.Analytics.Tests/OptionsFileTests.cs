using System.IO;
using AoiTwin.Core.Analytics;
using Xunit;

namespace AoiTwin.Analytics.Tests;

/// <summary>
/// 分析参数文件源（FR-M3-05 现场调参）：
/// 现场只写想改的字段、写错一处不该让整份生效、文件缺失不该让分析停摆。
/// </summary>
public sealed class OptionsFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-m3-opt-" + Guid.NewGuid().ToString("N")[..8]);

    public OptionsFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private string WriteFile(string json)
    {
        var path = Path.Combine(_root, "analytics-options.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Partial_document_keeps_defaults_for_unmentioned_fields()
    {
        var path = WriteFile("""
            {
              "light": { "warningFraction": 0.75 },
              "healthWeights": { "alarmPenalty": 40 }
            }
            """);

        Assert.True(AnalyticsOptionsFile.TryLoad(path, out var options, out var error), error);

        Assert.Equal(0.75, options.LightWarningFraction);
        Assert.Equal(40, options.Weights.AlarmPenalty);

        // 未提到的字段必须是代码默认值，而不是 0（0 会让预测静默失效）
        Assert.Equal(new AnalyticsOptions().LightCriticalFraction, options.LightCriticalFraction);
        Assert.Equal(new AnalyticsOptions().LightMinSamples, options.LightMinSamples);
        Assert.Equal(new AnalyticsOptions().TactDegradationPercent, options.TactDegradationPercent);
        Assert.Equal(TimeSpan.FromDays(30), options.Window);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        // 现场手改的常见形态：加一行注释、多一个逗号
        var path = WriteFile("""
            {
              // 光源额定值按标称填
              "light": { "ratedValue": 100, },
            }
            """);

        Assert.True(AnalyticsOptionsFile.TryLoad(path, out var options, out var error), error);
        Assert.Equal(100, options.LightRatedValue);
    }

    [Fact]
    public void Invalid_cross_field_combination_rejects_whole_file()
    {
        // 严重线高于预警线 —— 半份配置比默认值更危险，必须整份拒绝
        var path = WriteFile("""
            { "light": { "warningFraction": 0.5, "criticalFraction": 0.9 } }
            """);

        Assert.False(AnalyticsOptionsFile.TryLoad(path, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains("criticalFraction", error);
    }

    [Fact]
    public void Provider_keeps_last_good_options_when_file_breaks()
    {
        var path = WriteFile("""
            { "light": { "warningFraction": 0.7 } }
            """);

        var provider = new AnalyticsOptionsProvider(path);
        Assert.Equal(0.7, provider.Current.LightWarningFraction);
        Assert.Null(provider.LastError);

        File.WriteAllText(path, "{ 这不是 JSON");
        Assert.False(provider.Reload());
        Assert.NotNull(provider.LastError);

        // 分析层继续用上一份可用配置，而不是停摆或退回默认
        Assert.Equal(0.7, provider.Current.LightWarningFraction);
    }

    [Fact]
    public void Provider_without_path_uses_code_defaults()
    {
        var provider = new AnalyticsOptionsProvider();
        Assert.Null(provider.Source);
        Assert.Equal(new AnalyticsOptions().LightRatedValue, provider.Current.LightRatedValue);
        Assert.False(provider.Reload());
    }

    [Fact]
    public void Shipped_asset_file_is_loadable()
    {
        // 仓库里的 assets/analytics-options.json 必须能被自己的读取器吃下（否则现场照抄必错）
        var assetsPath = FindAsset("assets/analytics-options.json");
        Assert.NotNull(assetsPath);

        Assert.True(AnalyticsOptionsFile.TryLoad(assetsPath!, out var options, out var error), error);
        Assert.Equal(7, options.LightWarningHorizonDays);
        Assert.Equal(20, options.AxisMinSamples);
        Assert.Equal(TimeSpan.FromDays(30), options.Window);
    }

    private static string? FindAsset(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
