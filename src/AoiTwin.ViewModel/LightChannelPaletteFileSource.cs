using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace AoiTwin.ViewModel;

/// <summary>
/// 颜色配置源（NFR-18 热加载）：JSON 结构与 PRD §5.5 一致——
/// <code>
/// {
///   "lightChannelColors": { "1": "#FFE9C4", ... },   // 必需
///   "defectClassColors":  { "Scratch": "#FF6B81" },  // 可选，缺省回落 PRD 默认
///   "stateColors":        { "Run": "#2ED573", ... }  // 可选，含 M3 预留 Warning/Critical
/// }
/// </code>
/// FileSystemWatcher + 防抖 + 原子换表；解析失败保留旧表（与节流策略同构）。
/// 三张表整体换入——不做部分生效，避免「通道色更新了但缺陷色还是旧的」这种半新半旧状态。
/// </summary>
public sealed class LightChannelPaletteFileSource : IDisposable
{
    /// <summary>文件变更后的防抖窗口（编辑器多为多次写入的原子替换）。</summary>
    public const int DebounceMs = 150;

    private readonly string _path;
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer _debounce;
    private LightChannelPalette _palette;
    private string? _lastError;
    private int _disposed;

    public LightChannelPaletteFileSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);

        var (initial, initialError) = Load(_path);
        _palette = initial ?? new LightChannelPalette(LightChannelPalette.Default);
        _lastError = initialError;

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
    }

    /// <summary>当前完整配色（永不返回 null：文件缺失/损坏时回落 PRD 默认三表）。</summary>
    public LightChannelPalette Palette => Volatile.Read(ref _palette);

    /// <summary>当前通道映射（<see cref="Palette"/> 的快捷入口，供既有调用点使用）。</summary>
    public ImmutableDictionary<int, RgbColor> Channels => Volatile.Read(ref _palette).Channels;

    /// <summary>最近一次加载错误（null = 正常）；测试与状态栏用。</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>立即重读文件（热加载防抖的手动触发入口）。</summary>
    public void Reload()
    {
        try
        {
            var (palette, error) = Load(_path);
            if (palette is not null)
            {
                Volatile.Write(ref _palette, palette);
                Volatile.Write(ref _lastError, error);
            }
            else if (error is not null && File.Exists(_path))
            {
                // 文件存在但解析失败：保留旧表，只记录错误
                Volatile.Write(ref _lastError, error);
            }
        }
        catch (IOException)
        {
            // 原子替换瞬间的读写竞争：保留旧值，等下一次防抖/手动 Reload
        }
    }

    private static (LightChannelPalette? Palette, string? Error) Load(string path)
    {
        if (!File.Exists(path))
        {
            return (null, null); // 无配置文件不是错误：走默认三表
        }

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("lightChannelColors", out var rawChannels)
                || rawChannels.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("配置缺少 lightChannelColors 对象");
            }

            var channels = ImmutableDictionary.CreateBuilder<int, RgbColor>();
            foreach (var entry in rawChannels.EnumerateObject())
            {
                var channel = int.Parse(entry.Name, CultureInfo.InvariantCulture);
                channels[channel] = LightChannelPalette.ParseHex(entry.Value.GetString()
                    ?? throw new InvalidDataException($"通道 {entry.Name} 颜色值缺失"));
            }

            return (new LightChannelPalette(
                    channels.ToImmutable(),
                    ReadColorMap(doc.RootElement, "defectClassColors", StringComparer.Ordinal),
                    ReadColorMap(doc.RootElement, "stateColors", StringComparer.OrdinalIgnoreCase)),
                null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException)
        {
            return (null, $"配色解析失败，沿用旧配色：{ex.Message}");
        }
    }

    /// <summary>读取可选的颜色映射表；键缺失返回 null（调用方按 PRD 默认回落）。</summary>
    private static ImmutableDictionary<string, RgbColor>? ReadColorMap(
        JsonElement root,
        string property,
        StringComparer comparer)
    {
        if (!root.TryGetProperty(property, out var raw) || raw.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, RgbColor>(comparer);
        foreach (var entry in raw.EnumerateObject())
        {
            builder[entry.Name] = LightChannelPalette.ParseHex(entry.Value.GetString()
                ?? throw new InvalidDataException($"{property}.{entry.Name} 颜色值缺失"));
        }

        return builder.ToImmutable();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
