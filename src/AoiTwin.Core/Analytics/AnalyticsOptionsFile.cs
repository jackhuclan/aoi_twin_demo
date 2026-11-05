using System.Text.Json;
using System.Text.Json.Serialization;

namespace AoiTwin.Core.Analytics;

/// <summary>
/// 分析参数文件源（FR-M3-05「评分规则透明可配置」的落地方式）：
/// <c>assets/analytics-options.json</c> → <see cref="AnalyticsOptions"/>。
///
/// 语义与 M1/M2 的节流策略文件保持一致——**单字段缺省即用默认值，整份非法则保留旧值**：
/// 现场只写想改的两三项即可，其余走默认；改错一个字不会让分析层带着半个配置跑。
/// 阈值放在文件里而不是代码里的理由：调参不该触发重新编译与重新发布。
/// </summary>
public static class AnalyticsOptionsFile
{
    /// <summary>读取参数文件。<paramref name="options"/> 失败时为全默认值。</summary>
    /// <returns>成功为 true；文件缺失、JSON 非法、取值越界均为 false，并给出中文原因。</returns>
    public static bool TryLoad(string path, out AnalyticsOptions options, out string? error)
    {
        options = new AnalyticsOptions();

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "参数文件路径为空";
            return false;
        }

        Dto? dto;
        try
        {
            if (!File.Exists(path))
            {
                error = $"参数文件不存在: {path}";
                return false;
            }

            using var stream = File.OpenRead(path);
            dto = JsonSerializer.Deserialize<Dto>(stream, FileJson.Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            error = $"参数文件读取失败: {ex.Message}";
            return false;
        }

        if (dto is null)
        {
            error = "参数文件内容为空";
            return false;
        }

        var result = Map(dto);
        var problem = Validate(result);
        if (problem is not null)
        {
            error = problem;
            return false;
        }

        options = result;
        error = null;
        return true;
    }

    private static AnalyticsOptions Map(Dto dto) => new()
    {
        Window = TimeSpan.FromDays(dto.WindowDays ?? 30),
        SchedulerPeriod = TimeSpan.FromMinutes(dto.SchedulerMinutes ?? 5),
        RepeatSuppression = TimeSpan.FromMinutes(dto.RepeatSuppressionMinutes ?? 30),

        LightRatedValue = dto.Light?.RatedValue ?? 0,
        LightWarningFraction = dto.Light?.WarningFraction ?? 0.8,
        LightCriticalFraction = dto.Light?.CriticalFraction ?? 0.6,
        LightWarningHorizonDays = dto.Light?.WarningHorizonDays ?? 7,
        LightCriticalHorizonDays = dto.Light?.CriticalHorizonDays ?? 3,
        LightMinSamples = dto.Light?.MinSamples ?? 10,

        AxisWearSigmaThreshold = dto.Axis?.SigmaThreshold ?? 3.0,
        AxisDriftUmPerDay = dto.Axis?.DriftUmPerDay ?? 2.0,
        AxisHoldVelocityUmPerSec = dto.Axis?.HoldVelocityUmPerSec ?? 50,
        AxisMinSamples = dto.Axis?.MinSamples ?? 20,

        TactDegradationPercent = dto.Tact?.DegradationPercent ?? 5.0,
        TactMinSamples = dto.Tact?.MinSamples ?? 20,

        Weights = dto.HealthWeights is null
            ? HealthWeights.Default
            : new HealthWeights(
                AlarmPenalty: dto.HealthWeights.AlarmPenalty ?? 25,
                WarningPenalty: dto.HealthWeights.WarningPenalty ?? 5,
                TravelPenaltyPerKm: dto.HealthWeights.TravelPenaltyPerKm ?? 2,
                LightPenaltyPerHour: dto.HealthWeights.LightPenaltyPerHour ?? 0.02,
                TactPenaltyPerPercent: dto.HealthWeights.TactPenaltyPerPercent ?? 3,
                PredictorPenaltyWarning: dto.HealthWeights.PredictorPenaltyWarning ?? 8,
                PredictorPenaltyCritical: dto.HealthWeights.PredictorPenaltyCritical ?? 20),
    };

    /// <summary>越界即整份拒绝——半份配置比默认值更危险（例如只改了严重线忘了预警线）。</summary>
    private static string? Validate(AnalyticsOptions o)
    {
        if (o.Window <= TimeSpan.Zero)
        {
            return "windowDays 必须大于 0";
        }

        if (o.SchedulerPeriod <= TimeSpan.Zero)
        {
            return "schedulerMinutes 必须大于 0";
        }

        if (o.LightWarningFraction is <= 0 or > 1 || o.LightCriticalFraction is <= 0 or > 1)
        {
            return "light.warningFraction / criticalFraction 必须在 (0, 1] 内";
        }

        if (o.LightCriticalFraction >= o.LightWarningFraction)
        {
            return "light.criticalFraction 必须小于 warningFraction（否则严重线高于预警线）";
        }

        if (o.LightWarningHorizonDays < o.LightCriticalHorizonDays)
        {
            return "light.warningHorizonDays 不得小于 criticalHorizonDays";
        }

        if (o.LightMinSamples < 3 || o.AxisMinSamples < 3 || o.TactMinSamples < 3)
        {
            return "各模型 minSamples 不得小于 3（样本过少时回归无意义）";
        }

        if (o.AxisWearSigmaThreshold <= 0)
        {
            return "axis.sigmaThreshold 必须大于 0";
        }

        if (o.AxisDriftUmPerDay < 0)
        {
            return "axis.driftUmPerDay 不得为负";
        }

        if (o.AxisHoldVelocityUmPerSec <= 0)
        {
            return "axis.holdVelocityUmPerSec 必须大于 0";
        }

        if (o.TactDegradationPercent <= 0)
        {
            return "tact.degradationPercent 必须大于 0";
        }

        var w = o.Weights;
        if (w.AlarmPenalty < 0 || w.WarningPenalty < 0 || w.TravelPenaltyPerKm < 0 ||
            w.LightPenaltyPerHour < 0 || w.TactPenaltyPerPercent < 0 ||
            w.PredictorPenaltyWarning < 0 || w.PredictorPenaltyCritical < 0)
        {
            return "healthWeights 的扣分权重不得为负";
        }

        return null;
    }

    private static class FileJson
    {
        /// <summary>文件用 camelCase + 允许注释尾随逗号（现场手改的容错：多一个逗号不该让整份失效）。</summary>
        internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
        };
    }

    private sealed record Dto
    {
        [JsonPropertyName("windowDays")] public double? WindowDays { get; init; }
        [JsonPropertyName("schedulerMinutes")] public double? SchedulerMinutes { get; init; }
        [JsonPropertyName("repeatSuppressionMinutes")] public double? RepeatSuppressionMinutes { get; init; }
        [JsonPropertyName("light")] public LightDto? Light { get; init; }
        [JsonPropertyName("axis")] public AxisDto? Axis { get; init; }
        [JsonPropertyName("tact")] public TactDto? Tact { get; init; }
        [JsonPropertyName("healthWeights")] public WeightsDto? HealthWeights { get; init; }
    }

    private sealed record LightDto
    {
        [JsonPropertyName("ratedValue")] public double? RatedValue { get; init; }
        [JsonPropertyName("warningFraction")] public double? WarningFraction { get; init; }
        [JsonPropertyName("criticalFraction")] public double? CriticalFraction { get; init; }
        [JsonPropertyName("warningHorizonDays")] public int? WarningHorizonDays { get; init; }
        [JsonPropertyName("criticalHorizonDays")] public int? CriticalHorizonDays { get; init; }
        [JsonPropertyName("minSamples")] public int? MinSamples { get; init; }
    }

    private sealed record AxisDto
    {
        [JsonPropertyName("sigmaThreshold")] public double? SigmaThreshold { get; init; }
        [JsonPropertyName("driftUmPerDay")] public double? DriftUmPerDay { get; init; }
        [JsonPropertyName("holdVelocityUmPerSec")] public double? HoldVelocityUmPerSec { get; init; }
        [JsonPropertyName("minSamples")] public int? MinSamples { get; init; }
    }

    private sealed record TactDto
    {
        [JsonPropertyName("degradationPercent")] public double? DegradationPercent { get; init; }
        [JsonPropertyName("minSamples")] public int? MinSamples { get; init; }
    }

    private sealed record WeightsDto
    {
        [JsonPropertyName("alarmPenalty")] public double? AlarmPenalty { get; init; }
        [JsonPropertyName("warningPenalty")] public double? WarningPenalty { get; init; }
        [JsonPropertyName("travelPenaltyPerKm")] public double? TravelPenaltyPerKm { get; init; }
        [JsonPropertyName("lightPenaltyPerHour")] public double? LightPenaltyPerHour { get; init; }
        [JsonPropertyName("tactPenaltyPerPercent")] public double? TactPenaltyPerPercent { get; init; }
        [JsonPropertyName("predictorPenaltyWarning")] public double? PredictorPenaltyWarning { get; init; }
        [JsonPropertyName("predictorPenaltyCritical")] public double? PredictorPenaltyCritical { get; init; }
    }
}

/// <summary>
/// 热加载持有者（TB.6/TB.7 现场调参）：<see cref="Reload"/> 失败时**保留上一份可用配置**。
/// 用法：调度器的 options 工厂每次取 <see cref="Current"/>，重载失败只会让日志多一行，不会让分析停摆。
/// </summary>
public sealed class AnalyticsOptionsProvider
{
    private readonly string? _path;

    public AnalyticsOptionsProvider(string? path = null)
    {
        _path = path;
        Current = new AnalyticsOptions();

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (AnalyticsOptionsFile.TryLoad(path, out var loaded, out var error))
        {
            Current = loaded;
            LastError = null;
        }
        else
        {
            LastError = error;
        }
    }

    /// <summary>当前生效参数（永不为 null）。</summary>
    public AnalyticsOptions Current { get; private set; }

    /// <summary>最近一次重载失败的原因；成功后置 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>参数文件路径（未配置时为 null，表示使用代码默认值）。</summary>
    public string? Source => _path;

    /// <summary>重新读取；成功返回 true。未配置路径时返回 false 且不改动当前值。</summary>
    public bool Reload()
    {
        if (string.IsNullOrWhiteSpace(_path))
        {
            return false;
        }

        if (!AnalyticsOptionsFile.TryLoad(_path, out var loaded, out var error))
        {
            LastError = error;
            return false;
        }

        Current = loaded;
        LastError = null;
        return true;
    }
}
