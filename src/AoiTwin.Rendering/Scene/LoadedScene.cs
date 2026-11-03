using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Catalog;

namespace AoiTwin.Rendering.Scene;

/// <summary>LOD 档位（docs/asset-spec.md §2）。</summary>
public enum LodLevel
{
    /// <summary>L1：装配中模（默认）。</summary>
    L1 = 1,

    /// <summary>L2：低模（远景，隐藏线缆/门缝等细节）。</summary>
    L2 = 2,
}

/// <summary>单个部件的几何数据（已 Freeze，可跨线程安全共享）。</summary>
public sealed record LoadedPart(
    string PartId,
    PartCatalogEntry Entry,
    MeshGeometry3D Geometry,
    Color Color,
    int TriangleCount);

/// <summary>加载完成后的整机场景数据（几何已冻结，装配成可视树是 UI 线程的事）。</summary>
public sealed record LoadedScene(
    string CatalogPath,
    string AssetsRoot,
    LodLevel Lod,
    IReadOnlyList<LoadedPart> Parts,
    TimeSpan LoadElapsed,
    string Machine = "",
    string AssetVersion = "")
{
    public int TotalTriangles => Parts.Sum(p => p.TriangleCount);

    public LoadedPart? Find(string partId)
        => Parts.FirstOrDefault(p => string.Equals(p.PartId, partId, StringComparison.Ordinal));
}

/// <summary>加载进度（部件级；UI 直接绑定到进度条/状态文字）。</summary>
public readonly record struct AssetLoadProgress(int Completed, int Total, string? CurrentPartId)
{
    public double Fraction => Total <= 0 ? 0 : (double)Completed / Total;

    public int Percent => (int)Math.Round(Fraction * 100);
}

/// <summary>资产包定位失败（台账或 GLB 缺失/损坏），UI 需以可读提示呈现而非崩溃。</summary>
public sealed class SceneAssetException(string message, Exception? inner = null) : Exception(message, inner);
