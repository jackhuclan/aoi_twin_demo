using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media.Media3D;
using AoiTwin.Adapters.Mock;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Recording;
using AoiTwin.Rendering.Interaction;
using AoiTwin.Rendering.Lod;
using AoiTwin.Rendering.Scene;

namespace AoiTwin.Soak;

/// <summary>
/// M1 验收工具（T4.2/T4.3）：
///   soak &lt;minutes&gt; &lt;recordDir&gt; &lt;reportPath&gt;           — Mock 全链路浸泡（采集+录制+内存采样）
///   bench &lt;catalogPath&gt; &lt;assetsRoot&gt; &lt;reportPath&gt;       — NFR-01/02/03 性能基准（Hub/GLB/点选/录制写入）
/// 报告为键值文本，验收文档（docs/m1-*）直接引用其中的实测数字。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "soak")
        {
            return RunSoak(args[1], args[2], args[3]);
        }

        if (args.Length == 4 && args[0] == "bench")
        {
            return RunBench(args[1], args[2], args[3]);
        }

        Console.Error.WriteLine("用法:");
        Console.Error.WriteLine("  AoiTwin.Soak soak <minutes> <recordDir> <reportPath>");
        Console.Error.WriteLine("  AoiTwin.Soak bench <catalogPath> <assetsRoot> <reportPath>");
        return 2;
    }

    // ---------------------------------------------------------------- soak

    private static int RunSoak(string minutesArg, string recordDir, string reportPath)
    {
        if (!double.TryParse(minutesArg, out var minutes) || minutes <= 0)
        {
            Console.Error.WriteLine($"非法时长: {minutesArg}");
            return 2;
        }

        Directory.CreateDirectory(recordDir);
        var process = Process.GetCurrentProcess();
        var report = new StringBuilder();
        var started = DateTimeOffset.Now;

        report.AppendLine($"mode=soak");
        report.AppendLine($"started={started:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"plannedMinutes={minutes}");
        report.AppendLine($"recordDir={Path.GetFullPath(recordDir)}");

        Console.WriteLine($"浸泡开始：{minutes} 分钟，录制目录 {recordDir}");

        using var session = MockCollectionFactory.CreateSession(new RealtimeHub(), new MockScenario(), new RecordingOptions
        {
            Directory = recordDir,
            DeviceId = "AOI-01",
            ShiftId = $"SOAK-{started:yyyyMMdd-HHmmss}",
            MaxFileDuration = TimeSpan.FromHours(2),
            // 浸泡盘为数据盘：用真实磁盘水位（与现场口径一致）
        });

        // StartRecording 返回 bool（是否成功启动）；录制文件是懒创建的，路径要等收尾后
        // 由 StopRecording() 返回，此处不打印（早期快照里还是 null，打出来会误导）
        var recordingStarted = session.StartRecording();
        session.Start();
        report.AppendLine($"recordingStarted={recordingStarted}");

        // 预热 1 分钟后的工作集作为基线（进程启动/JIT 的一次性开销不计入增长）
        var warmup = TimeSpan.FromMinutes(1);
        var sw = Stopwatch.StartNew();
        long baselineWorkingSet = 0;
        long maxGrowth = 0;
        long maxWorkingSet = 0;
        long maxDrops = 0;
        var sampling = new List<string>();
        var sampleInterval = TimeSpan.FromSeconds(30);
        var nextSample = TimeSpan.Zero;

        while (sw.Elapsed.TotalMinutes < minutes)
        {
            Thread.Sleep(500);
            var snapshot = session.Snapshot();

            maxDrops = Math.Max(maxDrops, snapshot.Recording.DroppedFrameCount);
            if (snapshot.Recording.DroppedFrameCount > 0)
            {
                report.AppendLine($"ALERT droppedFrames={snapshot.Recording.DroppedFrameCount} at={sw.Elapsed}");
            }

            process.Refresh();
            var workingSet = process.WorkingSet64;
            maxWorkingSet = Math.Max(maxWorkingSet, workingSet);

            if (sw.Elapsed < warmup)
            {
                baselineWorkingSet = workingSet;
            }
            else
            {
                maxGrowth = Math.Max(maxGrowth, workingSet - baselineWorkingSet);
            }

            if (sw.Elapsed >= nextSample)
            {
                nextSample = sw.Elapsed + sampleInterval;
                sampling.Add(
                    $"sample t={sw.Elapsed:hh\\:mm\\:ss} msgs={snapshot.TotalMessages} " +
                    $"rate={snapshot.AggregateRatePerSecond:F0}/s frames={snapshot.Recording.FrameCount} " +
                    $"bytes={snapshot.Recording.BytesWritten} drops={snapshot.Recording.DroppedFrameCount} " +
                    $"ws={workingSet / 1024 / 1024}MB");
                Console.WriteLine(sampling[^1]);
            }
        }

        var elapsed = sw.Elapsed;
        var recordingFile = session.StopRecording();
        session.Stop();
        process.Refresh();

        report.AppendLine($"actualDuration={elapsed:hh\\:mm\\:ss}");
        report.AppendLine($"baselineWorkingSetMB={baselineWorkingSet / 1024 / 1024}");
        report.AppendLine($"maxWorkingSetMB={maxWorkingSet / 1024 / 1024}");
        report.AppendLine($"maxGrowthMB={maxGrowth / 1024 / 1024}");
        report.AppendLine($"sampleCount={sampling.Count}");
        foreach (var line in sampling)
        {
            report.AppendLine(line);
        }

        // 录制文件一致性：帧数对账 + SHA-256 + 全帧 CRC
        var inspectErrors = -1;
        if (recordingFile is not null)
        {
            var info = RecordingReader.Inspect(recordingFile);
            inspectErrors = info.Errors.Count;
            report.AppendLine($"recordingFile={recordingFile}");
            report.AppendLine($"inspectFrames={info.ValidFrameCount}");
            report.AppendLine($"inspectFinalized={info.IsFinalized}");
            report.AppendLine($"inspectErrors={inspectErrors}");
            report.AppendLine($"fileSizeMB={new FileInfo(recordingFile).Length / 1024 / 1024}");
        }
        else
        {
            report.AppendLine("recordingFile=（无，录制未产出文件）");
        }

        // 三条判据同时成立才算 PASS：内存有界 + 零丢帧 + 录制文件可校验
        var growthOk = maxGrowth < 50L * 1024 * 1024;
        var dropsOk = maxDrops == 0;
        var inspectOk = inspectErrors == 0;
        report.AppendLine($"maxDrops={maxDrops}");
        report.AppendLine($"memoryGrowthOk={growthOk}");
        report.AppendLine($"dropsOk={dropsOk}");
        report.AppendLine($"inspectOk={inspectOk}");
        report.AppendLine($"result={(growthOk && dropsOk && inspectOk ? "PASS" : "FAIL")}");

        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"报告已写入 {reportPath}");
        return growthOk ? 0 : 1;
    }

    // ---------------------------------------------------------------- bench

    private static int RunBench(string catalogPath, string assetsRoot, string reportPath)
    {
        var report = new StringBuilder();
        report.AppendLine("mode=bench");
        report.AppendLine($"machine={Environment.MachineName}");
        report.AppendLine($"processor={Environment.ProcessorCount}C");
        report.AppendLine($"runtime={Environment.Version}");

        // --- NFR-03：Hub 发布—订阅单条开销 ---
        var hub = new RealtimeHub();
        var received = 0L;
        hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, _ => Interlocked.Increment(ref received));

        const int hubMessages = 200_000;
        var baseTime = DateTimeOffset.Now;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < hubMessages; i++)
        {
            hub.Publish(TwinTopic.Motion, new AxisSnapshot("ROBOT", "R", i % 150_000, i % 3, baseTime.AddMilliseconds(i)));
        }

        sw.Stop();
        var hubPerOpMs = sw.Elapsed.TotalMilliseconds / hubMessages;
        report.AppendLine($"hub.subscribers=1");
        report.AppendLine($"hub.messages={hubMessages}");
        report.AppendLine($"hub.received={Interlocked.Read(ref received)}");
        report.AppendLine($"hub.perOpMicros={hubPerOpMs * 1000:F3}");
        report.AppendLine($"hub.budgetOk={(hubPerOpMs <= 0.1 ? "PASS" : "FAIL")}（NFR-03 ≤ 0.1ms）");

        // --- NFR-03：录制写入吞吐（含 CRC/SHA/落盘） ---
        var recordDir = Path.Combine(Path.GetTempPath(), $"aoi-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(recordDir);
        long writeBytes;
        using (var writer = new RecordingWriter(new RecordingOptions
        {
            Directory = recordDir,
            DeviceId = "AOI-01",
            ShiftId = "BENCH",
            DiskUsedRatioProvider = () => 0.1,
        }))
        {
            const int writeMessages = 50_000;
            sw.Restart();
            for (var i = 0; i < writeMessages; i++)
            {
                writer.Write(TwinTopic.Motion, new AxisSnapshot("ROBOT", "R", i % 150_000, i % 3, baseTime.AddMilliseconds(i)));
            }

            sw.Stop();
            writeBytes = writer.TotalBytesWritten;
            report.AppendLine($"recording.messages={writeMessages}");
            report.AppendLine($"recording.throughputPerSec={writeMessages / sw.Elapsed.TotalSeconds:F0}");
            report.AppendLine($"recording.perOpMicros={sw.Elapsed.TotalMilliseconds / writeMessages * 1000:F3}");
            report.AppendLine($"recording.bytesPerMessage={writeBytes / (double)writeMessages:F1}");
        }

        Directory.Delete(recordDir, recursive: true);

        // --- NFR-02：GLB 后台预加载（LOD1+LOD2 两档 + 合批） ---
        var loadTimes = new List<double>();
        LodPackage? package = null;
        for (var i = 0; i < 5; i++)
        {
            sw.Restart();
            package = LodPackage.LoadAsync(catalogPath, assetsRoot).GetAwaiter().GetResult();
            sw.Stop();
            loadTimes.Add(sw.Elapsed.TotalMilliseconds);
        }

        report.AppendLine($"glb.loadMs=" + string.Join(",", loadTimes.Select(t => t.ToString("F0"))));
        report.AppendLine($"glb.avgMs={loadTimes.Average():F0}");
        report.AppendLine($"glb.budgetOk={(loadTimes.Average() <= 3000 ? "PASS" : "FAIL")}（NFR-02 ≤ 3s）");

        // --- NFR（FR-M1-05）：LOD Draw Call 对比 ---
        var nearMeshes = package!.MeshCount(LodLevel.L1);
        var farMeshes = package.MeshCount(LodLevel.L2);
        report.AppendLine($"lod.meshesL1={nearMeshes}");
        report.AppendLine($"lod.meshesL2={farMeshes}");
        report.AppendLine($"lod.drawCallReduction={1 - (farMeshes / (double)nearMeshes):P0}");

        // --- NFR（T1.8）：点选响应 ---
        var picker = new PartPicker(package.PickParts.Select(PickPart.From));
        var origins = new List<Point3D>(1000);
        var random = new Random(42);
        for (var i = 0; i < 1000; i++)
        {
            origins.Add(new Point3D(2 + random.NextDouble() * 3, -3 + (random.NextDouble() * 3), 1 + (random.NextDouble() * 2)));
        }

        sw.Restart();
        var hits = 0;
        foreach (var origin in origins)
        {
            var direction = new System.Windows.Media.Media3D.Vector3D(-origin.X, -origin.Y, 0.7 - origin.Z);
            if (picker.Pick(origin, direction) is not null)
            {
                hits++;
            }
        }

        sw.Stop();
        report.AppendLine($"pick.probes={origins.Count}");
        report.AppendLine($"pick.hits={hits}");
        report.AppendLine($"pick.perOpMicros={sw.Elapsed.TotalMilliseconds / origins.Count * 1000:F1}");
        report.AppendLine($"pick.totalMs={sw.Elapsed.TotalMilliseconds:F0}");
        report.AppendLine($"pick.budgetOk={(sw.Elapsed.TotalMilliseconds / origins.Count <= 50 ? "PASS" : "FAIL")}（≤ 50ms/次）");

        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        Console.WriteLine(report.ToString());
        Console.WriteLine($"报告已写入 {reportPath}");
        return 0;
    }
}
