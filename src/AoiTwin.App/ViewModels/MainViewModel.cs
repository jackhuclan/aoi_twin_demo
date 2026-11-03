using CommunityToolkit.Mvvm.ComponentModel;

namespace AoiTwin.App.ViewModels;

/// <summary>
/// 主窗口视图模型（T3.2）：持有各功能页视图模型，并负责页间协同
/// （如录制管理页「回放」→ 切到回放页并加载文件）。业务逻辑一律留在各页视图模型内。
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public MainViewModel(ReplayViewModel replay, RecordingManagerViewModel recordings)
    {
        Replay = replay;
        Recordings = recordings;
        Recordings.ReplayRequested += OnReplayRequested;
    }

    public ReplayViewModel Replay { get; }

    public RecordingManagerViewModel Recordings { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    public string Version => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public void Dispose()
    {
        Recordings.ReplayRequested -= OnReplayRequested;
        Replay.Dispose();
    }

    private void OnReplayRequested(string path)
    {
        Replay.LoadFile(path);
        SelectedTabIndex = 0;
    }
}
