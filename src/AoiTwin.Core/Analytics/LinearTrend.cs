namespace AoiTwin.Core.Analytics;

/// <summary>
/// 一元线性回归（TB.7/TB.8/TB.9 共用）：y = Intercept + Slope·x，x 以「天」为单位。
///
/// 三个模型共用同一份回归实现，是为了让「斜率」「R²」「残差」这些出现在预警证据里的量
/// 口径完全一致——否则光衰说 R²=0.93、节拍说 R²=0.88，却可能一个是决定系数、
/// 一个是被改过的相关系数平方，现场无法比较。
/// </summary>
public readonly record struct LinearTrend(
    double Intercept,
    double Slope,
    double RSquared,
    double ResidualStdError,
    int Count)
{
    /// <summary>拟合；样本不足或 x 无变化时返回 <see cref="Empty"/>。</summary>
    public static LinearTrend Fit(IReadOnlyList<(double X, double Y)> points)
    {
        if (points is null || points.Count < 2)
        {
            return Empty;
        }

        var n = points.Count;
        double sumX = 0, sumY = 0;
        foreach (var (x, y) in points)
        {
            sumX += x;
            sumY += y;
        }

        var meanX = sumX / n;
        var meanY = sumY / n;

        double sxx = 0, sxy = 0, syy = 0;
        foreach (var (x, y) in points)
        {
            var dx = x - meanX;
            var dy = y - meanY;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }

        if (sxx <= double.Epsilon)
        {
            return Empty;
        }

        var slope = sxy / sxx;
        var intercept = meanY - (slope * meanX);
        var r2 = syy <= double.Epsilon ? 1.0 : (sxy * sxy) / (sxx * syy);

        double ssRes = 0;
        foreach (var (x, y) in points)
        {
            var residual = y - (intercept + (slope * x));
            ssRes += residual * residual;
        }

        var stdError = n > 2 ? Math.Sqrt(ssRes / (n - 2)) : 0;
        return new LinearTrend(intercept, slope, Math.Clamp(r2, 0, 1), stdError, n);
    }

    public static LinearTrend Empty { get; } = new(0, 0, 0, 0, 0);

    public bool IsValid => Count >= 2;

    public double Predict(double x) => Intercept + (Slope * x);

    /// <summary>
    /// 达到目标值所需的天数（相对 x=0，即首个样本的时间基准）。
    /// 斜率方向与目标不成立时返回 <see cref="double.PositiveInfinity"/>——调用方据此「不报」而不是报一个负数。
    /// </summary>
    public double DaysToReach(double target)
    {
        if (Math.Abs(Slope) <= double.Epsilon)
        {
            return double.PositiveInfinity;
        }

        var days = (target - Intercept) / Slope;
        return days >= 0 ? days : double.PositiveInfinity;
    }

    /// <summary>预测天数的不确定区间半宽（95%，用残差标准误近似）；样本过少时返回无穷大。</summary>
    public double PredictionHalfWidthDays(double target)
    {
        if (!IsValid || Count < 4 || Math.Abs(Slope) <= double.Epsilon || ResidualStdError <= 0)
        {
            return double.PositiveInfinity;
        }

        return 1.96 * ResidualStdError / Math.Abs(Slope);
    }
}
