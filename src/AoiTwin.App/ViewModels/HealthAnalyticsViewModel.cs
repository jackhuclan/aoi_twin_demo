using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using AoiTwin.App.Services;
using AoiTwin.Core.Analytics;
using AoiTwin.Core.Bus;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AoiTwin.App.ViewModels;

/// <summary>评分因子行（扣分明细）。</summary>
public sealed record FactorRow(string Name, double Value, string Unit, string? Note)
{
    public string ValueText => $"{Value:F1} {Unit}";

    public string DisplayText => Note is null ? $"{Name} {ValueText}" : $"{Name} {ValueText} · {Note}";
}

/// <summary>
/// 部件健康行：对象在刷新间复用（同 <see cref="SourceRow"/>）——
/// 每秒重建集合会让选中项丢失，而操作员点开一个部件的因子明细后不该被刷新顶掉。
/// </summary>
public sealed partial class PartHealthRow : ObservableObject
{
    public PartHealthRow(HealthScore score)
    {
        PartId = score.PartId;
        Apply(score);
    }

    public string PartId { get; }

    [ObservableProperty]
    private double _score;

    [ObservableProperty]
    private HealthGrade _grade;

    [ObservableProperty]
    private IReadOnlyList<FactorRow> _factors = [];

    public string ScoreText => Score.ToString("F0");

    public string GradeText => Grade switch
    {
        HealthGrade.Good => "良好",
        HealthGrade.Watch => "关注",
        HealthGrade.Warning => "预警",
        _ => "严重",
    };

    /// <summary>扣分最多的那一项（列表右侧一行摘要，避免为看一眼原因就点开）。</summary>
    public string TopFactorText => Factors.Count == 0
        ? "无扣分项"
        : Factors.OrderByDescending(f => f.Value).First().DisplayText;

    public void Apply(HealthScore score)
    {
        Score = score.Score;
        Grade = score.Grade;
        Factors = score.Factors.Select(f => new FactorRow(f.Name, f.Value, f.Unit, f.Note)).ToList();
    }

    partial void OnScoreChanged(double value) => OnPropertyChanged(nameof(ScoreText));

    partial void OnGradeChanged(HealthGrade value) => OnPropertyChanged(nameof(GradeText));

    partial void OnFactorsChanged(IReadOnlyList<FactorRow> value) => OnPropertyChanged(nameof(TopFactorText));
}

/// <summary>预警时间线行。</summary>
public sealed partial class AlertRow : ObservableObject
{
    public AlertRow(AlertEvent alert)
    {
        Alert = alert;
    }

    public AlertEvent Alert { get; }

    public Severity Severity => Alert.Severity;

    public string TimeText => Alert.Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss");

    public string PartId => Alert.PartId;

    public string KindText => Alert.Kind switch
    {
        AlertKind.HealthScore => "健康度",
        AlertKind.LightDecay => "光源衰减",
        AlertKind.AxisWear => "轴磨损",
        AlertKind.TactDegradation => "节拍劣化",
        _ => "自定义",
    };

    public string SeverityText => Alert.Severity switch
    {
        Severity.Alarm => "严重",
        Severity.Warning => "预警",
        _ => "恢复",
    };

    public string Summary => Alert.Summary;

    public string MetaText
    {
        get
        {
            var parts = new List<string>(3);
            if (Alert.HorizonDays is { } days)
            {
                parts.Add($"提前 {days} 天");
            }

            if (Alert.Confidence is { } confidence)
            {
                parts.Add($"R²={confidence:F2}");
            }

            if (Alert.Severity != Severity.Info)
            {
                parts.Add($"评分 {Alert.Score:F0}");
            }

            return parts.Count == 0 ? "—" : string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// 健康分析页（TB.5–TB.16 的界面出口）：健康评分列表、扣分因子、预警时间线、
/// 分析口径与存储/落盘状态，以及导出与手动分析。
///
/// 边界：本类只做**格式化与命令编排**。所有磁盘与计算都在 <see cref="AnalyticsRuntime"/>，
/// 刷新只读内存快照（1Hz 不会随数据量变慢）；手动分析是唯一会读库的动作，因此放到后台线程。
/// </summary>
public sealed partial class HealthAnalyticsViewModel : ObservableObject, IDisposable
{
    private readonly AnalyticsRuntime _runtime;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, PartHealthRow> _rows = new(StringComparer.Ordinal);
    private int _alertCount = -1;
    private bool _disposed;

    public HealthAnalyticsViewModel(AnalyticsRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;

        StorePathText = runtime.StoreRoot;
        ExportPathText = runtime.ExportDirectory;
        OptionsSourceText = runtime.OptionsFilePath is { Length: > 0 } path
            ? $"参数文件：{Path.GetFileName(path)}"
            : "参数：代码默认值";

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    public ObservableCollection<PartHealthRow> Parts { get; } = [];

    public ObservableCollection<AlertRow> Alerts { get; } = [];

    public string StorePathText { get; }

    public string ExportPathText { get; }

    public string OptionsSourceText { get; }

    [ObservableProperty]
    private PartHealthRow? _selectedPart;

    // ---------- 运行状态 ----------

    [ObservableProperty]
    private string _schedulerText = "—";

    [ObservableProperty]
    private string _lastRunText = "尚未分析";

    [ObservableProperty]
    private string _persistenceText = "—";

    [ObservableProperty]
    private string _storeText = "—";

    [ObservableProperty]
    private string _caliberText = "—";

    [ObservableProperty]
    private string _alertCountText = "0 条";

    [ObservableProperty]
    private string _emptyHint = "尚无观测数据：请在「采集监控」启动采集，健康档案随消息实时累积。";

    [ObservableProperty]
    private bool _isAnalyzing;

    // ---------- 提示条 ----------

    [ObservableProperty]
    private string _noteText = string.Empty;

    [ObservableProperty]
    private bool _hasNote;

    [ObservableProperty]
    private bool _isNoteError;

    [ObservableProperty]
    private string _exportResultText = string.Empty;

    public bool HasParts => Parts.Count > 0;

    // ---------- 命令 ----------

    /// <summary>立即分析：唯一会读时序库的动作，放后台线程执行，避免窗口冻结。</summary>
    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (IsAnalyzing)
        {
            return;
        }

        IsAnalyzing = true;
        try
        {
            var published = await Task.Run(_runtime.AnalyzeNow).ConfigureAwait(true);
            ExportResultText = published.Count == 0
                ? $"分析完成（{DateTime.Now:HH:mm:ss}）：本轮无新预警"
                : $"分析完成：发布 {published.Count} 条预警（{string.Join("、", published.Select(a => a.PartId).Distinct())}）";
            Refresh();
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private void ExportHealth()
    {
        try
        {
            var result = _runtime.ExportHealth();
            ExportResultText = $"已导出健康档案 {result.Rows} 行：{Path.GetFileName(result.FilePath)}";
        }
        catch (Exception ex)
        {
            ExportResultText = $"导出失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ExportAlerts()
    {
        try
        {
            var result = _runtime.ExportAlerts();
            ExportResultText = result.Rows == 0
                ? "暂无预警可导出"
                : $"已导出预警历史 {result.Rows} 条：{Path.GetFileName(result.FilePath)}";
        }
        catch (Exception ex)
        {
            ExportResultText = $"导出失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenStoreFolder() => Reveal(_runtime.StoreRoot);

    [RelayCommand]
    private void OpenExportFolder() => Reveal(_runtime.ExportDirectory);

    /// <summary>拉取一次快照并刷新绑定属性（UI 线程；1Hz 或命令触发）。</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _runtime.Snapshot();

        ApplyParts(snapshot.Scores);
        ApplyAlerts(snapshot.Alerts);

        SchedulerText = snapshot.SchedulerRunning
            ? $"运行中 · 每 {snapshot.Options.SchedulerPeriod.TotalMinutes:F0} 分钟"
            : "已停止";
        LastRunText = snapshot.LastRunAt is { } at ? at.ToLocalTime().ToString("HH:mm:ss") : "尚未分析";

        var persistence = snapshot.Persistence;
        PersistenceText = $"落盘 {persistence.Written:N0} 条 · 待刷 {persistence.Pending:N0}"
            + (persistence.Healthy ? string.Empty : $" · 丢弃 {persistence.DroppedByBuffer + persistence.DroppedAfterRetry:N0}");

        StoreText = $"{snapshot.Partitions} 分区 · {FormatBytes(snapshot.StoreBytes)}";
        AlertCountText = $"{snapshot.Alerts.Count} 条";

        // 口径必须写在界面上：换阈值就换结论，同一批数据会得出不同的「剩余天数」
        var options = snapshot.Options;
        CaliberText = $"窗口 {options.Window.TotalDays:F0} 天 · 光源预警 {options.LightWarningFraction:P0}"
            + $" / 严重 {options.LightCriticalFraction:P0} · 提前 {options.LightWarningHorizonDays} 天"
            + $" · 节拍劣化 {options.TactDegradationPercent:F0}%";

        ApplyNote(snapshot);
        OnPropertyChanged(nameof(HasParts));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        // 运行时由容器持有（其它页面/后续模块也可能用），这里不 Dispose
    }

    private void ApplyParts(IReadOnlyList<HealthScore> scores)
    {
        var selectedId = SelectedPart?.PartId;

        foreach (var score in scores)
        {
            if (_rows.TryGetValue(score.PartId, out var row))
            {
                row.Apply(score);
            }
            else
            {
                _rows[score.PartId] = new PartHealthRow(score);
            }
        }

        // 移除已消失的部件（档案不会凭空消失，但换库/清库后会）
        var alive = scores.Select(s => s.PartId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _rows.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _rows.Remove(id);
        }

        // 按分数升序对齐集合：只在顺序真的变了时 Move，避免每秒重建（会丢选中）
        for (var i = 0; i < scores.Count; i++)
        {
            var target = _rows[scores[i].PartId];
            if (i < Parts.Count && ReferenceEquals(Parts[i], target))
            {
                continue;
            }

            var existing = Parts.IndexOf(target);
            if (existing < 0)
            {
                Parts.Insert(i, target);
            }
            else
            {
                Parts.Move(existing, i);
            }
        }

        while (Parts.Count > scores.Count)
        {
            Parts.RemoveAt(Parts.Count - 1);
        }

        if (selectedId is not null && _rows.TryGetValue(selectedId, out var keep))
        {
            SelectedPart = keep;
        }
        else if (SelectedPart is null && Parts.Count > 0)
        {
            SelectedPart = Parts[0];
        }
    }

    private void ApplyAlerts(IReadOnlyList<AlertEvent> alerts)
    {
        // 运行时按时间倒序保存，长度或首条变化即重建（≤200 行，代价可忽略）
        if (alerts.Count == _alertCount && (alerts.Count == 0 || ReferenceEquals(Alerts.FirstOrDefault()?.Alert, alerts[0])))
        {
            return;
        }

        _alertCount = alerts.Count;
        Alerts.Clear();
        foreach (var alert in alerts.Take(50))
        {
            Alerts.Add(new AlertRow(alert));
        }
    }

    private void ApplyNote(AnalyticsSnapshot snapshot)
    {
        if (snapshot.OptionsError is { Length: > 0 } optionsError)
        {
            IsNoteError = true;
            NoteText = $"分析参数文件未生效，沿用上一次可用配置：{optionsError}";
            HasNote = true;
            return;
        }

        if (snapshot.LastError is { Length: > 0 } error)
        {
            IsNoteError = true;
            NoteText = error;
            HasNote = true;
            return;
        }

        var persistence = snapshot.Persistence;
        if (!persistence.Healthy)
        {
            IsNoteError = true;
            NoteText = $"时序落盘降级：失败 {persistence.FailedBatches} 批 · 缓冲丢弃 {persistence.DroppedByBuffer:N0} 条"
                + $" · 重试超窗丢弃 {persistence.DroppedAfterRetry:N0} 条（实时链路未受影响，历史数据可能不完整）";
            HasNote = true;
            return;
        }

        if (snapshot.FailedPredictor)
        {
            var names = snapshot.Runs.Where(r => r.Failed).Select(r => $"{r.PredictorName}（{r.Error}）");
            IsNoteError = false;
            NoteText = $"预测器未完成：{string.Join("；", names)} —— 其余模型结论仍有效";
            HasNote = true;
            return;
        }

        IsNoteError = false;
        NoteText = string.Empty;
        HasNote = false;
    }

    private void Reveal(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ExportResultText = $"打开目录失败：{ex.Message}";
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:F2} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B",
    };
}
