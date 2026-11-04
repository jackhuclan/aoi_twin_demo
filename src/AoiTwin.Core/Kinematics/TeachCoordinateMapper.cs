using System.Collections.Immutable;
using System.Text.Json;

namespace AoiTwin.Core.Kinematics;

/// <summary>
/// 单轴示教标定（TA.3，FR-M2-03 / NFR-18）：把设备坐标换算为模型坐标。
/// 模型位移 = 方向轴 × (Sign × Position × Scale + Offset)。
/// </summary>
/// <param name="PartId">台账部件号。</param>
/// <param name="AxisName">设备轴名（X/Y/Z/R/THETA…）。</param>
/// <param name="Direction">模型空间方向 X/Y/Z（本朝向为模型坐标轴名，非设备轴名）。</param>
/// <param name="Scale">设备单位 → 米系数（µm 传感器 1e-6；mm 传感器 1e-3）。</param>
/// <param name="Sign">方向翻转（+1/-1：设备轴正向与模型轴正向相反时取 -1）。</param>
/// <param name="Offset">模型空间偏置（米）：设备零位在模型坐标中的位置。</param>
public sealed record AxisCalibration(
    string PartId,
    string AxisName,
    string Direction,
    double Scale,
    double Sign,
    double Offset)
{
    /// <summary>恒等标定：位置原样映射到默认方向轴（与 Mapper 的降级方向表语义一致）。</summary>
    public static AxisCalibration Identity(string partId, string axisName) =>
        new(partId, axisName, axisName.ToUpperInvariant() switch
        {
            "Y" => "Y",
            "Z" => "Z",
            _ => "X", // X / R / THETA 均按 X 占位
        }, Scale: 1, Sign: 1, Offset: 0);
}

/// <summary>
/// 示教坐标 → 模型坐标换算器（TA.3）。
/// 标定文件缺失或键缺失时按恒等映射降级（PRD FR-M2-03：标定缺失不得阻塞渲染）。
/// </summary>
public sealed class TeachCoordinateMapper
{
    /// <summary>设备轴名 → 模型方向默认表（无标定时的恒等方向；机械手回转 R 暂按 X 平移占位）。</summary>
    private static readonly IReadOnlyDictionary<string, string> DefaultDirections =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X"] = "X",
            ["Y"] = "Y",
            ["Z"] = "Z",
            ["R"] = "X",
            ["THETA"] = "X",
        };

    private readonly ImmutableDictionary<(string PartId, string AxisName), AxisCalibration> _calibrations;

    public TeachCoordinateMapper(IEnumerable<AxisCalibration> calibrations)
    {
        _calibrations = calibrations.ToImmutableDictionary(c => (c.PartId, c.AxisName));
    }

    /// <summary>空标定表（全部走恒等降级）。</summary>
    public static TeachCoordinateMapper Empty { get; } = new([]);

    /// <summary>
    /// 从 JSON 文件加载标定。文件缺失返回空表（恒等降级）；
    /// JSON 损坏抛异常（配置坏了应该被看见，与节流策略的"保留旧值"不同——标定没有旧值可保）。
    /// </summary>
    public static TeachCoordinateMapper Load(string path)
    {
        if (!File.Exists(path))
        {
            return Empty;
        }

        var json = File.ReadAllText(path);
        var raw = JsonSerializer.Deserialize<List<AxisCalibrationEntry>>(json)
            ?? throw new InvalidDataException("标定文件根节点必须是数组");

        return new TeachCoordinateMapper(raw.Select(e => new AxisCalibration(
            e.PartId, e.AxisName, e.Direction, e.Scale, e.Sign, e.Offset)));
    }

    /// <summary>换算设备位置 → 模型空间位移向量（米）。键缺失按恒等映射。</summary>
    public (double Dx, double Dy, double Dz) Map(string partId, string axisName, double devicePosition)
    {
        if (_calibrations.TryGetValue((partId, axisName), out var cal))
        {
            var distance = cal.Sign * devicePosition * cal.Scale + cal.Offset;
            return cal.Direction.ToUpperInvariant() switch
            {
                "X" => (distance, 0, 0),
                "Y" => (0, distance, 0),
                "Z" => (0, 0, distance),
                _ => (0, 0, 0), // 方向名非法：不动（比抛异常更适合渲染热路径）
            };
        }

        // 恒等降级：默认方向表 + 位置原样
        var direction = DefaultDirections.TryGetValue(axisName, out var d) ? d : null;
        return direction switch
        {
            "X" => (devicePosition, 0, 0),
            "Y" => (0, devicePosition, 0),
            "Z" => (0, 0, devicePosition),
            _ => (0, 0, 0),
        };
    }

    /// <summary>是否存在指定轴的显式标定。</summary>
    public bool Has(string partId, string axisName) =>
        _calibrations.ContainsKey((partId, axisName));

    private sealed record AxisCalibrationEntry(
        string PartId, string AxisName, string Direction, double Scale, double Sign, double Offset);
}
