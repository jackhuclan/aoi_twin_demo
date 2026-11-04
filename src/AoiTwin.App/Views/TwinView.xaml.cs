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
    private Rendering.Dynamics.OpticsSyncDriver? _opticsDriver;
    private Rendering.Dynamics.ScanFrustumDriver? _scanFrustumDriver;
    private Rendering.Dynamics.WaferMapDriver? _waferMapDriver;
    private Rendering.Dynamics.DefectPointDriver? _defectPointDriver;
    private Rendering.Fx.FxOverlay? _fxOverlay;
    private Rendering.Fx.AlarmPulseFx? _alarmPulse;
    private Rendering.Fx.TransportParticlesFx? _transportParticles;
    private Rendering.Camera.FollowCamera? _followCamera;
    private Window? _defectImageWindow;

    public TwinView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Viewport.FrameRendered += OnViewportFrame;
        Viewport.SceneClicked += OnSceneClicked;
        Viewport.SceneRightClicked += OnSceneRightClicked;
        DefectListBox.SelectionChanged += OnDefectSelectionChanged;
    }

    /// <summary>视口实例（测试与场景挂载需要编程访问）。</summary>
    public Rendering.Viewport.TwinViewport3D TwinViewport => Viewport;

    /// <summary>运动同步驱动（TA.4，测试直接驱动单帧用；随 DataContext 生命周期重建）。</summary>
    public Rendering.Dynamics.MotionSyncDriver? MotionDriver => _motionDriver;

    /// <summary>光学状态驱动（TA.5，测试直接驱动单帧用）。</summary>
    public Rendering.Dynamics.OpticsSyncDriver? OpticsDriver => _opticsDriver;

    /// <summary>线扫视野锥驱动（TA.6，测试直接驱动单帧用）。</summary>
    public Rendering.Dynamics.ScanFrustumDriver? ScanFrustumDriver => _scanFrustumDriver;

    /// <summary>Wafer Map 纹理驱动（TA.7，测试直接驱动单帧用）。</summary>
    public Rendering.Dynamics.WaferMapDriver? WaferMapDriver => _waferMapDriver;

    /// <summary>缺陷点云驱动（TA.8；TA.14 的 2D→3D 高亮经此接线）。</summary>
    public Rendering.Dynamics.DefectPointDriver? DefectPointDriver => _defectPointDriver;

    /// <summary>FX 框架（TA.10；特效句柄层）。</summary>
    public Rendering.Fx.FxOverlay? FxOverlay => _fxOverlay;

    /// <summary>报警脉冲（TA.11；LOD 换档后调 Refresh 重建壳）。</summary>
    public Rendering.Fx.AlarmPulseFx? AlarmPulse => _alarmPulse;

    /// <summary>跟随模式（TA.15，测试直接驱动单帧用）。</summary>
    public Rendering.Camera.FollowCamera? FollowCamera => _followCamera;

    /// <summary>渲染帧 → 自动 LOD 评估（VM 内部按 250ms 节流，开销可忽略）。</summary>
    private void OnViewportFrame(object? sender, TimeSpan delta)
        => _viewModel?.OnCameraMoved(Viewport.Camera.Position);

    /// <summary>点选（T1.8）：射线求交 → 优先命中缺陷点（TA.14），否则选中部件（空白 = 清除选中）。</summary>
    private void OnSceneClicked(object? sender, Point position)
    {
        if (_viewModel?.Package is not { } package)
        {
            return;
        }

        var ray = Viewport.RayFrom(position);

        // 缺陷优先：点云点是细小目标，命中即视为缺陷选择，不再穿透到部件
        if (_viewModel.DefectLink.IsAttached
            && _viewModel.FocusDefectFrom3D(ray.Origin, ray.Direction))
        {
            DockTab2.IsChecked = true; // 切到缺陷列表页签（列表已滚到目标行）
            return;
        }

        var hit = BuildPicker(package).Pick(ray.Origin, ray.Direction);
        _viewModel.SelectPart(hit?.PartId);
    }

    /// <summary>2D 列表选中：把选中行滚进可视区（选中本身由绑定完成，3D 高亮由服务完成）。</summary>
    private void OnDefectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DefectListBox.SelectedItem is { } item)
        {
            DefectListBox.ScrollIntoView(item);
        }
    }

    /// <summary>打开原图窗口（TA.14 终点）：按选中缺陷取行，重复打开复用同一窗口。</summary>
    private void OnOpenImageClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.DefectList.Selected is not { } row)
        {
            return;
        }

        _viewModel.DefectLink.OpenImage(row.DefectId);
    }

    /// <summary>原图窗口请求：非模态展示，避免挡住 3D 交互（点选下一个缺陷即刷新内容）。</summary>
    private void OnImageRequested(object? sender, AoiTwin.ViewModel.DefectRow row)
    {
        if (_viewModel is null)
        {
            return;
        }

        var viewModel = new DefectImageViewModel(row, _viewModel.DefectList.WaferId);

        // 未入宿主窗口（无头测试 / 尚未挂到 MainWindow）时不弹窗：联动计数在服务层已完成，
        // 这里只负责可视化，避免测试环境里产生游离顶层窗口
        if (Window.GetWindow(this) is not { } owner)
        {
            return;
        }

        if (_defectImageWindow is Views.DefectImageWindow existing && existing.IsLoaded)
        {
            existing.DataContext = viewModel;
            existing.Title = $"缺陷原图 · #{viewModel.DefectId}";
            existing.Activate();
            return;
        }

        _defectImageWindow = new Views.DefectImageWindow(viewModel) { Owner = owner };
        _defectImageWindow.Closed += (_, _) => _defectImageWindow = null;
        _defectImageWindow.Show();
    }

    /// <summary>右键诊断跳转（TA.13 FR-M2-09）：拾取部件 → VM 按类型分类 → 切到诊断页签。</summary>
    private void OnSceneRightClicked(object? sender, System.Windows.Point position)
    {
        if (_viewModel?.Package is not { } package)
        {
            return;
        }

        var ray = Viewport.RayFrom(position);
        var hit = BuildPicker(package).Pick(ray.Origin, ray.Direction);
        if (hit is null)
        {
            return;
        }

        _viewModel.OpenDiagnostic(hit.PartId);
        DockTab5.IsChecked = true; // 切到诊断页签
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
            _viewModel.DefectLink.ImageRequested -= OnImageRequested;
            _viewModel.DefectLink.Attach(null); // 3D 端即将释放，先解绑避免悬空引用
        }

        // 驱动器跟随上一份 DataContext 生命周期释放
        _motionDriver?.Dispose();
        _motionDriver = null;
        _opticsDriver?.Dispose();
        _opticsDriver = null;
        _scanFrustumDriver?.Dispose();
        _scanFrustumDriver = null;
        _waferMapDriver?.Dispose();
        _waferMapDriver = null;
        _defectPointDriver?.Dispose();
        _defectPointDriver = null;
        _transportParticles?.Dispose();
        _transportParticles = null;
        _followCamera?.Dispose();
        _followCamera = null;
        _alarmPulse?.Dispose();
        _alarmPulse = null;
        _fxOverlay?.Dispose();
        _fxOverlay = null;

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

            // 光学状态（TA.5）：环光源 8 段变色 + 同轴发光壳，叠加视觉随装配重绑
            _opticsDriver = new Rendering.Dynamics.OpticsSyncDriver(
                Viewport,
                _viewModel.Visual,
                () => _viewModel.PaletteSource,
                () => _viewModel.Assembly);
            _opticsDriver.OnFrame();

            // 线扫视野锥与扫描进度（TA.6）：直接订阅 Optics Topic 过滤 ScanProgress
            _scanFrustumDriver = new Rendering.Dynamics.ScanFrustumDriver(
                Viewport,
                _viewModel.VisualHub,
                () => _viewModel.Assembly);
            _scanFrustumDriver.OnFrame();

            // Wafer Map 纹理（TA.7）：整片事件走 WritePixels，几何零重建
            _waferMapDriver = new Rendering.Dynamics.WaferMapDriver(
                Viewport,
                _viewModel.Visual,
                () => _viewModel.Assembly);
            _waferMapDriver.OnFrame();

            // 缺陷点云（TA.8）：按等级分池 512 点/块增量追加
            _defectPointDriver = new Rendering.Dynamics.DefectPointDriver(
                Viewport,
                _viewModel.Visual,
                () => _viewModel.Assembly);
            _defectPointDriver.OnFrame();

            // 缺陷 2D/3D 联动（TA.14）：3D 端点云驱动注入服务，原图窗口由视图接管
            _viewModel.DefectLink.Attach(_defectPointDriver);
            _viewModel.DefectLink.ImageRequested += OnImageRequested;

            // FX 层（TA.10/TA.11）：报警脉冲常驻监听；传输粒子做晶圆流转演示动效
            _fxOverlay = new Rendering.Fx.FxOverlay(Viewport);
            _alarmPulse = new Rendering.Fx.AlarmPulseFx(
                Viewport, _viewModel.VisualHub, () => _viewModel.Assembly, _fxOverlay);
            _transportParticles = new Rendering.Fx.TransportParticlesFx(
                Viewport,
                _fxOverlay,
                new System.Windows.Media.Media3D.Point3D(0.40, -0.62, 0.10), // Track 装载位
                new System.Windows.Media.Media3D.Point3D(0.0, 0.0, 0.60));   // 检测位

            // 跟随模式（TA.15）：目标 = 载台世界坐标（装配位 + 运动平移）
            _followCamera = new Rendering.Camera.FollowCamera(
                Viewport,
                () => _viewModel.Assembly?.Find("STAGE") is { } stage
                    ? new System.Windows.Media.Media3D.Point3D(
                        stage.Geometry.Bounds.X + (stage.Geometry.Bounds.SizeX / 2) + stage.Motion.OffsetX,
                        stage.Geometry.Bounds.Y + (stage.Geometry.Bounds.SizeY / 2) + stage.Motion.OffsetY,
                        stage.Geometry.Bounds.Z + (stage.Geometry.Bounds.SizeZ / 2) + stage.Motion.OffsetZ)
                    : null);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TwinSceneViewModel.IsImmersive) && _viewModel is not null)
        {
            ApplyImmersive(_viewModel.IsImmersive);
        }
        else if (e.PropertyName == nameof(TwinSceneViewModel.ActiveLod))
        {
            // LOD 换档 = 新装配实例：报警壳重建到新几何（TA.11）
            _alarmPulse?.Refresh();
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

    /// <summary>跟随载台开关（TA.15 FR-M2-11）：切入无跳变，解除停在当前位置。</summary>
    private void OnFollowClick(object sender, RoutedEventArgs e)
    {
        if (_followCamera is null)
        {
            return;
        }

        if (_followCamera.IsFollowing)
        {
            _followCamera.Stop();
            FollowButton.Content = "跟随载台";
        }
        else
        {
            _followCamera.Start();
            FollowButton.Content = _followCamera.IsFollowing ? "解除跟随" : "跟随载台";
        }
    }

    private void OnResetViewClick(object sender, RoutedEventArgs e) => Viewport.ResetView();

    private void OnToggleProbeClick(object sender, RoutedEventArgs e)
    {
        Viewport.IsFrameProbeVisible = !Viewport.IsFrameProbeVisible;
        ProbeToggleButton.Content = Viewport.IsFrameProbeVisible ? "隐藏帧探针" : "显示帧探针";
    }
}
