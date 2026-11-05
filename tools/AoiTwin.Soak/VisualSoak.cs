using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using AoiTwin.Adapters;
using AoiTwin.Adapters.Mock;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Kinematics;
using AoiTwin.Rendering.Dynamics;
using AoiTwin.Rendering.Lod;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;
using AoiTwin.ViewModel;

namespace AoiTwin.Soak;

/// <summary>soak-visual 参数（命令行为准，缺省即现场口径）。</summary>
internal sealed record VisualSoakOptions
{
    /// <summary>计划时长（分钟，允许小数；72h = 4320）。</summary>
    public required double Minutes { get; init; }

    /// <summary>报告落盘路径（同目录另写 <c>.progress</c> 心跳文件）。</summary>
    public required string ReportPath { get; init; }

    /// <summary>资产根目录（缺省从可执行目录向上查找 <c>assets/part-catalog.json</c>）。</summary>
    public string? AssetsRoot { get; init; }

    /// <summary>晶圆结果间隔（秒）。默认 6.5s 取 PRD 节拍：短于此值等于放大缺陷点云增长，长跑结论会失真。</summary>
    public double WaferIntervalSec { get; init; } = 6.5;

    /// <summary>每片缺陷数均值（Mock 场景参数）。</summary>
    public int DefectsPerWafer { get; init; } = 4;

    /// <summary>窗口宽（像素）。渲染必须真实提交帧，故一定开窗。</summary>
    public int Width { get; init; } = 1600;

    /// <summary>窗口高（像素）。</summary>
    public int Height { get; init; } = 900;

    /// <summary>窗口是否可见（默认移到屏幕外，不挡班次界面；可见与否不影响是否渲染）。</summary>
    public bool Visible { get; init; }

    /// <summary>采样周期（秒）。</summary>
    public int SampleSeconds { get; init; } = 30;

    /// <summary>预热时长（分钟，超过计划时长 10% 时按 10% 收）。</summary>
    public double WarmupMinutes { get; init; } = 5;
}

/// <summary>
/// 带 VM 与渲染循环的长跑（TA.18，NFR-11/12 的现场判据）：真实 GLB 装配 + 六路同步驱动器 +
/// WPF 帧循环（<see cref="TwinViewport3D.FrameRendered"/>，即 CompositionTarget）+ Mock 四路数据源。
///
/// 与 <c>soak</c>（M1 采集链路）的分工：本模式量**渲染侧**——帧率衰减与渲染态内存增长；
/// 采集/录制链路由 <c>soak</c> 覆盖，判据不同，不合并成一个模式。
///
/// 三处工程取舍（现场会踩，故写在这里）：
/// 1. **必须开窗**：WPF 的渲染节拍来自窗口合成，无窗口就是计时器空转、帧率无从谈起；
///    默认把窗口移到屏幕外——能渲染，又不挡操作员。
/// 2. **合成中断要能看见**：远程桌面/最小化会让 <c>CompositionTarget.Rendering</c> 停摆，
///    此时由 1s 看门狗兜底泵帧（保证 VM/驱动器仍被驱动），报告标 <c>frameClock=stalled</c>
///    并**跳过帧率判据**——拿空转帧数当帧率是假结论。
/// 3. **崩溃要有物证**：每个采样点立即追加到 <c>&lt;report&gt;.progress</c>；进程被系统杀掉时
///    正式报告写不出来，心跳文件会停在最后一条，配时间戳就能定位死在哪个时段。
/// </summary>
internal static class VisualSoak
{
    /// <summary>TA.18 判据：预热后工作集增长上限（MB）。</summary>
    public const long GrowthBudgetMb = 100;

    /// <summary>TA.18 判据：末段帧率相对基线的衰减上限。</summary>
    public const double FpsDecayBudget = 0.05;

    /// <summary>长帧阈值（与视口探针 NFR-02 口径一致）。</summary>
    private const double LongFrameMs = 100;

    /// <summary>看门狗判定「合成停摆」的空窗（秒）。</summary>
    private const int StallSeconds = 2;

    public static int Run(VisualSoakOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var reportPath = Path.GetFullPath(options.ReportPath);
        var reportDir = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(reportDir))
        {
            Directory.CreateDirectory(reportDir);
        }

        Console.WriteLine($"[soak-visual] 计划 {options.Minutes:F1} 分钟，报告 {reportPath}");

        Application app;
        try
        {
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
        catch (Exception ex)
        {
            WriteAbort(reportPath, "无法初始化 WPF Application（本机无桌面会话？）", ex);
            return 3;
        }

        var viewport = new TwinViewport3D();
        var window = new Window
        {
            Title = "AoiTwin soak-visual",
            Content = viewport,
            Width = options.Width,
            Height = options.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = options.Visible ? 0 : -20000,
            Top = options.Visible ? 0 : -20000,
        };

        var session = new SoakSession(options, reportPath, viewport);
        window.Loaded += async (_, _) => await session.StartAsync().ConfigureAwait(true);

        // 启动看门狗：无桌面会话时窗口可能永远不触发 Loaded，无人值守的 72h 不能就这么空等
        var startupGuard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        startupGuard.Tick += (_, _) =>
        {
            startupGuard.Stop();
            session.GuardStartup();
        };
        startupGuard.Start();

        try
        {
            app.Run(window);
        }
        catch (Exception ex)
        {
            session.Fail("渲染循环异常退出", ex);
        }

        startupGuard.Stop();
        return session.ExitCode;
    }

    /// <summary>Application 都建不起来时也要留一份报告——空手而归在现场等于白跑一个班次。</summary>
    private static void WriteAbort(string reportPath, string reason, Exception ex)
    {
        var text = new StringBuilder()
            .AppendLine("mode=soak-visual")
            .AppendLine($"started={DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"machine={Environment.MachineName}")
            .AppendLine($"failureReason={reason}")
            .AppendLine($"exception={ex.GetType().Name}: {ex.Message}")
            .AppendLine("result=FAIL")
            .ToString();
        File.WriteAllText(reportPath, text, new UTF8Encoding(false));
        Console.Error.WriteLine(text);
    }

    /// <summary>一次采样的定稿（内存/帧率/业务计数同拍取样，便于归因）。</summary>
    private readonly record struct Sample(
        TimeSpan At,
        double Fps,
        double P95Ms,
        long WorkingSetMb,
        long PrivateMb,
        long ManagedHeapMb,
        int DefectPoints,
        long Wafers,
        long HandlerErrors,
        long DroppedWafers,
        int Gen2);

    /// <summary>长跑会话：装配 → 四路数据 → 帧循环 → 采样 → 收口报告。</summary>
    private sealed class SoakSession
    {
        private readonly VisualSoakOptions _options;
        private readonly string _reportPath;
        private readonly TwinViewport3D _viewport;
        private readonly List<Sample> _samples = [];
        private readonly double[] _deltas = new double[8192];
        private readonly Dictionary<string, string> _header = new(StringComparer.Ordinal);
        private readonly Stopwatch _clock = new();
        private readonly Process _process = Process.GetCurrentProcess();

        private IRealtimeHub? _hub;
        private TwinVisualViewModel? _visual;
        private HandlingStateViewModel? _handling;
        private SceneAssembly? _assembly;
        private CollectionSession? _collection;
        private IDisposable? _statusFeeder;
        private MotionSyncDriver? _motion;
        private OpticsSyncDriver? _optics;
        private ScanFrustumDriver? _frustum;
        private WaferMapDriver? _waferMap;
        private DefectPointDriver? _defects;
        private HandlingSyncDriver? _handlingDriver;
        private AoiTwin.Rendering.Fx.FxOverlay? _fxOverlay;
        private AoiTwin.Rendering.Fx.AlarmPulseFx? _alarmPulse;
        private AoiTwin.Rendering.Fx.TransportParticlesFx? _transportParticles;
        private AoiTwin.Rendering.Camera.FollowCamera? _followCamera;
        private DispatcherTimer? _sampleTimer;
        private DispatcherTimer? _watchdog;

        private DateTimeOffset _startedAt;
        private TimeSpan _warmup;
        private TimeSpan _duration;
        private int _deltaCount;
        private long _intervalFrames;
        private long _totalFrames;
        private long _pumped;
        private long _longFrames;
        private int _stallStreak;
        private bool _renderStalled;
        private bool _finished;
        private long _wafersSeen;
        private int _defectPointsAtEnd;
        private bool _started;
        private string? _failureReason;

        public SoakSession(VisualSoakOptions options, string reportPath, TwinViewport3D viewport)
        {
            _options = options;
            _reportPath = reportPath;
            _viewport = viewport;
        }

        /// <summary>进程退出码：0 = 判据全过；1 = 判据未过；3 = 环境/资产不可用。</summary>
        public int ExitCode { get; private set; } = 1;

        public async Task StartAsync()
        {
            _startedAt = DateTimeOffset.Now;
            _duration = TimeSpan.FromMinutes(_options.Minutes);
            // 预热：JIT、GLB 解析、GPU 表面分配都是一次性开销，不预热则「增长」全是冷启动
            _warmup = TimeSpan.FromMinutes(Math.Min(_options.WarmupMinutes, _options.Minutes * 0.1));

            try
            {
                var assetsRoot = ResolveAssetsRoot(_options.AssetsRoot);
                var catalog = Path.Combine(assetsRoot, "part-catalog.json");
                if (!File.Exists(catalog))
                {
                    Fail($"台账不存在：{catalog}", null);
                    return;
                }

                FillHeader(assetsRoot, catalog);

                // --- 场景：真实 GLB 优先；解析失败退回合成件集并如实记账（长跑要能自证跑的是什么）
                _hub = new RealtimeHub();
                try
                {
                    var package = await LodPackage.LoadAsync(catalog, assetsRoot).ConfigureAwait(true);
                    _assembly = SceneTreeBuilder.Build(package.Near, _viewport.SceneRoot);
                    _header["scene"] = "glb";
                    _header["scene.parts"] = package.Near.Parts.Count.ToString(CultureInfo.InvariantCulture);
                    _header["scene.triangles"] = package.Near.TotalTriangles.ToString("N0", CultureInfo.InvariantCulture);
                    _header["scene.meshesL1"] = package.MeshCount(LodLevel.L1).ToString(CultureInfo.InvariantCulture);
                    _header["scene.meshesL2"] = package.MeshCount(LodLevel.L2).ToString(CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is SceneAssetException or IOException or InvalidDataException or NotSupportedException)
                {
                    _assembly = SyntheticAssembly.Build(_viewport.SceneRoot);
                    _header["scene"] = "synthetic";
                    _header["scene.fallbackReason"] = $"{ex.GetType().Name}: {ex.Message}";
                }

                // --- VM：真标定 + 真节流策略文件（与现场同一条链路，不做测试专用简化）
                _visual = new TwinVisualViewModel(
                    _hub,
                    new MotionPoseEngine(TeachCoordinateMapper.Load(Path.Combine(assetsRoot, "teach-calibration.json"))),
                    TryPolicySource(Path.Combine(assetsRoot, "throttle-policy.json")));
                _handling = new HandlingStateViewModel(_hub);

                // --- 六路驱动器：顺序与 TwinView.xaml.cs 一致，少挂一路就等于少测一路
                _motion = new MotionSyncDriver(_viewport, _visual, () => _assembly, static id => id);
                _optics = new OpticsSyncDriver(_viewport, _visual, static () => null, () => _assembly);
                _frustum = new ScanFrustumDriver(_viewport, _hub, () => _assembly);
                _waferMap = new WaferMapDriver(_viewport, _visual, () => _assembly);
                _defects = new DefectPointDriver(
                    _viewport,
                    _visual,
                    () => _assembly,
                    () => LightChannelPalette.DefaultDefectClassColors);
                _handlingDriver = new HandlingSyncDriver(_viewport, _handling, () => _assembly);

                // --- FX 与跟随相机：与 TwinView.xaml.cs 同样挂进帧循环。
                // 不做「只测同步层」的减法：粒子/报警壳每帧都在改可视树，漏掉它们等于漏掉一类泄漏源。
                _fxOverlay = new AoiTwin.Rendering.Fx.FxOverlay(_viewport);
                _alarmPulse = new AoiTwin.Rendering.Fx.AlarmPulseFx(_viewport, _hub, () => _assembly, _fxOverlay);
                _transportParticles = new AoiTwin.Rendering.Fx.TransportParticlesFx(
                    _viewport,
                    _fxOverlay,
                    new Point3D(0.40, -0.62, 0.10), // Track 装载位
                    new Point3D(0.0, 0.0, 0.60));   // 检测位
                _followCamera = new AoiTwin.Rendering.Camera.FollowCamera(
                    _viewport,
                    () => _assembly?.Find("STAGE") is { } stage
                        ? new Point3D(
                            stage.Geometry.Bounds.X + (stage.Geometry.Bounds.SizeX / 2) + stage.Motion.OffsetX,
                            stage.Geometry.Bounds.Y + (stage.Geometry.Bounds.SizeY / 2) + stage.Motion.OffsetY,
                            stage.Geometry.Bounds.Z + (stage.Geometry.Bounds.SizeZ / 2) + stage.Motion.OffsetZ)
                        : null);

                PumpOnce();
                _header["drivers"] = "motion,optics,frustum,wafermap,defects,handling,fx(alarm,particles),followCamera";

                // --- 装载状态喂料：Mock 四路不含 Status，要覆盖晶圆占位/槽位点亮只能自己发
                _statusFeeder = _hub.Subscribe<WaferResult>(TwinTopic.Result, OnResultForStatus);

                // --- Mock 采集：四路适配器 → 同一个 Hub（与采集页、数据集生成器同一份装配）
                _collection = MockCollectionFactory.CreateSession(_hub, BuildScenario(_options));
                _collection.Start();
                _header["feed"] =
                    $"mock waferSec={_options.WaferIntervalSec:F1} defectsPerWafer={_options.DefectsPerWafer}";

                _viewport.FrameRendered += OnFrame;
                _clock.Start();

                _sampleTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromSeconds(_options.SampleSeconds),
                };
                _sampleTimer.Tick += (_, _) => OnSampleTick();
                _sampleTimer.Start();

                _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
                _watchdog.Tick += (_, _) => OnWatchdogTick();
                _watchdog.Start();

                Console.WriteLine("[soak-visual] 已启动：场景与六路驱动器就位，开始采样");
                _started = true;
            }
            catch (Exception ex)
            {
                Fail("启动阶段失败", ex);
            }
        }

        /// <summary>帧回调（UI 线程）：只记数、不做任何分配——它本身就是被测对象。</summary>
        private void OnFrame(object? sender, TimeSpan delta)
        {
            _intervalFrames++;
            _totalFrames++;
            _stallStreak = 0;
            if (_deltaCount < _deltas.Length)
            {
                _deltas[_deltaCount++] = delta.TotalMilliseconds;
            }

            if (delta.TotalMilliseconds > LongFrameMs)
            {
                _longFrames++;
            }
        }

        /// <summary>看门狗：合成节拍停了就自己泵一帧并如实标注（不把空转帧当渲染帧）。</summary>
        private void OnWatchdogTick()
        {
            if (_finished)
            {
                return;
            }

            if (++_stallStreak < StallSeconds)
            {
                return;
            }

            _stallStreak = 0;
            PumpOnce();
            _pumped++;
            if (_clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                _renderStalled = true;
            }
        }

        private void PumpOnce()
        {
            _motion?.OnFrame();
            _optics?.OnFrame();
            _frustum?.OnFrame();
            _waferMap?.OnFrame();
            _defects?.OnFrame();
            _handlingDriver?.OnFrame();
        }

        private void OnSampleTick()
        {
            if (_finished)
            {
                return;
            }

            CollectSample();
            if (_clock.Elapsed >= _duration)
            {
                Finish();
            }
        }

        private void CollectSample()
        {
            _process.Refresh();
            var seconds = Math.Max(1, _options.SampleSeconds);
            var sample = new Sample(
                _clock.Elapsed,
                _intervalFrames / (double)seconds,
                Percentile95(),
                _process.WorkingSet64 / 1024 / 1024,
                _process.PrivateMemorySize64 / 1024 / 1024,
                GC.GetTotalMemory(false) / 1024 / 1024,
                _defects?.PointCount ?? 0,
                Interlocked.Read(ref _wafersSeen),
                _visual?.HandlerErrorCount ?? 0,
                _visual?.WaferQueue.DroppedTotal ?? 0,
                GC.CollectionCount(2));

            _samples.Add(sample);
            _intervalFrames = 0;
            _deltaCount = 0;

            var line = FormatSample(sample);
            AppendProgress(line);
            Console.WriteLine(line);
        }

        private static string FormatSample(in Sample s) =>
            $"sample t={s.At:hh\\:mm\\:ss} ws={s.WorkingSetMb}MB private={s.PrivateMb}MB heap={s.ManagedHeapMb}MB " +
            $"fps={s.Fps:F1} p95={s.P95Ms:F1}ms wafers={s.Wafers} points={s.DefectPoints} " +
            $"errors={s.HandlerErrors} drops={s.DroppedWafers} gen2={s.Gen2}";

        private double Percentile95()
        {
            if (_deltaCount == 0)
            {
                return 0;
            }

            var ordered = _deltas.AsSpan(0, _deltaCount).ToArray();
            Array.Sort(ordered);
            return ordered[Math.Min(ordered.Length - 1, (int)(ordered.Length * 0.95))];
        }

        /// <summary>收口：停喂料 → 拆驱动器 → 算判据 → 落报告。</summary>
        private void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _sampleTimer?.Stop();
            _watchdog?.Stop();
            _viewport.FrameRendered -= OnFrame;
            _clock.Stop();

            // 点云点数必须在 Dispose 之前取：驱动器释放会清池，事后再读就是 0
            _defectPointsAtEnd = _defects?.PointCount ?? 0;

            _collection?.Stop();
            _statusFeeder?.Dispose();
            _collection?.Dispose();

            _motion?.Dispose();
            _optics?.Dispose();
            _frustum?.Dispose();
            _waferMap?.Dispose();
            _defects?.Dispose();
            _handlingDriver?.Dispose();
            _transportParticles?.Dispose();
            _alarmPulse?.Dispose();
            _fxOverlay?.Dispose();
            _followCamera?.Dispose();
            _visual?.Dispose();
            _handling?.Dispose();

            var report = ComposeReport();
            File.WriteAllText(_reportPath, report, new UTF8Encoding(false));
            WriteSamplesCsv();
            AppendProgress($"FINISHED {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine(report);
            Console.WriteLine($"[soak-visual] 报告已写入 {_reportPath}");

            ExitCode = report.Contains("result=PASS", StringComparison.Ordinal) ? 0 : 1;
            Application.Current?.Shutdown();
        }

        private string ComposeReport()
        {
            var post = _samples.Where(s => s.At >= _warmup).ToList();
            Sample? baseline = post.Count > 0 ? post[0] : null;
            var maxWorkingSet = _samples.Count == 0 ? 0 : _samples.Max(s => s.WorkingSetMb);
            var maxGrowth = baseline is { } b && post.Count > 0 ? post.Max(s => s.WorkingSetMb) - b.WorkingSetMb : 0;
            var hours = Math.Max(_clock.Elapsed.TotalHours, 1e-6);

            // 帧率基线取预热后前 5 分钟；末段取最后 3 个采样点（采样不足则退回全段）
            var baseWindow = post.Where(s => s.At < _warmup + TimeSpan.FromMinutes(5)).ToList();
            var tailWindow = post.Count >= 3 ? post.TakeLast(3).ToList() : post;
            var baselineFps = baseWindow.Count == 0 ? 0 : baseWindow.Average(s => s.Fps);
            var tailFps = tailWindow.Count == 0 ? 0 : tailWindow.Average(s => s.Fps);
            var decay = baselineFps <= 0.1 ? 0 : (baselineFps - tailFps) / baselineFps;

            var growthOk = maxGrowth < GrowthBudgetMb;
            var fpsJudged = !_renderStalled && baselineFps > 0.1 && post.Count >= 4;
            var fpsOk = fpsJudged && decay <= FpsDecayBudget;
            var handlerErrors = _visual?.HandlerErrorCount ?? 0;
            var handlerOk = handlerErrors == 0;
            var pass = _failureReason is null && growthOk && handlerOk && (fpsJudged ? fpsOk : true);

            var sb = new StringBuilder();
            sb.AppendLine("mode=soak-visual");
            sb.AppendLine($"started={_startedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"finished={DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"plannedMinutes={_options.Minutes:F1}");
            sb.AppendLine($"actualDuration={_clock.Elapsed:hh\\:mm\\:ss}");
            sb.AppendLine($"machine={Environment.MachineName}");
            sb.AppendLine($"processor={Environment.ProcessorCount}C");
            sb.AppendLine($"os={Environment.OSVersion.VersionString}");
            sb.AppendLine($"runtime={Environment.Version}");
            foreach (var pair in _header)
            {
                sb.AppendLine($"{pair.Key}={pair.Value}");
            }

            sb.AppendLine($"window={_options.Width}x{_options.Height} {(_options.Visible ? "visible" : "offscreen")}");
            sb.AppendLine($"frameClock={(_renderStalled ? "stalled" : "rendering")}");
            sb.AppendLine($"warmupMinutes={_warmup.TotalMinutes:F1}");
            sb.AppendLine($"framesRendered={_totalFrames}");
            sb.AppendLine($"framesPumped={_pumped}");
            sb.AppendLine($"longFrames={_longFrames}");
            sb.AppendLine($"baselineWorkingSetMB={baseline?.WorkingSetMb ?? 0}");
            sb.AppendLine($"maxWorkingSetMB={maxWorkingSet}");
            sb.AppendLine($"maxGrowthMB={maxGrowth}");
            sb.AppendLine($"growthRateMBPerHour={(hours >= 1 ? (maxGrowth / hours).ToString("F2", CultureInfo.InvariantCulture) : "NA")}");
            sb.AppendLine($"baselineFps={baselineFps:F1}");
            sb.AppendLine($"tailFps={tailFps:F1}");
            sb.AppendLine($"fpsDecayPercent={decay * 100:F2}");
            sb.AppendLine($"fpsJudged={(fpsJudged ? "yes" : "no")}");
            sb.AppendLine($"wafers={Interlocked.Read(ref _wafersSeen)}");
            sb.AppendLine($"defectPoints={_defectPointsAtEnd}");
            sb.AppendLine($"droppedWafers={_visual?.WaferQueue.DroppedTotal ?? 0}");
            sb.AppendLine($"handlerErrors={handlerErrors}");
            sb.AppendLine($"gc.gen0={GC.CollectionCount(0)}");
            sb.AppendLine($"gc.gen1={GC.CollectionCount(1)}");
            sb.AppendLine($"gc.gen2={GC.CollectionCount(2)}");
            sb.AppendLine($"samples={_samples.Count}");
            sb.AppendLine($"memoryGrowthOk={growthOk}");
            sb.AppendLine($"fpsDecayOk={(fpsJudged ? fpsOk.ToString() : "SKIPPED")}");
            sb.AppendLine($"handlerOk={handlerOk}");
            if (_failureReason is not null)
            {
                sb.AppendLine($"failureReason={_failureReason}");
            }

            sb.AppendLine($"result={(pass ? "PASS" : "FAIL")}");

            // 全量明细在 .progress 心跳文件；报告按 ≤240 行抽稀，72h 的报告要能一眼看完
            sb.AppendLine();
            sb.AppendLine("---- 采样明细（抽稀） ----");
            var step = Math.Max(1, _samples.Count / 240);
            for (var i = 0; i < _samples.Count; i += step)
            {
                sb.AppendLine(FormatSample(_samples[i]));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 采样曲线导出（CSV，Excel/pandas 直接画内存与帧率两条线）：
        /// 报告是给评审签字的，曲线是给定位问题的人看的——两者都要，但不要塞进同一个文件。
        /// </summary>
        private void WriteSamplesCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("elapsedSec,elapsedHms,workingSetMb,privateMb,managedHeapMb,fps,p95FrameMs,wafers,defectPoints,handlerErrors,droppedWafers,gen2");
            foreach (var s in _samples)
            {
                sb.Append((int)s.At.TotalSeconds).Append(',')
                  .Append(s.At.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.WorkingSetMb).Append(',')
                  .Append(s.PrivateMb).Append(',')
                  .Append(s.ManagedHeapMb).Append(',')
                  .Append(s.Fps.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.P95Ms.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.Wafers).Append(',')
                  .Append(s.DefectPoints).Append(',')
                  .Append(s.HandlerErrors).Append(',')
                  .Append(s.DroppedWafers).Append(',')
                  .Append(s.Gen2)
                  .AppendLine();
            }

            try
            {
                File.WriteAllText(_reportPath + ".csv", sb.ToString(), new UTF8Encoding(false));
            }
            catch (IOException)
            {
                // 曲线文件写不出来不影响报告本身
            }
        }

        /// <summary>装载状态喂料：每片结果换一次「有片/无片」，Cassette 计数按 25 槽循环。</summary>
        private void OnResultForStatus(WaferResult result)
        {
            var index = Interlocked.Increment(ref _wafersSeen);
            var now = DateTimeOffset.Now;
            _hub!.Publish(TwinTopic.Status, new StatusEvent(
                HandlingStateViewModel.TrackPartId,
                "WAFER.TRACK",
                Severity.Info,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["WaferPresent"] = index % 2 == 0 ? "1" : "0",
                },
                now));

            _hub.Publish(TwinTopic.Status, new StatusEvent(
                HandlingStateViewModel.LoadPortPartId,
                "CASSETTE.COUNT",
                Severity.Info,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["CassetteCount"] = (index % 25 + 1).ToString(CultureInfo.InvariantCulture),
                    ["CassetteCapacity"] = HandlingStateViewModel.DefaultCassetteCapacity.ToString(CultureInfo.InvariantCulture),
                },
                now));
        }

        private void FillHeader(string assetsRoot, string catalog)
        {
            _header["assetsRoot"] = assetsRoot;
            _header["catalog"] = catalog;
            _header["version"] = typeof(VisualSoak).Assembly.GetName().Version?.ToString() ?? "unknown";
        }

        private void AppendProgress(string line)
        {
            try
            {
                File.AppendAllText(_reportPath + ".progress", line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (IOException)
            {
                // 心跳文件写不进去不能反过来打断长跑本身
            }
        }

        /// <summary>启动看门狗回调：60s 内没进入采样就判定环境不可用并落报告。</summary>
        public void GuardStartup()
        {
            if (_started || _finished)
            {
                return;
            }

            Fail("启动超时：60s 内未完成装配（无桌面会话 / 资产加载阻塞 / GLB 过大）", null);
        }

        public void Fail(string reason, Exception? ex)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _failureReason = ex is null ? reason : $"{reason}: {ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine($"[soak-visual] {_failureReason}");

            try
            {
                _sampleTimer?.Stop();
                _watchdog?.Stop();
                _viewport.FrameRendered -= OnFrame;
                _clock.Stop();
                var sb = new StringBuilder();
                sb.AppendLine("mode=soak-visual");
                sb.AppendLine($"started={_startedAt:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"actualDuration={_clock.Elapsed:hh\\:mm\\:ss}");
                foreach (var pair in _header)
                {
                    sb.AppendLine($"{pair.Key}={pair.Value}");
                }

                sb.AppendLine($"failureReason={_failureReason}");
                sb.AppendLine("result=FAIL");
                File.WriteAllText(_reportPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (IOException)
            {
                // 报告都写不进去只能靠控制台输出（上面的 stderr 已有）
            }

            ExitCode = 3;
            Application.Current?.Shutdown();
        }

        /// <summary>节流策略文件源（缺失/损坏时返回 null，VM 回落默认 200ms 按键合并）。</summary>
        private static Func<ThrottlePolicy?>? TryPolicySource(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return new ThrottlePolicyFileSource(path).PolicyOf("Motion");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>资产根定位：与 App 侧 AssetPackageLocator 同一套路（向上找 assets/part-catalog.json）。</summary>
        private static string ResolveAssetsRoot(string? explicitRoot)
        {
            if (!string.IsNullOrWhiteSpace(explicitRoot))
            {
                return Path.GetFullPath(explicitRoot);
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "assets");
                if (File.Exists(Path.Combine(candidate, "part-catalog.json")))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            return Path.Combine(AppContext.BaseDirectory, "assets");
        }

        /// <summary>Mock 场景：轴表对齐台账真 PartId（否则驱动器一个部件都对不上，长跑等于空转）。</summary>
        private static MockScenario BuildScenario(VisualSoakOptions options) => new()
        {
            ScenarioName = "soak-visual",
            Axes =
            [
                new MockAxisSpec("GATE_AXIS_X", "X", AmplitudeUm: 20_000, PeriodSec: 40),
                new MockAxisSpec("GATE_AXIS_Y", "Y", AmplitudeUm: 12_000, PeriodSec: 55),
                new MockAxisSpec("GATE_AXIS_Z", "Z", AmplitudeUm: 6_000, PeriodSec: 20),
                new MockAxisSpec("ROBOT", "R", AmplitudeUm: 100_000, PeriodSec: 45),
                new MockAxisSpec("ROBOT", "THETA", AmplitudeUm: 30_000, PeriodSec: 60),
                new MockAxisSpec("ROBOT", "Z", AmplitudeUm: 8_000, PeriodSec: 25),
                new MockAxisSpec("STAGE", "X", AmplitudeUm: 10_000, PeriodSec: 30),
                new MockAxisSpec("STAGE", "Y", AmplitudeUm: 10_000, PeriodSec: 35),
                new MockAxisSpec("STAGE", "Z", AmplitudeUm: 2_000, PeriodSec: 18),
                new MockAxisSpec("STAGE", "THETA", AmplitudeUm: 20_000, PeriodSec: 50),
                new MockAxisSpec("PRE_ALIGNER", "THETA", AmplitudeUm: 15_000, PeriodSec: 40),
            ],
            WaferIntervalSec = options.WaferIntervalSec,
            DefectsPerWafer = options.DefectsPerWafer,
            WaferMapSize = 32,
        };
    }

    /// <summary>
    /// 合成件集降级（GLB 缺失/损坏时）：只保证六路驱动器有可绑定的部件——
    /// 长跑要的是「渲染循环与驱动器在 72h 内的行为」，不是资产保真度；
    /// 但报告必须标 <c>scene=synthetic</c>，否则等于拿假场景冒充真机结论。
    /// </summary>
    private static class SyntheticAssembly
    {
        public static SceneAssembly Build(ModelVisual3D sceneRoot)
        {
            var visuals = new List<PartVisual>
            {
                MakePart("STAGE", new Point3D(0, 0, 0.30), new Size3D(0.30, 0.30, 0.02)),
                MakePart(WaferMapDriver.WaferPartId, new Point3D(0, 0, 0.585), new Size3D(0.30, 0.30, 0.004)),
                MakePart("LIGHT_RING", new Point3D(0, 0, 0.72), new Size3D(0.16, 0.16, 0.03)),
                MakePart("LIGHT_COAX", new Point3D(0, 0, 0.78), new Size3D(0.06, 0.06, 0.03)),
                MakePart("TRACK", new Point3D(0.40, -0.62, 0.10), new Size3D(0.50, 0.20, 0.06)),
            };

            foreach (var part in visuals)
            {
                sceneRoot.Children.Add(part.Visual);
            }

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
}
