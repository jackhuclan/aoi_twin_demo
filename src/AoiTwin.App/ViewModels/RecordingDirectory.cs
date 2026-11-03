using System.IO;

namespace AoiTwin.App.ViewModels;

/// <summary>
/// 录制目录定位（T3.3）：从可执行目录向上查找 `assets/recordings`，
/// 使开发态（bin/Debug/...）与部署态（仓库根）都能命中同一批录制文件。
/// </summary>
public static class RecordingDirectory
{
    public const string RelativePath = "assets/recordings";

    public static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "assets", "recordings");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "recordings");
    }
}
