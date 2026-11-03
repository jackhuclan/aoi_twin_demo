using AoiTwin.Core.Catalog;
using Xunit;

namespace AoiTwin.Core.Tests.Catalog;

public class PartCatalogTests
{
    private const string ValidJson = """
        {
          "assetVersion": "1.0.0",
          "machine": "AOI-01",
          "parts": [
            { "partId": "ROBOT", "name": "传输机械手", "module": "AOI_Equipment", "model": "R-XYZ-3A",
              "lod": { "l1": "robot_l1.glb", "l2": "robot_l2.glb" }, "axes": ["R", "Theta", "Z"] },
            { "partId": "LIGHT_RING", "name": "环形光源", "module": "Optics", "model": "LR-8CH",
              "lod": { "l1": "light_ring_l1.glb", "l2": "light_ring_l2.glb" } }
          ]
        }
        """;

    [Fact]
    public void Load_valid_json_and_find_by_part_id()
    {
        var catalog = PartCatalog.FromJson(ValidJson);

        Assert.Equal("1.0.0", catalog.Document.AssetVersion);
        Assert.Equal(2, catalog.Parts.Count);

        var robot = catalog.Find("ROBOT");
        Assert.NotNull(robot);
        Assert.Equal("传输机械手", robot!.Name);
        Assert.Equal(["R", "Theta", "Z"], robot.Axes);

        Assert.NotNull(catalog.Find("LIGHT_RING"));
        Assert.Null(catalog.Find("NOT_EXIST"));
    }

    [Fact]
    public void Entry_without_axes_defaults_to_empty()
    {
        var catalog = PartCatalog.FromJson(ValidJson);
        var lightRing = catalog.Find("LIGHT_RING")!;
        Assert.Empty(lightRing.Axes);
    }

    [Fact]
    public void Duplicate_part_id_throws()
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
        var ex = Assert.Throws<ArgumentException>(() => PartCatalog.FromJson(json));
        Assert.Contains("ROBOT", ex.Message);
    }

    [Theory]
    [InlineData("robot")]
    [InlineData("LIGHT-RING")]
    [InlineData("A")]
    [InlineData("")]
    public void Invalid_part_id_throws(string partId)
    {
        var json = $$"""
            {
              "assetVersion": "1.0.0", "machine": "AOI-01",
              "parts": [ { "partId": "{{partId}}", "name": "a", "module": "m", "model": "x" } ]
            }
            """;
        Assert.ThrowsAny<ArgumentException>(() => PartCatalog.FromJson(json));
    }

    [Fact]
    public void Empty_parts_throws()
    {
        const string json = """{ "assetVersion": "1.0.0", "machine": "AOI-01", "parts": [] }""";
        Assert.ThrowsAny<ArgumentException>(() => PartCatalog.FromJson(json));
    }

    [Fact]
    public void Malformed_json_throws()
    {
        Assert.ThrowsAny<Exception>(() => PartCatalog.FromJson("{ not json "));
    }
}
