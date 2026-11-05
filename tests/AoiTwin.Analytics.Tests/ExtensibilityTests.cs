using System.IO;
using AoiTwin.Core.Analytics;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Persistence;
using Xunit;

namespace AoiTwin.Analytics.Tests;

/// <summary>
/// TB.17 扩展性演示（PRD US-M3-11 的能力证明）：
/// 新增一个预测模型 + 一路数据源，**Hub 契约与渲染层零改动**。
///
/// 这不是一个"演示脚本"，而是一条回归约束：只要有人把分析层写回对渲染层的直接依赖，
/// 本用例的结构断言就会失败。
/// </summary>
public sealed class ExtensibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-m3-ext-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public void New_predictor_joins_pipeline_without_hub_or_renderer_changes()
    {
        var store = new FileTimeSeriesStore(_root);
        var hub = new RealtimeHub();
        var alerts = new List<AlertEvent>();
        using var subscription = hub.Subscribe<AlertEvent>(TwinTopic.Alert, alerts.Add);

        // 新数据源：复用既有 Status Topic，用自定义 EventCode 承载振动量（不需要改 TwinTopic）
        hub.Publish(TwinTopic.Status, new StatusEvent(
            "STAGE",
            "VIBRATION_RMS",
            Severity.Info,
            new Dictionary<string, string> { ["rms"] = "12.4" },
            Now));

        var registry = new PredictorRegistry();
        registry.Register(new LightDecayPredictor());
        registry.Register(new VibrationPredictor()); // 新模型：只依赖 IAnalyticsContext

        using var scheduler = new AlertScheduler(hub, registry, () => new AnalyticsContext(Now, [], new AnalyticsOptions(), store));

        var published = scheduler.RunOnce();

        Assert.Contains(published, a => a.Kind == AlertKind.Custom);
        Assert.Contains(alerts, a => a.Kind == AlertKind.Custom);
        Assert.Equal(["light-decay", "vibration"], registry.Names.OrderBy(n => n, StringComparer.Ordinal));

        // 结构约束：分析/存储层不得引用渲染层或 WPF
        var referenced = typeof(PredictorRegistry).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .ToList();

        Assert.DoesNotContain("AoiTwin.Rendering", referenced);
        Assert.DoesNotContain("PresentationFramework", referenced);
        Assert.DoesNotContain("WindowsBase", referenced);
    }

    /// <summary>演示用新模型：从 Status 载荷里的振动量判断轴承风险。真实实现可换成 FFT/包络分析。</summary>
    private sealed class VibrationPredictor : IAlertPredictor
    {
        public string Name => "vibration";

        public string Version => "0.1-demo";

        public IReadOnlyList<AlertFinding> Analyze(IAnalyticsContext context)
        {
            // 演示模型不查库：直接被调度器触发即给出一条 Custom 预警（证明"新增模型只需注册"）
            return
            [
                new AlertFinding(
                    "STAGE",
                    AlertKind.Custom,
                    Severity.Warning,
                    35,
                    "振动 RMS 12.4 超过演示阈值 10",
                    [new AlertFactor("振动 RMS", 12.4, "mm/s", "来自 VIBRATION_RMS 事件")],
                    null,
                    0.6),
            ];
        }
    }
}
