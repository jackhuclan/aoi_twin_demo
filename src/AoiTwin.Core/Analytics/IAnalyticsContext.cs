using AoiTwin.Core.Persistence;

namespace AoiTwin.Core.Analytics;

/// <summary>光源亮度样本（TB.7 回测输入）。</summary>
public readonly record struct LightSample(DateTimeOffset Timestamp, string PartId, int Channel, double Value);

/// <summary>轴位置样本（TB.8 回测输入；Position 单位 µm）。</summary>
public readonly record struct AxisSample(DateTimeOffset Timestamp, string PartId, string AxisName, double Position, double Velocity);

/// <summary>节拍样本（TB.9 回测输入）。</summary>
public readonly record struct TactSample(DateTimeOffset Timestamp, string Station, double Seconds);

/// <summary>
/// 分析上下文（TB.5）：预测器**只能**通过本接口读数据，不直接持有 Hub 或存储。
/// 这样插件可以离线回测、可以在真机上只读实时档案，也可以被单元测试用纯内存数据驱动——
/// 三种场景同一份模型代码，回测结论才对真机有效。
/// </summary>
public interface IAnalyticsContext
{
    DateTimeOffset Now { get; }

    /// <summary>全部部件健康档案（可能为空：无观测的部件不在其中）。</summary>
    IReadOnlyList<PartHealthProfile> Profiles { get; }

    AnalyticsOptions Options { get; }

    /// <summary>时间窗内的光源样本（按时间升序）。无存储时返回空。</summary>
    IReadOnlyList<LightSample> LightSamples(TimeSpan window);

    /// <summary>时间窗内的轴样本（按时间升序）。</summary>
    IReadOnlyList<AxisSample> AxisSamples(TimeSpan window);

    /// <summary>时间窗内的节拍样本（按时间升序）。</summary>
    IReadOnlyList<TactSample> TactSamples(TimeSpan window);
}

/// <summary>
/// 预警预测器插件契约（TB.5 / TB.17）：新增一个模型 = 实现本接口 + 注册，
/// <b>Hub 与渲染层零改动</b>（PRD US-M3-11 的能力证明即由此机制支撑）。
/// </summary>
public interface IAlertPredictor
{
    /// <summary>模型名（稳定标识，出现在预警因子与日志中）。</summary>
    string Name { get; }

    /// <summary>模型版本（回测报告需要能对应到具体参数版本）。</summary>
    string Version => "1.0";

    /// <summary>执行一次分析。<b>不得抛异常</b>——注册表仍会兜底隔离，但抛异常意味着模型自身缺陷。</summary>
    IReadOnlyList<AlertFinding> Analyze(IAnalyticsContext context);
}

/// <summary>单个预测器的执行结果（含失败信息，供健康页与日志展示）。</summary>
public sealed record PredictorRun(string PredictorName, IReadOnlyList<AlertFinding> Findings, string? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// 预测器注册表（TB.5）：注册、隔离执行、异常聚合。
///
/// 隔离是硬要求（NFR-22）：一个第三方插件崩溃或死循环之外的异常，绝不能中断其他模型，
/// 更不能冒到实时链路。这里逐个 try/catch，失败只记录在 <see cref="PredictorRun.Error"/>。
/// </summary>
public sealed class PredictorRegistry
{
    private readonly List<IAlertPredictor> _predictors = [];
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _predictors.Count; }
    }

    public IReadOnlyList<string> Names
    {
        get { lock (_gate) return _predictors.Select(p => p.Name).ToList(); }
    }

    public void Register(IAlertPredictor predictor)
    {
        ArgumentNullException.ThrowIfNull(predictor);
        lock (_gate)
        {
            if (_predictors.Any(p => string.Equals(p.Name, predictor.Name, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"预测器重名: {predictor.Name}", nameof(predictor));
            }

            _predictors.Add(predictor);
        }
    }

    public void RegisterRange(IEnumerable<IAlertPredictor> predictors)
    {
        ArgumentNullException.ThrowIfNull(predictors);
        foreach (var predictor in predictors)
        {
            Register(predictor);
        }
    }

    /// <summary>执行全部预测器；失败的模型不影响其他模型。</summary>
    public IReadOnlyList<PredictorRun> RunAll(IAnalyticsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IAlertPredictor[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _predictors];
        }

        var runs = new List<PredictorRun>(snapshot.Length);
        foreach (var predictor in snapshot)
        {
            try
            {
                var findings = predictor.Analyze(context) ?? [];
                runs.Add(new PredictorRun(predictor.Name, findings, null));
            }
            catch (Exception ex)
            {
                runs.Add(new PredictorRun(predictor.Name, [], $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        return runs;
    }
}
