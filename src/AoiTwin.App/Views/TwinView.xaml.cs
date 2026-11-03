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

    public TwinView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Viewport.FrameRendered += OnViewportFrame;
    }

    /// <summary>视口实例（测试与场景挂载需要编程访问）。</summary>
    public Rendering.Viewport.TwinViewport3D TwinViewport => Viewport;

    /// <summary>渲染帧 → 自动 LOD 评估（VM 内部按 250ms 节流，开销可忽略）。</summary>
    private void OnViewportFrame(object? sender, TimeSpan delta)
        => _viewModel?.OnCameraMoved(Viewport.Camera.Position);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.BookmarkRequested -= OnBookmarkRequested;
        }

        _viewModel = e.NewValue as TwinSceneViewModel;

        if (_viewModel is not null)
        {
            _viewModel.BookmarkRequested += OnBookmarkRequested;
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
