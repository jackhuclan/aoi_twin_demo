using Xunit;
using Xunit.Abstractions;

namespace AoiTwin.AssetTools.Tests;

/// <summary>T1.2 资产校验工具测试：覆盖 docs/asset-spec.md §4 的 E001–E007 规则（红绿循环）。</summary>
public class AssetValidatorTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public AssetValidatorTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "aoitwin-assetval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteCatalog(string json)
    {
        var path = Path.Combine(_dir, "part-catalog.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string CatalogWithLod(string partId, string l1, string l2) => $$"""
        {
          "assetVersion": "1.0.0",
          "machine": "AOI-01",
          "parts": [
            { "partId": "{{partId}}", "name": "测试部件", "module": "AOI_Equipment", "model": "M-1",
              "lod": { "l1": "{{l1}}", "l2": "{{l2}}" } }
          ]
        }
        """;

    [Fact]
    public void Valid_catalog_with_two_small_glbs_passes()
    {
        TestGlb.WriteTrianglesGlb(_dir, "robot_l1.glb", 4);
        TestGlb.WriteTrianglesGlb(_dir, "robot_l2.glb", 2);
        var catalog = WriteCatalog(CatalogWithLod("ROBOT", "robot_l1.glb", "robot_l2.glb"));

        var issues = new AssetValidator().Validate(catalog, _dir);
        Log(issues);

        Assert.Empty(issues);
    }

    [Fact]
    public void Missing_glb_file_reports_E004()
    {
        TestGlb.WriteTrianglesGlb(_dir, "robot_l1.glb", 2);
        var catalog = WriteCatalog(CatalogWithLod("ROBOT", "robot_l1.glb", "robot_l2.glb"));

        var issues = new AssetValidator().Validate(catalog, _dir);

        Assert.Contains(issues, i => i.Code == "E004" && i.PartId == "ROBOT" && i.Message.Contains("robot_l2.glb"));
    }

    [Fact]
    public void Broken_glb_reports_E005()
    {
        File.WriteAllText(Path.Combine(_dir, "robot_l1.glb"), "not a glb");
        File.WriteAllText(Path.Combine(_dir, "robot_l2.glb"), "not a glb");
        var catalog = WriteCatalog(CatalogWithLod("ROBOT", "robot_l1.glb", "robot_l2.glb"));

        var issues = new AssetValidator().Validate(catalog, _dir);

        Assert.Contains(issues, i => i.Code == "E005" && i.PartId == "ROBOT");
    }

    [Fact]
    public void Faces_over_per_part_budget_reports_E006()
    {
        TestGlb.WriteTrianglesGlb(_dir, "robot_l1.glb", 120);
        TestGlb.WriteTrianglesGlb(_dir, "robot_l2.glb", 2);
        var catalog = WriteCatalog(CatalogWithLod("ROBOT", "robot_l1.glb", "robot_l2.glb"));

        var issues = new AssetValidator(new AssetValidationOptions { MaxFacesPerPart = 100, MaxFacesTotal = 1000 })
            .Validate(catalog, _dir);

        Assert.Contains(issues, i => i.Code == "E006" && i.PartId == "ROBOT" && i.Message.Contains("l1"));
    }

    [Fact]
    public void Faces_over_total_budget_reports_E006()
    {
        TestGlb.WriteTrianglesGlb(_dir, "robot_l1.glb", 80);
        TestGlb.WriteTrianglesGlb(_dir, "robot_l2.glb", 80);
        var catalog = WriteCatalog(CatalogWithLod("ROBOT", "robot_l1.glb", "robot_l2.glb"));

        var issues = new AssetValidator(new AssetValidationOptions { MaxFacesPerPart = 100, MaxFacesTotal = 100 })
            .Validate(catalog, _dir);

        Assert.Contains(issues, i => i.Code == "E006" && i.PartId is null && i.Message.Contains("整机"));
    }

    [Fact]
    public void Invalid_part_id_reports_E002()
    {
        var catalog = WriteCatalog(CatalogWithLod("robot", "a.glb", "b.glb"));

        var issues = new AssetValidator().Validate(catalog, _dir);

        Assert.Contains(issues, i => i.Code == "E002");
    }

    [Fact]
    public void Duplicate_part_id_reports_E003()
    {
        const string json = """
            {
              "assetVersion": "1.0.0", "machine": "AOI-01",
              "parts": [
                { "partId": "ROBOT", "name": "a", "module": "m", "model": "x" },
                { "partId": "ROBOT", "name": "b", "module": "m", "model": "y" }
              ]
            }
            """;
        var catalog = WriteCatalog(json);

        var issues = new AssetValidator().Validate(catalog, _dir);

        Assert.Contains(issues, i => i.Code == "E003");
    }

    [Fact]
    public void Empty_parts_reports_E007()
    {
        var catalog = WriteCatalog("""{ "assetVersion": "1.0.0", "machine": "AOI-01", "parts": [] }""");

        var issues = new AssetValidator().Validate(catalog, _dir);

        Assert.Contains(issues, i => i.Code == "E007");
    }

    [Fact]
    public void Malformed_json_reports_E001()
    {
        var catalog = WriteCatalog("{ not json ");

        var issues = new AssetValidator().Validate(catalog, _dir);

        Assert.Contains(issues, i => i.Code == "E001");
    }

    private void Log(IReadOnlyList<AssetValidationIssue> issues)
    {
        foreach (var issue in issues)
        {
            _output.WriteLine(issue.ToString());
        }
    }
}
