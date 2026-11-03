using AoiTwin.Core.Recording;

namespace AoiTwin.AssetTools;

/// <summary>
/// .aotrec 一致性校验核（T3.5）：校验录制文件完整性并输出报告。
/// 报告内容：设备/班次、起止时间、有效帧数、Footer SHA-256 状态、逐帧错误明细。
/// 退出码：0 = 通过；1 = 校验失败；2 = 文件不存在。
/// </summary>
public static class RecVerifier
{
    public static int Verify(string filePath, TextWriter output, bool repair = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!File.Exists(filePath))
        {
            output.WriteLine($"文件不存在: {filePath}");
            return 2;
        }

        var info = repair ? RecordingReader.Repair(filePath) : RecordingReader.Inspect(filePath);
        output.WriteLine($"文件: {info.FilePath}");
        output.WriteLine($"设备: {info.DeviceId}  班次: {info.ShiftId}");
        output.WriteLine($"起始: {info.StartedAt:yyyy-MM-dd HH:mm:ss.fff}  结束: {info.EndedAt?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "（无 Footer，未正常关闭）"}");
        output.WriteLine($"有效帧数: {info.ValidFrameCount}");
        output.WriteLine($"状态: {(info.IsFinalized ? "已正常关闭（Footer SHA-256 校验通过）" : "未正常关闭（疑似断电/截断）")}{(info.Repaired ? "（本次已修复）" : string.Empty)}");

        foreach (var error in info.Errors)
        {
            output.WriteLine($"[错误] {error}");
        }

        if (!info.HasErrors && info.IsFinalized)
        {
            output.WriteLine("校验通过。");
            return 0;
        }

        output.WriteLine(repair
            ? "校验失败：修复后仍存在问题，文件不可用于回放。"
            : "校验失败。可加 --repair 尝试扫描修复（截断到最后一个完整帧并重建 Footer）。");
        return 1;
    }
}
