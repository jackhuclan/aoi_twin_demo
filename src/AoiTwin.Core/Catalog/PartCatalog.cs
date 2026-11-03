using System.Text.Json;
using System.Text.Json.Serialization;

namespace AoiTwin.Core.Catalog;

/// <summary>PartId 命名规则（docs/asset-spec.md §1）。</summary>
public static class PartCatalogRules
{
    public const string PartIdPattern = "^[A-Z][A-Z0-9_]+$";

    public static bool IsValidPartId(string? partId)
        => !string.IsNullOrWhiteSpace(partId)
           && System.Text.RegularExpressions.Regex.IsMatch(partId, PartIdPattern);
}

public sealed record PartLod(string L1, string L2);

public sealed record PartCatalogEntry(
    string PartId,
    string Name,
    string Module,
    string Model,
    PartLod? Lod,
    IReadOnlyList<string>? Axes)
{
    public string PartId { get; init; } = PartCatalogRules.IsValidPartId(PartId)
        ? PartId
        : throw new ArgumentException($"partId 不符合命名规则 {PartCatalogRules.PartIdPattern}: {PartId}", nameof(PartId));

    public string Name { get; init; } =
        string.IsNullOrWhiteSpace(Name) ? throw new ArgumentException("name 不能为空", nameof(Name)) : Name;

    public string Module { get; init; } =
        string.IsNullOrWhiteSpace(Module) ? throw new ArgumentException("module 不能为空", nameof(Module)) : Module;

    public string Model { get; init; } =
        string.IsNullOrWhiteSpace(Model) ? throw new ArgumentException("model 不能为空", nameof(Model)) : Model;

    public IReadOnlyList<string> Axes { get; init; } = Axes ?? [];
}

/// <summary>台账文档顶层结构（docs/part-catalog.schema.json）。</summary>
public sealed record PartCatalogDocument(
    [property: JsonPropertyName("assetVersion")] string AssetVersion,
    [property: JsonPropertyName("machine")] string Machine,
    [property: JsonPropertyName("parts")] IReadOnlyList<PartCatalogEntry>? Parts)
{
    public IReadOnlyList<PartCatalogEntry> Parts { get; init; } =
        Parts is { Count: > 0 } ? Parts : throw new ArgumentException("parts 不能为空", nameof(Parts));
}

/// <summary>
/// 部件台账（PRD-AOI-TWIN-M1 §5.6）：PartId → 部件信息的唯一权威来源，
/// 3D 节点、钻取面板、时序档案（M3）均以 PartId 为主键。
/// </summary>
public sealed class PartCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, PartCatalogEntry> _byPartId;

    public PartCatalog(PartCatalogDocument document)
    {
        Document = document;
        var dup = document.Parts.GroupBy(p => p.PartId).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (dup is not null)
        {
            throw new ArgumentException($"partId 重复: {dup}", nameof(document));
        }

        _byPartId = document.Parts.ToDictionary(p => p.PartId, StringComparer.Ordinal);
    }

    public PartCatalogDocument Document { get; }

    public IReadOnlyList<PartCatalogEntry> Parts => Document.Parts;

    public PartCatalogEntry? Find(string partId)
        => _byPartId.GetValueOrDefault(partId);

    public static PartCatalog Load(string jsonPath) => FromJson(File.ReadAllText(jsonPath));

    public static PartCatalog FromJson(string json)
    {
        var doc = JsonSerializer.Deserialize<PartCatalogDocument>(json, JsonOptions)
                  ?? throw new JsonException("台账 JSON 反序列化结果为空");
        return new PartCatalog(doc);
    }
}
