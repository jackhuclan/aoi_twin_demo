using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AoiTwin.Adapters;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Recording;

namespace AoiTwin.Adapters.Mock;

/// <summary>
/// Mock 合成场景定义（assets/mock-dataset/mock-scenario.json）。
/// 决策说明：M1 不预生成整班次 .aotrec 文件（8h × 5Hz ≈ 14 万帧，产物大且无离线消费方），
/// 以确定性种子场景 + <see cref="MockDatasetRunner"/> 按需生成采集数据；
/// T3.4 现场采集前的空跑预演用本工具输出完整班次文件。
/// </summary>
public sealed record MockScenario
{
    [JsonPropertyName("scenarioName")]
    public string ScenarioName { get; init; } = "AOI-01-standard-shift";

    [JsonPropertyName("seed")]
    public int Seed { get; init; } = 20261103;

    /// <summary>运动部件与轴（µm 轨迹参数）。</summary>
    [JsonPropertyName("axes")]
    public IReadOnlyList<MockAxisSpec> Axes { get; init; } =
    [
        new MockAxisSpec("ROBOT", "R", AmplitudeUm: 150_000, PeriodSec: 45),
        new MockAxisSpec("ROBOT", "Theta", AmplitudeUm: 30_000, PeriodSec: 60),
        new MockAxisSpec("STAGE", "Z", AmplitudeUm: 8_000, PeriodSec: 20),
    ];

    /// <summary>光源通道数（LightRing 8CH）。</summary>
    [JsonPropertyName("lightChannels")]
    public int LightChannels { get; init; } = 8;

    /// <summary>光衰减通道（模拟光源老化：该通道亮度随时间线性下降，M3 光衰预测素材）。</summary>
    [JsonPropertyName("decayChannel")]
    public int DecayChannel { get; init; } = 3;

    /// <summary>晶圆结果间隔（秒）。</summary>
    [JsonPropertyName("waferIntervalSec")]
    public double WaferIntervalSec { get; init; } = 0.5;

    /// <summary>晶圆 Map 尺寸（die）。</summary>
    [JsonPropertyName("waferMapSize")]
    public int WaferMapSize { get; init; } = 64;

    /// <summary>每片期望缺陷数均值。</summary>
    [JsonPropertyName("defectsPerWafer")]
    public double DefectsPerWafer { get; init; } = 4.0;

    /// <summary>标定像素尺寸（µm/px）。</summary>
    [JsonPropertyName("pixelSizeUm")]
    public double PixelSizeUm { get; init; } = 0.5;

    public static MockScenario Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MockScenario>(json)
            ?? throw new InvalidDataException("场景 JSON 反序列化失败");
    }
}

/// <summary>Mock 轴轨迹参数：位置 = 振幅 × sin(2π t / 周期 + 相位)。</summary>
public sealed record MockAxisSpec(
    [property: JsonPropertyName("partId")] string PartId,
    [property: JsonPropertyName("axisName")] string AxisName,
    [property: JsonPropertyName("amplitudeUm")] double AmplitudeUm,
    [property: JsonPropertyName("periodSec")] double PeriodSec);

/// <summary>PLC Mock：确定性相位轨迹，原始寄存器 = µm（i32 高低字）。</summary>
public sealed class MockPlcTransport(MockScenario scenario, DateTimeOffset? start = null) : IPlcTransport
{
    private readonly MockScenario _scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
    private readonly DateTimeOffset _start = start ?? DateTimeOffset.UtcNow;

    public IReadOnlyList<PlcAxisMapping> Mappings { get; } = scenario.Axes
        .Select((a, i) => new PlcAxisMapping(a.PartId, a.AxisName, i * 4))
        .ToList();

    public void Connect(CancellationToken cancellationToken) { }

    public short[] ReadBlock(int startAddress, int length, CancellationToken cancellationToken)
    {
        if (length != 4)
        {
            throw new ArgumentException("Mock PLC 轴块固定为 4 字", nameof(length));
        }

        var spec = _scenario.Axes[startAddress / 4];
        var elapsed = (DateTimeOffset.UtcNow - _start).TotalSeconds;
        var positionUm = spec.AmplitudeUm * Math.Sin(2 * Math.PI * elapsed / spec.PeriodSec);
        var velocityUm = spec.AmplitudeUm * 2 * Math.PI / spec.PeriodSec * Math.Cos(2 * Math.PI * elapsed / spec.PeriodSec);
        var position = (int)Math.Round(positionUm);
        var velocity = (int)Math.Round(velocityUm);
        return [(short)(position >> 16), (short)position, (short)(velocity >> 16), (short)velocity];
    }

    public void Dispose() { }
}

/// <summary>相机 SDK Mock：按 ~10 事件/秒 产出参数/进度事件（限频，避免灌爆链路）。</summary>
public sealed class MockCameraTransport(MockScenario scenario) : ICameraSdkTransport
{
    private readonly Random _random = new(scenario.Seed);
    private readonly int _intervalMs = 100;
    private long _seq;
    private long _lastTicks;

    public void Connect(CancellationToken cancellationToken) { }

    public bool TryDequeueEvent(out RawCameraEvent raw)
    {
        var now = DateTimeOffset.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastTicks);
        if (now - last < _intervalMs * TimeSpan.TicksPerMillisecond)
        {
            raw = null!;
            return false;
        }

        Interlocked.Exchange(ref _lastTicks, now);
        var seq = Interlocked.Increment(ref _seq);
        raw = new RawCameraEvent(
            seq % 2 == 0 ? RawCameraEventKind.ScanProgress : RawCameraEventKind.CameraParam,
            Channel: _random.Next(0, 4),
            Value: Math.Round(_random.NextDouble() * 100, 1),
            Timestamp: DateTimeOffset.UtcNow);
        return true;
    }

    public void Dispose() { }
}

/// <summary>光源 Mock：N 通道，衰减通道亮度随时间线性下降（供 M3 光衰预测回放素材）。</summary>
public sealed class MockLightTransport(MockScenario scenario) : ILightTransport
{
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
    private readonly Random _random = new(scenario.Seed + 1);

    public int ChannelCount => scenario.LightChannels;

    public void Connect(CancellationToken cancellationToken) { }

    public int ReadChannelBrightness(int channel, CancellationToken cancellationToken)
    {
        var elapsedHours = (DateTimeOffset.UtcNow - _start).TotalHours;
        var value = channel == scenario.DecayChannel
            ? 100 - elapsedHours * 2 // 每小时衰减 2%（M3 素材）
            : 70 + _random.Next(-2, 3);
        return (int)Math.Clamp(value, 0, 100);
    }

    public void Dispose() { }
}

/// <summary>结果队列 Mock：按晶圆间隔产出判定结果（环形 die 图 + 随机缺陷）。</summary>
public sealed class MockResultQueueTransport(MockScenario scenario, DateTimeOffset? start = null) : IResultQueueTransport
{
    private readonly MockScenario _scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
    private readonly ConcurrentQueue<ResultEnvelope> _queue = new();
    private readonly Random _random = new(scenario.Seed + 2);
    private readonly DateTimeOffset _start = start ?? DateTimeOffset.UtcNow;
    private long _waferSeq;

    public void Connect(CancellationToken cancellationToken) { }

    public bool TryTake(out ResultEnvelope envelope)
    {
        var elapsed = (DateTimeOffset.UtcNow - _start).TotalSeconds;
        var dueCount = (int)(elapsed / _scenario.WaferIntervalSec) + 1;
        while (_waferSeq < dueCount)
        {
            _waferSeq++;
            _queue.Enqueue(GenerateWafer($"W-{_waferSeq:D6}", _waferSeq));
        }

        return _queue.TryDequeue(out envelope!);
    }

    private ResultEnvelope GenerateWafer(string waferId, long seq)
    {
        var size = _scenario.WaferMapSize;
        var dieMap = new byte[size * size];
        var center = size / 2.0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - center;
                var dy = y - center;
                dieMap[y * size + x] = dx * dx + dy * dy <= center * center ? (byte)1 : (byte)0;
            }
        }

        var defectCount = Math.Max(0, (int)Math.Round(_scenario.DefectsPerWafer + _random.NextDouble() * 4 - 2));
        var defects = Enumerable.Range(0, defectCount)
            .Select(_ => new RawDefect(
                DefectId: seq * 1000 + _random.Next(1000),
                Px: _random.Next(size),
                Py: _random.Next(size),
                SizePx: Math.Round(_random.NextDouble() * 8 + 1, 1),
                DefectClass: _random.Next(10) < 7 ? "particle" : "scratch",
                GrayLevel: _random.Next(256)))
            .ToList();

        return new ResultEnvelope(
            waferId,
            size,
            size,
            dieMap,
            defects,
            TactMs: (long)(_scenario.WaferIntervalSec * 1000),
            CapturedAt: DateTimeOffset.UtcNow);
    }

    public void Dispose() { }
}

/// <summary>
/// Mock 采集装配（T2.5/T2.4 共用）：按场景构造四路适配器，供数据集生成器与采集监控面板复用，
/// 保证「演示面板看到的链路」与「测试验证过的链路」是同一份装配。
/// </summary>
public static class MockCollectionFactory
{
    /// <summary>四路 Mock 适配器（PLC / 相机 / 光源 / 结果队列），顺序即面板显示顺序。</summary>
    public static AdapterBase[] BuildAdapters(MockScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return
        [
            new PlcAdapter(new MockPlcTransport(scenario), BuildPlcMappings(scenario)),
            new CameraSdkAdapter(new MockCameraTransport(scenario)),
            new LightSourceAdapter(new MockLightTransport(scenario)),
            new ResultQueueAdapter(new MockResultQueueTransport(scenario), scenario.PixelSizeUm),
        ];
    }

    /// <summary>以 Mock 数据源建立采集会话（recordingOptions 为 null 时只监控不录制）。</summary>
    public static CollectionSession CreateSession(
        IRealtimeHub hub,
        MockScenario scenario,
        RecordingOptions? recordingOptions = null)
        => new(hub, BuildAdapters(scenario), recordingOptions);

    internal static IReadOnlyList<PlcAxisMapping> BuildPlcMappings(MockScenario scenario)
        => scenario.Axes
            .Select((a, i) => new PlcAxisMapping(a.PartId, a.AxisName, i * 4))
            .ToList();
}

/// <summary>
/// 合成数据集生成器（US-M1-08 全链路验证入口）：四路 Mock → 适配器 → Hub → 录制。
/// </summary>
public static class MockDatasetRunner
{
    /// <summary>
    /// 以 Mock 数据驱动一次完整采集链路（适配器 → Hub → 录制器）并落盘 .aotrec。
    /// duration 极短时也保证至少一片晶圆结果（场景内 WaferIntervalSec 已调小）。
    /// diskUsedRatioProvider：磁盘水位探针（测试注入）；默认按输出目录所在盘实测。
    /// </summary>
    public static string Generate(string outputDirectory, MockScenario scenario, TimeSpan duration, Func<double>? diskUsedRatioProvider = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var hub = new RealtimeHub();
        using var writer = new RecordingWriter(new RecordingOptions
        {
            Directory = outputDirectory,
            DeviceId = "AOI-01",
            ShiftId = scenario.ScenarioName,
            DiskUsedRatioProvider = diskUsedRatioProvider,
        });

        var adapters = MockCollectionFactory.BuildAdapters(scenario);

        // 录制订阅：Hub 四类 Topic → 录制器逐帧落盘
        hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, m => writer.Write(TwinTopic.Motion, m));
        hub.Subscribe<OpticsEvent>(TwinTopic.Optics, m => writer.Write(TwinTopic.Optics, m));
        hub.Subscribe<WaferResult>(TwinTopic.Result, m => writer.Write(TwinTopic.Result, m));
        hub.Subscribe<StatusEvent>(TwinTopic.Status, m => writer.Write(TwinTopic.Status, m));

        foreach (var adapter in adapters)
        {
            adapter.MessageEmitted += (topic, message) => Publish(hub, topic, message);
            adapter.Start();
        }

        try
        {
            Thread.Sleep(duration);
        }
        finally
        {
            foreach (var adapter in adapters)
            {
                adapter.Stop();
            }
        }

        return writer.CurrentFilePath
            ?? throw new InvalidOperationException("录制未产生文件");
    }

    /// <summary>按消息运行时类型显式分发（四类消息封闭集合，新增类型须同步登记）。</summary>
    private static void Publish(RealtimeHub hub, TwinTopic topic, ITwinMessage message)
    {
        switch (message)
        {
            case AxisSnapshot m:
                hub.Publish(topic, m);
                break;
            case StatusEvent m:
                hub.Publish(topic, m);
                break;
            case OpticsEvent m:
                hub.Publish(topic, m);
                break;
            case WaferResult m:
                hub.Publish(topic, m);
                break;
            default:
                throw new NotSupportedException($"未登记的消息类型: {message.GetType().Name}");
        }
    }
}
