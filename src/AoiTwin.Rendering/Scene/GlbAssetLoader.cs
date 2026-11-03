using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Catalog;

namespace AoiTwin.Rendering.Scene;

/// <summary>
/// GLB 后台预加载（T1.4 / PRD §5.7）：台账解析 + 各部件 GLB 并行解析全部在后台线程完成，
/// 只把「已 Freeze 的几何数据」交回 UI 线程装配，避免启动期卡住渲染循环（NFR-02 长帧判据）。
///
/// 失败语义：台账缺失/损坏、GLB 缺失/损坏都抛 <see cref="SceneAssetException"/>，
/// 消息里始终带 PartId，便于现场按台账定位问题资产。
/// </summary>
public static class GlbAssetLoader
{
    public const string CatalogFileName = "part-catalog.json";

    /// <summary>
    /// 异步加载整机场景。<paramref name="progress"/> 按「已完成部件数」回报（0→Total），
    /// 顺序不保证（并行加载），但 Completed 单调递增。
    /// </summary>
    public static async Task<LoadedScene> LoadAsync(
        string catalogPath,
        string assetsRoot,
        LodLevel lod = LodLevel.L1,
        IProgress<AssetLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);

        var stopwatch = Stopwatch.StartNew();
        var fullAssetsRoot = Path.GetFullPath(assetsRoot);

        var catalog = await Task.Run(() => LoadCatalog(catalogPath), cancellationToken).ConfigureAwait(false);
        var entries = catalog.Parts;
        var results = new LoadedPart[entries.Count];
        var completed = 0;

        progress?.Report(new AssetLoadProgress(0, entries.Count, null));

        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, entries.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
                    CancellationToken = cancellationToken,
                },
                (index, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    results[index] = LoadPart(entries[index], fullAssetsRoot, lod);
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(new AssetLoadProgress(done, entries.Count, entries[index].PartId));
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SceneAssetException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SceneAssetException($"场景加载失败: {ex.Message}", ex);
        }

        stopwatch.Stop();
        return new LoadedScene(
            Path.GetFullPath(catalogPath),
            fullAssetsRoot,
            lod,
            results,
            stopwatch.Elapsed,
            catalog.Document.Machine,
            catalog.Document.AssetVersion);
    }

    /// <summary>加载单个部件（供 T1.8 点选后按需补载 L1 细节、M2 新增部件等场景复用）。</summary>
    public static LoadedPart LoadPart(PartCatalogEntry entry, string assetsRoot, LodLevel lod)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var relative = lod == LodLevel.L2 ? entry.Lod?.L2 : entry.Lod?.L1;
        if (string.IsNullOrWhiteSpace(relative))
        {
            throw new SceneAssetException($"台账未声明 {lod} 资产: {entry.PartId}");
        }

        var fullPath = Path.Combine(assetsRoot, relative);
        if (!File.Exists(fullPath))
        {
            throw new SceneAssetException($"{lod} 资产文件不存在: {relative}（PartId {entry.PartId}）");
        }

        MeshGeometry3D geometry;
        Color color;
        try
        {
            geometry = GlbMeshReader.Read(fullPath, out color);
        }
        catch (SceneAssetException ex)
        {
            // 统一在这里补上 PartId，现场定位问题资产只需看一条消息
            throw new SceneAssetException($"{lod} 资产读取失败（PartId {entry.PartId}）: {ex.Message}", ex);
        }

        return new LoadedPart(
            entry.PartId,
            entry,
            geometry,
            color,
            geometry.TriangleIndices.Count / 3);
    }

    private static PartCatalog LoadCatalog(string catalogPath)
    {
        if (!File.Exists(catalogPath))
        {
            throw new SceneAssetException($"部件台账不存在: {catalogPath}");
        }

        try
        {
            return PartCatalog.Load(catalogPath);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or IOException)
        {
            throw new SceneAssetException($"部件台账无法解析: {Path.GetFileName(catalogPath)}（{ex.Message}）", ex);
        }
    }
}
