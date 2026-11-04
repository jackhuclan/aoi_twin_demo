using System.Diagnostics;
using System.Windows.Media.Media3D;
using AoiTwin.Rendering.Dynamics;
using AoiTwin.ViewModel;

namespace AoiTwin.App.Services;

/// <summary>
/// 缺陷 2D/3D 双向联动服务（TA.14，FR-M2-07）：
/// - 2D→3D：列表选中 → 按 DefectId 查索引 → 3D 线框高亮（纯内存寻址，判据 ≤ 100ms）；
/// - 3D→2D：视口射线 → 点云索引反查最近缺陷 → 列表滚窗选中 → 原图窗口（ImageRequested）。
///
/// 两端由同一 DefectId 主键寻址，服务不持有几何也不缓存数据；
/// 3D 端（<see cref="IDefectPickSource"/>）由视图装配后 <see cref="Attach"/> 注入，
/// 未装配时所有联动为无操作（LOD 换档/加载期不抛异常）。
/// </summary>
public sealed class DefectLinkService : IDisposable
{
    private readonly DefectListViewModel _list;
    private IDefectPickSource? _driver;
    private bool _suppressListHighlight;
    private bool _disposed;

    public DefectLinkService(DefectListViewModel list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _list = list;
        _list.SelectionChanged += OnListSelectionChanged;
    }

    /// <summary>3D 点选命中后请求打开原图窗口（视图消费；无头环境只计数）。</summary>
    public event EventHandler<DefectRow>? ImageRequested;

    /// <summary>3D 端是否已装配。</summary>
    public bool IsAttached => _driver is not null;

    /// <summary>最近一次 2D→3D 高亮耗时（判据 ≤ 100ms）。</summary>
    public TimeSpan LastHighlightElapsed { get; private set; }

    /// <summary>最近一次被高亮的 DefectId（null = 未高亮过）。</summary>
    public long? LastHighlightedId { get; private set; }

    /// <summary>最近一次 3D 点选反查到的索引项（诊断/测试用）。</summary>
    public DefectIndexEntry? LastPickEntry { get; private set; }

    /// <summary>原图窗口打开次数与最后一次的 DefectId（无头判据）。</summary>
    public int ImageOpenCount { get; private set; }

    public long? LastImageDefectId { get; private set; }

    /// <summary>装配/解绑 3D 端（LOD 换档重建驱动时重新 Attach，旧引用由视图负责释放）。</summary>
    public void Attach(IDefectPickSource? driver) => _driver = driver;

    /// <summary>
    /// 2D 列表选中 → 3D 高亮。索引里没有该 DefectId（例如列表已换成新一片）时返回 false，
    /// 高亮保持原状不误导。
    /// </summary>
    public bool HighlightFrom2D(long defectId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_driver is null || !_driver.TryGetEntry(defectId, out _))
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        _driver.Highlight(defectId);
        stopwatch.Stop();

        LastHighlightElapsed = stopwatch.Elapsed;
        LastHighlightedId = defectId;
        return true;
    }

    /// <summary>
    /// 3D 点选 → 反查 DefectId → 2D 列表定位 → 原图窗口。
    /// 命中容差默认 <see cref="DefectPointDriver.DefaultPickTolerance"/>。
    /// </summary>
    public bool FocusFrom3D(
        Point3D origin,
        Vector3D direction,
        double tolerance = DefectPointDriver.DefaultPickTolerance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_driver is null || !_driver.TryPickOnRay(origin, direction, out var hit, tolerance))
        {
            return false;
        }

        LastPickEntry = hit;
        LastHighlightedId = hit.DefectId;
        _driver.Highlight(hit.DefectId); // 2D 尚未有该行时也要有 3D 反馈

        // 2D 定位：SelectById 会回抛 SelectionChanged，此处抑制重复高亮
        _suppressListHighlight = true;
        try
        {
            _list.SelectById(hit.DefectId);
        }
        finally
        {
            _suppressListHighlight = false;
        }

        return OpenImage(hit.DefectId);
    }

    /// <summary>打开原图窗口（按 DefectId 取行，行不在当前窗口时返回 false）。</summary>
    public bool OpenImage(long defectId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_list.TryGetRow(defectId, out var row) || row is null)
        {
            return false;
        }

        ImageOpenCount++;
        LastImageDefectId = defectId;
        ImageRequested?.Invoke(this, row);
        return true;
    }

    /// <summary>清除高亮并释放列表订阅（视图卸载时调，订阅数回到基线）。</summary>
    public void ClearHighlight()
    {
        _driver?.ClearHighlight();
        LastHighlightedId = null;
    }

    private void OnListSelectionChanged(object? sender, DefectRow row)
    {
        if (_suppressListHighlight || _disposed)
        {
            return;
        }

        HighlightFrom2D(row.DefectId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _list.SelectionChanged -= OnListSelectionChanged;
        _driver = null;
    }
}
