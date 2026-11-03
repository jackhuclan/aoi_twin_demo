using System.IO;

namespace AoiTwin.App.ViewModels;

/// <summary>
/// 模型资产包定位（T1.4）：从可执行目录向上查找 `assets/part-catalog.json`，
/// 开发态（bin/Debug/...）与部署态（仓库根）都命中同一份资产包，与 <see cref="RecordingDirectory"/> 同一套路。
/// </summary>
public static class AssetPackageLocator
{
    public const string CatalogFileName = "part-catalog.json";

    /// <summary>返回台账绝对路径；未找到时返回「可执行目录下 assets」的预期路径（便于错误提示可读）。</summary>
    public static string LocateCatalog()
    {
        var root = LocateAssetsRoot();
        return Path.Combine(root, CatalogFileName);
    }

    /// <summary>返回资产根目录（台账里 lod 路径的相对基准，与 AssetTools validate 的第二个参数一致）。</summary>
    public static string LocateAssetsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "assets");
            if (File.Exists(Path.Combine(candidate, CatalogFileName)))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "assets");
    }
}
