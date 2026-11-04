using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Rendering.Viewport;
using HelixToolkit.Wpf;

namespace AoiTwin.Rendering.Fx;

/// <summary>
/// 传输粒子流特效（TA.11，FR-M2-12）：N 个粒子沿指定线段循环推进
/// （晶圆流转的示意动效——Track 装载位 → 检测位）。挂在 FxOverlay 上由调用方控制启停；
/// 推进由 <see cref="OnTick"/> 驱动（视图层帧循环调用），粒子状态是纯数学相位，无分配。
/// </summary>
public sealed class TransportParticlesFx : IDisposable
{
    /// <summary>粒子数：16 个足以表达流动感，几何开销可忽略。</summary>
    public const int ParticleCount = 16;

    private static readonly Color ParticleColor = Color.FromRgb(0x4F, 0xC3, 0xF7);

    private readonly TwinViewport3D _viewport;
    private readonly FxOverlay _overlay;
    private readonly PointsVisual3D _points;
    private readonly FxOverlay.FxHandle _handle;
    private readonly Point3D _from;
    private readonly Point3D _to;
    private double _phase;
    private bool _disposed;

    /// <param name="from">起点（世界坐标）。</param>
    /// <param name="to">终点（世界坐标）。</param>
    public TransportParticlesFx(TwinViewport3D viewport, FxOverlay overlay, Point3D from, Point3D to)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(overlay);

        _viewport = viewport;
        _overlay = overlay;
        _from = from;
        _to = to;

        _points = new PointsVisual3D
        {
            Color = ParticleColor,
            Size = 4,
            Points = new Point3DCollection(ParticleCount),
        };
        _handle = overlay.Attach(FxKind.TransportParticles, "TRACK_FLOW", Wrap(_points));
        UpdatePoints();
    }

    /// <summary>推进一步（视图层帧循环调用；phase 0..1 为整段归一化进度）。</summary>
    public void OnTick(double delta)
    {
        if (_disposed)
        {
            return;
        }

        _phase = (_phase + delta) % 1.0;
        UpdatePoints();
    }

    private void UpdatePoints()
    {
        var span = _to - _from;
        var collection = new Point3DCollection(ParticleCount);
        for (var i = 0; i < ParticleCount; i++)
        {
            // 粒子均匀相位差，沿段循环流动
            var t = (_phase + (double)i / ParticleCount) % 1.0;
            collection.Add(_from + span * t);
        }

        _points.Points = collection;
    }

    private static ModelVisual3D Wrap(PointsVisual3D points)
    {
        var container = new ModelVisual3D();
        container.Children.Add(points);
        return container;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }
}
