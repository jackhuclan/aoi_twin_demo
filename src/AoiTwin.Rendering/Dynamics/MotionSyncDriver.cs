using System.Windows.Media.Media3D;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;
using AoiTwin.ViewModel;

namespace AoiTwin.Rendering.Dynamics;

/// <summary>
/// 运动同步驱动（TA.4，FR-M2-03）：把 <see cref="TwinVisualViewModel"/> 里的双缓冲位姿
/// 按帧写入场景节点的 <c>TranslateTransform3D</c>——只改 Offset，不重建场景树。
///
/// 帧循环（UI 线程，挂在 <c>Viewport.FrameRendered</c>）：
/// 1. <see cref="TwinVisualViewModel.RefreshMotion"/>：位姿引擎按当前时刻插值 → 刷入各部件 VM；
/// 2. 遍历绑定表把 ReadLatest() 的平移写进 Transform。
///
/// 绑定表随装配重建（LOD 换档 = 新 SceneAssembly 实例）惰性刷新：
/// LOD1 逐件绑定（部件名即 PartId）；LOD2 代理按装配名经 resolver 找组内驱动件，
/// 固定结构组不绑定（resolver 返回 null）。
/// </summary>
public sealed class MotionSyncDriver : IDisposable
{
    private readonly TwinViewport3D _viewport;
    private readonly TwinVisualViewModel _visualViewModel;
    private readonly Func<SceneAssembly?> _assemblyProvider;
    private readonly Func<string, string?> _drivePartResolver;
    private List<(TranslateTransform3D Transform, IMotionPartViewModel Part)>? _bindings;
    private SceneAssembly? _boundAssembly;
    private bool _disposed;

    public MotionSyncDriver(
        TwinViewport3D viewport,
        TwinVisualViewModel visualViewModel,
        Func<SceneAssembly?> assemblyProvider,
        Func<string, string?> drivePartResolver)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(visualViewModel);
        ArgumentNullException.ThrowIfNull(assemblyProvider);
        ArgumentNullException.ThrowIfNull(drivePartResolver);

        _viewport = viewport;
        _visualViewModel = visualViewModel;
        _assemblyProvider = assemblyProvider;
        _drivePartResolver = drivePartResolver;

        viewport.FrameRendered += OnViewportFrameRendered;
    }

    /// <summary>当前绑定数（测试/诊断用）。</summary>
    public int BindingCount => _bindings?.Count ?? 0;

    private void OnViewportFrameRendered(object? sender, TimeSpan e) => OnFrame();

    /// <summary>单帧驱动（公开供无头测试直接调用）。</summary>
    public void OnFrame()
    {
        if (_disposed)
        {
            return;
        }

        var assembly = _assemblyProvider();
        if (!ReferenceEquals(assembly, _boundAssembly))
        {
            Rebind(assembly);
        }

        if (_bindings is null || _bindings.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _visualViewModel.RefreshMotion(now);

        foreach (var (transform, part) in _bindings)
        {
            var pose = part.ReadLatest();
            if (ReferenceEquals(pose, PoseSnapshot.Identity))
            {
                continue; // 尚无数据：保持装配态，不做属性写（避免无谓的失效重绘）
            }

            transform.OffsetX = pose.Matrix[3];
            transform.OffsetY = pose.Matrix[7];
            transform.OffsetZ = pose.Matrix[11];
        }
    }

    private void Rebind(SceneAssembly? assembly)
    {
        _boundAssembly = assembly;
        if (assembly is null)
        {
            _bindings = null;
            return;
        }

        var bindings = new List<(TranslateTransform3D, IMotionPartViewModel)>(assembly.Parts.Count);
        foreach (var visual in assembly.Parts)
        {
            // 装配名 → 驱动 PartId：LOD1 原样（resolver 透传），LOD2 代理映射到组内运动件
            var partId = _drivePartResolver(visual.PartId);
            if (string.IsNullOrEmpty(partId))
            {
                continue; // 固定结构组 / 解析不到：不驱动
            }

            bindings.Add((visual.Motion, _visualViewModel.GetMotionPart(partId)));
        }

        _bindings = bindings;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewport.FrameRendered -= OnViewportFrameRendered;
    }
}
