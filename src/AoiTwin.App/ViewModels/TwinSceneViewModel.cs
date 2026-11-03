using System.IO;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using AoiTwin.Rendering.Scene;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AoiTwin.App.ViewModels;

/// <summary>
/// 3D 场景页视图模型（T1.4）：把「资产包加载 → 场景树装配」的状态映射为绑定属性。
///
/// 线程模型（PRD §5.7）：几何解析在后台线程（<see cref="GlbAssetLoader"/>），
/// 可视树装配与属性更新都在 UI 线程；加载期间渲染循环不受阻塞（NFR-02 长帧判据）。
/// 加载失败不抛到 UI：置 <see cref="HasError"/> 并把可读原因写进 <see cref="DetailText"/>。
/// </summary>
public sealed partial class TwinSceneViewModel : ObservableObject
{
    private readonly string _catalogPath;
    private readonly string _assetsRoot;
    private readonly Dispatcher _dispatcher;
    private ModelVisual3D? _sceneRoot;
    private bool _loading;

    /// <summary>DI 用：按仓库/部署目录自动定位资产包。</summary>
    public TwinSceneViewModel()
        : this(AssetPackageLocator.LocateCatalog(), AssetPackageLocator.LocateAssetsRoot())
    {
    }

    /// <summary>测试用：显式指定资产包位置。</summary>
    public TwinSceneViewModel(string catalogPath, string assetsRoot)
    {
        _catalogPath = catalogPath;
        _assetsRoot = assetsRoot;

        // 视口装配必须在 UI 线程；显式持有 Dispatcher，避免测试/后台上下文里依赖同步上下文恢复
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>一行结论（装配成功给「部件数 · 面数 · LOD」，失败给「场景加载失败」）。</summary>
    [ObservableProperty]
    private string _statusText = "正在装配 AOI 中模…";

    /// <summary>次级信息（资产版本/设备号/耗时，或失败原因）。</summary>
    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _hasScene;

    public LoadedScene? Scene { get; private set; }

    public SceneAssembly? Assembly { get; private set; }

    /// <summary>场景挂载点（视口提供）；由视图在首次加载时注入。</summary>
    public ModelVisual3D? SceneRoot => _sceneRoot;

    /// <summary>
    /// 加载并装配场景。重复调用是安全的：已有场景会先清空（<see cref="SceneTreeBuilder.Clear"/>），
    /// 供「重新加载」「资产热更新」复用。
    /// </summary>
    public async Task LoadAsync(ModelVisual3D sceneRoot, LodLevel lod = LodLevel.L1)
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
            var scene = await GlbAssetLoader
                .LoadAsync(_catalogPath, _assetsRoot, lod, new DispatcherProgress(this))
                .ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() => ApplyScene(scene, sceneRoot));
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

    private void ApplyScene(LoadedScene scene, ModelVisual3D sceneRoot)
    {
        Scene = scene;
        Assembly = SceneTreeBuilder.Build(scene, sceneRoot);
        ProgressPercent = 100;
        HasScene = true;

        StatusText = $"{scene.Machine} 中模已装配：{scene.Parts.Count} 部件 · {scene.TotalTriangles:N0} 三角面 · {LodLabel(scene.Lod)}";
        DetailText = $"资产包 v{scene.AssetVersion} · 台账 {Path.GetFileName(scene.CatalogPath)} · 加载 {scene.LoadElapsed.TotalMilliseconds:F0} ms";
    }

    private void ApplyError(string reason, ModelVisual3D sceneRoot)
    {
        HasError = true;
        HasScene = false;
        Scene = null;
        Assembly = null;
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

    private static string LodLabel(LodLevel lod) => lod == LodLevel.L2 ? "LOD2 低模" : "LOD1 中模";
}
