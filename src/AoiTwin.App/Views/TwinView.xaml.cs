using System.Windows;
using System.Windows.Controls;
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

    public TwinView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Viewport.FrameRendered += OnViewportFrame;
        Viewport.SceneClicked += OnSceneClicked;
    }

    /// <summary>视口实例（测试与场景挂载需要编程访问）。</summary>
    public Rendering.Viewport.TwinViewport3D TwinViewport => Viewport;

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

        _viewModel = e.NewValue as TwinSceneViewModel;

        if (_viewModel is not null)
        {
            _viewModel.BookmarkRequested += OnBookmarkRequested;
            _viewModel.SelectionChanged += OnSelectionChanged;

            // 叠加层跟随视图生命周期重建
            _outline = new Rendering.Interaction.SelectionOutlineFx(Viewport.OverlayRoot);
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
