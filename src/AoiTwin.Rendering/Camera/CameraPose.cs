using System.Windows.Media.Media3D;

namespace AoiTwin.Rendering.Camera;

/// <summary>
/// 相机位姿（T1.5）：位置 + 观察目标点 + 垂直视场角，与具体相机类型解耦，便于持久化与插值。
///
/// 插值按「绕目标点球面插值」而非位置直线插值：直线插值会让相机穿过设备（穿模），
/// 球面插值等价于操作员的环绕手势，观感自然且不会撞进模型内部。
/// </summary>
public readonly record struct CameraPose(Point3D Position, Point3D Target, double FieldOfView = 45)
{
    /// <summary>默认垂直视场角（度）。</summary>
    public const double DefaultFieldOfView = 45;

    public Vector3D LookDirection => Target - Position;

    public double Distance => LookDirection.Length;

    /// <summary>位姿是否可用（位置与目标不得重合）。</summary>
    public bool IsValid => Distance > 1e-4 && double.IsFinite(FieldOfView) && FieldOfView is > 0 and < 180;

    public static CameraPose FromCamera(ProjectionCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var fov = camera is PerspectiveCamera perspective ? perspective.FieldOfView : DefaultFieldOfView;
        return new CameraPose(camera.Position, camera.Position + camera.LookDirection, fov);
    }

    /// <summary>生成一台新的透视相机（不影响现有相机实例）。</summary>
    public PerspectiveCamera ToCamera()
        => new(Position, LookDirection, new Vector3D(0, 0, 1), FieldOfView);

    /// <summary>把位姿写回既有相机实例（Helix 视口靠相机属性变更重绘，不能换实例）。</summary>
    public void ApplyTo(ProjectionCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);

        camera.Position = Position;
        camera.LookDirection = LookDirection;

        if (camera is PerspectiveCamera perspective && double.IsFinite(FieldOfView) && FieldOfView > 0)
        {
            perspective.FieldOfView = FieldOfView;
        }
    }

    /// <summary>
    /// 线性插值（<paramref name="t"/> 已含缓动）：目标点直线插值、相机相对目标点的偏移量球面插值。
    /// t &lt;= 0 返回自身，t &gt;= 1 返回 <paramref name="to"/>（端点严格相等，避免切换后残留抖动）。
    /// </summary>
    public CameraPose Interpolate(CameraPose to, double t)
    {
        if (t <= 0)
        {
            return this;
        }

        if (t >= 1)
        {
            return to;
        }

        var target = Lerp(Target, to.Target, t);

        var fromOffset = Position - Target;
        var toOffset = to.Position - to.Target;

        var a = Spherical.From(fromOffset);
        var b = Spherical.From(toOffset);

        // 方位角走最短弧（跨 ±π 时不绕远路）
        var deltaTheta = NormalizeAngle(b.Theta - a.Theta);
        var offset = Spherical.To(
            a.Radius + ((b.Radius - a.Radius) * t),
            a.Theta + (deltaTheta * t),
            a.Phi + ((b.Phi - a.Phi) * t));

        var fov = FieldOfView + ((to.FieldOfView - FieldOfView) * t);
        return new CameraPose(target + offset, target, fov);
    }

    /// <summary>两个位姿之间的「视角变化程度」（0 = 完全一致，1 = 反向或距离差 2 倍以上）。</summary>
    public double ChangeRatio(CameraPose to)
    {
        var a = Spherical.From(Position - Target);
        var b = Spherical.From(to.Position - to.Target);

        var angle = Math.Abs(NormalizeAngle(b.Theta - a.Theta)) / Math.PI;      // 0..1
        var tilt = Math.Abs(b.Phi - a.Phi) / Math.PI;                            // 0..1
        var move = Math.Abs(b.Radius - a.Radius) / Math.Max(Math.Max(a.Radius, b.Radius), 1e-6);
        var pan = (to.Target - Target).Length / Math.Max(Math.Max(a.Radius, b.Radius), 1e-6);

        return Math.Clamp(Math.Max(Math.Max(angle, tilt), Math.Max(move, pan)), 0, 1);
    }

    public override string ToString()
        => $"({Position.X:F2}, {Position.Y:F2}, {Position.Z:F2}) → ({Target.X:F2}, {Target.Y:F2}, {Target.Z:F2})";

    private static Point3D Lerp(Point3D a, Point3D b, double t)
        => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));

    /// <summary>把角度归一到 (-π, π]，保证球面插值走最短弧。</summary>
    private static double NormalizeAngle(double angle)
    {
        const double twoPi = Math.PI * 2;
        var normalized = angle % twoPi;
        if (normalized > Math.PI)
        {
            normalized -= twoPi;
        }
        else if (normalized <= -Math.PI)
        {
            normalized += twoPi;
        }

        return normalized;
    }

    /// <summary>相对目标点的球坐标：半径 / 方位角 / 天顶角（0 = +Z 正上方）。</summary>
    private readonly record struct Spherical(double Radius, double Theta, double Phi)
    {
        public static Spherical From(Vector3D offset)
        {
            var radius = offset.Length;
            if (radius < 1e-9)
            {
                return new Spherical(0, 0, 0);
            }

            var theta = Math.Atan2(offset.Y, offset.X);
            var phi = Math.Acos(Math.Clamp(offset.Z / radius, -1, 1));
            return new Spherical(radius, theta, phi);
        }

        public static Vector3D To(double radius, double theta, double phi)
        {
            if (radius < 1e-9)
            {
                return default;
            }

            var sinPhi = Math.Sin(phi);
            return new Vector3D(
                radius * sinPhi * Math.Cos(theta),
                radius * sinPhi * Math.Sin(theta),
                radius * Math.Cos(phi));
        }
    }
}
