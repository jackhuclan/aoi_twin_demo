using System.Windows;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using AoiTwin.App.ViewModels;
using AoiTwin.Rendering.Camera;

namespace AoiTwin.App.Views;

/// <summary>
/// 3D 场景页（T1.3/T1.4/T1.5）：视口交互属视图行为（等同 ScrollViewer 的滚动语义），
/// 场景装配状态由 <see cref="TwinSceneViewModel"/> 承载；本类只负责把视口的挂载点交给视图模型，
/// 并在首次可见时触发一次后台加载、把书签请求翻译成相机过渡。
/// </summary>
public partial class TwinView : UserControl
{
    private bool _loadRequested;
    private TwinSceneViewModel? _viewModel;
    private Rendering.Interaction.SelectionOutlineFx? _outline;
    private Rendering.Dynamics.MotionSyncDriver? _motionDriver;

    public TwinView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Viewport.FrameRendered += OnViewportFrame;
        Viewport.SceneClicked += OnSceneClicked;
        BuildPlaceholderWaferMap();
    }

    /// <summary>视口实例（测试与场景挂载需要编程访问）。</summary>
    public Rendering.Viewport.TwinViewport3D TwinViewport => Viewport;

    /// <summary>运动同步驱动（TA.4，测试直接驱动单帧用；随 DataContext 生命周期重建）。</summary>
    public Rendering.Dynamics.MotionSyncDriver? MotionDriver => _motionDriver;

    /// <summary>渲染帧 → 自动 LOD 评估（VM 内部按 250ms 节流，开销可忽略）。</summary>
    private void OnViewportFrame(object? sender, TimeSpan delta)
        => _viewModel?.OnCameraMoved(Viewport.Camera.Position);

    /// <summary>点选（T1.8）：射线求交 → 选中部件（空白处点击 = 清除选中）。</summary>
    private void OnSceneClicked(object? sender, Point position)
    {
        if (_viewModel?.Package is not { } package)
        {
            return;
        }

        var ray = Viewport.RayFrom(position);
        var hit = BuildPicker(package).Pick(ray.Origin, ray.Direction);
        _viewModel.SelectPart(hit?.PartId);
    }

    /// <summary>
    /// 组装拾取集合：LOD1 全保真几何 + 部件节点当前的世界变换（M2 运动后点选仍然准确）。
    /// 每次点击重建（10 个条目的构建成本远低于维护失效状态的复杂度）。
    /// </summary>
    private Rendering.Interaction.PartPicker BuildPicker(AoiTwin.Rendering.Lod.LodPackage package)
    {
        var parts = package.PickParts.Select(part =>
        {
            var motion = _viewModel?.Assembly?.Find(part.PartId)?.Motion;
            var world = motion?.Value ?? System.Windows.Media.Media3D.Matrix3D.Identity;
            return new Rendering.Interaction.PickPart(part.PartId, part.Geometry, world);
        });

        return new Rendering.Interaction.PartPicker(parts);
    }

    /// <summary>选中变化 → 描边跟随（LOD2 档也用 LOD1 几何描边，保证轮廓完整）。</summary>
    private void OnSelectionChanged(object? sender, string? partId)
    {
        if (_outline is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(partId))
        {
            _outline.Clear();
            return;
        }

        var part = _viewModel?.Package?.Near.Find(partId);
        if (part is null)
        {
            _outline.Clear();
            return;
        }

        var motion = _viewModel?.Assembly?.Find(partId)?.Motion;
        var world = motion?.Value ?? System.Windows.Media.Media3D.Matrix3D.Identity;
        _outline.Show(part.Geometry, world);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.BookmarkRequested -= OnBookmarkRequested;
            _viewModel.SelectionChanged -= OnSelectionChanged;
        }

        // 驱动器跟随上一份 DataContext 生命周期释放
        _motionDriver?.Dispose();
        _motionDriver = null;

        _viewModel = e.NewValue as TwinSceneViewModel;

        if (_viewModel is not null)
        {
            _viewModel.BookmarkRequested += OnBookmarkRequested;
            _viewModel.SelectionChanged += OnSelectionChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplyImmersive(_viewModel.IsImmersive);

            // 叠加层跟随视图生命周期重建
            _outline = new Rendering.Interaction.SelectionOutlineFx(Viewport.OverlayRoot);

            // 运动同步（TA.4）：帧循环挂进视口，绑定表随 LOD 装配惰性刷新
            _motionDriver = new Rendering.Dynamics.MotionSyncDriver(
                Viewport,
                _viewModel.Visual,
                () => _viewModel.Assembly,
                _viewModel.ResolveDrivePartId);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TwinSceneViewModel.IsImmersive) && _viewModel is not null)
        {
            ApplyImmersive(_viewModel.IsImmersive);
        }
    }

    /// <summary>
    /// 沉浸全屏（T4.1 FR-M1-15）：隐藏右侧停靠区、分栏条与书签栏，3D 铺满。
    /// 仅改可视性，不动视口本身——布局切换对 3D 帧率零影响（无场景重建）。
    /// </summary>
    private void ApplyImmersive(bool immersive)
    {
        var dockVisibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        RightDock.Visibility = dockVisibility;
        DockSplitter.Visibility = dockVisibility;
        BookmarkBar.Visibility = dockVisibility;

        // 折叠右侧列本身（列宽不随子元素可视性变化）
        ContentGrid.ColumnDefinitions[2].Width = immersive ? new GridLength(0) : new GridLength(290);
    }

    /// <summary>图像映射占位（T4.1）：8×8 晶圆示意数据，M2 换成实时结果流。</summary>
    private void BuildPlaceholderWaferMap()
    {
        // 稳定的伪随机：同一版式每次渲染一致（截图核对与验收都要求可复现）
        var random = new Random(20261103);
        var dies = new[]
        {
            Brushes.Transparent,
            new SolidColorBrush(Color.FromRgb(0x24, 0x40, 0x36)),
            new SolidColorBrush(Color.FromRgb(0x2E, 0x54, 0x44)),
            new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
            new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)),
        };

        foreach (var _ in Enumerable.Range(0, 64))
        {
            var roll = random.NextDouble();
            var brush = roll switch
            {
                < 0.06 => dies[3],   // 缺陷
                < 0.12 => dies[4],   // 边缘临界
                < 0.25 => dies[1],
                _ => dies[2],        // 良品
            };

            WaferMapStrip.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Fill = brush,
                RadiusX = 2,
                RadiusY = 2,
                Margin = new Thickness(1.5),
            });
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadRequested || DataContext is not TwinSceneViewModel viewModel)
        {
            return;
        }

        _loadRequested = true;
        await viewModel.LoadAsync(Viewport.SceneRoot);
    }

    /// <summary>书签切换：统一走视口过渡（时长由位姿变化量决定，硬顶 760ms）。</summary>
    private void OnBookmarkRequested(object? sender, CameraPose pose)
    {
        if (Viewport is null)
        {
            return;
        }

        Viewport.AnimateTo(pose);
    }

    private void OnBookmarkSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || BookmarkCombo.SelectedItem is not CameraBookmark bookmark)
        {
            return;
        }

        // 保存/删除后的程序性重选不触发过渡（否则视角会被重置回列表默认项）
        _viewModel.SelectedBookmark = bookmark;
        if (!_viewModel.IsRefreshingBookmarks)
        {
            _viewModel.ActivateSelectedBookmark();
        }
    }

    private void OnSaveBookmarkClick(object sender, RoutedEventArgs e)
        => _viewModel?.SaveBookmark(Viewport.CapturePose());

    private void OnDeleteBookmarkClick(object sender, RoutedEventArgs e)
        => _viewModel?.DeleteSelectedBookmark();

    /// <summary>LOD 模式切换（XAML 里 SelectedIndex=0 会在初始化时触发一次，此时视图模型未就绪）。</summary>
    private void OnLodModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || LodCombo is null)
        {
            return;
        }

        _viewModel.SetLodMode(LodCombo.SelectedIndex switch
        {
            1 => TwinLodMode.Near,
            2 => TwinLodMode.Far,
            _ => TwinLodMode.Auto,
        });
    }

    private void OnResetViewClick(object sender, RoutedEventArgs e) => Viewport.ResetView();

    private void OnToggleProbeClick(object sender, RoutedEventArgs e)
    {
        Viewport.IsFrameProbeVisible = !Viewport.IsFrameProbeVisible;
        ProbeToggleButton.Content = Viewport.IsFrameProbeVisible ? "隐藏帧探针" : "显示帧探针";
    }
}
