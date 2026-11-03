using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using AoiTwin.Core.Recording;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AoiTwin.App.ViewModels;

/// <summary>录制文件列表项（T3.3）：列表展示所需的文件摘要。</summary>
public sealed record RecordingFileItem(
    string FileName,
    string FullPath,
    string DeviceId,
    string ShiftId,
    DateTimeOffset StartedAt,
    TimeSpan? Duration,
    long FrameCount,
    long SizeBytes,
    bool IsFinalized,
    int ErrorCount)
{
    public string StartedText => StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    public string DurationText => Duration is { } d
        ? $"{(int)d.TotalHours:00}:{d.Minutes:00}:{d.Seconds:00}"
        : "—";

    public string SizeText => SizeBytes >= 1024L * 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024 / 1024:F2} GB"
        : $"{SizeBytes / 1024.0 / 1024:F1} MB";

    /// <summary>文件状态：正常关闭 / 未正常关闭（断电或录制中）/ 有损坏帧。</summary>
    public string StateText => !IsFinalized
        ? "未正常关闭"
        : ErrorCount > 0 ? $"损坏 {ErrorCount} 帧" : "正常";

    public bool HasProblem => !IsFinalized || ErrorCount > 0;
}

/// <summary>
/// 录制管理视图模型（T3.3）：按目录/班次浏览录制文件、查看校验摘要、
/// 二次确认后删除、直接送去回放页复现。
/// </summary>
public sealed partial class RecordingManagerViewModel : ObservableObject
{
    private readonly Func<string, bool> _confirmDelete;

    public RecordingManagerViewModel()
        : this(path => System.Windows.MessageBox.Show(
            $"确认删除录制文件？此操作不可恢复。\n\n{Path.GetFileName(path)}",
            "删除确认",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK)
    {
    }

    /// <summary>注入确认对话框（便于将来替换为 UI 服务或测试替身）。</summary>
    public RecordingManagerViewModel(Func<string, bool> confirmDelete)
    {
        _confirmDelete = confirmDelete;
        DirectoryPath = RecordingDirectory.Locate();
        Refresh();
    }

    public ObservableCollection<RecordingFileItem> Files { get; } = [];

    /// <summary>请求切换到回放页并加载该文件（由 MainViewModel 订阅）。</summary>
    public event Action<string>? ReplayRequested;

    [ObservableProperty]
    private string _directoryPath = string.Empty;

    [ObservableProperty]
    private RecordingFileItem? _selected;

    [ObservableProperty]
    private string _summaryText = "（空）";

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand]
    private void Refresh()
    {
        Files.Clear();
        if (!Directory.Exists(DirectoryPath))
        {
            SummaryText = $"目录不存在：{DirectoryPath}";
            return;
        }

        var files = new DirectoryInfo(DirectoryPath)
            .EnumerateFiles("*.aotrec")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        long totalBytes = 0;
        foreach (var file in files)
        {
            totalBytes += file.Length;
            Files.Add(Describe(file));
        }

        SummaryText = Files.Count == 0
            ? "该目录下暂无 .aotrec 录制文件"
            : $"{Files.Count} 个文件 · 合计 {totalBytes / 1024.0 / 1024:F1} MB · {DirectoryPath}";
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "选择录制目录", InitialDirectory = DirectoryPath };
        if (dialog.ShowDialog() == true)
        {
            DirectoryPath = dialog.FolderName;
            Refresh();
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(DirectoryPath) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Replay(RecordingFileItem? item)
    {
        if (item is not null)
        {
            ReplayRequested?.Invoke(item.FullPath);
        }
    }

    [RelayCommand]
    private void Delete(RecordingFileItem? item)
    {
        if (item is null || !_confirmDelete(item.FullPath))
        {
            return;
        }

        try
        {
            File.Delete(item.FullPath);
        }
        catch (IOException ex)
        {
            SummaryText = $"删除失败：{ex.Message}";
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            SummaryText = $"删除失败：{ex.Message}";
            return;
        }

        Refresh();
    }

    /// <summary>单文件摘要：解析头部与帧计数（在线/损坏文件也要能列出，故不抛异常）。</summary>
    private static RecordingFileItem Describe(FileInfo file)
    {
        try
        {
            var info = RecordingReader.Inspect(file.FullName);
            var duration = info.EndedAt is { } ended ? ended - info.StartedAt : (TimeSpan?)null;
            return new RecordingFileItem(
                file.Name,
                file.FullName,
                info.DeviceId,
                info.ShiftId,
                info.StartedAt,
                duration,
                info.ValidFrameCount,
                file.Length,
                info.IsFinalized,
                info.Errors.Count);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // 头部不可解析（空文件/非录制文件）：仍列出，状态标为损坏
            return new RecordingFileItem(
                file.Name, file.FullName, "—", "—", file.LastWriteTimeUtc,
                null, 0, file.Length, false, 1);
        }
    }
}
