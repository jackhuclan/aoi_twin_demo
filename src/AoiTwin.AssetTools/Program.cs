namespace AoiTwin.AssetTools;

/// <summary>
/// 资产/录制校验 CLI（docs/asset-spec.md §4、PLAN T1.2/T3.5）。
/// 用法：
///   AssetTools validate &lt;catalog.json&gt; &lt;assetsRoot&gt;   — 模型资产校验（E001–E007）
///   AssetTools verify &lt;file.aotrec&gt; [--repair]          — 录制文件一致性校验（可选修复）
/// 退出码：0 = 通过；1 = 存在校验错误；2 = 用法错误/文件不存在。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 3 && string.Equals(args[0], "validate", StringComparison.Ordinal))
        {
            return RunValidate(args[1], args[2]);
        }

        if (args.Length >= 2 && string.Equals(args[0], "verify", StringComparison.Ordinal))
        {
            var repair = args.Contains("--repair", StringComparer.Ordinal);
            return RecVerifier.Verify(args[1], Console.Out, repair);
        }

        Console.Error.WriteLine("用法:");
        Console.Error.WriteLine("  AssetTools validate <catalog.json> <assetsRoot>");
        Console.Error.WriteLine("  AssetTools verify <file.aotrec> [--repair]");
        return 2;
    }

    private static int RunValidate(string catalogPath, string assetsRoot)
    {
        var issues = new AssetValidator().Validate(catalogPath, assetsRoot);
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
