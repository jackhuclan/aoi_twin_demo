using System.Globalization;
using System.Text;
using AoiTwin.Core.Persistence;

namespace AoiTwin.Core.Analytics.Export;

/// <summary>导出格式（TB.16）。</summary>
public enum ExportFormat
{
    Csv,
    Json,
}

/// <summary>导出范围（决定 ACL 要求）。</summary>
public enum ExportScope
{
    /// <summary>健康档案/评分汇总（运维与班组长可见）。</summary>
    HealthSummary,

    /// <summary>预警历史（工程师及以上）。</summary>
    AlertHistory,

    /// <summary>原始时序片段（管理员，含设备原始量）。</summary>
    RawTimeSeries,
}

/// <summary>调用者角色（真机由上位机登录态映射；离线验收用枚举直传）。</summary>
public enum ExportRole
{
    Viewer = 0,
    Engineer = 1,
    Admin = 2,
}

/// <summary>
/// 导出 ACL（TB.16，NFR-24）：范围 × 角色的最小权限矩阵。
/// 导出接口一旦开放给外部系统，最容易出的问题是「顺手把原始时序全量导出去」——
/// 因此原始时序只对 Admin 开放，且必须显式脱敏。
/// </summary>
public static class ExportGuard
{
    public static bool IsAllowed(ExportRole role, ExportScope scope) => scope switch
    {
        ExportScope.HealthSummary => true,
        ExportScope.AlertHistory => role >= ExportRole.Engineer,
        ExportScope.RawTimeSeries => role == ExportRole.Admin,
        _ => false,
    };

    public static void Authorize(ExportRole role, ExportScope scope)
    {
        if (!IsAllowed(role, scope))
        {
            throw new UnauthorizedAccessException($"角色 {role} 无权导出 {scope}（NFR-24 ACL）");
        }
    }
}

/// <summary>导出选项（脱敏默认开启：导出物一旦离开上位机，就无法回收）。</summary>
public sealed record ExportOptions(
    bool MaskDeviceId = true,
    bool MaskWaferId = true,
    string? DeviceId = null);

/// <summary>导出结果。</summary>
public sealed record ExportResult(string FilePath, int Rows, long Bytes);

/// <summary>
/// 导出服务（TB.16）：健康档案 / 预警历史 / 时序片段的 CSV 与 JSON 输出，含脱敏与 ACL。
///
/// 设计取舍：
/// - **CSV 与 JSON 都从同一份内存行生成**，避免两套字段口径（现场最常见的问题是
///   「报表里叫 tact，接口里叫 tact_sec」，下游对不上）；
/// - **脱敏只作用于标识类字段**（设备号/晶圆号），数值一律不改——分析结论要能复核；
/// - 写入采用 UTF-8（无 BOM）+ 原子替换（先写 .tmp 再 Move），断电不会留下半个文件。
/// </summary>
public sealed class AnalyticsExportService
{
    private readonly ExportOptions _options;

    public AnalyticsExportService(ExportOptions? options = null)
    {
        _options = options ?? new ExportOptions();
    }

    /// <summary>健康评分导出。</summary>
    public ExportResult ExportHealth(
        IReadOnlyList<HealthScore> scores,
        string filePath,
        ExportFormat format,
        ExportRole role = ExportRole.Engineer,
        ExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ExportGuard.Authorize(role, ExportScope.HealthSummary);

        var rows = scores.Select(s => new[]
        {
            s.PartId,
            s.Score.ToString("F1", CultureInfo.InvariantCulture),
            s.Grade.ToString(),
            string.Join("; ", s.Factors.Select(f => $"{f.Name}={f.Value:F2}{f.Unit}")),
        }).ToList();

        return Write(filePath, format, ["partId", "score", "grade", "factors"], rows, options);
    }

    /// <summary>预警历史导出。</summary>
    public ExportResult ExportAlerts(
        IReadOnlyList<AlertEvent> alerts,
        string filePath,
        ExportFormat format,
        ExportRole role = ExportRole.Engineer,
        ExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ExportGuard.Authorize(role, ExportScope.AlertHistory);

        var rows = alerts.Select(a => new[]
        {
            a.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            a.PartId,
            a.Kind.ToString(),
            a.Severity.ToString(),
            a.Score.ToString("F1", CultureInfo.InvariantCulture),
            a.Summary,
            a.HorizonDays?.ToString(CultureInfo.InvariantCulture) ?? "",
            a.Confidence?.ToString("F3", CultureInfo.InvariantCulture) ?? "",
            string.Join("; ", a.Factors.Select(f => $"{f.Name}={f.Value:F2}{f.Unit}")),
        }).ToList();

        return Write(
            filePath,
            format,
            ["timestamp", "partId", "kind", "severity", "score", "summary", "horizonDays", "confidence", "factors"],
            rows,
            options);
    }

    /// <summary>原始时序片段导出（Admin 限定）。</summary>
    public ExportResult ExportTimeSeries(
        IEnumerable<TimeSeriesRecord> records,
        string filePath,
        ExportFormat format,
        ExportRole role = ExportRole.Admin,
        ExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ExportGuard.Authorize(role, ExportScope.RawTimeSeries);

        var rows = new List<string[]>();
        foreach (var record in records)
        {
            rows.Add(
            [
                record.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                record.Topic.ToString(),
                record.PartId,
                record.PayloadJson,
            ]);
        }

        return Write(filePath, format, ["timestamp", "topic", "partId", "payload"], rows, options);
    }

    /// <summary>标识脱敏：保留尾部若干字符，其余替换为 *（设备号/晶圆号通用）。</summary>
    public static string Mask(string value, int keepTail = 4)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var tail = Math.Clamp(keepTail, 0, value.Length);
        return new string('*', value.Length - tail) + value[^tail..];
    }

    private ExportResult Write(
        string filePath,
        ExportFormat format,
        IReadOnlyList<string> headers,
        IReadOnlyList<string[]> rows,
        ExportOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var effective = options ?? _options;
        var masked = rows.Select(r => r.Select(cell => MaskCell(cell, effective)).ToArray()).ToList();

        var content = format == ExportFormat.Csv
            ? BuildCsv(headers, masked)
            : BuildJson(headers, masked);

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = filePath + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, filePath, overwrite: true);

        return new ExportResult(filePath, masked.Count, new FileInfo(filePath).Length);
    }

    private static string MaskCell(string cell, ExportOptions options)
    {
        if (string.IsNullOrEmpty(cell))
        {
            return cell;
        }

        if (options.MaskDeviceId && !string.IsNullOrWhiteSpace(options.DeviceId)
            && string.Equals(cell, options.DeviceId, StringComparison.Ordinal))
        {
            return Mask(cell, 3);
        }

        // 晶圆号：形如 ST01-000123（含 '-' 且长度 ≥ 8）——按标识处理
        if (options.MaskWaferId && cell.Contains('-', StringComparison.Ordinal) && cell.Length >= 8 && cell.All(c => char.IsLetterOrDigit(c) || c == '-'))
        {
            return Mask(cell, 4);
        }

        return cell;
    }

    private static string BuildCsv(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', headers.Select(Escape)));
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', row.Select(Escape)));
        }

        return builder.ToString();
    }

    private static string BuildJson(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var builder = new StringBuilder();
        builder.Append('[');
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append('{');
            for (var j = 0; j < headers.Count; j++)
            {
                if (j > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"').Append(EscapeJson(headers[j])).Append("\":\"").Append(EscapeJson(rows[i][j])).Append('"');
            }

            builder.Append('}');
        }

        builder.Append(']');
        return builder.ToString();
    }

    private static string Escape(string value)
        => value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal)
            ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : value;

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
