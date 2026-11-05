using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using AoiTwin.Core.Analytics;
using AoiTwin.Core.Bus;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;

namespace AoiTwin.Rendering.Fx;

/// <summary>
/// 预警状态 FX（TB.10，FR-M3-09）：订阅 <see cref="TwinTopic.Alert"/>，
/// 在部件包围盒上叠加状态色发光壳——Warning 黄（静态半透明）、Critical 红（呼吸）。
///
/// 与 M2 报警脉冲（AlarmPulseFx，订阅 StatusEvent）**并存不冲突**：
/// 两者使用不同的 <see cref="FxKind"/> 键，PLC 报警与预测预警可以同时亮在同一个部件上。
/// 解除（<see cref="Severity.Info"/>）即摘壳，保证 3D 不会「永远红着」。
/// </summary>
public sealed class AlertStateFx : IDisposable
{
    /// <summary>Warning 状态色（与 assets/light-channel-colors.json 的 stateColors.Warning 一致）。</summary>
    public static readonly Color WarningColor = Color.FromRgb(0xFF, 0xC5, 0x3D);

    /// <summary>Critical 状态色。</summary>
    public static readonly Color CriticalColor = Color.FromRgb(0xFF, 0x47, 0x57);

    private const int PulsePeriodMs = 1000;

    private readonly TwinViewport3D _viewport;
    private readonly Func<SceneAssembly?> _assemblyProvider;
    private readonly FxOverlay _overlay;
    private readonly DispatcherTimer _timer;
    private readonly IDisposable _subscription;
    private readonly Dictionary<string, Active> _active = new(StringComparer.Ordinal);
    private readonly List<SolidColorBrush> _pulseBrushes = [];
    private long _tick;
    private bool _disposed;

    public AlertStateFx(
        TwinViewport3D viewport,
        IRealtimeHub hub,
        Func<SceneAssembly?> assemblyProvider,
        FxOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(assemblyProvider);
        ArgumentNullException.ThrowIfNull(overlay);

        _viewport = viewport;
        _assemblyProvider = assemblyProvider;
        _overlay = overlay;
        _subscription = hub.Subscribe<AlertEvent>(TwinTopic.Alert, OnAlert);

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => OnTick();
    }

    public int ActiveCount => _active.Count;

    public int WarningCount => _active.Values.Count(a => a.Severity == Severity.Warning);

    public int CriticalCount => _active.Values.Count(a => a.Severity == Severity.Alarm);

    /// <summary>状态色映射（预警等级 → 视觉色）；Info 表示解除，返回 null。</summary>
    public static Color? ColorFor(Severity severity) => severity switch
    {
        Severity.Warning => WarningColor,
        Severity.Alarm => CriticalColor,
        _ => null,
    };

    private void OnAlert(AlertEvent alert)
    {
        var color = ColorFor(alert.Severity);
        if (color is null)
        {
            Remove(alert.PartId);
            return;
        }

        Rebuild(alert.PartId, color.Value, alert.Severity);
    }

    private void Rebuild(string partId, Color color, Severity severity)
    {
        _overlay.Detach(FxKind.AlertState, partId);
        _active.Remove(partId);

        if (_assemblyProvider()?.Find(partId) is not { } part)
        {
            return; // 装配未就绪（或该 PartId 不在台账）：不建壳，换档重绑时若仍有预警再补
        }

        // Warning 静态 110/255；Critical 由呼吸定时器接管透明度
        var visual = FxShell.Create(part.Geometry.Bounds, color, 1.05, severity == Severity.Alarm ? (byte)0 : (byte)110, out var brush);
        if (severity == Severity.Alarm)
        {
            _pulseBrushes.Add(brush);
        }

        _overlay.Attach(FxKind.AlertState, partId, visual);
        _active[partId] = new Active(severity, brush);

        if (_pulseBrushes.Count > 0)
        {
            _timer.Start();
        }
    }

    private void Remove(string partId)
    {
        _overlay.Detach(FxKind.AlertState, partId);
        if (_active.Remove(partId, out var active))
        {
            _pulseBrushes.Remove(active.Brush);
        }

        if (_pulseBrushes.Count == 0)
        {
            _timer.Stop();
        }
    }

    private void OnTick()
    {
        _tick++;
        var phase = 2 * Math.PI * (_tick * 16 % PulsePeriodMs) / PulsePeriodMs;
        var alpha = (byte)Math.Max(0, Math.Sin(phase) * 170);
        foreach (var brush in _pulseBrushes)
        {
            brush.Color = Color.FromArgb(alpha, CriticalColor.R, CriticalColor.G, CriticalColor.B);
        }
    }

    /// <summary>LOD 换档后重建壳（装配实例变化时由视图层调用）。</summary>
    public void Refresh()
    {
        foreach (var (partId, active) in _active.ToArray())
        {
            Rebuild(partId, active.Severity == Severity.Alarm ? CriticalColor : WarningColor, active.Severity);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscription.Dispose();
        _timer.Stop();
        foreach (var partId in _active.Keys.ToArray())
        {
            Remove(partId);
        }

        _pulseBrushes.Clear();
    }

    private sealed record Active(Severity Severity, SolidColorBrush Brush);
}
