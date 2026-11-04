using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using AoiTwin.App.Services;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Kinematics;
using AoiTwin.Rendering.Camera;
using AoiTwin.Rendering.Lod;
using AoiTwin.Rendering.Scene;
using AoiTwin.ViewModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AoiTwin.App.ViewModels;

/// <summary>LOD 档位选择模式：跟随相机距离自动，或锁定某一档（FR-M1-05）。</summary>
public enum TwinLodMode
{
    Auto = 0,
    Near = 1,
    Far = 2,
}

/// <summary>
/// 3D 场景页视图模型（T1.4/T1.6）：把「资产包加载 → 场景树装配 → LOD 切换」映射为绑定属性。
///
/// 线程模型（PRD §5.7）：几何解析在后台线程（<see cref="GlbAssetLoader"/>），
/// 可视树装配与属性更新都在 UI 线程；加载期间渲染循环不受阻塞（NFR-02 长帧判据）。
/// 加载失败不抛到 UI：置 <see cref="HasError"/> 并把可读原因写进 <see cref="DetailText"/>。
/// </summary>
public sealed partial class TwinSceneViewModel : ObservableObject
{
    /// <summary>自动档位评估周期：相机连续平移时按此节流，切换本身是整树重建（无中间帧）。</summary>
    private static readonly TimeSpan LodEvaluationInterval = TimeSpan.FromMilliseconds(250);

    private readonly string _catalogPath;
    private readonly string _assetsRoot;
    private readonly Dispatcher _dispatcher;
    private readonly CameraBookmarkService _bookmarkService;
    private readonly LodLevelSelector _lodSelector = new();
    private readonly Stopwatch _lodClock = Stopwatch.StartNew();
    private ModelVisual3D? _sceneRoot;
    private bool _loading;

    /// <summary>DI 用：按仓库/部署目录自动定位资产包。</summary>
    public TwinSceneViewModel()
        : this(AssetPackageLocator.LocateCatalog(), AssetPackageLocator.LocateAssetsRoot())
    {
    }

    /// <summary>测试用：显式指定资产包位置。</summary>
    public TwinSceneViewModel(string catalogPath, string assetsRoot)
        : this(catalogPath, assetsRoot, new CameraBookmarkService())
    {
    }

    /// <summary>测试用：显式指定资产包位置与书签存储（避免污染真实用户配置）。</summary>
    /// <param name="catalogPath">台账路径。</param>
    /// <param name="assetsRoot">资产根目录。</param>
    /// <param name="bookmarkService">书签存储。</param>
    /// <param name="visual">3D 视觉 VM（DI 注入共享实例；缺省自建独立 hub + 恒等标定引擎）。</param>
    /// <param name="hub">自建 Visual 时的总线（测试发布轴数据用）。</param>
    /// <param name="paletteSource">通道配色文件源（DI 注入；缺省无文件走默认八色）。</param>
    public TwinSceneViewModel(
        string catalogPath,
        string assetsRoot,
        CameraBookmarkService bookmarkService,
        TwinVisualViewModel? visual = null,
        IRealtimeHub? hub = null,
        LightChannelPaletteFileSource? paletteSource = null)
    {
        _catalogPath = catalogPath;
        _assetsRoot = assetsRoot;
        _bookmarkService = bookmarkService;

        // 视口装配必须在 UI 线程；显式持有 Dispatcher，避免测试/后台上下文里依赖同步上下文恢复
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        VisualHub = hub ?? new RealtimeHub();
        Visual = visual ?? new TwinVisualViewModel(
            VisualHub,
            new MotionPoseEngine(TeachCoordinateMapper.Load(Path.Combine(assetsRoot, "teach-calibration.json"))));
        PaletteSource = paletteSource;

        Handling = new HandlingStateViewModel(VisualHub);

        // Result 直推可能来自数据线程：列表填充统一回 UI 线程（无头测试注入 null → 同步执行）
        DefectList = new DefectListViewModel(VisualHub, action => _dispatcher.Invoke(action));
        DefectLink = new DefectLinkService(DefectList);

        RefreshBookmarks();
    }

    /// <summary>通道配色文件源（TA.5；null = 走 LightChannelPalette.Default 八色）。</summary>
    public LightChannelPaletteFileSource? PaletteSource { get; }

    /// <summary>部件钻取面板（TA.12，FR-M2-08）：选中即开实时会话，取消即关。</summary>
    public DrillDownViewModel DrillDown { get; } = new();

    /// <summary>装载状态（TA.16）：Track 晶圆占位 + LoadPort Cassette 计数（状态页头徽标）。</summary>
    public HandlingStateViewModel Handling { get; }

    /// <summary>2D 缺陷列表（TA.14）：订阅 Result Topic，整片替换 + 滑窗定位。</summary>
    public DefectListViewModel DefectList { get; }

    /// <summary>缺陷 2D/3D 双向联动服务（TA.14）：3D 端由视图装配后 Attach。</summary>
    public DefectLinkService DefectLink { get; }

    /// <summary>诊断面板分类（TA.13：相机参数 / 光源控制 / 轴调试 / 通用诊断）。</summary>
    [ObservableProperty]
    private string _diagnosticTitle = "诊断（未选择部件）";

    /// <summary>诊断面板内容行（按部件类型给出对应 2D 诊断入口）。</summary>
    public ObservableCollection<string> DiagnosticLines { get; } = [];

    private PartDrillDownService? _drillDownService;

    /// <summary>3D 视觉 VM（TA.4）：位姿/光学/Wafer 双缓冲的唯一持有者，驱动器每帧读取。</summary>
    public TwinVisualViewModel Visual { get; }

    /// <summary>Visual 使用的实时总线（自建实例时测试可通过它发布轴数据；DI 注入时与全局共享）。</summary>
    public IRealtimeHub VisualHub { get; }

    /// <summary>两档场景包：LOD1 全保真（点选用）+ LOD2 合批代理（远档渲染用）。</summary>
    public LodPackage? Package { get; private set; }

    /// <summary>当前装配树（LOD 切换时重建；拾取变换与状态栏使用）。</summary>
    public SceneAssembly? Assembly { get; private set; }

    /// <summary>
    /// 装配节点名 → 运动驱动 PartId（TA.4）：LOD1 部件名透传；
    /// LOD2 代理（装配名 = 组显示名）映射到组内首个有轴定义的部件，
    /// 固定结构组返回 null（驱动器跳过）。代理按组随动——组内各件位姿同源。
    /// </summary>
    public string? ResolveDrivePartId(string assemblyPartName)
    {
        ArgumentNullException.ThrowIfNull(assemblyPartName);
        var package = Package;
        if (package is null)
        {
            return null;
        }

        foreach (var proxy in package.Proxies)
        {
            if (!string.Equals(proxy.DisplayName, assemblyPartName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var partId in proxy.PartIds)
            {
                var part = package.Near.Find(partId);
                if (part is { } entry && entry.Entry.Axes is { Count: > 0 })
                {
                    return partId;
                }
            }

            return null; // 运动组内没有带轴部件（理论上只有 Static 组会走到）
        }

        return assemblyPartName; // 不是代理名 → LOD1 部件名
    }

    /// <summary>当前生效的 LOD 档位。</summary>
    public LodLevel ActiveLod => _lodSelector.Current;

    /// <summary>部件列表（台账信息区左列，T1.8）。</summary>
    public ObservableCollection<PartSummary> PartSummaries { get; } = [];

    /// <summary>选中部件的详情行（键值文本）。</summary>
    public ObservableCollection<string> SelectedPartLines { get; } = [];

    /// <summary>视角书签（预置 + 自定义），下拉框数据源。</summary>
    public ObservableCollection<CameraBookmark> Bookmarks { get; } = [];

    /// <summary>正在刷新书签列表（保存/删除后重选时抑制过渡动画，避免视角被程序性重置）。</summary>
    public bool IsRefreshingBookmarks { get; private set; }

    /// <summary>请求视图执行一次相机过渡（视图负责动画，VM 不持有相机）。</summary>
    public event EventHandler<CameraPose>? BookmarkRequested;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>一行结论（装配成功给「部件数 · 面数 · LOD」，失败给「场景加载失败」）。</summary>
    [ObservableProperty]
    private string _statusText = "正在装配 AOI 中模…";

    /// <summary>次级信息（资产版本/设备号/耗时/LOD 明细，或失败原因）。</summary>
    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _hasScene;

    /// <summary>当前选中的视角书签（切到预置/自定义即触发过渡）。</summary>
    [ObservableProperty]
    private CameraBookmark? _selectedBookmark;

    /// <summary>保存书签时用的名称（留空则自动命名）。</summary>
    [ObservableProperty]
    private string _newBookmarkName = string.Empty;

    /// <summary>书签操作提示（保存成功/重名/上限淘汰等一行反馈）。</summary>
    [ObservableProperty]
    private string _bookmarkHint = "预置 4 视角 · 自定义书签保存在本机";

    /// <summary>部件列表当前选中项（列表双向绑定）。</summary>
    [ObservableProperty]
    private PartSummary? _selectedPartSummary;

    /// <summary>当前选中的 PartId（空 = 未选中）。</summary>
    [ObservableProperty]
    private string? _selectedPartId;

    /// <summary>选中部件的详情标题。</summary>
    [ObservableProperty]
    private string _selectionHint = "点击 3D 模型或在列表中选择部件";

    /// <summary>选中对象变化（视图据此更新描边；partId 为空表示清除）。</summary>
    public event EventHandler<string?>? SelectionChanged;

    /// <summary>
    /// 右侧停靠区当前页：0 部件台账 / 1 配方树 / 2 图像映射 / 3 直方图·光谱（T4.1）。
    /// 配方树与图像映射在 M1 为静态占位（M2/M3 接入实时数据），版式与交互先定型。
    /// </summary>
    [ObservableProperty]
    private int _dockIndex;

    /// <summary>沉浸全屏 3D（T4.1 FR-M1-15）：隐藏右侧停靠区与书签栏，切换 ≤ 1s。</summary>
    [ObservableProperty]
    private bool _isImmersive;

    /// <summary>全屏切换按钮文本。</summary>
    public string ImmersiveToggleText => IsImmersive ? "退出全屏" : "全屏 3D";

    partial void OnIsImmersiveChanged(bool value) => OnPropertyChanged(nameof(ImmersiveToggleText));

    /// <summary>切换沉浸全屏（按钮命令）。</summary>
    [RelayCommand]
    private void ToggleImmersive() => IsImmersive = !IsImmersive;

    /// <summary>LOD 模式（自动/锁定近档/锁定远档）。</summary>
    [ObservableProperty]
    private TwinLodMode _lodMode = TwinLodMode.Auto;

    /// <summary>当前档位的一行摘要（工具栏显示）。</summary>
    [ObservableProperty]
    private string _lodText = "LOD1 中模 · 自动";

    /// <summary>场景挂载点（视口提供）；由视图在首次加载时注入。</summary>
    public ModelVisual3D? SceneRoot => _sceneRoot;

    /// <summary>
    /// 加载两档场景并装配当前档位。重复调用安全：先清空再重建（重新加载/资产热更新复用）。
    /// </summary>
    public async Task LoadAsync(ModelVisual3D sceneRoot)
    {
        ArgumentNullException.ThrowIfNull(sceneRoot);
        if (_loading)
        {
            return;
        }

        _loading = true;
        _sceneRoot ??= sceneRoot;

        IsLoading = true;
        HasError = false;
        ProgressPercent = 0;
        StatusText = "正在装配 AOI 中模…";
        DetailText = "后台线程解析 GLB，渲染循环不阻塞";

        try
        {
            // 加载在后台线程完成（ConfigureAwait(false)），状态更新统一切回 UI 线程
            var package = await LodPackage
                .LoadAsync(_catalogPath, _assetsRoot, new DispatcherProgress(this))
                .ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() => ApplyPackage(package, sceneRoot));
        }
        catch (SceneAssetException ex)
        {
            await _dispatcher.InvokeAsync(() => ApplyError(ex.Message, sceneRoot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dispatcher.InvokeAsync(() => ApplyError($"资产包不可读: {ex.Message}", sceneRoot));
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => IsLoading = false);
            _loading = false;
        }
    }

    /// <summary>切换 LOD 模式；有场景时立即按新模式重建可视树。</summary>
    public void SetLodMode(TwinLodMode mode)
    {
        LodMode = mode;
        _lodSelector.Reset(mode switch
        {
            TwinLodMode.Near => LodLevel.L1,
            TwinLodMode.Far => LodLevel.L2,
            _ => _lodSelector.Current,
        });

        if (Package is { } package && _sceneRoot is { } root)
        {
            RebuildScene(package, _lodSelector.Current);
        }

        UpdateLodText();
    }

    /// <summary>
    /// 相机位置变化时评估自动档位（视图按帧转发，内部按 <see cref="LodEvaluationInterval"/> 节流）。
    /// 只在自动模式生效；档位变化时整树一次性重建，无闪烁帧。
    /// </summary>
    public void OnCameraMoved(Point3D cameraPosition)
    {
        if (LodMode != TwinLodMode.Auto || Package is not { } package || _sceneRoot is null)
        {
            return;
        }

        if (_lodClock.Elapsed < LodEvaluationInterval)
        {
            return;
        }

        _lodClock.Restart();

        var distance = LodLevelSelector.DistanceTo(package.SceneBounds, cameraPosition);
        if (_lodSelector.Evaluate(distance))
        {
            RebuildScene(package, _lodSelector.Current);
            UpdateLodText();
        }
    }

    private void ApplyPackage(LodPackage package, ModelVisual3D sceneRoot)
    {
        Package = package;
        _lodSelector.Reset(LodMode switch
        {
            TwinLodMode.Near => LodLevel.L1,
            TwinLodMode.Far => LodLevel.L2,
            _ => LodLevel.L1,
        });

        RebuildScene(package, _lodSelector.Current);
        UpdateLodText();
        ProgressPercent = 100;
        HasScene = true;

        // 台账列表（T1.8）：PartId 顺序与台账一致
        PartSummaries.Clear();
        foreach (var part in package.Near.Parts)
        {
            PartSummaries.Add(new PartSummary(part.PartId, part.Entry.Name));
        }

        SelectPart(null);

        StatusText = $"{package.Near.Machine} 已装配：{package.Near.Parts.Count} 部件 · {package.Near.TotalTriangles:N0} 三角面（LOD1）";
        DetailText = $"资产包 v{package.Near.AssetVersion} · LOD1 {package.MeshCount(LodLevel.L1)} 网格 / LOD2 {package.MeshCount(LodLevel.L2)} 网格 · 加载 {package.Near.LoadElapsed.TotalMilliseconds:F0} ms";
    }

    private void RebuildScene(LodPackage package, LodLevel level)
    {
        if (_sceneRoot is not { } root)
        {
            return;
        }

        Assembly = level == LodLevel.L1
            ? SceneTreeBuilder.Build(package.Near, root)
            : SceneTreeBuilder.BuildProxies(package.Proxies, root);

        OnPropertyChanged(nameof(ActiveLod));
    }

    /// <summary>
    /// 选中部件（T1.8）。空/未知的 PartId 视为清除选中（3D 空白处点击也是这个语义）。
    /// </summary>
    /// <summary>
    /// 右键诊断跳转（TA.13，FR-M2-09）：按台账 module 决定诊断分类，并返回应切到的页签索引。
    /// OPTICS 相机件 → 相机参数；OPTICS 光源件 → 光源控制；MOTION/STAGE 有轴件 → 轴调试；其余 → 通用诊断。
    /// </summary>
    public int OpenDiagnostic(string partId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partId);

        var part = Package?.Near.Find(partId);
        var entry = part?.Entry;
        var module = entry?.Module ?? "";
        var isCamera = partId.StartsWith("CAMERA", StringComparison.Ordinal);
        var isLight = partId.StartsWith("LIGHT", StringComparison.Ordinal);
        var hasAxis = entry?.Axes.Count > 0;

        DiagnosticLines.Clear();
        string title;
        if (module == "OPTICS" && isCamera)
        {
            title = $"相机参数 · {entry?.Name ?? partId}";
            DiagnosticLines.Add($"部件：{partId}");
            DiagnosticLines.Add("2D 页：相机参数（曝光/增益/触发模式）");
            DiagnosticLines.Add("实时：Optics Topic CameraParam（M3 接入参数编辑）");
        }
        else if (module == "OPTICS" && isLight)
        {
            title = $"光源控制 · {entry?.Name ?? partId}";
            DiagnosticLines.Add($"部件：{partId}");
            DiagnosticLines.Add("2D 页：光源控制（8 通道强度/亮度）");
            DiagnosticLines.Add($"配色：assets/light-channel-colors.json（热加载）");
        }
        else if (hasAxis)
        {
            title = $"轴调试 · {entry?.Name ?? partId}";
            DiagnosticLines.Add($"部件：{partId}");
            DiagnosticLines.Add($"轴：{string.Join(" / ", entry!.Axes)}");
            DiagnosticLines.Add("2D 页：轴调试（点动/回零/软限位）");
            DiagnosticLines.Add("实时：Motion Topic 轴快照（200ms 合并 → 60fps 插值）");
        }
        else
        {
            title = $"通用诊断 · {entry?.Name ?? partId}";
            DiagnosticLines.Add($"部件：{partId}");
            DiagnosticLines.Add($"模块：{module}");
            DiagnosticLines.Add("2D 页：部件详情（IO 状态/报警历史）");
        }

        DiagnosticTitle = title;
        return DiagnosticTabIndex;
    }

    /// <summary>诊断页签索引（右侧第五个分段页签之后的固定位置）。</summary>
    public const int DiagnosticTabIndex = 5;

    /// <summary>缺陷页签索引（图像映射 = 右侧第二个分段页签）。</summary>
    public const int DefectTabIndex = 1;

    /// <summary>
    /// 2D→3D（TA.14）：按 DefectId 高亮 3D 缺陷面片。列表选中已自动触发，本入口供
    /// 「原图窗口/诊断」等旁路主动定位；索引缺该 DefectId 时返回 false。
    /// </summary>
    public bool HighlightDefect(long defectId) => DefectLink.HighlightFrom2D(defectId);

    /// <summary>3D→2D（TA.14）：射线反查最近缺陷 → 2D 列表滚窗定位 → 原图窗口。</summary>
    public bool FocusDefectFrom3D(Point3D origin, Vector3D direction)
        => DefectLink.FocusFrom3D(origin, direction);

    public void SelectPart(string? partId)
    {
        SelectedPartSummary = string.IsNullOrEmpty(partId)
            ? null
            : PartSummaries.FirstOrDefault(p => string.Equals(p.PartId, partId, StringComparison.Ordinal));

        ApplySelection(SelectedPartSummary?.PartId);
    }

    partial void OnSelectedPartSummaryChanged(PartSummary? value)
    {
        // 列表驱动与 SelectPart 驱动汇合到同一入口；重复选中不重复刷新
        if (!string.Equals(value?.PartId, SelectedPartId, StringComparison.Ordinal))
        {
            ApplySelection(value?.PartId);
        }
    }

    private void ApplySelection(string? partId)
    {
        SelectedPartId = partId;

        SelectedPartLines.Clear();
        if (string.IsNullOrEmpty(partId))
        {
            SelectionHint = "点击 3D 模型或在列表中选择部件";
            SelectionChanged?.Invoke(this, null);
            _drillDownService?.Close();
            return;
        }

        var part = Package?.Near.Find(partId);
        if (part is null)
        {
            SelectionHint = "点击 3D 模型或在列表中选择部件";
            SelectionChanged?.Invoke(this, null);
            _drillDownService?.Close();
            return;
        }

        var entry = part.Entry;
        var bounds = part.Geometry.Bounds;
        SelectionHint = $"{entry.PartId} · {entry.Name}";

        SelectedPartLines.Add($"模块：{entry.Module}");
        SelectedPartLines.Add($"型号：{entry.Model}");
        SelectedPartLines.Add($"运动轴：{FormatAxes(entry.Axes)}");
        SelectedPartLines.Add($"三角面：{part.TriangleCount:N0}（LOD1）");
        SelectedPartLines.Add($"包围盒：{bounds.SizeX:F2} × {bounds.SizeY:F2} × {bounds.SizeZ:F2} m");
        SelectedPartLines.Add($"资产：{entry.Lod?.L1 ?? "—"}");
        SelectionChanged?.Invoke(this, partId);
        OpenDrillDown(partId, entry.Name);
    }

    /// <summary>打开/切换钻取会话（TA.12；懒建 service，随 VisualHub 生命周期）。</summary>
    private void OpenDrillDown(string partId, string partName)
    {
        _drillDownService ??= new PartDrillDownService(
            VisualHub,
            Visual,
            DrillDown,
            PartSummaries.ToDictionary(p => p.PartId, p => p.Name, StringComparer.Ordinal));
        _drillDownService.Open(partId);
    }

    private static string FormatAxes(IReadOnlyList<string> axes)
        => axes.Count == 0 ? "固定" : string.Join(" / ", axes);

    /// <summary>部件列表条目（台账信息区）。</summary>
    public sealed record PartSummary(string PartId, string Name)
    {
        public string Display => $"{PartId}  {Name}";
    }

    private void UpdateLodText()
    {
        var levelLabel = _lodSelector.Current == LodLevel.L1 ? "LOD1 中模" : "LOD2 低模";
        var modeLabel = LodMode switch
        {
            TwinLodMode.Near => "锁定近档",
            TwinLodMode.Far => "锁定远档",
            _ => "自动",
        };
        LodText = $"{levelLabel} · {modeLabel}";
    }

    private void ApplyError(string reason, ModelVisual3D sceneRoot)
    {
        HasError = true;
        HasScene = false;
        Package = null;
        Assembly = null;
        ProgressPercent = 0;
        PartSummaries.Clear();
        SelectPart(null);
        SceneTreeBuilder.Clear(sceneRoot);
        StatusText = "场景加载失败";
        DetailText = reason;
    }

    private void ReportProgress(AssetLoadProgress p)
    {
        ProgressPercent = p.Percent;
        StatusText = $"正在装配 AOI 中模… {p.Completed}/{p.Total}";
        if (p.CurrentPartId is { } partId)
        {
            DetailText = $"已加载 {partId}";
        }
    }

    /// <summary>加载线程 → UI 线程的进度投递（不依赖 SynchronizationContext）。</summary>
    private sealed class DispatcherProgress(TwinSceneViewModel owner) : IProgress<AssetLoadProgress>
    {
        public void Report(AssetLoadProgress value)
        {
            if (owner._dispatcher.HasShutdownStarted)
            {
                return;
            }

            owner._dispatcher.BeginInvoke(() => owner.ReportProgress(value));
        }
    }

    /// <summary>重新装配（资产包更新、LOD 切换后使用）。</summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (_sceneRoot is null || _loading)
        {
            return;
        }

        await LoadAsync(_sceneRoot);
    }

    /// <summary>从磁盘重载书签并刷新下拉框（预览/删除后调用）。</summary>
    public void RefreshBookmarks()
    {
        _bookmarkService.Load();

        IsRefreshingBookmarks = true;
        try
        {
            SelectedBookmark = null;
            Bookmarks.Clear();
            foreach (var bookmark in _bookmarkService.All)
            {
                Bookmarks.Add(bookmark);
            }

            SelectedBookmark = Bookmarks.FirstOrDefault(b => b.IsPreset) ?? Bookmarks.FirstOrDefault();
        }
        finally
        {
            IsRefreshingBookmarks = false;
        }
    }

    /// <summary>把当前相机位姿存为自定义书签（重名覆盖，与预置重名给出提示而非崩溃）。</summary>
    public bool SaveBookmark(CameraPose pose)
    {
        var name = string.IsNullOrWhiteSpace(NewBookmarkName)
            ? $"视角 {_bookmarkService.Custom.Count + 1}"
            : NewBookmarkName.Trim();

        try
        {
            var saved = _bookmarkService.Save(name, pose);
            RefreshBookmarks();
            SelectByName(saved.Name);
            NewBookmarkName = string.Empty;
            BookmarkHint = $"已保存「{saved.Name}」· {_bookmarkService.Custom.Count}/{CameraBookmarkService.MaxCustomBookmarks}";
            return true;
        }
        catch (ArgumentException ex)
        {
            BookmarkHint = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BookmarkHint = $"书签写入失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>删除当前选中的自定义书签（预置不可删）。</summary>
    public bool DeleteSelectedBookmark()
    {
        if (SelectedBookmark is not { } selected)
        {
            BookmarkHint = "先选一个书签";
            return false;
        }

        if (selected.IsPreset)
        {
            BookmarkHint = "预置视角不可删除";
            return false;
        }

        var removed = _bookmarkService.Delete(selected.Name);
        RefreshBookmarks();
        BookmarkHint = removed ? $"已删除「{selected.Name}」" : "书签已不存在";
        return removed;
    }

    /// <summary>激活当前选中书签（视图收到事件后执行 ≤800ms 过渡）。</summary>
    public void ActivateSelectedBookmark()
    {
        if (SelectedBookmark is { } selected)
        {
            BookmarkRequested?.Invoke(this, selected.Pose);
        }
    }

    private void SelectByName(string name)
        => SelectedBookmark = Bookmarks.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.Ordinal))
            ?? SelectedBookmark;
}
