using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using AoiTwin.Rendering.Camera;
using AoiTwin.Rendering.Lod;
using AoiTwin.Rendering.Scene;
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
    public TwinSceneViewModel(string catalogPath, string assetsRoot, CameraBookmarkService bookmarkService)
    {
        _catalogPath = catalogPath;
        _assetsRoot = assetsRoot;
        _bookmarkService = bookmarkService;

        // 视口装配必须在 UI 线程；显式持有 Dispatcher，避免测试/后台上下文里依赖同步上下文恢复
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        RefreshBookmarks();
    }

    /// <summary>两档场景包：LOD1 全保真（点选用）+ LOD2 合批代理（远档渲染用）。</summary>
    public LodPackage? Package { get; private set; }

    /// <summary>当前生效的 LOD 档位。</summary>
    public LodLevel ActiveLod => _lodSelector.Current;

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

        StatusText = $"{package.Near.Machine} 已装配：{package.Near.Parts.Count} 部件 · {package.Near.TotalTriangles:N0} 三角面（LOD1）";
        DetailText = $"资产包 v{package.Near.AssetVersion} · LOD1 {package.MeshCount(LodLevel.L1)} 网格 / LOD2 {package.MeshCount(LodLevel.L2)} 网格 · 加载 {package.Near.LoadElapsed.TotalMilliseconds:F0} ms";
    }

    private void RebuildScene(LodPackage package, LodLevel level)
    {
        if (_sceneRoot is not { } root)
        {
            return;
        }

        if (level == LodLevel.L1)
        {
            SceneTreeBuilder.Build(package.Near, root);
        }
        else
        {
            SceneTreeBuilder.BuildProxies(package.Proxies, root);
        }

        OnPropertyChanged(nameof(ActiveLod));
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
        ProgressPercent = 0;
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
