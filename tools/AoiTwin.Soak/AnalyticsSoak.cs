using System.Diagnostics;
using System.IO;
using System.Text;
using AoiTwin.Core.Analytics;
using AoiTwin.Core.Analytics.Export;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Persistence;

namespace AoiTwin.Soak;

/// <summary>
/// M3 分析链路离线验收（TB.18 的加速版）：
/// <c>analytics &lt;days&gt; &lt;storeRoot&gt; &lt;reportPath&gt;</c>
///
/// 做四件事：合成 N 天时序 → 经 PersistenceSink 落盘 → 每日存储治理 → 30 天窗口分析 + 评分 + 导出。
/// 它不是"真机 30 天长稳"的替代：内存与分区是有界性的结构验证，真机数据量/IO 压力仍需现场。
/// 但它是唯一能在没有设备时证明「写入 → 治理 → 回查 → 预测 → 导出」整条链路可运行的物证。
/// </summary>
internal static class AnalyticsSoak
{
    public static int Run(int days, string storeRoot, string reportPath, string? optionsPath = null)
    {
        var report = new StringBuilder();
        var started = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var optionsProvider = new AnalyticsOptionsProvider(optionsPath);
        var options = optionsProvider.Current;

        Directory.CreateDirectory(storeRoot);
        var store = new FileTimeSeriesStore(storeRoot);
        var hub = new RealtimeHub();
        using var sink = new PersistenceSink(store, hub, new PersistenceOptions(storeRoot, BatchSize: 20_000, FlushInterval: TimeSpan.FromHours(1)));
        using var aggregator = new PartHealthAggregator(hub);
        var governor = new StorageGovernor(store, new StorageGovernanceOptions(RetentionDays: 30, MinFreeBytes: 1024), _ => long.MaxValue);

        var registry = new PredictorRegistry();
        registry.RegisterRange([new LightDecayPredictor(), new AxisWearPredictor(), new TactTrendAnalyzer()]);
        var scorer = new HealthScorer();

        var start = started.AddDays(-days);
        var map = new WaferMapSnapshot(1, 1, [1]);
        var last = start;
        long prunedBytes = 0;

        for (var day = 0; day < days; day++)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                var ts = start.AddDays(day).AddHours(hour);
                last = ts;

                hub.Publish(TwinTopic.Optics, new OpticsEvent("LIGHT_RING", OpticsKind.LightBrightness, 0, 100 - (day * 0.6), ts));
                hub.Publish(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 5000 + ((hour % 3) - 1), 0, ts));
                hub.Publish(TwinTopic.Result, new WaferResult($"ST01-{day:D2}{hour:D2}", map, null, TimeSpan.FromSeconds(6 + (day * 0.02)), ts));
            }

            sink.Flush();
            prunedBytes += governor.RunOnce(last.AddHours(1)).DeletedBytes;
        }

        var profiles = aggregator.Snapshot();
        var context = new AnalyticsContext(last, profiles, options, store, windowOverride: TimeSpan.FromDays(days));
        var runs = registry.RunAll(context);
        var findings = runs.SelectMany(r => r.Findings).ToList();
        var scores = scorer.ScoreAll(profiles, findings);

        var export = new AnalyticsExportService(new ExportOptions(DeviceId: "AOI-01"));
        var csvPath = Path.ChangeExtension(reportPath, ".health.csv");
        var alertPath = Path.ChangeExtension(reportPath, ".alerts.json");
        var healthResult = export.ExportHealth(scores, csvPath, ExportFormat.Csv);
        var alertResult = export.ExportAlerts(
            findings.Select(f => new AlertEvent(f.PartId, f.Kind, f.Severity, f.Score, f.Summary, f.Factors, f.HorizonDays, f.Confidence, last)).ToList(),
            alertPath,
            ExportFormat.Json);

        sw.Stop();

        report.AppendLine("mode=analytics");
        report.AppendLine($"started={started:yyyy-MM-dd HH:mm:ss}Z");
        report.AppendLine($"days={days}");
        report.AppendLine($"storeRoot={Path.GetFullPath(storeRoot)}");
        report.AppendLine($"optionsSource={optionsProvider.Source ?? "(code defaults)"}");
        report.AppendLine($"optionsError={optionsProvider.LastError ?? "(none)"}");
        // 关键阈值随报告落盘：同一份数据换阈值结论会变，证据必须自带口径
        report.AppendLine(
            $"options.light=rated={options.LightRatedValue}|warn={options.LightWarningFraction}|horizon={options.LightWarningHorizonDays}d|min={options.LightMinSamples}");
        report.AppendLine(
            $"options.axis=sigma={options.AxisWearSigmaThreshold}|drift={options.AxisDriftUmPerDay}um/d|hold={options.AxisHoldVelocityUmPerSec}um/s");
        report.AppendLine($"options.tact={options.TactDegradationPercent}%");
        report.AppendLine($"recordsPublished={sink.Stats.Enqueued}");
        report.AppendLine($"recordsWritten={sink.Stats.Written}");
        report.AppendLine($"sinkHealthy={sink.Stats.Healthy}");
        report.AppendLine($"sinkLastError={sink.Stats.LastError ?? "(none)"}");
        report.AppendLine($"partitions={store.Partitions().Count}");
        report.AppendLine($"storeBytes={store.EstimateBytes(start.AddDays(-1), last.AddDays(2))}");
        report.AppendLine($"prunedBytes={prunedBytes}");
        report.AppendLine($"profiles={profiles.Count}");
        report.AppendLine($"healthScored={scores.Count}");

        foreach (var score in scores.Take(5))
        {
            report.AppendLine($"health.{score.PartId}={score.Score:F1}/{score.Grade}");
        }

        foreach (var run in runs)
        {
            report.AppendLine($"predictor.{run.PredictorName}.findings={run.Findings.Count}");
            report.AppendLine($"predictor.{run.PredictorName}.error={run.Error ?? "(none)"}");
        }

        foreach (var finding in findings.Take(10))
        {
            report.AppendLine($"finding.{finding.Kind}.{finding.PartId}={finding.Severity}|score={finding.Score:F1}|horizon={finding.HorizonDays?.ToString() ?? "-"}|{finding.Summary}");
        }

        report.AppendLine($"export.health={healthResult.FilePath} rows={healthResult.Rows} bytes={healthResult.Bytes}");
        report.AppendLine($"export.alerts={alertResult.FilePath} rows={alertResult.Rows} bytes={alertResult.Bytes}");
        report.AppendLine($"elapsedMs={sw.Elapsed.TotalMilliseconds:F0}");

        var partitionsOk = store.Partitions().Count <= 31;
        var analysisOk = runs.All(r => !r.Failed);
        var sinkOk = sink.Stats.Healthy;
        report.AppendLine($"partitionsOk={partitionsOk}");
        report.AppendLine($"analysisOk={analysisOk}");
        report.AppendLine($"result={(partitionsOk && analysisOk && sinkOk ? "PASS" : "FAIL")}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        Console.WriteLine(report.ToString());
        Console.WriteLine($"报告已写入 {reportPath}");
        return partitionsOk && analysisOk && sinkOk ? 0 : 1;
    }
}
