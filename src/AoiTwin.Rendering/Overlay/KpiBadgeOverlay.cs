using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AoiTwin.Rendering.Overlay;

/// <summary>KPI 标注（TB.15）：世界坐标 + 文本 + 部件寻址。</summary>
public sealed record KpiBadge(string PartId, string Text, Point3D World);

/// <summary>
/// 世界坐标 → 屏幕投影（TB.15）。纯数学，不依赖可视树，因此可单测——
/// 这是 UI3D 标注最容易出错的部分（坐标系手性、w 分量、Y 轴翻转），
/// 把它从 Adorner 里拆出来是唯一能稳定验证的办法。
///
/// 约定：WPF 左手系，相机看向 +Z，Y 向上；传入的矩阵为 world→clip（行向量约定）。
/// </summary>
public static class WorldToScreenProjector
{
    /// <summary>由相机参数构造 world→clip 矩阵（透视）。</summary>
    public static Matrix3D FromCamera(
        Point3D eye,
        Vector3D look,
        Vector3D up,
        double fovYDegrees,
        double aspect,
        double nearPlane = 0.01)
    {
        if (look.Length < 1e-9)
        {
            throw new ArgumentException("视线方向不能为零向量", nameof(look));
        }

        if (aspect <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(aspect));
        }

        if (fovYDegrees is <= 0 or >= 180)
        {
            throw new ArgumentOutOfRangeException(nameof(fovYDegrees));
        }

        var zAxis = look;
        zAxis.Normalize();

        var xAxis = Vector3D.CrossProduct(up, zAxis);
        if (xAxis.Length < 1e-9)
        {
            xAxis = Vector3D.CrossProduct(new Vector3D(1, 0, 0), zAxis);
        }

        xAxis.Normalize();
        var yAxis = Vector3D.CrossProduct(zAxis, xAxis);
        yAxis.Normalize();

        var eyeVector = new Vector3D(eye.X, eye.Y, eye.Z);
        var view = Matrix3D.Identity;
        view.M11 = xAxis.X;
        view.M21 = xAxis.Y;
        view.M31 = xAxis.Z;
        view.OffsetX = -Vector3D.DotProduct(eyeVector, xAxis);

        view.M12 = yAxis.X;
        view.M22 = yAxis.Y;
        view.M32 = yAxis.Z;
        view.OffsetY = -Vector3D.DotProduct(eyeVector, yAxis);

        view.M13 = zAxis.X;
        view.M23 = zAxis.Y;
        view.M33 = zAxis.Z;
        view.OffsetZ = -Vector3D.DotProduct(eyeVector, zAxis);

        var f = 1.0 / Math.Tan(fovYDegrees * Math.PI / 180.0 / 2.0);
        var projection = Matrix3D.Identity;
        projection.M11 = f / aspect;
        projection.M22 = f;
        projection.M34 = 1; // w' = z_view（相机前方为正）
        projection.M44 = 0;
        projection.M33 = 1;

        view.Append(projection);
        _ = nearPlane; // near/far 只影响深度，本项目用 z_view>0 做近裁剪即可
        return view;
    }

    /// <summary>
    /// 投影到屏幕像素坐标；落在相机后方返回 null（调用方据此隐藏标注，而不是把标注挤到屏幕边缘）。
    /// </summary>
    public static Point? Project(Matrix3D worldToClip, Point3D world, double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var m = worldToClip;
        var x = (world.X * m.M11) + (world.Y * m.M21) + (world.Z * m.M31) + m.OffsetX;
        var y = (world.X * m.M12) + (world.Y * m.M22) + (world.Z * m.M32) + m.OffsetY;
        var w = (world.X * m.M14) + (world.Y * m.M24) + (world.Z * m.M34) + m.M44;

        if (w <= 1e-9)
        {
            return null;
        }

        var ndcX = x / w;
        var ndcY = y / w;
        if (double.IsNaN(ndcX) || double.IsNaN(ndcY) || double.IsInfinity(ndcX) || double.IsInfinity(ndcY))
        {
            return null;
        }

        return new Point((ndcX + 1) * 0.5 * width, (1 - ndcY) * 0.5 * height);
    }
}

/// <summary>
/// UI3D KPI 标注层（TB.15，FR-M3-11）：把 KPI 文本按世界坐标贴到 3D 视口上方。
///
/// 1s 节流（PRD 要求）：投影每帧算不贵，但 TextBlock 的布局/排版会触发 UI 线程开销，
/// 每帧重排 20 个标注足以把 60fps 打成 45fps——节流是性能措施，不是偷懒。
/// 标注可一键关闭（<see cref="SetVisible"/>），关闭时整层 Visibility=Collapsed，不参与布局。
/// </summary>
public sealed class KpiBadgeOverlay : IDisposable
{
    private readonly Panel _host;
    private readonly Func<Matrix3D?> _matrixProvider;
    private readonly Func<(double Width, double Height)> _sizeProvider;
    private readonly TimeSpan _minInterval;
    private readonly Dictionary<string, TextBlock> _elements = new(StringComparer.Ordinal);
    private IReadOnlyList<KpiBadge> _badges = [];
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private bool _disposed;

    public KpiBadgeOverlay(
        Panel host,
        Func<Matrix3D?> matrixProvider,
        Func<(double Width, double Height)> sizeProvider,
        TimeSpan? minInterval = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _matrixProvider = matrixProvider ?? throw new ArgumentNullException(nameof(matrixProvider));
        _sizeProvider = sizeProvider ?? throw new ArgumentNullException(nameof(sizeProvider));
        _minInterval = minInterval ?? TimeSpan.FromSeconds(1);
    }

    public bool Visible { get; private set; } = true;

    public int RenderedCount => _elements.Count;

    public long RefreshCount { get; private set; }

    public void SetBadges(IReadOnlyList<KpiBadge> badges)
    {
        _badges = badges ?? [];
    }

    public void SetVisible(bool visible)
    {
        Visible = visible;
        _host.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            foreach (var element in _elements.Values)
            {
                element.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>刷新投影与位置；未到节流间隔且非强制时直接返回（返回是否真正刷新）。</summary>
    public bool Refresh(DateTimeOffset? now = null, bool force = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        if (!force && timestamp - _lastRefresh < _minInterval)
        {
            return false;
        }

        _lastRefresh = timestamp;
        RefreshCount++;

        if (!Visible || _matrixProvider() is not { } matrix)
        {
            return true;
        }

        var (width, height) = _sizeProvider();
        var active = new HashSet<string>(StringComparer.Ordinal);

        foreach (var badge in _badges)
        {
            if (WorldToScreenProjector.Project(matrix, badge.World, width, height) is not { } screen)
            {
                continue;
            }

            if (!_elements.TryGetValue(badge.PartId, out var element))
            {
                element = CreateElement();
                _elements[badge.PartId] = element;
                _host.Children.Add(element);
            }

            element.Text = badge.Text;
            element.Visibility = Visibility.Visible;
            Canvas.SetLeft(element, screen.X + 8);
            Canvas.SetTop(element, screen.Y - 10);
            active.Add(badge.PartId);
        }

        foreach (var (key, element) in _elements.ToArray())
        {
            if (!active.Contains(key))
            {
                _host.Children.Remove(element);
                _elements.Remove(key);
            }
        }

        return true;
    }

    private static TextBlock CreateElement() => new()
    {
        FontSize = 11,
        Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3)),
        Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x12, 0x18, 0x20)),
        Padding = new Thickness(4, 1, 4, 1),
        IsHitTestVisible = false,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var element in _elements.Values)
        {
            _host.Children.Remove(element);
        }

        _elements.Clear();
    }
}
