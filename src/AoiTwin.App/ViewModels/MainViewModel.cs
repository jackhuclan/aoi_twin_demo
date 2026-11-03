using CommunityToolkit.Mvvm.ComponentModel;

namespace AoiTwin.App.ViewModels;

/// <summary>
/// 主窗口视图模型（T3.2）：持有各功能页视图模型，并负责页间协同
/// （如录制管理页「回放」→ 切到回放页并加载文件）。业务逻辑一律留在各页视图模型内。
/// 页序：0 3D 场景（T1.3）· 1 采集监控（T2.5）· 2 回放调试 · 3 录制管理。
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int ReplayTabIndex = 2;

    public MainViewModel(
        TwinSceneViewModel twin,
        CollectionMonitorViewModel monitor,
        ReplayViewModel replay,
        RecordingManagerViewModel recordings)
    {
        Twin = twin;
        Monitor = monitor;
        Replay = replay;
        Recordings = recordings;
        Recordings.ReplayRequested += OnReplayRequested;
    }

    /// <summary>3D 场景页（T1.4）。</summary>
    public TwinSceneViewModel Twin { get; }

    public CollectionMonitorViewModel Monitor { get; }

    public ReplayViewModel Replay { get; }

    public RecordingManagerViewModel Recordings { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    public string Version => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public void Dispose()
    {
        Recordings.ReplayRequested -= OnReplayRequested;
        Monitor.Dispose();
        Replay.Dispose();
    }

    private void OnReplayRequested(string path)
    {
        Replay.LoadFile(path);
        SelectedTabIndex = ReplayTabIndex;
    }
}
