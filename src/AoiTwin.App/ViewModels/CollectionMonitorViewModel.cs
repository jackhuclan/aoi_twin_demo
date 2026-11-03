using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using AoiTwin.Adapters;
using AoiTwin.Adapters.Mock;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Recording;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AoiTwin.App.ViewModels;

/// <summary>数据源状态行（T2.5）：行对象在采集全程复用，只更新属性——每秒重建集合会让列表闪烁并丢选中。</summary>
public sealed partial class SourceRow : ObservableObject
{
    public SourceRow(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty]
    private AdapterHealth _health = AdapterHealth.Stopped;

    [ObservableProperty]
    private long _messageCount;

    [ObservableProperty]
    private double _ratePerSecond;

    [ObservableProperty]
    private DateTimeOffset? _lastMessageAt;

    public bool IsConnected => Health == AdapterHealth.Connected;

    public bool IsReconnecting => Health == AdapterHealth.Reconnecting;

    public string HealthText => Health switch
    {
        AdapterHealth.Connected => "已连接",
        AdapterHealth.Connecting => "连接中",
        AdapterHealth.Reconnecting => "重连中",
        _ => "已停止",
    };

    public string CountText => MessageCount.ToString("N0");

    public string RateText => $"{RatePerSecond:F1} 条/s";

    public string LastText => LastMessageAt is { } t
        ? t.LocalDateTime.ToString("HH:mm:ss")
        : "—";

    public void Apply(SourceSnapshot snapshot)
    {
        Health = snapshot.Health;
        MessageCount = snapshot.MessageCount;
        RatePerSecond = snapshot.RatePerSecond;
        LastMessageAt = snapshot.LastMessageAt;
    }

    partial void OnHealthChanged(AdapterHealth value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsReconnecting));
        OnPropertyChanged(nameof(HealthText));
    }

    partial void OnMessageCountChanged(long value) => OnPropertyChanged(nameof(CountText));

    partial void OnRatePerSecondChanged(double value) => OnPropertyChanged(nameof(RateText));

    partial void OnLastMessageAtChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(LastText));
}

/// <summary>Topic 吞吐行（T2.5）。</summary>
public sealed partial class TopicRow : ObservableObject
{
    public TopicRow(TwinTopic topic, string displayName)
    {
        Topic = topic;
        DisplayName = displayName;
    }

    public TwinTopic Topic { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    private long _count;

    [ObservableProperty]
    private double _ratePerSecond;

    public string CountText => Count.ToString("N0");

    public string RateText => RatePerSecond >= 0.05 ? $"{RatePerSecond:F1} 条/s" : "—";

    public void Apply(TopicSnapshot snapshot)
    {
        Count = snapshot.Count;
        RatePerSecond = snapshot.RatePerSecond;
    }

    partial void OnCountChanged(long value) => OnPropertyChanged(nameof(CountText));

    partial void OnRatePerSecondChanged(double value) => OnPropertyChanged(nameof(RateText));
}

/// <summary>
/// 采集监控视图模型（T2.5）：本轮 M1 采集的「单一操作台」——
/// 启动/停止采集、开停录制、四路数据源健康与吞吐、逐 Topic 吞吐、磁盘水位与告警。
///
/// 数据来源：<see cref="CollectionSession"/> 每秒一份快照（采样与计算都在会话内，VM 只做格式化）。
/// 线程模型：全程 UI 线程（DispatcherTimer 1Hz），会话侧计数在数据线程累加，读取无锁热路径。
/// 数据源：M1 为 Mock 合成场景（真实 PLC/相机 SDK 接口在 M2 接入，届时只替换会话装配）。
/// </summary>
public sealed partial class CollectionMonitorViewModel : ObservableObject, IDisposable
{
    /// <summary>磁盘水位告警阈值（与 RecordingOptions 默认值一致，PRD §5.5）。</summary>
    public const double DiskWatermark = 0.85;

    private readonly DispatcherTimer _timer;
    private readonly CollectionSession _session;
    private readonly string _recordingDirectory;
    private bool _disposed;

    public CollectionMonitorViewModel(IRealtimeHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);

        _recordingDirectory = LocateMockRecordingDirectory();
        var scenario = new MockScenario();
        _session = MockCollectionFactory.CreateSession(hub, scenario, new RecordingOptions
        {
            Directory = _recordingDirectory,
            DeviceId = "AOI-01",
            ShiftId = "MOCK-DEMO",
            MaxFileDuration = TimeSpan.FromMinutes(30), // 演示长跑时按段滚动，避免单个文件无限增长
        });

        ScenarioName = scenario.ScenarioName;

        foreach (var name in _session.SourceNames)
        {
            Sources.Add(new SourceRow(name));
        }

        foreach (var topic in new[] { TwinTopic.Motion, TwinTopic.Status, TwinTopic.Optics, TwinTopic.Result })
        {
            Topics.Add(new TopicRow(topic, DisplayName(topic)));
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    public ObservableCollection<SourceRow> Sources { get; } = [];

    public ObservableCollection<TopicRow> Topics { get; } = [];

    public string ScenarioName { get; }

    public string DataSourceText => $"Mock 合成场景 · {ScenarioName}（真实设备接口 M2 接入）";

    public string RecordingDirectoryText => _recordingDirectory;

    // ---------- 采集状态 ----------

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _runStateText = "已停止";

    [ObservableProperty]
    private string _elapsedText = "00:00:00";

    [ObservableProperty]
    private string _totalMessagesText = "累计 0 帧";

    [ObservableProperty]
    private string _aggregateRateText = "0.0 条/s";

    // ---------- 录制状态 ----------

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string _recordingStateText = "未录制";

    [ObservableProperty]
    private string _recordingFileText = "—";

    [ObservableProperty]
    private string _recordingFramesText = "0 帧";

    [ObservableProperty]
    private string _recordingSizeText = "0 B";

    [ObservableProperty]
    private string _droppedText = "0";

    // ---------- 磁盘 ----------

    [ObservableProperty]
    private double _diskPercent;

    [ObservableProperty]
    private string _diskText = "—";

    [ObservableProperty]
    private bool _isDiskAlarm;

    // ---------- 告警 ----------

    [ObservableProperty]
    private string _alarmText = string.Empty;

    [ObservableProperty]
    private bool _hasAlarm;

    public bool CanStart => !IsRunning;

    public bool CanStop => IsRunning;

    // ---------- 命令 ----------

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        _session.Start();
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _session.Stop();
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void ToggleRecording()
    {
        if (IsRecording)
        {
            _session.StopRecording();
        }
        else
        {
            _session.StartRecording();
        }

        Refresh();
    }

    [RelayCommand]
    private void OpenRecordingFolder()
    {
        if (!Directory.Exists(_recordingDirectory))
        {
            Directory.CreateDirectory(_recordingDirectory);
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_recordingDirectory)
        {
            UseShellExecute = true,
        });
    }

    /// <summary>手动刷新一次快照（命令入口；测试与「刷新」按钮共用同一实现）。</summary>
    [RelayCommand]
    private void RefreshNow() => Refresh();

    /// <summary>拉取一份会话快照并刷新全部绑定属性（UI 线程调用；Timer 1Hz 或手动命令触发）。</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _session.Snapshot();

        for (var i = 0; i < Sources.Count && i < snapshot.Sources.Count; i++)
        {
            Sources[i].Apply(snapshot.Sources[i]);
        }

        for (var i = 0; i < Topics.Count && i < snapshot.Topics.Count; i++)
        {
            Topics[i].Apply(snapshot.Topics[i]);
        }

        IsRunning = snapshot.IsRunning;
        RunStateText = snapshot.IsRunning ? "采集中" : "已停止";
        ElapsedText = FormatDuration(snapshot.Elapsed);
        TotalMessagesText = $"累计 {snapshot.TotalMessages:N0} 帧";
        AggregateRateText = $"{snapshot.AggregateRatePerSecond:F1} 条/s";

        var recording = snapshot.Recording;
        IsRecording = recording.IsRecording;
        RecordingStateText = recording.IsRecording
            ? "录制中"
            : recording.FilePath is null ? "未录制" : "已结束";
        RecordingFileText = recording.FilePath is null ? "—" : Path.GetFileName(recording.FilePath);
        RecordingFramesText = $"{recording.FrameCount:N0} 帧";
        RecordingSizeText = FormatBytes(recording.BytesWritten);
        DroppedText = recording.DroppedFrameCount.ToString("N0");

        var ratio = Math.Clamp(snapshot.DiskUsedRatio, 0, 1);
        DiskPercent = ratio * 100;
        DiskText = $"{DiskPercent:F1}% / 阈值 {DiskWatermark:P0}";
        IsDiskAlarm = ratio >= DiskWatermark;

        AlarmText = BuildAlarm(snapshot, ratio);
        HasAlarm = AlarmText.Length > 0;

        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ToggleRecordingCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _session.Dispose();
    }

    private string BuildAlarm(CollectionSnapshot snapshot, double diskRatio)
    {
        if (snapshot.Recording.PausedByDiskWatermark)
        {
            return $"磁盘水位 {diskRatio:P0} ≥ {DiskWatermark:P0}：录制已暂停，帧由环形缓冲保压，请清理录制目录后自动恢复";
        }

        if (!snapshot.IsRunning)
        {
            return string.Empty;
        }

        var faults = snapshot.Sources.Where(s => s.IsFaulted).Select(s => s.Name).ToList();
        return faults.Count == 0
            ? string.Empty
            : $"数据源中断：{string.Join("、", faults)} —— 其余通道继续采集（NFR-05 单源故障隔离），正在按 1s→30s 退避重连";
    }

    /// <summary>Mock 录制目录：独立于交付目录 assets/recordings，避免演示数据污染现场班次采集清单（T3.4）。</summary>
    private static string LocateMockRecordingDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, "AoiTwin", "mock-recordings");
    }

    private static string DisplayName(TwinTopic topic) => topic switch
    {
        TwinTopic.Motion => "Motion · 运动",
        TwinTopic.Status => "Status · 状态",
        TwinTopic.Optics => "Optics · 光学",
        TwinTopic.Result => "Result · 结果",
        _ => topic.ToString(),
    };

    private static string FormatDuration(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:F2} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B",
    };
}
