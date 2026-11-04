using System.Windows.Media.Media3D;
using AoiTwin.Rendering.Viewport;

namespace AoiTwin.Rendering.Camera;

/// <summary>
/// 跟随模式（TA.15，FR-M2-11）：视角锁定运动部件实时追踪。
/// 每帧取目标世界坐标，做一阶低通（α ≈ 0.2）平滑——相邻帧位移连续无跳变；
/// 相机与目标的相对偏移保持不变（随目标平移、不旋转），解除后停在当前位置即恢复自由视角。
/// </summary>
public sealed class FollowCamera : IDisposable
{
    /// <summary>低通系数：越大跟得越紧、抖动越多；0.2 在 60fps 下约 80ms 收敛到 90%。</summary>
    public const double SmoothingAlpha = 0.2;

    private readonly TwinViewport3D _viewport;
    private readonly Func<Point3D?> _targetProvider;
    private Vector3D _offset;
    private Point3D _smoothedTarget;
    private bool _following;
    private bool _disposed;

    public FollowCamera(TwinViewport3D viewport, Func<Point3D?> targetProvider)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(targetProvider);

        _viewport = viewport;
        _targetProvider = targetProvider;
        viewport.FrameRendered += OnViewportFrameRendered;
    }

    /// <summary>是否跟随中（诊断用）。</summary>
    public bool IsFollowing => _following;

    private void OnViewportFrameRendered(object? sender, TimeSpan e) => OnFrame();

    /// <summary>单帧驱动（公开供无头测试直接调用）。</summary>
    public void OnFrame()
    {
        if (_disposed || !_following)
        {
            return;
        }

        if (_targetProvider() is not { } desired)
        {
            return; // 目标暂无数据（LOD 换档瞬间）：保持上一帧视角
        }

        var camera = _viewport.Camera;

        _smoothedTarget = Lerp(_smoothedTarget, desired, SmoothingAlpha);
        camera.Position = _smoothedTarget + _offset;
        camera.LookDirection = _smoothedTarget - camera.Position;
    }

    /// <summary>开始跟随当前目标（以当前视角无跳变切入）。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_following || _targetProvider() is not { } desired)
        {
            return;
        }

        var camera = _viewport.Camera;
        _smoothedTarget = desired;
        _offset = camera.Position - desired;
        _following = true;
    }

    /// <summary>解除跟随：相机停在当前位置，视角保持自由（可随时解除）。</summary>
    public void Stop() => _following = false;

    private static Point3D Lerp(Point3D a, Point3D b, double t) => new(
        a.X + ((b.X - a.X) * t),
        a.Y + ((b.Y - a.Y) * t),
        a.Z + ((b.Z - a.Z) * t));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _following = false;
        _viewport.FrameRendered -= OnViewportFrameRendered;
    }
}
