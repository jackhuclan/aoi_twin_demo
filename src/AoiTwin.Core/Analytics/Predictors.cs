using AoiTwin.Core.Bus;

namespace AoiTwin.Core.Analytics;

/// <summary>
/// 光源衰减预测（TB.7）：对「亮度—时间」做一元线性回归，外推到预警线/严重线，
/// 提前量 ≥ <see cref="AnalyticsOptions.LightWarningHorizonDays"/> 天即预警。
///
/// 三个刻意的保守设计：
/// 1) 衰减速率 ≥ 0 直接不发预测——产线最怕「亮度正常却报衰减」；
/// 2) 样本不足 <see cref="AnalyticsOptions.LightMinSamples"/> 不发——短窗口的噪声斜率会被外推成荒谬结论；
/// 3) 证据里必带 R² 与残差区间——置信度低的预测可以被人为忽略，但必须可见。
///
/// 真机验证点（见 docs/m3-field-verification.md）：光源 Value 的物理口径（百分比 / 亮度计读数）
/// 必须与额定值统一；同部件多通道要确认「通道号」在设备侧与配色表一致，否则会把 8 个通道混成一条曲线。
/// </summary>
public sealed class LightDecayPredictor : IAlertPredictor
{
    public string Name => "light-decay";

    public string Version => "1.0";

    public IReadOnlyList<AlertFinding> Analyze(IAnalyticsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = context.Options;
        var samples = context.LightSamples(options.Window);
        if (samples.Count < options.LightMinSamples)
        {
            return [];
        }

        var findings = new List<AlertFinding>();

        // 按部件聚合：3D 预警只认 PartId，同部件多通道取最紧迫的一条（避免 8 条重复预警刷屏）
        foreach (var partGroup in samples.GroupBy(s => s.PartId, StringComparer.Ordinal))
        {
            AlertFinding? worst = null;
            foreach (var channelGroup in partGroup.GroupBy(s => s.Channel))
            {
                var finding = AnalyzeChannel(channelGroup.OrderBy(s => s.Timestamp).ToList(), options, context.Now);
                if (finding is not null && (worst is null || Rank(finding.Severity) > Rank(worst.Severity)))
                {
                    worst = finding;
                }
            }

            if (worst is not null)
            {
                findings.Add(worst);
            }
        }

        return findings;
    }

    private static AlertFinding? AnalyzeChannel(IReadOnlyList<LightSample> samples, AnalyticsOptions options, DateTimeOffset now)
    {
        if (samples.Count < options.LightMinSamples)
        {
            return null;
        }

        var rated = options.LightRatedValue > 0 ? options.LightRatedValue : samples[0].Value;
        if (rated <= 0)
        {
            return null;
        }

        var start = samples[0].Timestamp;
        var points = samples.Select(s => ((s.Timestamp - start).TotalDays, s.Value)).ToList();
        var trend = LinearTrend.Fit(points);
        if (!trend.IsValid || trend.Slope >= 0)
        {
            return null; // 未观测到衰减：不报
        }

        var warningLine = rated * options.LightWarningFraction;
        var criticalLine = rated * options.LightCriticalFraction;

        // DaysToReach 返回的是「相对首样本的天数」；对外要的是「从现在起还剩几天」，
        // 两者差一个样本窗口长度。搞混会把「已经跌破」报成「还有 20 天」，这是最危险的一类假阴性。
        var elapsedDays = Math.Max(0, (now - start).TotalDays);
        var daysToWarning = trend.DaysToReach(warningLine) - elapsedDays;
        var daysToCritical = trend.DaysToReach(criticalLine) - elapsedDays;
        if (double.IsPositiveInfinity(daysToWarning))
        {
            return null;
        }

        daysToWarning = Math.Max(0, daysToWarning);
        daysToCritical = Math.Max(0, daysToCritical);

        Severity? severity = null;
        double horizon = 0;
        double limit = 0;
        if (daysToCritical <= options.LightCriticalHorizonDays)
        {
            severity = Severity.Alarm;
            horizon = daysToCritical;
            limit = options.LightCriticalHorizonDays;
        }
        else if (daysToWarning <= options.LightWarningHorizonDays)
        {
            severity = Severity.Warning;
            horizon = daysToWarning;
            limit = options.LightWarningHorizonDays;
        }

        if (severity is null)
        {
            return null;
        }

        var score = Math.Clamp(100 * (1 - (horizon / Math.Max(limit, 0.01))), 0, 100);
        var halfWidth = trend.PredictionHalfWidthDays(warningLine);
        var factors = new List<AlertFactor>
        {
            new("当前亮度", Math.Round(samples[^1].Value, 2), "", $"通道 {samples[0].Channel}"),
            new("衰减速率", Math.Round(-trend.Slope, 3), "/天", "线性回归斜率取反"),
            new("预警线", Math.Round(warningLine, 2), "", $"额定 {rated:F1} 的 {options.LightWarningFraction:P0}"),
            new("距预警线", Math.Round(daysToWarning, 1), "天", double.IsInfinity(halfWidth) ? "区间不可估（样本不足）" : $"±{halfWidth:F1} 天（95%）"),
            new("拟合优度", Math.Round(trend.RSquared, 3), "R²", $"{trend.Count} 个样本"),
        };

        var summary = daysToWarning < 0.05
            ? $"通道 {samples[0].Channel} 已跌破预警线（当前 {samples[^1].Value:F1} < 预警线 {warningLine:F1}），R²={trend.RSquared:F2}"
            : $"通道 {samples[0].Channel} 预计 {daysToWarning:F1} 天后跌至预警线（额定 {options.LightWarningFraction:P0}），R²={trend.RSquared:F2}";
        return new AlertFinding(
            samples[0].PartId,
            AlertKind.LightDecay,
            severity.Value,
            Math.Round(score, 1),
            summary,
            factors,
            (int)Math.Round(daysToWarning),
            Math.Round(trend.RSquared, 3));
    }

    private static int Rank(Severity severity) => severity switch
    {
        Severity.Alarm => 2,
        Severity.Warning => 1,
        _ => 0,
    };
}

/// <summary>
/// 轴磨损预警（TB.8）：以「到位置保持段内的位置离散度」作为重复定位偏差的代理量，
/// 用滚动基线 + σ 带判定劣化，并给出偏差漂移速率（µm/天）。
///
/// 为什么用保持段离散度：真实重复定位偏差需要「指令位置 vs 反馈位置」双值，
/// 而 M2 的总线只承载位置/速度。保持段内（速度≈0）的位置抖动，物理上就是定位重复性的表现，
/// 可作为**离线/历史数据的代理指标**；真机必须同时接入 PLC 的指令值与反馈值做交叉验证
/// （见 docs/m3-field-verification.md 第 3 条）——这是本模型唯一的「代理量」假设。
/// </summary>
public sealed class AxisWearPredictor : IAlertPredictor
{
    public string Name => "axis-wear";

    public string Version => "1.0";

    public IReadOnlyList<AlertFinding> Analyze(IAnalyticsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = context.Options;
        var samples = context.AxisSamples(options.Window);
        if (samples.Count < options.AxisMinSamples)
        {
            return [];
        }

        var findings = new List<AlertFinding>();
        foreach (var partGroup in samples.GroupBy(s => s.PartId, StringComparer.Ordinal))
        {
            AlertFinding? worst = null;
            foreach (var axisGroup in partGroup.GroupBy(s => s.AxisName, StringComparer.Ordinal))
            {
                var finding = AnalyzeAxis(partGroup.Key, axisGroup.Key, axisGroup.OrderBy(s => s.Timestamp).ToList(), options);
                if (finding is not null && (worst is null || SeverityRank(finding.Severity) > SeverityRank(worst.Severity)))
                {
                    worst = finding;
                }
            }

            if (worst is not null)
            {
                findings.Add(worst);
            }
        }

        return findings;
    }

    private static AlertFinding? AnalyzeAxis(
        string partId,
        string axisName,
        IReadOnlyList<AxisSample> samples,
        AnalyticsOptions options)
    {
        var segments = HoldSegments(samples, options.AxisHoldVelocityUmPerSec);
        if (segments.Count < 4)
        {
            return null; // 保持段太少：不足以判断重复定位性
        }

        var start = segments[0].Timestamp;
        var points = segments.Select(s => ((s.Timestamp - start).TotalDays, s.DeviationUm)).ToList();
        var trend = LinearTrend.Fit(points);

        var half = segments.Count / 2;
        var baseline = segments.Take(half).Select(s => s.DeviationUm).ToList();
        var recent = segments.Skip(half).Select(s => s.DeviationUm).ToList();
        if (baseline.Count == 0 || recent.Count == 0)
        {
            return null;
        }

        var baselineMean = baseline.Average();
        var recentMean = recent.Average();
        var baselineSigma = StdDev(baseline, baselineMean);
        var sigma = Math.Max(baselineSigma, 0.05); // 地板：亚微米级噪声不该产生无限大的 z
        var z = (recentMean - baselineMean) / sigma;
        var drift = trend.IsValid ? trend.Slope : 0;

        Severity? severity = null;
        if (drift >= options.AxisDriftUmPerDay)
        {
            severity = Severity.Alarm;
        }
        else if (z >= options.AxisWearSigmaThreshold)
        {
            severity = Severity.Warning;
        }

        if (severity is null)
        {
            return null;
        }

        var zScore = Math.Clamp((z / options.AxisWearSigmaThreshold) * 50, 0, 100);
        var driftScore = Math.Clamp((drift / Math.Max(options.AxisDriftUmPerDay, 0.01)) * 50, 0, 100);
        var factors = new List<AlertFactor>
        {
            new("基线离散度", Math.Round(baselineMean, 3), "µm", $"{baseline.Count} 个保持段"),
            new("近期离散度", Math.Round(recentMean, 3), "µm", $"{recent.Count} 个保持段"),
            new("z 分数", Math.Round(z, 2), "σ", $"门槛 {options.AxisWearSigmaThreshold:F1}σ"),
            new("漂移速率", Math.Round(drift, 3), "µm/天", $"门槛 {options.AxisDriftUmPerDay:F1}"),
            new("拟合优度", Math.Round(trend.IsValid ? trend.RSquared : 0, 3), "R²", "离散度随时间"),
        };

        var summary = drift >= options.AxisDriftUmPerDay
            ? $"轴 {axisName} 重复定位偏差漂移 {drift:F2} µm/天，超过门槛 {options.AxisDriftUmPerDay:F1}"
            : $"轴 {axisName} 重复定位偏差较基线上升 {z:F1}σ（{recentMean:F2} vs {baselineMean:F2} µm）";

        return new AlertFinding(
            partId,
            AlertKind.AxisWear,
            severity.Value,
            Math.Round(Math.Max(zScore, driftScore), 1),
            summary,
            factors,
            null,
            Math.Round(trend.IsValid ? trend.RSquared : 0, 3));
    }

    /// <summary>
    /// 提取「到位置保持段」并计算段内位置标准差（µm）。
    /// 速度缺失（全 0）时用相邻位置差分估算——Mock/历史数据常没有速度字段，
    /// 直接用 0 会把整条正弦轨迹当成「静止」，算出一个巨大的假偏差。
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset Timestamp, double DeviationUm)> HoldSegments(
        IReadOnlyList<AxisSample> samples,
        double holdVelocityUmPerSec)
    {
        var result = new List<(DateTimeOffset, double)>();
        var segment = new List<double>();
        DateTimeOffset? segmentStart = null;
        double? previousPosition = null;
        DateTimeOffset? previousTimestamp = null;

        foreach (var sample in samples)
        {
            var velocity = Math.Abs(sample.Velocity);
            if (velocity <= 0 && previousPosition is not null && previousTimestamp is not null)
            {
                var dt = (sample.Timestamp - previousTimestamp.Value).TotalSeconds;
                if (dt > 1e-6)
                {
                    velocity = Math.Abs(sample.Position - previousPosition.Value) / dt;
                }
            }

            previousPosition = sample.Position;
            previousTimestamp = sample.Timestamp;

            if (velocity <= holdVelocityUmPerSec)
            {
                segmentStart ??= sample.Timestamp;
                segment.Add(sample.Position);
            }
            else
            {
                if (segment.Count >= 3 && segmentStart is not null)
                {
                    result.Add((segmentStart.Value, StdDev(segment, segment.Average())));
                }

                segment.Clear();
                segmentStart = null;
            }
        }

        if (segment.Count >= 3 && segmentStart is not null)
        {
            result.Add((segmentStart.Value, StdDev(segment, segment.Average())));
        }

        return result;
    }

    private static double StdDev(IReadOnlyList<double> values, double mean)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        double sum = 0;
        foreach (var value in values)
        {
            var delta = value - mean;
            sum += delta * delta;
        }

        return Math.Sqrt(sum / (values.Count - 1));
    }

    private static int SeverityRank(Severity severity) => severity switch
    {
        Severity.Alarm => 2,
        Severity.Warning => 1,
        _ => 0,
    };
}

/// <summary>
/// 节拍劣化分析（TB.9）：按站点做节拍趋势回归 + 基线段对比，输出劣化预警与瓶颈 Top3。
/// 站点从 WaferId 解析（<c>ST02-000123</c>），真机应改为台账/结果队列中的站点字段。
/// </summary>
public sealed class TactTrendAnalyzer : IAlertPredictor
{
    public string Name => "tact-trend";

    public string Version => "1.0";

    public IReadOnlyList<AlertFinding> Analyze(IAnalyticsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = context.Options;
        var samples = context.TactSamples(options.Window);
        if (samples.Count < options.TactMinSamples)
        {
            return [];
        }

        var findings = new List<AlertFinding>();
        foreach (var stationGroup in samples.GroupBy(s => s.Station, StringComparer.Ordinal))
        {
            var ordered = stationGroup.OrderBy(s => s.Timestamp).ToList();
            if (ordered.Count < options.TactMinSamples)
            {
                continue;
            }

            var start = ordered[0].Timestamp;
            var points = ordered.Select(s => ((s.Timestamp - start).TotalDays, s.Seconds)).ToList();
            var trend = LinearTrend.Fit(points);

            var head = ordered.Take(Math.Max(1, ordered.Count / 4)).Average(s => s.Seconds);
            var tail = ordered.Skip(ordered.Count - Math.Max(1, ordered.Count / 4)).Average(s => s.Seconds);
            if (head <= 0)
            {
                continue;
            }

            var degradationPercent = (tail - head) / head * 100;
            if (degradationPercent < options.TactDegradationPercent)
            {
                continue;
            }

            var severity = degradationPercent >= options.TactDegradationPercent * 2
                ? Severity.Alarm
                : Severity.Warning;

            var factors = new List<AlertFactor>
            {
                new("基线段节拍", Math.Round(head, 2), "s", "前 25% 样本均值"),
                new("末段节拍", Math.Round(tail, 2), "s", "后 25% 样本均值"),
                new("劣化幅度", Math.Round(degradationPercent, 1), "%", $"门槛 {options.TactDegradationPercent:F1}%"),
                new("趋势斜率", Math.Round(trend.IsValid ? trend.Slope : 0, 4), "s/天", $"{ordered.Count} 片"),
            };

            findings.Add(new AlertFinding(
                ordered[0].Station,
                AlertKind.TactDegradation,
                severity,
                Math.Clamp(degradationPercent * 5, 0, 100),
                $"站点 {ordered[0].Station} 节拍劣化 {degradationPercent:F1}%（{head:F2}s → {tail:F2}s）",
                factors,
                null,
                Math.Round(trend.IsValid ? trend.RSquared : 0, 3)));
        }

        return findings;
    }

    /// <summary>瓶颈工序 Top N（按窗口内平均节拍降序）。</summary>
    public static IReadOnlyList<(string Station, double MeanTactSec, int Samples)> Bottlenecks(
        IAnalyticsContext context,
        int top = 3)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);

        return context.TactSamples(context.Options.Window)
            .GroupBy(s => s.Station, StringComparer.Ordinal)
            .Select(g => (Station: g.Key, MeanTactSec: g.Average(s => s.Seconds), Samples: g.Count()))
            .OrderByDescending(x => x.MeanTactSec)
            .ThenBy(x => x.Station, StringComparer.Ordinal)
            .Take(top)
            .ToList();
    }
}
