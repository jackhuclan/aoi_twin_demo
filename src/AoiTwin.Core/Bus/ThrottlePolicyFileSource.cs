using System.Collections.Immutable;
using System.Text.Json;

namespace AoiTwin.Core.Bus;

/// <summary>
/// 节流策略配置文件源（NFR-18：策略可配置、修改不需重编译）。
///
/// 文件格式（UTF-8 JSON，键为策略名，值为策略或 null=直推）：
/// <code>
/// { "Motion": { "MinIntervalMs": 200, "KeepLatest": true }, "Status": null }
/// </code>
///
/// 热加载：FileSystemWatcher 监听文件变更，防抖后整体重读并原子替换字典；
/// 解析失败保留旧策略并记录 LastError（不抛出、不回退为全直推）。
/// </summary>
public sealed class ThrottlePolicyFileSource : IDisposable
{
    private const int DebounceMs = 150;

    private readonly object _gate = new();
    private readonly FileSystemWatcher _watcher;
    private readonly System.Threading.Timer _debounce;
    private ImmutableDictionary<string, ThrottlePolicy?> _policies;
    private string? _lastError;
    private bool _disposed;

    public ThrottlePolicyFileSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("配置文件路径不能为空", nameof(path));
        }

        _path = Path.GetFullPath(path);
        var (initial, initialError) = Load(_path);
        _policies = initial ?? ImmutableDictionary<string, ThrottlePolicy?>.Empty;
        _lastError = initialError;

        // 防抖定时器先建好再挂监视器（lambda 引用字段，避免可空性告警）
        _debounce = new System.Threading.Timer(_ => Reload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => _debounce.Change(TimeSpan.FromMilliseconds(DebounceMs), Timeout.InfiniteTimeSpan);
        }
        else
        {
            // 文件不存在（首次运行尚未落配置）：不监视目录，保留空表；后续 Reload 可手动触发。
            _watcher = null!;
        }
    }

    private readonly string _path;

    /// <summary>最近一次热加载失败的错误描述；null 表示当前配置完好。</summary>
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <summary>取指定策略名的当前策略；键缺失或显式 null 返回 null（= 直推）。</summary>
    public ThrottlePolicy? Get(string key)
    {
        lock (_gate)
        {
            return _policies.TryGetValue(key, out var policy) ? policy : null;
        }
    }

    /// <summary>
    /// 返回可传给 <see cref="RealtimeHub.Subscribe{T}(TwinTopic, Action{T}, Func{ThrottlePolicy?})"/> 的
    /// 活策略查询委托——配置热加载后无需重新订阅即生效。
    /// </summary>
    public Func<ThrottlePolicy?> PolicyOf(string key) => () => Get(key);

    /// <summary>立即重读配置文件（监视器变更事件的防抖回调，也可手动调用）。</summary>
    public void Reload()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var (policies, error) = Load(_path);
                // 解析失败时 policies 为 null：不替换字典，旧策略继续生效
                if (policies is not null)
                {
                    _policies = policies;
                }

                _lastError = error;
            }
            catch (IOException)
            {
                // 编辑器原子替换瞬间的读写竞争：保留旧值，等下一次防抖/手动 Reload。
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debounce.Dispose();
            _watcher?.Dispose();
        }
    }

    /// <summary>
    /// 读取并解析配置文件。返回值：policies 非 null = 解析成功；
    /// policies 为 null = 解析失败（error 携带原因，调用方必须保留旧字典）。
    /// </summary>
    private static (ImmutableDictionary<string, ThrottlePolicy?>? Policies, string? Error) Load(string path)
    {
        if (!File.Exists(path))
        {
            return (ImmutableDictionary<string, ThrottlePolicy?>.Empty, null);
        }

        try
        {
            var json = File.ReadAllText(path);
            var raw = JsonSerializer.Deserialize<Dictionary<string, PolicyEntry?>>(json)
                ?? throw new InvalidDataException("配置根节点必须是对象");

            var builder = ImmutableDictionary.CreateBuilder<string, ThrottlePolicy?>(StringComparer.Ordinal);
            foreach (var (key, entry) in raw)
            {
                builder[key] = entry is null
                    ? null
                    : new ThrottlePolicy(TimeSpan.FromMilliseconds(entry.MinIntervalMs), entry.KeepLatest);
            }

            return (builder.ToImmutable(), null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException)
        {
            return (null, $"节流策略配置解析失败，沿用旧策略：{ex.Message}");
        }
    }

    private sealed record PolicyEntry(int MinIntervalMs, bool KeepLatest = true);
}
