using AoiTwin.Core.Catalog;

namespace AoiTwin.AssetTools;

public sealed record AssetValidationIssue(string Code, string Level, string Message, string? PartId = null)
{
    public override string ToString() => $"[{Level}] {Code}{(PartId is null ? "" : $" ({PartId})")}: {Message}";
}

public sealed record AssetValidationOptions
{
    /// <summary>单部件面数预算（docs/asset-spec.md §2）。</summary>
    public int MaxFacesPerPart { get; init; } = 50_000;

    /// <summary>整机合计面数预算。</summary>
    public int MaxFacesTotal { get; init; } = 150_000;
}

/// <summary>
/// 资产校验（T1.2 / docs/asset-spec.md §4）：
/// 台账规则（E001/E002/E003/E007）→ LOD 文件存在性（E004）→ GLB 可加载（E005）→ 面数预算（E006）。
/// </summary>
public sealed class AssetValidator(AssetValidationOptions? options = null)
{
    private readonly AssetValidationOptions _options = options ?? new();

    public IReadOnlyList<AssetValidationIssue> Validate(string catalogPath, string assetsRootDir)
    {
        var issues = new List<AssetValidationIssue>();
        assetsRootDir = Path.GetFullPath(assetsRootDir);

        PartCatalog? catalog = LoadCatalog(catalogPath, issues);
        if (catalog is null)
        {
            return issues;
        }

        int totalFaces = 0;
        foreach (var part in catalog.Parts)
        {
            ValidatePartLodFiles(part, assetsRootDir, issues, ref totalFaces);
        }

        if (issues.All(i => i.Code != "E005") && totalFaces > _options.MaxFacesTotal)
        {
            issues.Add(new AssetValidationIssue("E006", "error",
                $"整机面数 {totalFaces:N0} 超预算 {_options.MaxFacesTotal:N0}"));
        }

        return issues;
    }

    private static PartCatalog? LoadCatalog(string catalogPath, List<AssetValidationIssue> issues)
    {
        if (!File.Exists(catalogPath))
        {
            issues.Add(new AssetValidationIssue("E001", "error", $"台账文件不存在: {catalogPath}"));
            return null;
        }

        try
        {
            return PartCatalog.Load(catalogPath);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("partId 不符合命名规则"))
        {
            issues.Add(new AssetValidationIssue("E002", "error", $"partId 命名规则校验失败: {ex.Message}"));
            return null;
        }
        catch (ArgumentException ex) when (ex.Message.Contains("partId 重复"))
        {
            issues.Add(new AssetValidationIssue("E003", "error", $"台账存在重复 partId: {ex.Message}"));
            return null;
        }
        catch (ArgumentException ex) when (ex.Message.Contains("parts 不能为空"))
        {
            issues.Add(new AssetValidationIssue("E007", "error", "台账 parts 为空"));
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
        {
            issues.Add(new AssetValidationIssue("E001", "error", $"台账 JSON 校验失败: {ex.Message}"));
            return null;
        }
    }

    private void ValidatePartLodFiles(
        PartCatalogEntry part, string assetsRootDir, List<AssetValidationIssue> issues, ref int totalFaces)
    {
        if (part.Lod is null)
        {
            issues.Add(new AssetValidationIssue("E004", "error", "缺少 lod 定义（l1/l2 必填）", part.PartId));
            return;
        }

        totalFaces += CheckSingleLod(part, part.Lod.L1, "l1", assetsRootDir, issues);
        totalFaces += CheckSingleLod(part, part.Lod.L2, "l2", assetsRootDir, issues);
    }

    private int CheckSingleLod(
        PartCatalogEntry part, string relativePath, string lodName,
        string assetsRootDir, List<AssetValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            issues.Add(new AssetValidationIssue("E004", "error", $"lod.{lodName} 路径为空", part.PartId));
            return 0;
        }

        var fullPath = Path.Combine(assetsRootDir, relativePath);
        if (!File.Exists(fullPath))
        {
            issues.Add(new AssetValidationIssue("E004", "error",
                $"lod.{lodName} 文件不存在: {relativePath}", part.PartId));
            return 0;
        }

        int faces;
        try
        {
            faces = Glb.GlbInspector.CountFaces(fullPath);
        }
        catch (InvalidDataException ex)
        {
            issues.Add(new AssetValidationIssue("E005", "error", ex.Message, part.PartId));
            return 0;
        }

        if (faces > _options.MaxFacesPerPart)
        {
            issues.Add(new AssetValidationIssue("E006", "error",
                $"lod.{lodName} 面数 {faces:N0} 超单部件预算 {_options.MaxFacesPerPart:N0}", part.PartId));
        }

        return faces;
    }
}
