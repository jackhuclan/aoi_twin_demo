namespace AoiTwin.Core.Recording;

/// <summary>
/// 磁盘水位探测（PRD-AOI-TWIN-M1 §5.5）：录制器与采集监控面板共用同一套口径，
/// 避免「面板显示 83%、录制器却按 86% 暂停」这类口径不一致造成的现场误判。
/// </summary>
public static class DiskSpace
{
    /// <summary>指定目录所在卷的已用比例（0–1）。卷不可用/无法解析时返回 0（视作水位正常）。</summary>
    public static double UsedRatio(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrEmpty(root))
        {
            return 0;
        }

        DriveInfo drive;
        try
        {
            drive = new DriveInfo(root);
        }
        catch (ArgumentException)
        {
            return 0;
        }

        if (!drive.IsReady || drive.TotalSize == 0)
        {
            return 0;
        }

        return (double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize;
    }
}
