using System.IO;
using System.Text.Json;
using AoiTwin.Core.Analytics;
using AoiTwin.Core.Analytics.Export;
using AoiTwin.Core.Bus;
using Xunit;

namespace AoiTwin.Analytics.Tests;

/// <summary>
/// S4 导出验收（TB.16）：CSV/JSON 口径一致、脱敏生效、ACL 拒绝越权。
/// </summary>
public sealed class ExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-m3-exp-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    public ExportTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void Health_export_writes_csv_with_factors()
    {
        var service = new AnalyticsExportService();
        var scores = new List<HealthScore>
        {
            new("STAGE", 62.5, HealthGrade.Warning, [new AlertFactor("报警", 50, "分", "2 次")]),
        };

        var result = service.ExportHealth(scores, Path.Combine(_root, "health.csv"), ExportFormat.Csv);

        Assert.Equal(1, result.Rows);
        var text = File.ReadAllText(result.FilePath);
        Assert.Contains("partId,score,grade,factors", text, StringComparison.Ordinal);
        Assert.Contains("STAGE,62.5,Warning", text, StringComparison.Ordinal);
        Assert.Contains("报警=50.00分", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Alert_export_masks_wafer_like_identifiers()
    {
        var service = new AnalyticsExportService();
        var alerts = new List<AlertEvent>
        {
            new("ST01-000123", AlertKind.TactDegradation, Severity.Warning, 30, "节拍劣化", [], null, 0.9, Now),
        };

        var result = service.ExportAlerts(alerts, Path.Combine(_root, "alerts.json"), ExportFormat.Json, ExportRole.Engineer);
        var text = File.ReadAllText(result.FilePath);

        Assert.Contains("******0123", text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(1, document.RootElement.GetArrayLength());
        Assert.Equal("TactDegradation", document.RootElement[0].GetProperty("kind").GetString());
    }

    [Fact]
    public void Export_guard_rejects_viewer_alert_history_and_engineer_raw_series()
    {
        var service = new AnalyticsExportService();
        var path = Path.Combine(_root, "x.csv");

        Assert.Throws<UnauthorizedAccessException>(() =>
            service.ExportAlerts([], path, ExportFormat.Csv, ExportRole.Viewer));

        Assert.Throws<UnauthorizedAccessException>(() =>
            service.ExportTimeSeries([], path, ExportFormat.Csv, ExportRole.Engineer));

        Assert.True(ExportGuard.IsAllowed(ExportRole.Viewer, ExportScope.HealthSummary));
        Assert.True(ExportGuard.IsAllowed(ExportRole.Admin, ExportScope.RawTimeSeries));
    }

    [Fact]
    public void Mask_keeps_tail_and_hides_prefix()
    {
        Assert.Equal("*******0123", AnalyticsExportService.Mask("ST01-000123"));
        Assert.Equal("***", AnalyticsExportService.Mask("AOI", keepTail: 0));
        Assert.Equal("", AnalyticsExportService.Mask(""));
    }
}
