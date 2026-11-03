using System.Text;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Recording;
using Xunit;

namespace AoiTwin.AssetTools.Tests;

/// <summary>T3.5 一致性校验工具测试：正常文件通过；翻转字节/截断必须报错；--repair 可修复。</summary>
public class RecVerifierTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    private readonly string _dir;

    public RecVerifierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"recver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string BuildRecording()
    {
        using var writer = new RecordingWriter(new RecordingOptions
        {
            Directory = _dir,
            DeviceId = "AOI-01",
            ShiftId = "S1",
            DiskUsedRatioProvider = () => 0.1,
        });
        for (var i = 0; i < 10; i++)
        {
            writer.Write(TwinTopic.Motion, new AxisSnapshot("ROBOT", "R", i, 0.5, Base.AddMilliseconds(i * 100)));
        }

        return Assert.Single(Directory.GetFiles(_dir, "*.aotrec"));
    }

    [Fact]
    public void Healthy_file_passes_with_report()
    {
        var file = BuildRecording();
        var sb = new StringBuilder();
        using var output = new StringWriter(sb);

        var exit = RecVerifier.Verify(file, output);

        Assert.Equal(0, exit);
        Assert.Contains("校验通过", sb.ToString());
        Assert.Contains("有效帧数: 10", sb.ToString());
        Assert.Contains("AOI-01", sb.ToString());
    }

    [Fact]
    public void Corrupted_payload_fails()
    {
        var file = BuildRecording();
        var bytes = File.ReadAllBytes(file);
        bytes[^56] ^= 0xFF; // 翻转最后一帧 payload 内的一个字节（Footer 56B 之外）
        File.WriteAllBytes(file, bytes);

        var sb = new StringBuilder();
        using var output = new StringWriter(sb);

        var exit = RecVerifier.Verify(file, output);

        Assert.Equal(1, exit);
        var report = sb.ToString();
        Assert.Contains("[错误]", report);
    }

    [Fact]
    public void Truncated_file_fails_and_repair_recovers()
    {
        var file = BuildRecording();
        var bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(file, bytes[..^40]); // 截断 Footer，模拟断电

        var sb = new StringBuilder();
        using var output = new StringWriter(sb);
        var exit = RecVerifier.Verify(file, output);
        Assert.Equal(1, exit);
        Assert.Contains("未正常关闭", sb.ToString());

        // --repair 修复后应通过
        sb.Clear();
        exit = RecVerifier.Verify(file, output, repair: true);
        Assert.Equal(0, exit);
        Assert.Contains("校验通过", sb.ToString());
        Assert.Contains("已修复", sb.ToString());
    }

    [Fact]
    public void Missing_file_returns_usage_error()
    {
        var sb = new StringBuilder();
        using var output = new StringWriter(sb);

        var exit = RecVerifier.Verify(Path.Combine(_dir, "not-exist.aotrec"), output);

        Assert.Equal(2, exit);
    }
}
