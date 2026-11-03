using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Rendering.Diagnostics;
using HelixToolkit.Wpf;

namespace AoiTwin.Rendering.Viewport;

/// <summary>
/// 3D 视口宿主（T1.3）：封装 HelixToolkit 视口、相机、光照与渲染循环挂点，向应用层暴露
/// 「挂场景 / 复位视角 / 帧时间探针」三件事。
///
/// 分层约定（PRD §5.7）：本控件不加载资产、不解析台账——T1.4 把 GLB 生成好的
/// <see cref="ModelVisual3D"/> 挂到 <see cref="SceneRoot"/> 即可。所有场景节点操作都在 UI 线程，
/// 后台线程只负责解析（T1.4 的 GlbAssetLoader）。
///
/// 场景坐标：米制、Z 轴向上（与 docs/asset-spec.md §2 一致），故 ModelUpDirection 设为 (0,0,1)，
/// 相机默认取等轴测视角，空场景也有明确的尺度参照（8m 地面网格 + 设备占地轮廓）。
/// </summary>
public sealed class TwinViewport3D : UserControl
{
    /// <summary>设备占地轮廓尺寸（米）：AOI 整机安装基准面，T1.4 挂载真实模型前的尺度参照。</summary>
    private const double FootprintWidth = 1.6;
    private const double FootprintDepth = 1.2;

    private static readonly Point3D DefaultPosition = new(2.6, -3.2, 2.4);
    private static readonly Point3D DefaultTarget = new(0, 0, 0.65);

    public static readonly DependencyProperty FrameProbeTextProperty = DependencyProperty.Register(
        nameof(FrameProbeText),
        typeof(string),
        typeof(TwinViewport3D),
        new PropertyMetadata("FPS —"));

    public static readonly DependencyProperty IsFrameProbeVisibleProperty = DependencyProperty.Register(
        nameof(IsFrameProbeVisible),
        typeof(bool),
        typeof(TwinViewport3D),
        new PropertyMetadata(true, OnFrameProbeVisibilityChanged));

    public static readonly DependencyProperty HasLongFrameProperty = DependencyProperty.Register(
        nameof(HasLongFrame),
        typeof(bool),
        typeof(TwinViewport3D),
        new PropertyMetadata(false));

    private readonly TextBlock _probeText;
    private readonly Grid _probeBadge;

    public TwinViewport3D()
    {
        Viewport = new HelixViewport3D
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x11, 0x20)),
            ModelUpDirection = new Vector3D(0, 0, 1),
            ShowCoordinateSystem = true,
            CoordinateSystemHeight = 0.8,
            ShowViewCube = true,
            ShowFrameRate = false, // 用本项目的探针（带 P95 与长帧判据），Helix 自带只显示瞬时帧率
            ShowTriangleCountInfo = false,
            ShowCameraInfo = false,
            IsHeadLightEnabled = true,
            ZoomExtentsWhenLoaded = false, // 相机由书签统一管理，避免加载后跳到不可预期的视角
            IsRotationEnabled = true,
            IsPanEnabled = true,
            IsZoomEnabled = true,
            RotateAroundMouseDownPoint = false,
            ZoomAroundMouseDownPoint = true,
            CameraMode = CameraMode.Inspect,
            CameraRotationMode = CameraRotationMode.Turntable,
        };

        Viewport.Children.Add(BuildFloor());
        Viewport.Children.Add(BuildFootprint());
        Viewport.Children.Add(BuildLights());

        SceneRoot = new ModelVisual3D();
        OverlayRoot = new ModelVisual3D();
        Viewport.Children.Add(SceneRoot);
        Viewport.Children.Add(OverlayRoot);

        ResetView();

        _probeText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xB0, 0xCC)),
        };

        _probeBadge = new Grid
        {
            Margin = new Thickness(0, 10, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x11, 0x1A, 0x2B)),
        };
        _probeBadge.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x35, 0x53)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Child = _probeText,
        });

        Probe = new FrameTimeProbe();
        Probe.StatsUpdated += OnStatsUpdated;

        var root = new Grid();
        root.Children.Add(Viewport);
        root.Children.Add(_probeBadge);
        root.Children.Add(BuildHint());
        Content = root;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Helix 视口本体（相机手势、视图立方体、坐标轴均由其内置）。</summary>
    public HelixViewport3D Viewport { get; }

    /// <summary>静态场景挂载点（T1.4 挂 GLB 装配树、T1.6 挂 LOD 组）。</summary>
    public ModelVisual3D SceneRoot { get; }

    /// <summary>叠加层挂载点（T1.8 点选描边、选中态高亮等非资产内容）。</summary>
    public ModelVisual3D OverlayRoot { get; }

    /// <summary>帧时间探针：渲染循环挂点（<see cref="FrameTimeProbe.Frame"/>）+ 滚动统计。</summary>
    public FrameTimeProbe Probe { get; }

    /// <summary>每渲染帧回调（M2 运动学插值挂点；UI 线程，不得阻塞）。</summary>
    public event EventHandler<TimeSpan>? FrameRendered;

    /// <summary>帧探针文本（如「FPS 60 · P95 16.8ms」），供应用层状态条绑定。</summary>
    public string FrameProbeText
    {
        get => (string)GetValue(FrameProbeTextProperty);
        set => SetValue(FrameProbeTextProperty, value);
    }

    public bool IsFrameProbeVisible
    {
        get => (bool)GetValue(IsFrameProbeVisibleProperty);
        set => SetValue(IsFrameProbeVisibleProperty, value);
    }

    /// <summary>窗口内是否出现 &gt; 100ms 长帧（NFR-02 判据）；出现即告警色显示。</summary>
    public bool HasLongFrame
    {
        get => (bool)GetValue(HasLongFrameProperty);
        set => SetValue(HasLongFrameProperty, value);
    }

    /// <summary>复位到默认等轴测视角（不做动画；T1.5 的书签切换才带过渡）。</summary>
    public void ResetView()
    {
        Viewport.Camera = new PerspectiveCamera(DefaultPosition, DefaultTarget - DefaultPosition, new Vector3D(0, 0, 1), 45);
    }

    /// <summary>按当前场景包围盒贴合视角（T1.4 加载完成后调用）。</summary>
    public void ZoomExtents() => Viewport.ZoomExtents(0);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Probe.Frame -= OnFrame;
        Probe.Frame += OnFrame;
        Probe.Reset();
        Probe.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Probe.Frame -= OnFrame;
        Probe.Stop();
    }

    private void OnFrame(object? sender, TimeSpan delta) => FrameRendered?.Invoke(this, delta);

    private void OnStatsUpdated(object? sender, FrameTimeStats stats)
    {
        // 长帧判据：窗口内最大值超 100ms 即提示（NFR-02 加载/GC 卡顿的可见证据）
        HasLongFrame = stats.MaxFrameMs > 100;
        FrameProbeText = stats.HasSamples
            ? $"FPS {stats.AverageFps:F0} · P95 {stats.P95FrameMs:F1}ms"
            : "FPS —";
        _probeText.Text = FrameProbeText;
        _probeText.Foreground = new SolidColorBrush(HasLongFrame
            ? Color.FromRgb(0xF5, 0xA6, 0x23)
            : Color.FromRgb(0x9A, 0xB0, 0xCC));
    }

    private static void OnFrameProbeVisibilityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TwinViewport3D viewport)
        {
            viewport._probeBadge.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>地面网格：1m 主格 + 0.25m 次格，8m × 8m，给空场景一个可靠的尺度参照。</summary>
    private static GridLinesVisual3D BuildFloor() => new()
    {
        Width = 8,
        Length = 8,
        MajorDistance = 1,
        MinorDistance = 0.25,
        Thickness = 0.008,
        Fill = new SolidColorBrush(Color.FromRgb(0x2A, 0x3D, 0x5F)),
    };

    /// <summary>
    /// 设备占地轮廓（安装基准面），T1.4 挂载真实模型后保留作为对位参考。
    /// 注意 LinesVisual3D 按「点对」绘制线段：闭合轮廓必须显式回到起点（8 点 = 4 段）。
    /// </summary>
    private static LinesVisual3D BuildFootprint()
    {
        var halfW = FootprintWidth / 2;
        var halfD = FootprintDepth / 2;
        const double z = 0.002;
        var points = new Point3DCollection
        {
            new(-halfW, -halfD, z), new(halfW, -halfD, z), // 前边
            new(halfW, -halfD, z), new(halfW, halfD, z),   // 右边
            new(halfW, halfD, z), new(-halfW, halfD, z),   // 后边
            new(-halfW, halfD, z), new(-halfW, -halfD, z), // 左边
        };

        return new LinesVisual3D
        {
            Points = points,
            Color = Color.FromRgb(0x3E, 0x8B, 0xFF),
            Thickness = 1.6,
        };
    }

    /// <summary>三向主光 + 环境光：产线暗光环境下仍要能分辨部件轮廓（M1 统一 Blinn-Phong 级材质）。</summary>
    private static SunLight BuildLights() => new()
    {
        Ambient = 0.45,
        Brightness = 0.85,
        Altitude = 55,
        Azimuth = 35,
    };

    private static UIElement BuildHint()
    {
        var hint = new TextBlock
        {
            Text = "左键旋转 · 右键平移 · 滚轮缩放 · 双击复位（Z 轴向上，单位：米）",
            FontSize = 11,
            Margin = new Thickness(14, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x7A, 0x99)),
            IsHitTestVisible = false,
        };

        return hint;
    }
}
