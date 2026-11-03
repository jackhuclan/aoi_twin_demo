namespace AoiTwin.AssetTools;

/// <summary>
/// 资产校验 CLI（docs/asset-spec.md §4）。
/// 用法：AssetTools validate &lt;catalog.json&gt; &lt;assetsRoot&gt;
/// 退出码：0 = 通过；1 = 存在校验错误。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 3 || !string.Equals(args[0], "validate", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("用法: AssetTools validate <catalog.json> <assetsRoot>");
            return 2;
        }

        var issues = new AssetValidator().Validate(args[1], args[2]);
        foreach (var issue in issues)
        {
            Console.Error.WriteLine(issue);
        }

        if (issues.Count == 0)
        {
            Console.WriteLine("资产校验通过。");
            return 0;
        }

        Console.Error.WriteLine($"资产校验未通过，共 {issues.Count} 项问题。");
        return 1;
    }
}
