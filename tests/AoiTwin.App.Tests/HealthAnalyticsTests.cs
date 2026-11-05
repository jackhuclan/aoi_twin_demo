using System.IO;
using System.Threading;
using AoiTwin.App.Services;
using AoiTwin.App.ViewModels;
using AoiTwin.Core.Analytics;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Persistence;
using Xunit;

namespace AoiTwin.App.Tests;

/// <summary>
/// 健康分析页（M3 的界面出口）：只验证「接线是否正确、降级是否可见」——
/// 预测算法的正确性由 <c>AoiTwin.Analytics.Tests</c> 覆盖，此处不重复。
///
/// 三条硬要求：
/// 1) 存储写失败必须在页面上变成一条可见提示，且**不抛到 UI**（NFR-22）；
/// 2) 参数文件坏掉必须沿用上一份可用配置并说明原因（不能悄悄用默认值算出一个数）；
/// 3) 刷新不能重建行对象，否则操作员点开的因子明细每秒被顶掉。
/// </summary>
public sealed class HealthAnalyticsTests
{
    [Fact]
    public void Store_write_failure_shows_note_without_crashing_page()
    {
        RunOnStaThread(() =>
        {
            var dir = NewTempDir();
            try
            {
                var hub = new RealtimeHub();
                using var runtime = new AnalyticsRuntime(
                    hub,
                    new AnalyticsRuntimeOptions(Path.Combine(dir, "store"), Path.Combine(dir, "exports"), AutoStart: false),
                    new ThrowingStore(Path.Combine(dir, "store")));
                using var health = new HealthAnalyticsViewModel(runtime);

                hub.Publish(TwinTopic.Motion, new AxisSnapshot("STAGE", "X", 1_000, 0, DateTimeOffset.UtcNow));
                Assert.True(runtime.Flush() >= 0); // 写失败只计数，不抛

                health.Refresh();
                Assert.True(health.HasNote, "落盘失败未在页面提示");
                Assert.True(health.IsNoteError, "落盘失败应按错误级提示（红）");
                Assert.Contains("落盘降级", health.NoteText);

                // 实时链路仍然有档案：分析层故障不影响采集与评分
                Assert.Contains(health.Parts, p => p.PartId == "STAGE");
            }
            finally
            {
                Delete(dir);
            }
        });
    }

    [Fact]
    public void Invalid_options_file_keeps_defaults_and_explains_why()
    {
        RunOnStaThread(() =>
        {
            var dir = NewTempDir();
            try
            {
                var bad = Path.Combine(dir, "analytics-options.json");
                File.WriteAllText(bad, "{ \"light\": { \"warningFraction\": 0.9 }, \"windowDays\": \"三十天\" }");

                using var runtime = new AnalyticsRuntime(
                    new RealtimeHub(),
                    new AnalyticsRuntimeOptions(
                        Path.Combine(dir, "store"),
                        Path.Combine(dir, "exports"),
                        OptionsFilePath: bad,
                        AutoStart: false));
                using var health = new HealthAnalyticsViewModel(runtime);

                health.Refresh();
                Assert.True(health.HasNote);
                Assert.Contains("分析参数文件", health.NoteText);

                // 参数没生效但页面仍可用：口径按默认值展示（30 天窗口）
                Assert.Contains("窗口 30 天", health.CaliberText);
                Assert.Equal("参数文件：analytics-options.json", health.OptionsSourceText);
            }
            finally
            {
                Delete(dir);
            }
        });
    }

    [Fact]
    public void Analyze_publishes_light_decay_and_alert_history_is_readable()
    {
        RunOnStaThread(() =>
        {
            var dir = NewTempDir();
            try
            {
                var hub = new RealtimeHub();
                var now = DateTimeOffset.UtcNow;
                using var runtime = new AnalyticsRuntime(
                    hub,
                    new AnalyticsRuntimeOptions(Path.Combine(dir, "store"), Path.Combine(dir, "exports"), AutoStart: false));
                using var health = new HealthAnalyticsViewModel(runtime);

                // 20 天、每天降 1：额定取窗口首样本 100 → 预警线 80，约 1 天后跌破 → Warning
                for (var day = 19; day >= 0; day--)
                {
                    hub.Publish(TwinTopic.Optics, new OpticsEvent(
                        "LIGHT_RING", OpticsKind.LightBrightness, 0, 100 - (19 - day), now.AddDays(-day)));
                }

                runtime.Flush();
                var published = runtime.AnalyzeNow();

                Assert.Contains(published, a => a.Kind == AlertKind.LightDecay && a.Severity == Severity.Warning);

                health.Refresh();
                Assert.NotEmpty(health.Alerts);
                var row = health.Alerts.First(a => a.KindText == "光源衰减");
                Assert.Contains("预计", row.Summary);
                Assert.Contains("提前", row.MetaText);
                Assert.NotEqual("0 条", health.AlertCountText);

                // 导出预警历史：JSON 落盘且行数与时间线一致
                health.ExportAlertsCommand.Execute(null);
                var exported = Assert.Single(Directory.GetFiles(Path.Combine(dir, "exports"), "alerts-*.json"));
                Assert.Contains("LightDecay", File.ReadAllText(exported));
                Assert.Contains("已导出预警历史", health.ExportResultText);
            }
            finally
            {
                Delete(dir);
            }
        });
    }

    [Fact]
    public void Refresh_reuses_rows_and_keeps_selection()
    {
        RunOnStaThread(() =>
        {
            var dir = NewTempDir();
            try
            {
                var hub = new RealtimeHub();
                using var runtime = new AnalyticsRuntime(
                    hub,
                    new AnalyticsRuntimeOptions(Path.Combine(dir, "store"), Path.Combine(dir, "exports"), AutoStart: false));
                using var health = new HealthAnalyticsViewModel(runtime);

                hub.Publish(TwinTopic.Motion, new AxisSnapshot("STAGE", "X", 5_000, 0, DateTimeOffset.UtcNow));
                hub.Publish(TwinTopic.Motion, new AxisSnapshot("ROBOT", "R", 5_000, 0, DateTimeOffset.UtcNow));
                health.Refresh();

                Assert.Equal(2, health.Parts.Count);
                var stage = health.Parts.Single(p => p.PartId == "STAGE");
                health.SelectedPart = stage;

                // 再进一条数据（分数会变），行对象必须是同一个、选中不能丢
                hub.Publish(TwinTopic.Status, new StatusEvent("STAGE", "E1042", Severity.Alarm, null, DateTimeOffset.UtcNow));
                health.Refresh();

                Assert.Same(stage, health.SelectedPart);
                Assert.Same(stage, health.Parts.Single(p => p.PartId == "STAGE"));
                Assert.Equal(75, stage.Score, 1); // 100 − 报警 25
                Assert.Equal("关注", stage.GradeText);
            }
            finally
            {
                Delete(dir);
            }
        });
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Delete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>写入必失败的库：验证「磁盘坏了只丢历史、不拖垮实时与界面」。</summary>
    private sealed class ThrowingStore(string rootPath) : ITimeSeriesStore
    {
        public string RootPath { get; } = rootPath;

        public void Append(IReadOnlyList<TimeSeriesRecord> records) =>
            throw new IOException("模拟磁盘写入失败");

        public IEnumerable<TimeSeriesRecord> Query(TimeSeriesQuery query) => [];

        public IReadOnlyList<TimeSeriesPartition> Partitions() => [];

        public long EstimateBytes(DateTimeOffset from, DateTimeOffset to) => 0;

        public long Prune(DateOnly before) => 0;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA 线程超时");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA 线程内断言失败: {failure}");
        }
    }
}
