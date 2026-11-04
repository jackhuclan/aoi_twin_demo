using System.Collections.ObjectModel;
using AoiTwin.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AoiTwin.App.ViewModels;

/// <summary>
/// 部件钻取详情（TA.12，FR-M2-08）：点选部件后右侧面板显示的实时数据。
/// 数据由 <see cref="PartDrillDownService"/> 填充——IO 状态/寿命计数来自最新 StatusEvent
/// Payload，最近报警为累积历史（VM 层只存最新一条，历史由钻取会话持有）。
/// </summary>
public sealed class DrillDownViewModel : ObservableObject
{
    private string? _partId;
    private string? _partName;
    private string _opticsSummary = "—";
    private string _lifeCount = "—";
    private string _lastStatusAt = "—";

    /// <summary>当前钻取部件（null = 未选中）。</summary>
    public string? PartId
    {
        get => _partId;
        private set => SetProperty(ref _partId, value);
    }

    public string? PartName
    {
        get => _partName;
        private set => SetProperty(ref _partName, value);
    }

    /// <summary>面板标题。</summary>
    public string Title => PartId is null ? "部件详情（未选中）" : $"部件详情 · {PartName ?? PartId}";

    /// <summary>光学状态摘要（通道/亮度快照）。</summary>
    public string OpticsSummary
    {
        get => _opticsSummary;
        private set => SetProperty(ref _opticsSummary, value);
    }

    /// <summary>寿命计数（StatusEvent Payload["LifeCount"]）。</summary>
    public string LifeCount
    {
        get => _lifeCount;
        private set => SetProperty(ref _lifeCount, value);
    }

    /// <summary>最近状态时间。</summary>
    public string LastStatusAt
    {
        get => _lastStatusAt;
        private set => SetProperty(ref _lastStatusAt, value);
    }

    /// <summary>IO 状态行（Payload 键值对）。</summary>
    public ObservableCollection<string> IoLines { get; } = [];

    /// <summary>最近报警（新→旧，上限 <see cref="PartDrillDownService.MaxAlarmHistory"/>）。</summary>
    public ObservableCollection<string> Alarms { get; } = [];

    /// <summary>钻取会话更新（由 service 调用）。</summary>
    public void Reset(string? partId, string? partName)
    {
        PartId = partId;
        PartName = partName;
        OnPropertyChanged(nameof(Title));
        if (partId is null)
        {
            IoLines.Clear();
            Alarms.Clear();
            OpticsSummary = "—";
            LifeCount = "—";
            LastStatusAt = "—";
        }
    }

    public void SetOpticsSummary(string summary) => OpticsSummary = summary;

    public void SetLifeCount(string count) => LifeCount = count;

    public void SetLastStatusAt(string at) => LastStatusAt = at;

    public void ReplaceIoLines(IEnumerable<string> lines)
    {
        IoLines.Clear();
        foreach (var line in lines)
        {
            IoLines.Add(line);
        }
    }

    public void PushAlarm(string line)
    {
        Alarms.Insert(0, line); // 新→旧
        while (Alarms.Count > PartDrillDownService.MaxAlarmHistory)
        {
            Alarms.RemoveAt(Alarms.Count - 1);
        }
    }
}
