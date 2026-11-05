using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Rendering.Viewport;

namespace AoiTwin.Rendering.Fx;

/// <summary>特效种类（M3 预留：只追加不改名，已有值顺序不变——序列化/调试兼容）。</summary>
public enum FxKind
{
    /// <summary>选中描边（M1 SelectionOutlineFx 的接入位，由点选层直接驱动，不经 Attach）。</summary>
    SelectionOutline = 0,

    /// <summary>报警脉冲（FR-M2-12）。</summary>
    AlarmPulse = 1,

    /// <summary>传输粒子流（FR-M2-12）。</summary>
    TransportParticles = 2,

    /// <summary>M3 追加（只增不改）：预警状态色升级 + 脉冲（FR-M3-09）。</summary>
    AlertState = 3,

    /// <summary>M3 追加：缺陷密度热力晕染（FR-M3-13）。</summary>
    DefectHeatmap = 4,

    /// <summary>M3 追加：UI3D KPI 标注（FR-M3-11；2D 叠加层实现，枚举值保留供契约对齐）。</summary>
    KpiBadge = 5,
}

/// <summary>
/// FX 按需渲染框架（TA.10，FR-M2-12）：有则渲染、无则不进 Draw Call。
///
/// FX 视觉统一挂在本框架的 <see cref="_root"/> 下（独立容器，与光学/视野锥/纹理等
/// 常驻数据驱动的叠加层并存）——<see cref="_active"/> 为空时 root 不挂入 OverlayRoot，
/// 特效开关对基线帧率零影响（判据：空 FX 时 FX 子树零子节点）。
/// </summary>
public sealed class FxOverlay : IDisposable
{
    private readonly TwinViewport3D _viewport;
    private readonly ModelVisual3D _root = new();
    private readonly Dictionary<(FxKind Kind, string PartId), ModelVisual3D> _active = [];
    private bool _disposed;

    public FxOverlay(TwinViewport3D viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        _viewport = viewport;
    }

    /// <summary>当前活动特效数（诊断用）。</summary>
    public int ActiveCount => _active.Count;

    /// <summary>
    /// 挂入特效。同一 (kind, partId) 重复挂入 = 替换旧内容；返回句柄，Dispose 即摘除。
    /// </summary>
    public FxHandle Attach(FxKind kind, string partId, ModelVisual3D content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(content);

        var key = (kind, partId);
        if (_active.Remove(key, out var old))
        {
            _root.Children.Remove(old);
        }

        _active[key] = content;
        _root.Children.Add(content);
        EnsureAttached();
        return new FxHandle(this, key, content);
    }

    /// <summary>摘除指定特效（不存在则静默）。</summary>
    public void Detach(FxKind kind, string partId)
    {
        if (_active.Remove((kind, partId), out var content))
        {
            _root.Children.Remove(content);
            EnsureAttached();
        }
    }

    /// <summary>句柄摘除：仅当该键当前内容仍是句柄持有者时才摘（被替换的旧句柄变 no-op）。</summary>
    private void DetachIfCurrent((FxKind Kind, string PartId) key, ModelVisual3D content)
    {
        if (_active.TryGetValue(key, out var current) && ReferenceEquals(current, content))
        {
            Detach(key.Kind, key.PartId);
        }
    }

    /// <summary>摘除全部特效。</summary>
    public void Clear()
    {
        _active.Clear();
        _root.Children.Clear();
        EnsureAttached();
    }

    private void EnsureAttached()
    {
        var attached = _viewport.OverlayRoot.Children.IndexOf(_root) >= 0;
        if (_active.Count > 0 && !attached)
        {
            _viewport.OverlayRoot.Children.Add(_root);
        }
        else if (_active.Count == 0 && attached)
        {
            _viewport.OverlayRoot.Children.Remove(_root);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }

    /// <summary>特效句柄：Dispose 即摘除对应特效（被同键新特效替换后变 no-op）。</summary>
    public sealed class FxHandle(FxOverlay overlay, (FxKind Kind, string PartId) key, ModelVisual3D content) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                overlay.DetachIfCurrent(key, content);
            }
        }
    }
}
