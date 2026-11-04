using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AoiTwin.Core.Bus;

namespace AoiTwin.ViewModel;

/// <summary>2D 缺陷列表行（TA.14）：DefectId 为主键，与 3D 点云索引一一对应。</summary>
/// <param name="DefectId">缺陷唯一标识。</param>
/// <param name="DefectClass">缺陷等级（Scratch / Particle / Residue / 其它）。</param>
/// <param name="X">晶圆平面坐标 X（毫米，晶圆中心为原点）。</param>
/// <param name="Y">晶圆平面坐标 Y（毫米）。</param>
/// <param name="SizeUm">缺陷尺寸（µm，只展示不驱动渲染）。</param>
/// <param name="GrayLevel">灰度（0-255）。</param>
public sealed record DefectRow(
    long DefectId,
    string DefectClass,
    double X,
    double Y,
    double SizeUm,
    int GrayLevel)
{
    /// <summary>列表单行文本（等宽数字便于纵向扫读）。</summary>
    public string Summary => $"#{DefectId}  {DefectClass}  ({X,7:F1}, {Y,7:F1}) mm  {SizeUm,6:F1} µm";

    /// <summary>原图窗口标题行。</summary>
    public string Title => $"缺陷 #{DefectId} · {DefectClass}";
}

/// <summary>
/// 2D 缺陷列表视图模型（TA.14，FR-M2-07）：订阅 Result Topic，按片整片替换；
/// 超出 <see cref="MaxRows"/> 时只保留一个滑窗（<see cref="WindowStart"/> 起），
/// 3D→2D 定位落在窗外时把窗口滚过去并选中——列表容量恒定，选中永远可寻址。
///
/// 线程模型：Hub 直推可能来自数据线程，<c>dispatcher</c> 由宿主注入（WPF 传 Dispatcher.Invoke）；
/// 未注入时同步执行（无头测试确定性）。
/// </summary>
public sealed class DefectListViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>列表最多保留的行数（万片级缺陷下仍需保持 UI 线程填充在毫秒级）。</summary>
    public const int MaxRows = 1000;

    private readonly Action<Action>? _dispatcher;
    private readonly IDisposable _subscription;
    private readonly List<DefectRecord> _latest = [];
    private readonly Dictionary<long, int> _order = [];
    private DefectRow? _selected;
    private string _waferId = string.Empty;
    private int _totalCount;
    private int _windowStart;
    private bool _disposed;

    public DefectListViewModel(IRealtimeHub hub, Action<Action>? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _dispatcher = dispatcher;
        _subscription = hub.Subscribe<WaferResult>(TwinTopic.Result, OnResult);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>2D 列表选中项变化（2D→3D 高亮的触发源）。</summary>
    public event EventHandler<DefectRow>? SelectionChanged;

    /// <summary>当前窗口内的行（≤ <see cref="MaxRows"/>）。</summary>
    public ObservableCollection<DefectRow> Rows { get; } = [];

    /// <summary>最近一片晶圆号（空 = 尚未收到结果）。</summary>
    public string WaferId
    {
        get => _waferId;
        private set => Set(ref _waferId, value);
    }

    /// <summary>整片缺陷总数（不受窗口限制）。</summary>
    public int TotalCount
    {
        get => _totalCount;
        private set => Set(ref _totalCount, value);
    }

    /// <summary>当前窗口首行在整片中的序号（0 起始）。</summary>
    public int WindowStart
    {
        get => _windowStart;
        private set => Set(ref _windowStart, value);
    }

    /// <summary>列表选中项（双向：列表点选驱动 3D 高亮，3D 点选回写此属性）。</summary>
    public DefectRow? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            _selected = value;
            Raise();
            if (value is not null)
            {
                SelectionChanged?.Invoke(this, value);
            }
        }
    }

    /// <summary>列表状态行（页签标题下方）。</summary>
    public string Caption => string.IsNullOrEmpty(_waferId)
        ? "等待首片结果…"
        : _totalCount <= MaxRows
            ? $"{_waferId} · 缺陷 {_totalCount:N0} 处"
            : $"{_waferId} · 缺陷 {_totalCount:N0} 处 · 显示 {_windowStart + 1:N0}-{_windowStart + Rows.Count:N0}";

    /// <summary>
    /// 3D→2D 定位（TA.14）：按 DefectId 选中并保证可见。
    /// 目标在窗外时把窗口滚到以目标为中心的位置；未寻址到返回 false。
    /// </summary>
    public bool SelectById(long defectId)
    {
        if (!_order.TryGetValue(defectId, out var index))
        {
            return false;
        }

        var inWindow = index >= _windowStart && index < _windowStart + Rows.Count;
        if (!inWindow)
        {
            var start = Math.Clamp(index - MaxRows / 2, 0, Math.Max(0, _latest.Count - MaxRows));
            Rebuild(start);
        }

        var offset = index - _windowStart;
        if (offset < 0 || offset >= Rows.Count)
        {
            return false;
        }

        Selected = Rows[offset];
        return true;
    }

    /// <summary>按 DefectId 取行（原图窗口用）；行不在当前窗口时返回 false。</summary>
    public bool TryGetRow(long defectId, out DefectRow? row)
    {
        row = null;
        if (!_order.TryGetValue(defectId, out var index))
        {
            return false;
        }

        var offset = index - _windowStart;
        if (offset < 0 || offset >= Rows.Count)
        {
            return false;
        }

        row = Rows[offset];
        return true;
    }

    private void OnResult(WaferResult result)
    {
        var apply = () => Replace(result);
        if (_dispatcher is null)
        {
            apply();
        }
        else
        {
            _dispatcher(apply);
        }
    }

    private void Replace(WaferResult result)
    {
        _latest.Clear();
        _order.Clear();
        _latest.AddRange(result.Defects);
        for (var i = 0; i < _latest.Count; i++)
        {
            _order[_latest[i].DefectId] = i;
        }

        WaferId = result.WaferId;
        TotalCount = _latest.Count;
        Rebuild(0);
        Selected = null; // 整片替换后旧选中失效（避免指向已下线的晶圆）
    }

    private void Rebuild(int start)
    {
        WindowStart = Math.Clamp(start, 0, Math.Max(0, _latest.Count - 1));
        Rows.Clear();
        var end = Math.Min(_latest.Count, _windowStart + MaxRows);
        for (var i = _windowStart; i < end; i++)
        {
            var defect = _latest[i];
            Rows.Add(new DefectRow(
                defect.DefectId,
                defect.DefectClass,
                defect.X,
                defect.Y,
                defect.SizeUm,
                defect.GrayLevel));
        }

        Raise(nameof(Caption));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(name);
        Raise(nameof(Caption));
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscription.Dispose();
    }
}
