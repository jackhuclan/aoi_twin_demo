using System.Reflection;
using System.Runtime.Loader;

namespace AoiTwin.Core.Analytics;

/// <summary>
/// 插件加载策略（TB.5，NFR-25）。
/// <paramref name="AllowedPublicKeyTokens"/> 为**空**时，任何外部 DLL 都被拒绝——这是安全默认：
/// 现场机器上放一个 dll 就能跑任意代码，必须显式授权签名。
/// </summary>
public sealed record PluginLoadOptions(IReadOnlyList<string> AllowedPublicKeyTokens, int MaxPlugins = 32)
{
    public static PluginLoadOptions DenyAll { get; } = new([]);
}

/// <summary>插件加载结果。</summary>
public sealed record PluginLoadReport(
    int Loaded,
    int Rejected,
    IReadOnlyList<string> Messages)
{
    public bool AnyLoaded => Loaded > 0;
}

/// <summary>
/// 预测器插件加载器（TB.5）：从目录加载 <see cref="IAlertPredictor"/> 实现，带签名校验与失败隔离。
///
/// 三道闸：
/// 1) **签名**：只加载公钥 token 在白名单内的程序集（无强名 → token 为空 → 必然被拒）；
/// 2) **隔离加载**：每个 DLL 用独立 collectible <see cref="AssemblyLoadContext"/>，
///    加载失败只影响该 DLL，不会把宿主进程带崩；
/// 3) **数量上限**：防止误把 bin 目录当插件目录导致加载上百个程序集。
///
/// 说明：本机离线验收只能验证「拒绝未授权 / 加载合法」两条路径；
/// 真机上应把公司签发的公钥 token 写进配置后再做一次装载验证（见 docs/m3-field-verification.md）。
/// </summary>
public static class PluginLoader
{
    public static PluginLoadReport LoadPredictors(
        PredictorRegistry registry,
        string directory,
        PluginLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(options);

        var messages = new List<string>();
        if (!Directory.Exists(directory))
        {
            return new PluginLoadReport(0, 0, [$"插件目录不存在: {directory}"]);
        }

        int loaded = 0, rejected = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (loaded >= options.MaxPlugins)
            {
                messages.Add($"达到插件数量上限 {options.MaxPlugins}，其余 DLL 跳过");
                break;
            }

            string? token;
            try
            {
                token = TokenOf(AssemblyName.GetAssemblyName(path));
            }
            catch (Exception ex)
            {
                rejected++;
                messages.Add($"{Path.GetFileName(path)}: 无法读取程序集信息（{ex.GetType().Name}）");
                continue;
            }

            if (!IsTrusted(token, options.AllowedPublicKeyTokens))
            {
                rejected++;
                messages.Add($"{Path.GetFileName(path)}: 签名不受信任（公钥 token={(string.IsNullOrEmpty(token) ? "无强名" : token)}）");
                continue;
            }

            try
            {
                var context = new AssemblyLoadContext($"predictor-{Path.GetFileNameWithoutExtension(path)}", isCollectible: true);
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
                var added = RegisterFromAssembly(registry, assembly, messages);
                if (added > 0)
                {
                    loaded++;
                }
                else
                {
                    rejected++;
                }
            }
            catch (Exception ex)
            {
                rejected++;
                messages.Add($"{Path.GetFileName(path)}: 加载失败（{ex.GetType().Name}: {ex.Message}）");
            }
        }

        return new PluginLoadReport(loaded, rejected, messages);
    }

    /// <summary>把程序集中的全部可实例化预测器注册进注册表，返回新增数量。</summary>
    public static int RegisterFromAssembly(PredictorRegistry registry, Assembly assembly, List<string>? messages = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(assembly);

        var added = 0;
        foreach (var type in assembly.GetTypes())
        {
            if (!typeof(IAlertPredictor).IsAssignableFrom(type)
                || type.IsAbstract
                || type.IsInterface
                || type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            try
            {
                var predictor = (IAlertPredictor)Activator.CreateInstance(type)!;
                registry.Register(predictor);
                added++;
            }
            catch (Exception ex)
            {
                messages?.Add($"{type.FullName}: 实例化失败（{ex.GetType().Name}: {ex.Message}）");
            }
        }

        return added;
    }

    /// <summary>公钥 token 转十六进制小写；无强名返回 null。</summary>
    public static string? TokenOf(AssemblyName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var token = name.GetPublicKeyToken();
        if (token is null || token.Length == 0)
        {
            return null;
        }

        return Convert.ToHexString(token).ToLowerInvariant();
    }

    private static bool IsTrusted(string? token, IReadOnlyList<string> allowed)
        => !string.IsNullOrEmpty(token)
           && allowed.Any(a => string.Equals(a, token, StringComparison.OrdinalIgnoreCase));
}
