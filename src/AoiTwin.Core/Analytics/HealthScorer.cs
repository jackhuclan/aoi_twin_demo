using AoiTwin.Core.Persistence;

namespace AoiTwin.Core.Analytics;

/// <summary>健康等级（PRD FR-M3-05 的四档）。</summary>
public enum HealthGrade
{
    Good,
    Watch,
    Warning,
    Critical,
}

/// <summary>部件健康评分结果（评分 + 构成因子，钻取面板直接展示）。</summary>
public sealed record HealthScore(
    string PartId,
    double Score,
    HealthGrade Grade,
    IReadOnlyList<AlertFactor> Factors);

/// <summary>
/// 健康度评分（TB.6，FR-M3-05）：满分 100 起步的**加权扣分制**。
///
/// 为什么扣分制：加分制需要先定义「满分长什么样」，而产线部件的「健康满分」无法标定；
/// 扣分制把每个不利证据直接映射成可解释的扣分项——操作员看到 62 分时，
/// 因子列表会明确写出「报警 2 次 −50、行程 3.2km −6.4」，而不是一个黑盒数字。
///
/// 权重通过委托读取（热加载）：现场调整权重不需要重启。
/// 评分本身不做持久化——历史结论不应被今天的权重污染（档案只存原始量，见 PartHealthProfile）。
/// </summary>
public sealed class HealthScorer
{
    private readonly Func<HealthWeights> _weightsProvider;

    public HealthScorer(Func<HealthWeights>? weightsProvider = null)
    {
        _weightsProvider = weightsProvider ?? (() => HealthWeights.Default);
    }

    /// <summary>对单个部件评分；<paramref name="predictions"/> 为同一部件已产生的预测预警（可空）。</summary>
    public HealthScore Score(PartHealthProfile profile, IEnumerable<AlertFinding>? predictions = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var weights = _weightsProvider() ?? HealthWeights.Default;
        var factors = new List<AlertFactor>();
        var score = 100.0;

        void Penalize(string name, double penalty, string note)
        {
            if (penalty <= 0)
            {
                return;
            }

            score -= penalty;
            factors.Add(new AlertFactor(name, Math.Round(penalty, 2), "分", note));
        }

        // 报警：单类封顶 4 次，避免一次故障刷屏把分数永久压到 0（分数要能随修复回升）
        if (profile.AlarmCount > 0)
        {
            var count = Math.Min(profile.AlarmCount, 4);
            Penalize("报警", weights.AlarmPenalty * count, $"{profile.AlarmCount} 次报警（计前 {count} 次）");
        }

        if (profile.WarningCount > 0)
        {
            var count = Math.Min(profile.WarningCount, 8);
            Penalize("告警", weights.WarningPenalty * count, $"{profile.WarningCount} 次告警（计前 {count} 次）");
        }

        if (profile.TravelMm > 0)
        {
            Penalize("行程磨损", weights.TravelPenaltyPerKm * profile.TravelKm, $"{profile.TravelKm:F2} km 累计行程");
        }

        if (profile.LightOnHours > 0)
        {
            Penalize("光源点亮", weights.LightPenaltyPerHour * profile.LightOnHours, $"{profile.LightOnHours:F1} h 累计点亮");
        }

        // 节拍劣化：末片相对均值超出 5% 才计（噪声区间内不计）
        if (profile.WaferCount > 0 && profile.MeanTactSec > 0 && profile.LastTactSec > profile.MeanTactSec * 1.05)
        {
            var percent = (profile.LastTactSec - profile.MeanTactSec) / profile.MeanTactSec * 100;
            Penalize("节拍劣化", weights.TactPenaltyPerPercent * percent, $"末片节拍较均值高 {percent:F1}%");
        }

        if (predictions is not null)
        {
            foreach (var prediction in predictions.Where(p => string.Equals(p.PartId, profile.PartId, StringComparison.Ordinal)))
            {
                var penalty = prediction.Severity switch
                {
                    Bus.Severity.Alarm => weights.PredictorPenaltyCritical,
                    Bus.Severity.Warning => weights.PredictorPenaltyWarning,
                    _ => 0,
                };

                Penalize($"预测:{prediction.Kind}", penalty, prediction.Summary);
            }
        }

        score = Math.Clamp(Math.Round(score, 1), 0, 100);
        return new HealthScore(profile.PartId, score, GradeOf(score), factors);
    }

    /// <summary>批量评分（按分数升序，最需要关注的排在最前）。</summary>
    public IReadOnlyList<HealthScore> ScoreAll(
        IReadOnlyList<PartHealthProfile> profiles,
        IReadOnlyList<AlertFinding>? predictions = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles
            .Where(p => p.HasObservations)
            .Select(p => Score(p, predictions))
            .OrderBy(s => s.Score)
            .ThenBy(s => s.PartId, StringComparer.Ordinal)
            .ToList();
    }

    public static HealthGrade GradeOf(double score) => score switch
    {
        >= 85 => HealthGrade.Good,
        >= 70 => HealthGrade.Watch,
        >= 50 => HealthGrade.Warning,
        _ => HealthGrade.Critical,
    };
}
