namespace AoiTwin.ViewModel;

/// <summary>
/// 运动部件位姿快照（TA.2，PRD-AOI-TWIN-M2 FR-M2-02）。
/// 不可变对象：数据线程整体构造、渲染线程整引用读取——16 个矩阵元素要么全旧要么全新，天然无撕裂。
/// 行主序 4×4（与 WPF Matrix3D 同序）：渲染层用 GetMatrix3D 换算，本工程不依赖 WindowsBase。
/// </summary>
public sealed class PoseSnapshot
{
    public const int MatrixSize = 16;

    /// <summary>行主序 4×4 矩阵，长度恒为 16（构造时拷贝，外部修改不影响快照）。</summary>
    public double[] Matrix { get; }

    /// <summary>
    /// 快照对应的时刻。<c>set</c> 仅供本程序集内的双缓冲写入方使用——写入必须在
    /// 引用换入（<see cref="System.Threading.Volatile"/>.Write）之前完成，读者才会看到一致的二元组。
    /// </summary>
    public DateTimeOffset Timestamp { get; internal set; }

    public PoseSnapshot(double[] matrix, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.Length != MatrixSize)
        {
            throw new ArgumentException($"矩阵长度必须为 {MatrixSize}", nameof(matrix));
        }

        Matrix = (double[])matrix.Clone();
        Timestamp = timestamp;
    }

    private PoseSnapshot(double[] ownedBuffer, DateTimeOffset timestamp, bool ownsBuffer)
    {
        _ = ownsBuffer;
        Matrix = ownedBuffer;
        Timestamp = timestamp;
    }

    /// <summary>
    /// 零分配入口（NFR-13）：直接持有调用方提供的 16 元素缓冲，**不拷贝**。
    /// 只允许「每部件一块独立缓冲 + 引用换入」的写入方（<see cref="MotionPoseEngine"/>）使用：
    /// 写入方必须保证改写缓冲前，渲染线程已换到另一块缓冲对应的快照。
    /// </summary>
    internal static PoseSnapshot FromOwnedBuffer(double[] buffer, DateTimeOffset timestamp)
    {
        if (buffer.Length != MatrixSize)
        {
            throw new ArgumentException($"矩阵长度必须为 {MatrixSize}", nameof(buffer));
        }

        return new PoseSnapshot(buffer, timestamp, ownsBuffer: true);
    }

    public static PoseSnapshot Identity { get; } =
        new([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1], DateTimeOffset.MinValue);

    /// <summary>平移快照（M2 占位资产的运动只到平移级；旋转在 TA.3 换算层扩展）。</summary>
    public static PoseSnapshot Translation(double x, double y, double z, DateTimeOffset timestamp) =>
        new([1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1], timestamp);
}
