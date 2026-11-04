using System.Diagnostics;
using AoiTwin.App.ViewModels;
using AoiTwin.Core.Bus;
using AoiTwin.ViewModel;

namespace AoiTwin.App.Services;

/// <summary>
/// 部件钻取面板服务（TA.12，FR-M2-08）：Open 建临时 Hub 订阅（Status 过滤 PartId，
/// 累积最近报警），Optics/状态快照直接读 Visual VM 聚合；Close 释放订阅。
/// 切换 ≤ 200ms 的实现保证：Open 为同步订阅 + 一次快照填充，无 IO。
/// </summary>
public sealed class PartDrillDownService : IDisposable
{
    /// <summary>最近报警历史上限。</summary>
    public const int MaxAlarmHistory = 20;

    private readonly IRealtimeHub _hub;
    private readonly TwinVisualViewModel _visual;
    private readonly DrillDownViewModel _target;
    private readonly IReadOnlyDictionary<string, string> _partNames;
    private IDisposable? _subscription;
    private string? _openedPartId;
    private bool _disposed;

    /// <param name="partNames">PartId → 显示名（台账）。</param>
    public PartDrillDownService(
        IRealtimeHub hub,
        TwinVisualViewModel visual,
        DrillDownViewModel target,
        IReadOnlyDictionary<string, string>? partNames = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(visual);
        ArgumentNullException.ThrowIfNull(target);

        _hub = hub;
        _visual = visual;
        _target = target;
        _partNames = partNames ?? new Dictionary<string, string>();
    }

    /// <summary>当前钻取部件。</summary>
    public string? OpenedPartId => _openedPartId;

    /// <summary>打开钻取会话（重复调用 = 切换对象；null = 关闭）。</summary>
    public void Open(string? partId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Close();

        if (partId is null)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        _openedPartId = partId;
        _target.Reset(partId, _partNames.GetValueOrDefault(partId));

        // 快照：状态与光学直接读聚合（不重复订阅）
        if (_visual.TryGetStatus(partId, out var status))
        {
            ApplyStatus(status);
        }

        var optics = (OpticsPartViewModel)_visual.GetOpticsPart(partId);
        var state = optics.ReadLatest();
        if (state.Timestamp != DateTimeOffset.MinValue)
        {
            _target.SetOpticsSummary(
                $"亮度 {state.Brightness:P0} · 通道 {string.Join('/', state.ChannelValues.Select(v => v.ToString("0.0")))}");
        }

        // 增量：仅本部件的 Status 直推进来
        _subscription = _hub.Subscribe<StatusEvent>(TwinTopic.Status, OnStatus);

        stopwatch.Stop();
        LastOpenElapsed = stopwatch.Elapsed;
    }

    /// <summary>最近一次 Open 耗时（判据 ≤ 200ms；测试/诊断用）。</summary>
    public TimeSpan LastOpenElapsed { get; private set; }

    /// <summary>关闭钻取会话并释放订阅（订阅数回到基线，无泄漏）。</summary>
    public void Close()
    {
        _subscription?.Dispose();
        _subscription = null;
        _openedPartId = null;
        _target.Reset(null, null);
    }

    private void OnStatus(StatusEvent status)
    {
        if (status.PartId != _openedPartId || _disposed)
        {
            return; // 过滤：只收当前钻取部件
        }

        ApplyStatus(status);
    }

    private void ApplyStatus(StatusEvent status)
    {
        _target.SetLastStatusAt(status.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff"));
        if (status.Payload.TryGetValue("LifeCount", out var life))
        {
            _target.SetLifeCount(life);
        }

        _target.ReplaceIoLines(status.Payload
            .Where(kv => kv.Key != "LifeCount")
            .Select(kv => $"{kv.Key} = {kv.Value}"));

        if (status.Severity == Severity.Alarm)
        {
            _target.PushAlarm($"{status.Timestamp.LocalDateTime:HH:mm:ss}  {status.EventCode}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
    }
}
