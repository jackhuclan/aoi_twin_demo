namespace AoiTwin.Rendering.Upload;

/// <summary>上传优先级（TB.13）：越紧急越先上，且不受单帧预算的「先来先服务」拖累。</summary>
public enum UploadPriority
{
    Background = 0,
    Normal = 1,
    Critical = 2,
}

/// <summary>一次待上传项（纹理/几何）。<paramref name="Key"/> 相同 = 同一资源，后到覆盖先到。</summary>
public sealed record UploadItem(string Key, UploadPriority Priority, long Bytes, object? Payload = null);

/// <summary>单帧上传计划。</summary>
public sealed record UploadFrame(
    IReadOnlyList<UploadItem> Items,
    long Bytes,
    bool BudgetExhausted,
    int Remaining)
{
    public bool IsEmpty => Items.Count == 0;
}

/// <summary>
/// GPU 上传分帧闸门（TB.13，NFR-19）：把整片 Wafer Map / 大批点云的一次性上传
/// 切到多帧完成，单帧字节数不超过预算（默认 4MB）。
///
/// 三条语义：
/// 1) **优先级**：Critical（当前视口正在看的整片图）先上，Background（预取）最后；
/// 2) **同 Key 覆盖**：连续换片时只保留最新一张，不把中间帧也传上 GPU（省带宽即省卡顿）；
/// 3) **单条超预算独占一帧**：超大资源（例如 8K 纹理）不能被预算永久饿死，
///    否则界面会「永远差最后一张图」。
/// </summary>
public sealed class UploadBudgetGate
{
    /// <summary>默认单帧上传预算（字节）。4MB ≈ 2048² RGBA 一张，与 PRD NFR-19 一致。</summary>
    public const long DefaultBudgetBytes = 4L * 1024 * 1024;

    private readonly long _budgetPerFrame;
    private readonly List<UploadItem> _queue = [];
    private readonly object _gate = new();

    public UploadBudgetGate(long budgetPerFrame = DefaultBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetPerFrame);
        _budgetPerFrame = budgetPerFrame;
    }

    public long BudgetPerFrame => _budgetPerFrame;

    public int PendingCount
    {
        get { lock (_gate) return _queue.Count; }
    }

    public long PendingBytes
    {
        get { lock (_gate) return _queue.Sum(i => i.Bytes); }
    }

    /// <summary>入队；同 Key 的旧项被替换（位置按新优先级重新排序）。</summary>
    public void Enqueue(UploadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentOutOfRangeException.ThrowIfNegative(item.Bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Key);

        lock (_gate)
        {
            _queue.RemoveAll(i => string.Equals(i.Key, item.Key, StringComparison.Ordinal));
            _queue.Add(item);
        }
    }

    /// <summary>取出一帧的上传计划（每帧调用一次）。</summary>
    public UploadFrame Drain()
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                return new UploadFrame([], 0, false, 0);
            }

            var ordered = _queue
                .OrderByDescending(i => i.Priority)
                .ThenBy(i => i.Bytes)
                .ToList();

            var picked = new List<UploadItem>();
            long used = 0;

            foreach (var item in ordered)
            {
                if (item.Bytes > _budgetPerFrame)
                {
                    // 超大资源：独占一帧（前面已选中的让位到下一帧）
                    if (picked.Count == 0)
                    {
                        picked.Add(item);
                        used = item.Bytes;
                        break;
                    }

                    continue;
                }

                if (used + item.Bytes > _budgetPerFrame)
                {
                    continue; // 本帧放不下，留到下一帧
                }

                picked.Add(item);
                used += item.Bytes;
            }

            foreach (var item in picked)
            {
                _queue.Remove(item);
            }

            var exhausted = _queue.Count > 0;
            return new UploadFrame(picked, used, exhausted, _queue.Count);
        }
    }

    /// <summary>清空（切场景/释放）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _queue.Clear();
        }
    }
}
