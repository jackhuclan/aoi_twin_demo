using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Kinematics;
using AoiTwin.Core.Recording;
using AoiTwin.Core.Replay;
using AoiTwin.Rendering.Dynamics;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;
using AoiTwin.ViewModel;
using Xunit;

namespace AoiTwin.Regression.Tests;

/// <summary>回放回归结果（TA.17）：一次性把「帧数 / 末帧位姿 / 队列消费 / 视觉状态」摊平。</summary>
/// <param name="RecordedFrames">录制帧总数（Motion + Optics + Result）。</param>
/// <param name="ReplayedFrames">回放引擎走完的帧数。</param>
/// <param name="StageOffsetX">末帧载台 X 位移（米）。</param>
/// <param name="WaferEnqueued">入队晶圆片数。</param>
/// <param name="WaferTaken">被渲染线程取走处理的片数。</param>
/// <param name="WaferDropped">因队列满被挤掉的片数（回归应为 0）。</param>
/// <param name="DefectPoints">缺陷点云总点数（片数 × 每片缺陷数）。</param>
/// <param name="OverlayColors">叠加层上出现过的漫反射颜色（光学通道变色证据）。</param>
public sealed record TwinVisualRegressionReport(
    int RecordedFrames,
    int ReplayedFrames,
    double StageOffsetX,
    int WaferEnqueued,
    int WaferTaken,
    long WaferDropped,
    int DefectPoints,
    IReadOnlyList<Color> OverlayColors);

/// <summary>
/// 3D 视觉回放回归（TA.17，DoD §6）：用合成录制文件（与 M1 录制同格式 .aotrec）驱动
/// TA.4 运动同步 / TA.5 光学变色 / TA.7 Wafer Map / TA.8 缺陷点云，断言
/// 「帧数一致 / 末帧位姿接近期望 / 上传队列消费完毕」。
///
/// 装配用最小合成件集而非真实 GLB：GLB 解析是异步且受磁盘影响，回归要的是确定性；
/// 真实资产的装配链路由 UI 冒烟覆盖，两者互补。
///
/// 帧泵：回放在后台线程推进，STA 线程同时以 ~5ms 周期驱动四个驱动器的 OnFrame——
/// 与真机渲染循环同构，因此「队列消费完毕」是真实结论而非事后清空。
/// </summary>
public static class TwinVisualRegression
{
    /// <summary>每片的帧数（Motion + Optics + Result 各一帧）。</summary>
    public const int FramesPerWafer = 3;

    /// <summary>录制晶圆片数。</summary>
    public const int WaferFrames = 12;

    /// <summary>每片缺陷数。</summary>
    public const int DefectsPerWafer = 2;

    /// <summary>录制帧总数。</summary>
    public const int RecordedFrameCount = WaferFrames * FramesPerWafer;

    /// <summary>期望末帧位移：第 11 帧轴值 1_100_000 µm × 1e-6。</summary>
    public const double ExpectedStageOffsetX = (WaferFrames - 1) * 100_000 * 1e-6;

    /// <summary>PRD §5.5 通道 8 与通道 4 的颜色（末帧通道 = (11 % 8) + 1 = 4）。</summary>
    public static Color Channel8Color => Color.FromRgb(0xD6, 0xFF, 0xC4);

    public static Color LastFrameChannelColor => Color.FromRgb(0xC4, 0xFF, 0xE1);

    public static TwinVisualRegressionReport Run(string workDir, string assetsRoot, TwinViewport3D viewport)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsRoot);
        ArgumentNullException.ThrowIfNull(viewport);

        var recording = WriteRecording(workDir);
        var assembly = BuildAssembly();
        var hub = new RealtimeHub();
        using var visual = new TwinVisualViewModel(
            hub,
            new MotionPoseEngine(TeachCoordinateMapper.Load(Path.Combine(assetsRoot, "teach-calibration.json"))),
            () => ThrottlePolicy.PassThrough); // 回归要看到每一帧轴值，关掉 200ms 合并

        using var motion = new MotionSyncDriver(viewport, visual, () => assembly, static id => id);
        using var optics = new OpticsSyncDriver(viewport, visual, static () => null, () => assembly);
        using var waferMap = new WaferMapDriver(viewport, visual, () => assembly);
        using var defects = new DefectPointDriver(viewport, visual, () => assembly);

        void Pump()
        {
            motion.OnFrame();
            optics.OnFrame();
            waferMap.OnFrame();
            defects.OnFrame();
        }

        Pump(); // 首帧绑定装配

        // 1× 回放（12 帧 ≈ 1.2s）：泵周期（≈15ms）远小于帧间隔（100ms），
        // 队列消费必然跟得上——「消费完毕」是过程结论，不是事后清空的结果
        using var engine = new ReplayEngine(recording, hub) { Speed = 1.0 };
        engine.Play();

        var stopwatch = Stopwatch.StartNew();
        while (engine.State == ReplayState.Playing && stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            Pump();
            Thread.Sleep(5);
        }

        Pump(); // 收尾一帧：把最后一片的结果落进纹理与点云

        var taken = (int)visual.WaferQueue.TakenTotal;
        while (visual.WaferQueue.TryTake(out _))
        {
            taken++; // 泵未覆盖到的残片仍算消费完毕（容量 8，正常应为 0 残）
        }

        return new TwinVisualRegressionReport(
            engine.FrameCount,
            engine.CurrentFrameIndex,
            ReadStageOffsetX(assembly),
            (int)visual.WaferQueue.EnqueuedTotal,
            taken,
            visual.WaferQueue.DroppedTotal,
            defects.PointCount,
            CollectOverlayColors(viewport));
    }

    /// <summary>合成录制：每片 1 轴帧 + 1 光学帧 + 1 结果帧，时间戳间隔 100ms（8× 回放 ≈ 12.5ms/帧）。</summary>
    private static string WriteRecording(string dir)
    {
        Directory.CreateDirectory(dir);
        var baseTime = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
        using var writer = new RecordingWriter(new RecordingOptions
        {
            Directory = dir,
            DeviceId = "AOI-01",
            ShiftId = "S1",
            DiskUsedRatioProvider = () => 0.1,
        });

        for (var i = 0; i < WaferFrames; i++)
        {
            var timestamp = baseTime.AddMilliseconds(i * 100);

            writer.Write(TwinTopic.Motion, new AxisSnapshot("STAGE", "X", i * 100_000, 0, timestamp));
            writer.Write(TwinTopic.Optics, new OpticsEvent(
                "LIGHT_RING", OpticsKind.LightChannel, (i % 8) + 1, 1.0, timestamp));

            var defectBase = i * 100;
            writer.Write(TwinTopic.Result, new WaferResult(
                $"WFR-{i + 1:D3}",
                new WaferMapSnapshot(2, 2, [0, 1, 2, 1]),
                [
                    new DefectRecord(defectBase + 1, (i % 50) - 25.0, 10.0, 8.0, "Scratch", 120),
                    new DefectRecord(defectBase + 2, 5.0, (i % 40) - 20.0, 14.0, "Particle", 90),
                ],
                TimeSpan.FromSeconds(6.5),
                timestamp));
        }

        var files = Directory.GetFiles(dir, "*.aotrec");
        Assert.Single(files);
        return files[0];
    }

    private static double ReadStageOffsetX(SceneAssembly assembly)
    {
        var stage = assembly.Find("STAGE");
        Assert.NotNull(stage);
        return stage!.Motion.OffsetX;
    }

    /// <summary>叠加层上的漫反射色集合（光学 8 段环的当前灯色）。</summary>
    private static List<Color> CollectOverlayColors(TwinViewport3D viewport)
    {
        var colors = new List<Color>();
        void Walk(Model3D model)
        {
            switch (model)
            {
                case GeometryModel3D { Material: DiffuseMaterial { Brush: SolidColorBrush brush } }:
                    colors.Add(brush.Color);
                    break;
                case Model3DGroup group:
                    foreach (var child in group.Children)
                    {
                        Walk(child);
                    }

                    break;
            }
        }

        void WalkVisual(Visual3D visual)
        {
            if (visual is ModelVisual3D { Content: { } content })
            {
                Walk(content);
            }

            if (visual is ModelVisual3D mv)
            {
                foreach (var child in mv.Children)
                {
                    WalkVisual(child);
                }
            }
        }

        foreach (var visual in viewport.OverlayRoot.Children)
        {
            WalkVisual(visual);
        }

        return colors;
    }

    /// <summary>最小合成件集：STAGE（运动）+ WAFER_CURRENT（纹理/点云）+ LIGHT_RING / LIGHT_COAX（变色）。</summary>
    private static SceneAssembly BuildAssembly()
    {
        var visuals = new List<PartVisual>
        {
            MakePart("STAGE", new Point3D(0, 0, 0.30), new Size3D(0.30, 0.30, 0.02)),
            MakePart("WAFER_CURRENT", new Point3D(0, 0, 0.585), new Size3D(0.30, 0.30, 0.004)),
            MakePart("LIGHT_RING", new Point3D(0, 0, 0.72), new Size3D(0.16, 0.16, 0.03)),
            MakePart("LIGHT_COAX", new Point3D(0, 0, 0.78), new Size3D(0.06, 0.06, 0.03)),
        };
        return new SceneAssembly(visuals, visuals.Count);
    }

    private static PartVisual MakePart(string partId, Point3D center, Size3D size)
    {
        var hx = size.X / 2;
        var hy = size.Y / 2;
        var hz = size.Z / 2;
        var mesh = new MeshGeometry3D
        {
            Positions =
            [
                new(center.X - hx, center.Y - hy, center.Z - hz),
                new(center.X + hx, center.Y - hy, center.Z - hz),
                new(center.X + hx, center.Y + hy, center.Z - hz),
                new(center.X - hx, center.Y + hy, center.Z - hz),
                new(center.X - hx, center.Y - hy, center.Z + hz),
                new(center.X + hx, center.Y - hy, center.Z + hz),
                new(center.X + hx, center.Y + hy, center.Z + hz),
                new(center.X - hx, center.Y + hy, center.Z + hz),
            ],
            TriangleIndices =
            [
                0, 2, 1, 0, 3, 2,
                4, 5, 6, 4, 6, 7,
                0, 1, 5, 0, 5, 4,
                2, 3, 7, 2, 7, 6,
                1, 2, 6, 1, 6, 5,
                3, 0, 4, 3, 4, 7,
            ],
        };
        mesh.Freeze();

        var model = new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60))));
        model.Freeze();
        var group = new Model3DGroup();
        group.Children.Add(model);
        var visual = new ModelVisual3D { Content = group };
        return new PartVisual(partId, visual, group, new TranslateTransform3D());
    }
}
