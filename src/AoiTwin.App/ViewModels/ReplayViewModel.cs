using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Replay;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AoiTwin.App.ViewModels;

/// <summary>
/// 回放调试视图模型（T3.2）：把 <see cref="ReplaySession"/> 的命令与进度映射为绑定属性。
/// 线程模型：回放进度在回放线程触发，此处按 ~80ms 节流后封送到 UI 线程——
/// 整班次文件十几万帧，逐帧 Dispatcher 调用会拖垮渲染线程。
/// </summary>
public sealed partial class ReplayViewModel : ObservableObject, IDisposable
{
    private const int ProgressThrottleMs = 80;

    private readonly IRealtimeHub _hub;
    private readonly Dispatcher _dispatcher;
    private ReplaySession? _session;
    private bool _suppressSeek;
    private bool _syncingBatch;
    private ReplayProgress? _pending;
    private long _lastDispatchTick;
    private bool _disposed;

    public ReplayViewModel(IRealtimeHub hub)
    {
        _hub = hub;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        LoadNewestRecording();
    }

    /// <summary>启动即载入目录下最新录制：产线上复查场景九成是「看刚才那一片」，省一步选择。</summary>
    private void LoadNewestRecording()
    {
        try
        {
            var directory = RecordingDirectory.Locate();
            if (!Directory.Exists(directory))
            {
                return;
            }

            var newest = new DirectoryInfo(directory)
                .EnumerateFiles("*.aotrec")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newest is not null)
            {
                LoadFile(newest.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"扫描录制目录失败：{ex.Message}";
        }
    }

    public ObservableCollection<WaferBatch> Batches { get; } = [];

    /// <summary>播放按钮文案（随状态切换）。</summary>
    public string PlayButtonText => IsPlaying ? "⏸ 暂停" : "▶ 播放";

    /// <summary>变速档位（PRD：0.5×–8×）。</summary>
    public IReadOnlyList<double> SpeedOptions { get; } = [0.5, 1.0, 2.0, 4.0, 8.0];

    [ObservableProperty]
    private string _fileName = "未加载录制";

    [ObservableProperty]
    private string? _filePath;

    [ObservableProperty]
    private bool _hasSession;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private string _playStateText = "就绪";

    [ObservableProperty]
    private string _statusText = "选择 .aotrec 录制文件开始回放";

    [ObservableProperty]
    private string _timeText = "00:00 / 00:00";

    [ObservableProperty]
    private string _frameText = "帧 0 / 0";

    [ObservableProperty]
    private string _batchProgressText = "批次 —";

    [ObservableProperty]
    private string _currentBatchText = "—";

    [ObservableProperty]
    private string _lastMessageText = "—";

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _totalSeconds = 1;

    [ObservableProperty]
    private double _speed = 1.0;

    [ObservableProperty]
    private WaferBatch? _selectedBatch;

    // ---------- 命令 ----------

    [RelayCommand]
    private void Open()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择录制文件",
            Filter = "AOI 录制文件 (*.aotrec)|*.aotrec|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        var initial = RecordingDirectory.Locate();
        if (Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        if (dialog.ShowDialog() == true)
        {
            LoadFile(dialog.FileName);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSession))]
    private void PlayPause()
    {
        if (_session is null)
        {
            return;
        }

        _session.TogglePlayPause();
        ApplyProgress(_session.Snapshot(), forceState: true);
    }

    [RelayCommand(CanExecute = nameof(HasSession))]
    private void PrevBatch() => _session?.SeekRelativeBatch(-1);

    [RelayCommand(CanExecute = nameof(HasSession))]
    private void NextBatch() => _session?.SeekRelativeBatch(1);

    // ---------- 加载 ----------

    /// <summary>加载录制文件（录制管理页也可调用，故为公开方法）。</summary>
    public void LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                StatusText = $"文件不存在：{path}";
                return;
            }

            DisposeSession();

            var session = new ReplaySession(path, _hub) { Speed = Speed };
            session.Progress += OnProgress;
            session.Completed += OnCompleted;
            _session = session;

            Batches.Clear();
            foreach (var batch in session.Batches)
            {
                Batches.Add(batch);
            }

            FilePath = path;
            FileName = Path.GetFileName(path);
            TotalSeconds = Math.Max(0.001, session.TotalDuration.TotalSeconds);
            HasSession = true;
            StatusText = $"{session.FrameCount:N0} 帧 · {Batches.Count} 批 · 时长 {FormatTime(session.TotalDuration)}";
            ApplyProgress(session.Snapshot(), forceState: true);
            SyncSelectedBatch(session.CurrentBatch);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            HasSession = false;
            StatusText = $"加载失败：{ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeSession();
    }

    private void DisposeSession()
    {
        if (_session is null)
        {
            return;
        }

        _session.Progress -= OnProgress;
        _session.Completed -= OnCompleted;
        _session.Dispose();
        _session = null;
        HasSession = false;
        IsPlaying = false;
    }

    // ---------- 属性变更 ----------

    partial void OnSpeedChanged(double value)
    {
        if (_session is not null)
        {
            _session.Speed = value;
        }
    }

    partial void OnPositionSecondsChanged(double value)
    {
        // 进度回填（_suppressSeek）不触发 Seek；用户拖动时间轴才定位
        if (_suppressSeek || _session is null || !HasSession)
        {
            return;
        }

        _session.Seek(TimeSpan.FromSeconds(value));
    }

    partial void OnSelectedBatchChanged(WaferBatch? value)
    {
        if (_syncingBatch || value is null || _session is null)
        {
            return;
        }

        _session.SeekToBatch(value);
    }

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayButtonText));

    partial void OnHasSessionChanged(bool value)
    {
        PlayPauseCommand.NotifyCanExecuteChanged();
        PrevBatchCommand.NotifyCanExecuteChanged();
        NextBatchCommand.NotifyCanExecuteChanged();
    }

    // ---------- 进度 ----------

    private void OnProgress(ReplayProgress progress)
    {
        _pending = progress;

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastDispatchTick);
        if (now - last < ProgressThrottleMs
            || Interlocked.CompareExchange(ref _lastDispatchTick, now, last) != last)
        {
            return;
        }

        var snapshot = _pending;
        if (snapshot is null)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ApplyProgress(snapshot, forceState: false)));
    }

    private void OnCompleted() => _dispatcher.BeginInvoke(new Action(() =>
    {
        if (_session is not null)
        {
            ApplyProgress(_session.Snapshot(), forceState: true);
        }
    }));

    private void ApplyProgress(ReplayProgress p, bool forceState)
    {
        _suppressSeek = true;
        try
        {
            PositionSeconds = p.Position.TotalSeconds;
        }
        finally
        {
            _suppressSeek = false;
        }

        TimeText = $"{FormatTime(p.Position)} / {FormatTime(p.Total)}";
        FrameText = $"帧 {p.FrameIndex:N0} / {p.FrameCount:N0}";
        IsPlaying = p.State == ReplayState.Playing;
        PlayStateText = p.State switch
        {
            ReplayState.Playing => "回放中",
            ReplayState.Paused => "已暂停",
            ReplayState.Completed => "已播完",
            _ => "就绪",
        };

        if (p.Batch is not null)
        {
            CurrentBatchText = $"{p.Batch.Display} · {p.Batch.Summary}";
            BatchProgressText = $"批次 {p.Batch.Ordinal} / {Batches.Count}";
            SyncSelectedBatch(p.Batch);
        }
        else
        {
            CurrentBatchText = "—";
            BatchProgressText = $"批次 0 / {Batches.Count}";
        }

        if (p.LastTopic is { } topic && p.LastTypeId is { } typeId)
        {
            LastMessageText = $"{TopicName(topic)} · {TypeName(typeId)}";
        }
    }

    /// <summary>把播放位置对应的批次同步到列表选中项，但不回调 Seek（否则会把播放打断）。</summary>
    private void SyncSelectedBatch(WaferBatch? batch)
    {
        if (batch is null || SelectedBatch?.Ordinal == batch.Ordinal)
        {
            return;
        }

        var index = batch.Ordinal - 1;
        if (index < 0 || index >= Batches.Count)
        {
            return;
        }

        _syncingBatch = true;
        try
        {
            SelectedBatch = Batches[index];
        }
        finally
        {
            _syncingBatch = false;
        }
    }

    private static string FormatTime(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{t.Minutes:00}:{t.Seconds:00}";

    private static string TopicName(TwinTopic topic) => topic switch
    {
        TwinTopic.Motion => "Motion",
        TwinTopic.Status => "Status",
        TwinTopic.Optics => "Optics",
        TwinTopic.Result => "Result",
        _ => topic.ToString(),
    };

    private static string TypeName(byte typeId) => typeId switch
    {
        Core.Recording.RecFileFormat.MessageType.AxisSnapshot => "轴快照",
        Core.Recording.RecFileFormat.MessageType.StatusEvent => "状态事件",
        Core.Recording.RecFileFormat.MessageType.OpticsEvent => "光学事件",
        Core.Recording.RecFileFormat.MessageType.WaferResult => "晶圆结果",
        _ => $"typeId={typeId}",
    };
}
