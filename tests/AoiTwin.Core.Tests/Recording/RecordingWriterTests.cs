using System.Text;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Recording;
using Xunit;

namespace AoiTwin.Core.Tests.Recording;

/// <summary>T2.2 录制器测试：逐帧 CRC、断电修复、压测 0 丢帧、班次滚动、磁盘水位。</summary>
public class RecordingWriterTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    private readonly string _dir;

    public RecordingWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"aotrec-{Guid.NewGuid():N}");
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

    private RecordingOptions Options(string deviceId = "AOI-01", double? diskRatio = null) => new()
    {
        Directory = _dir,
        DeviceId = deviceId,
        ShiftId = "S1",
        DiskUsedRatioProvider = () => diskRatio ?? 0.1,
    };

    private static AxisSnapshot Axis(int i) => new("ROBOT", "R", i, 0.5, Base.AddMilliseconds(i));

    [Fact]
    public void Write_and_read_back_all_message_types_frames_match()
    {
        using (var writer = new RecordingWriter(Options()))
        {
            writer.Write(TwinTopic.Motion, Axis(0));
            writer.Write(TwinTopic.Status, new StatusEvent("LOADPORT", "CASSETTE_LOADED", Severity.Info, null, Base.AddSeconds(1)));
            writer.Write(TwinTopic.Optics, new OpticsEvent("LIGHT_RING", OpticsKind.LightBrightness, 3, 87.5, Base.AddSeconds(2)));
            writer.Write(TwinTopic.Result, new WaferResult("W-0001", new WaferMapSnapshot(2, 2, [255, 0, 128, 64]), null, TimeSpan.FromSeconds(31.5), Base.AddSeconds(3)));
        }

        var files = Directory.GetFiles(_dir, "*.aotrec");
        var file = Assert.Single(files);
        var info = RecordingReader.Inspect(file);
        Assert.True(info.IsFinalized, string.Join("\n", info.Errors));
        Assert.False(info.HasErrors);
        Assert.Equal(4, info.ValidFrameCount);
        Assert.Equal("AOI-01", info.DeviceId);
        Assert.Equal("S1", info.ShiftId);
        Assert.Equal(Base, info.StartedAt);
        Assert.NotNull(info.EndedAt);

        var received = new List<ITwinMessage>();
        RecordingReader.Inspect(file, m => received.Add(m));
        Assert.Equal(4, received.Count);
        var axis = Assert.IsType<AxisSnapshot>(received[0]);
        Assert.Equal(("ROBOT", "R", 0, 0.5), (axis.PartId, axis.AxisName, axis.Position, axis.Velocity));
        var status = Assert.IsType<StatusEvent>(received[1]);
        Assert.Empty(status.Payload);
        var optics = Assert.IsType<OpticsEvent>(received[2]);
        Assert.Equal((OpticsKind.LightBrightness, 3, 87.5), (optics.Kind, optics.Channel, optics.Value));
        var result = Assert.IsType<WaferResult>(received[3]);
        Assert.Equal("W-0001", result.WaferId);
        Assert.Equal(new byte[] { 255, 0, 128, 64 }, result.Map.DieMap);
    }

    [Fact]
    public void Corrupted_payload_detected_by_crc()
    {
        using (var writer = new RecordingWriter(new RecordingOptions
               {
                   Directory = _dir,
                   DeviceId = "AOI-01",
                   ShiftId = "S1",
                   DiskUsedRatioProvider = () => 0.1,
               }))
        {
            writer.Write(TwinTopic.Motion, Axis(0));
            writer.Write(TwinTopic.Motion, Axis(1));
        }

        var path = Assert.Single(Directory.GetFiles(_dir, "*.aotrec"));
        // 翻转最后一帧 payload 中的一个字节（Footer 占末尾 56B，取其前 10B 必在 payload 内）
        var bytes = File.ReadAllBytes(path);
        bytes[^66] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var info = RecordingReader.Inspect(path);
        Assert.True(info.HasErrors);
        Assert.Contains(info.Errors, e => e.Contains("CRC"));
        Assert.Equal(1, info.ValidFrameCount);
    }

    [Fact]
    public void Crash_without_finalize_repairable()
    {
        var writer = new RecordingWriter(new RecordingOptions { Directory = _dir, DeviceId = "AOI-01", ShiftId = "S1", DiskUsedRatioProvider = () => 0.1 });
        for (var i = 0; i < 100; i++)
        {
            Assert.True(writer.Write(TwinTopic.Motion, Axis(i)));
        }

        writer.Abandon(); // 模拟进程硬杀：无 Footer
        var path = Assert.Single(Directory.GetFiles(_dir, "*.aotrec"));

        var crashed = RecordingReader.Inspect(path);
        Assert.False(crashed.IsFinalized);
        Assert.Equal(100, crashed.ValidFrameCount);
        Assert.Null(crashed.EndedAt);

        var repaired = RecordingReader.Repair(path);
        Assert.True(repaired.IsFinalized);
        Assert.True(repaired.Repaired);
        Assert.Equal(100, repaired.ValidFrameCount);
        Assert.False(repaired.HasErrors);

        // 修复后文件再次校验通过且消息一致
        var count = 0;
        RecordingReader.Inspect(path, _ => count++);
        Assert.Equal(100, count);
    }

    [Fact]
    public void Torn_tail_frame_detected_and_repaired()
    {
        var writer = new RecordingWriter(new RecordingOptions { Directory = _dir, DeviceId = "AOI-01", ShiftId = "S1", DiskUsedRatioProvider = () => 0.1 });
        for (var i = 0; i < 50; i++)
        {
            writer.Write(TwinTopic.Motion, Axis(i));
        }

        writer.Abandon();
        var path = Assert.Single(Directory.GetFiles(_dir, "*.aotrec"));
        // 追加残缺帧（半帧垃圾），模拟写入中途断电
        File.AppendAllBytes(path, new byte[] { 0x40, 0x00, 0x00, 0x00, 0x12, 0x34, 0x56, 0x78, 0x01 });

        var info = RecordingReader.Repair(path);
        Assert.True(info.IsFinalized);
        Assert.Equal(50, info.ValidFrameCount);
        Assert.False(info.HasErrors);
    }

    [Fact]
    public void Million_messages_no_loss()
    {
        const int count = 1_000_000;
        using (var writer = new RecordingWriter(new RecordingOptions { Directory = _dir, DeviceId = "AOI-01", ShiftId = "STRESS", DiskUsedRatioProvider = () => 0.1 }))
        {
            for (var i = 0; i < count; i++)
            {
                if (!writer.Write(TwinTopic.Motion, Axis(i)))
                {
                    Assert.Fail($"第 {i} 条被磁盘水位暂停，压测失败");
                }
            }
        }

        long received = 0;
        var stressFile = Assert.Single(Directory.GetFiles(_dir, "*.aotrec"));
        var info = RecordingReader.Inspect(stressFile, _ => received++);
        Assert.Equal(count, received);
        Assert.Equal(count, info.ValidFrameCount);
        Assert.False(info.HasErrors);
    }

    [Fact]
    public void Shift_roll_creates_files_with_correct_naming_and_content()
    {
        using (var writer = new RecordingWriter(new RecordingOptions
        {
            Directory = _dir,
            DeviceId = "AOI-01",
            ShiftId = "S1",
            MaxFileDuration = TimeSpan.FromHours(1), // 每小时滚动
            DiskUsedRatioProvider = () => 0.1,
        }))
        {
            writer.Write(TwinTopic.Motion, Axis(0)); // 08:00
            writer.Write(TwinTopic.Motion, Axis(1)); // 同文件
            writer.Write(TwinTopic.Motion, new AxisSnapshot("ROBOT", "R", 2, 0.5, Base.AddHours(1))); // 09:00 → 滚动
            writer.Write(TwinTopic.Motion, Axis(3));
        }

        var files = Directory.GetFiles(_dir, "*.aotrec").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(
            ["AOI-01_20261103_0800.aotrec", "AOI-01_20261103_0900.aotrec"],
            files);

        var counts = new List<long>();
        foreach (var name in files)
        {
            var info = RecordingReader.Inspect(Path.Combine(_dir, name!));
            Assert.True(info.IsFinalized);
            counts.Add(info.ValidFrameCount);
        }

        Assert.Equal([2, 2], counts);
    }

    [Fact]
    public void Disk_watermark_pauses_writes_and_fires_event()
    {
        double ratio = 0.5;
        string? currentFile;
        using (var writer = new RecordingWriter(new RecordingOptions
        {
            Directory = _dir,
            DeviceId = "AOI-01",
            DiskUsedRatioProvider = () => ratio,
        }))
        {
            var fired = 0;
            writer.DiskWatermarkPaused += (_, _) => fired++;

            Assert.True(writer.Write(TwinTopic.Motion, Axis(0)));
            Assert.False(writer.PausedByDiskWatermark);

            ratio = 0.9; // 超过 0.85 水位
            Assert.False(writer.Write(TwinTopic.Motion, Axis(1)));
            Assert.True(writer.PausedByDiskWatermark);
            Assert.Equal(1, fired);

            ratio = 0.5; // 恢复
            Assert.True(writer.Write(TwinTopic.Motion, Axis(2)));
            Assert.False(writer.PausedByDiskWatermark);
            currentFile = writer.CurrentFilePath;
        }

        Assert.Equal(2, RecordingReader.Inspect(currentFile!).ValidFrameCount);
    }

    [Fact]
    public void Writer_rejects_unregistered_message_type()
    {
        using var writer = new RecordingWriter(Options());
        Assert.Throws<ArgumentException>(() => writer.Write(TwinTopic.Motion, new FakeMessage()));
    }

    private sealed record FakeMessage(string PartId = "X", DateTimeOffset Timestamp = default) : ITwinMessage;
}

/// <summary>T2.2 环形缓冲：FIFO、满拒绝、并发不丢。</summary>
public class RingBufferTests
{
    [Fact]
    public void Fifo_order_and_full_rejection()
    {
        var ring = new RingBuffer<int>(3);
        Assert.True(ring.TryEnqueue(1));
        Assert.True(ring.TryEnqueue(2));
        Assert.True(ring.TryEnqueue(3));
        Assert.False(ring.TryEnqueue(4));
        Assert.Equal(3, ring.Count);
        Assert.True(ring.TryTake(out var a));
        Assert.True(ring.TryTake(out var b));
        Assert.True(ring.TryTake(out var c));
        Assert.Equal((1, 2, 3), (a, b, c));
        Assert.False(ring.TryTake(out _));
    }

    [Fact]
    public void Concurrent_produce_consume_no_loss()
    {
        var ring = new RingBuffer<int>(1024);
        const int producers = 4;
        const int perProducer = 50_000;
        long consumed = 0;

        var consumer = Task.Run(() =>
        {
            for (var i = 0; i < producers * perProducer; i++)
            {
                ring.Take();
                Interlocked.Increment(ref consumed);
            }
        });

        var producerTasks = Enumerable.Range(0, producers)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < perProducer; i++)
                {
                    while (!ring.TryEnqueue(i))
                    {
                        Thread.Yield();
                    }
                }
            }))
            .ToArray();

        Task.WaitAll(producerTasks);
        consumer.Wait(TimeSpan.FromSeconds(30));
        Assert.Equal(producers * perProducer, consumed);
        Assert.Equal(0, ring.Count);
    }
}
