namespace AoiTwin.Rendering.Camera;

/// <summary>
/// 相机切换过渡（T1.5）：把「起点位姿 → 终点位姿」包装成带时长与缓动的纯计算对象。
///
/// 时长按视角变化量自适应（小角度快、大跨度接近上限），并**硬性封顶 760ms**，
/// 保证 PRD FR-M1-06「书签切换 ≤ 800ms」在数学上不可能被违反（测试断言此上限）。
/// 缓动用 smoothstep：起步与收尾都无速度突变，视觉上不会「弹一下」。
/// </summary>
public sealed record CameraTransition(CameraPose From, CameraPose To, TimeSpan Duration)
{
    /// <summary>最短过渡时长（毫秒）：再短会显得跳变。</summary>
    public const double MinDurationMs = 240;

    /// <summary>最长过渡时长（毫秒）：验收判据为 800ms，取 760ms 留 40ms 余量。</summary>
    public const double MaxDurationMs = 760;

    /// <summary>按位姿变化量推导过渡（变化越大时长越长）。</summary>
    public static CameraTransition Create(CameraPose from, CameraPose to)
    {
        var change = from.ChangeRatio(to);
        var ms = MinDurationMs + ((MaxDurationMs - MinDurationMs) * change);
        return new CameraTransition(from, to, TimeSpan.FromMilliseconds(Math.Clamp(ms, MinDurationMs, MaxDurationMs)));
    }

    /// <summary>归一化进度（0..1，超时钳制）。</summary>
    public double Progress(TimeSpan elapsed)
        => Duration <= TimeSpan.Zero ? 1 : Math.Clamp(elapsed.Ticks / (double)Duration.Ticks, 0, 1);

    /// <summary>按归一化进度取插值位姿（内部已套缓动）。</summary>
    public CameraPose PoseAt(double t) => From.Interpolate(To, Smoothstep(Math.Clamp(t, 0, 1)));

    /// <summary>smoothstep 缓动：3t² − 2t³。</summary>
    public static double Smoothstep(double t)
    {
        var x = Math.Clamp(t, 0, 1);
        return x * x * (3 - (2 * x));
    }

    public override string ToString()
        => $"{From} → {To} · {Duration.TotalMilliseconds:F0}ms";
}
