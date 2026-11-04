using System.ComponentModel;
using System.Runtime.CompilerServices;
using AoiTwin.Core.Bus;

namespace AoiTwin.ViewModel;

/// <summary>
/// 装载状态视图模型（TA.16，FR-M2-13）：Track 晶圆占位 + LoadPort Cassette 装载片数。
///
/// 数据契约（Status Topic，Payload 字典）：
/// - <c>TRACK</c>：<c>WaferPresent</c> ∈ {"1","true","yes","on"}（大小写不敏感）；
/// - <c>LOAD_PORT</c>：<c>CassetteCount</c>（已装片数）、<c>CassetteCapacity</c>（总槽位，缺省 25）。
/// 缺键/非法值一律保持旧值（NFR-15 降级语义），不让脏数据把 3D 状态打回零。
/// </summary>
public sealed class HandlingStateViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>轨道部件（晶圆占位信号来源）。</summary>
    public const string TrackPartId = "TRACK";

    /// <summary>上下料口部件（Cassette 装载状态来源）。</summary>
    public const string LoadPortPartId = "LOAD_PORT";

    /// <summary>Cassette 标准容量（PRD FR-M2-13：25 片）。</summary>
    public const int DefaultCassetteCapacity = 25;

    private readonly IDisposable _subscription;
    private bool _waferOnTrack;
    private int _cassetteCount;
    private int _cassetteCapacity = DefaultCassetteCapacity;
    private bool _disposed;

    public HandlingStateViewModel(IRealtimeHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _subscription = hub.Subscribe<StatusEvent>(TwinTopic.Status, OnStatus);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>状态发生变化（3D 驱动按 1s 节流消费，见 <c>HandlingSyncDriver</c>）。</summary>
    public event EventHandler? Changed;

    /// <summary>轨道上是否有晶圆。</summary>
    public bool WaferOnTrack
    {
        get => _waferOnTrack;
        private set => _waferOnTrack = value;
    }

    /// <summary>Cassette 已装片数（0..Capacity）。</summary>
    public int CassetteCount
    {
        get => _cassetteCount;
        private set => _cassetteCount = value;
    }

    /// <summary>Cassette 总槽位。</summary>
    public int CassetteCapacity
    {
        get => _cassetteCapacity;
        private set => _cassetteCapacity = value;
    }

    /// <summary>状态文本（页头徽标）：Track 有片/空 + Cassette 计数。</summary>
    public string Caption => $"Track {(WaferOnTrack ? "有片" : "空")} · Cassette {CassetteCount}/{CassetteCapacity}";

    /// <summary>收到的有效状态事件数（诊断用）。</summary>
    public int UpdateCount { get; private set; }

    private void OnStatus(StatusEvent status)
    {
        var changed = false;
        if (string.Equals(status.PartId, TrackPartId, StringComparison.Ordinal))
        {
            if (status.Payload.TryGetValue("WaferPresent", out var raw) && TryParseFlag(raw, out var present)
                && present != _waferOnTrack)
            {
                _waferOnTrack = present;
                Raise(nameof(WaferOnTrack));
                changed = true;
            }
        }
        else if (string.Equals(status.PartId, LoadPortPartId, StringComparison.Ordinal))
        {
            var capacity = _cassetteCapacity;
            if (status.Payload.TryGetValue("CassetteCapacity", out var capRaw)
                && int.TryParse(capRaw, out var parsedCapacity) && parsedCapacity > 0)
            {
                capacity = parsedCapacity;
            }

            if (status.Payload.TryGetValue("CassetteCount", out var countRaw)
                && int.TryParse(countRaw, out var parsedCount) && parsedCount >= 0)
            {
                var count = Math.Clamp(parsedCount, 0, capacity);
                if (count != _cassetteCount || capacity != _cassetteCapacity)
                {
                    _cassetteCount = count;
                    _cassetteCapacity = capacity;
                    Raise(nameof(CassetteCount));
                    Raise(nameof(CassetteCapacity));
                    changed = true;
                }
            }
            else if (capacity != _cassetteCapacity)
            {
                _cassetteCapacity = capacity;
                _cassetteCount = Math.Min(_cassetteCount, capacity);
                Raise(nameof(CassetteCapacity));
                Raise(nameof(CassetteCount));
                changed = true;
            }
        }
        else
        {
            return; // 其它部件状态与装载无关
        }

        if (!changed)
        {
            return;
        }

        UpdateCount++;
        Raise(nameof(Caption));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>布尔字面量解析（PLC 侧习惯用 1/0，同时兼容 true/on）。</summary>
    private static bool TryParseFlag(string raw, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                value = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                return true;
            default:
                return false;
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscription.Dispose();
    }
}
