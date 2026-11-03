using System.Text.Json;
using System.Text.Json.Serialization;

namespace AoiTwin.Core.Serialization;

/// <summary>
/// 总线消息 JSON 序列化契约（录制文件与回灌共用，属 T1.0 冻结范围）。
/// 变更此处序列化行为 = 变更 .aotrec 文件格式，必须走契约变更评审。
/// </summary>
public static class TwinJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };
}
