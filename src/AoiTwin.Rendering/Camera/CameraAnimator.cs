using System.Diagnostics;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace AoiTwin.Rendering.Camera;

/// <summary>
/// 相机过渡驱动（T1.5）：按渲染节拍（~60Hz DispatcherTimer）把 <see cref="CameraTransition"/>
/// 的插值位姿写回相机实例。
///
/// 为什么不用 WPF 动画（<c>DoubleAnimation</c>）直接动相机：相机位置是 3 个耦合分量，
/// 且需要球面插值 + 视场角同步，Storyboard 组合出来的关键帧反而更难保证端点严格落位。
/// 这里用单一时间基准（<see cref="Stopwatch"/>）驱动，掉帧只会让中间帧变稀，不会拉长总时长。
/// </summary>
public sealed class CameraAnimator : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private ProjectionCamera? _camera;
    private CameraTransition? _transition;
    private bool _disposed;

    public CameraAnimator()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += OnTick;
    }

    /// <summary>过渡完成（已落到终点位姿）。</summary>
    public event EventHandler<CameraPose>? Completed;

    /// <summary>过渡被新的过渡打断或被取消。</summary>
    public event EventHandler? Cancelled;

    public bool IsRunning => _transition is not null;

    /// <summary>最近一次过渡的实际时长（含插值，测试与帧探针可用）。</summary>
    public TimeSpan? LastDuration { get; private set; }

    /// <summary>
    /// 启动一次过渡；返回本次过渡的标称时长（≤ 760ms）。
    /// 正在运行的过渡会被立即打断（连续点书签不会排队卡顿）。
    /// </summary>
    public TimeSpan AnimateTo(ProjectionCamera camera, CameraPose target)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!target.IsValid)
        {
            throw new ArgumentException("目标相机位姿非法", nameof(target));
        }

        if (_transition is not null)
        {
            _transition = null;
            _timer.Stop();
            Cancelled?.Invoke(this, EventArgs.Empty);
        }

        var from = CameraPose.FromCamera(camera);
        var transition = CameraTransition.Create(from, target);
        LastDuration = transition.Duration;

        _camera = camera;
        _transition = transition;

        // 首帧立即落到起点，避免在旧位姿上多停一帧
        transition.From.ApplyTo(camera);
        _clock.Restart();
        _timer.Start();

        return transition.Duration;
    }

    /// <summary>取消进行中的过渡（相机停在当前插值位置）。</summary>
    public void Cancel()
    {
        if (_transition is null)
        {
            return;
        }

        _transition = null;
        _timer.Stop();
        _clock.Stop();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_transition is not { } transition || _camera is not { } camera)
        {
            _timer.Stop();
            return;
        }

        var progress = transition.Progress(_clock.Elapsed);
        if (progress >= 1)
        {
            // 终点严格落位：保证多次切换后不累积浮点误差
            transition.To.ApplyTo(camera);
            _transition = null;
            _timer.Stop();
            _clock.Stop();
            Completed?.Invoke(this, transition.To);
            return;
        }

        transition.PoseAt(progress).ApplyTo(camera);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _transition = null;
        _camera = null;
    }
}
