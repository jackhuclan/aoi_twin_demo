using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace AoiTwin.Core.Recording;

/// <summary>录制器配置。</summary>
public sealed record RecordingOptions
{
    /// <summary>录制文件输出目录。</summary>
    public required string Directory { get; init; }

    /// <summary>设备号（文件命名与文件头）。</summary>
    public required string DeviceId { get; init; }

    /// <summary>班次标识（文件头）。</summary>
    public string ShiftId { get; init; } = "";

    /// <summary>单文件最大时长（班次滚动），null = 直到显式 Roll/Dispose。</summary>
    public TimeSpan? MaxFileDuration { get; init; }

    /// <summary>磁盘已用比例水位，超过即暂停录制并告警（PRD：磁盘水位 ≥ 85% 暂停）。</summary>
    public double DiskUsedWatermark { get; init; } = 0.85;

    /// <summary>磁盘已用比例探针（测试注入）；null 时按输出目录所在盘 DriveInfo 实测。</summary>
    public Func<double>? DiskUsedRatioProvider { get; init; }
}

/// <summary>
/// .aotrec 录制写入器（T2.2）：逐帧 CRC32、全文件增量 SHA-256、班次滚动分文件、磁盘水位暂停。
/// Write 为同步写（发布线程直写），M1 数据率下无 IO 压力；如需异步缓冲由上层经 RingBuffer 泵接。
/// </summary>
public sealed class RecordingWriter : IDisposable
{
    private readonly RecordingOptions _options;
    private readonly object _writeLock = new();
    private readonly FileStream?[] _fileSlot = new FileStream?[1];
    private IncrementalHash? _sha;
    private long _frameCount;
    private long _totalFrameCount;
    private long _droppedFrameCount;
    private long _totalBytes;
    private DateTimeOffset _fileStart;
    private bool _disposed;

    public RecordingWriter(RecordingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.DeviceId))
        {
            throw new ArgumentException("DeviceId 不能为空", nameof(options));
        }

        System.IO.Directory.CreateDirectory(options.Directory);
    }

    /// <summary>磁盘水位触发暂停后置位；恢复写入后清除。供采集监控面板轮询（T2.5）。</summary>
    public bool PausedByDiskWatermark { get; private set; }

    /// <summary>当前写入中的文件完整路径（无活动文件时为 null）。</summary>
    public string? CurrentFilePath { get; private set; }

    /// <summary>累计已写入帧数（含班次滚动前的文件）。采集监控面板 T2.5 实时展示。</summary>
    public long TotalFrameCount => Interlocked.Read(ref _totalFrameCount);

    /// <summary>因磁盘水位被丢弃的帧数（上层环形缓冲保压，PRD §5.5）。</summary>
    public long DroppedFrameCount => Interlocked.Read(ref _droppedFrameCount);

    /// <summary>累计落盘字节数（帧头 + payload + Footer），用于面板显示写盘量。</summary>
    public long TotalBytesWritten => Interlocked.Read(ref _totalBytes);

    /// <summary>磁盘水位触发事件（录制监控告警）。</summary>
    public event EventHandler? DiskWatermarkPaused;

    /// <summary>
    /// 写入一帧。磁盘水位超限时返回 false（不写入，消息由上层环形缓冲保压）。
    /// 线程安全：多路适配器可能在不同线程并发发布，帧写入在锁内串行化以保证帧完整性。
    /// </summary>
    public bool Write(Bus.TwinTopic topic, Bus.ITwinMessage message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(message);

        lock (_writeLock)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RecordingWriter));
            }

            if (IsOverDiskWatermark())
            {
                if (!PausedByDiskWatermark)
                {
                    PausedByDiskWatermark = true;
                    DiskWatermarkPaused?.Invoke(this, EventArgs.Empty);
                }

                Interlocked.Increment(ref _droppedFrameCount);
                return false;
            }

            PausedByDiskWatermark = false;
            EnsureFile(message.Timestamp);

            var payload = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Serialization.TwinJson.Options);
            if (payload.Length > RecFileFormat.MaxPayloadLength)
            {
                throw new ArgumentException($"消息 payload 超过单帧上限 {RecFileFormat.MaxPayloadLength}B");
            }

            var crc = Crc32.Final(Crc32.Compute(payload));
            Span<byte> header = stackalloc byte[RecFileFormat.FrameHeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], crc);
            header[8] = (byte)topic;
            header[9] = RecFileFormat.TypeIdOf(message);
            BinaryPrimitives.WriteUInt16LittleEndian(header[10..], 0);
            BinaryPrimitives.WriteInt64LittleEndian(header[12..], message.Timestamp.ToUnixTimeMilliseconds());

            var stream = _fileSlot[0]!;
            _sha!.AppendData(header);
            _sha.AppendData(payload);
            stream.Write(header);
            stream.Write(payload);
            _frameCount++;
            Interlocked.Increment(ref _totalFrameCount);
            Interlocked.Add(ref _totalBytes, header.Length + payload.Length);
            return true;
        }
    }

    /// <summary>立即滚动新文件（班次边界/手动切分）。</summary>
    public void Roll(DateTimeOffset at)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FinalizeCurrentFile();
        OpenFile(at);
    }

    /// <summary>
    /// 崩溃模拟专用：不写 Footer 直接关闭流（模拟进程硬杀后的文件状态）。
    /// 正常路径请使用 Dispose。
    /// </summary>
    public void Abandon()
    {
        _fileSlot[0]?.Dispose();
        _fileSlot[0] = null;
        _sha?.Dispose();
        _sha = null;
        _frameCount = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FinalizeCurrentFile();
    }

    private bool IsOverDiskWatermark()
    {
        var used = _options.DiskUsedRatioProvider?.Invoke() ?? DiskSpace.UsedRatio(_options.Directory);
        return used > _options.DiskUsedWatermark;
    }

    private void EnsureFile(DateTimeOffset timestamp)
    {
        if (_fileSlot[0] is not null
            && (_options.MaxFileDuration is null || timestamp - _fileStart < _options.MaxFileDuration.Value))
        {
            return;
        }

        FinalizeCurrentFile();
        OpenFile(timestamp);
    }

    private void OpenFile(DateTimeOffset shiftStart)
    {
        _fileStart = shiftStart;
        CurrentFilePath = ResolveFilePath(shiftStart);
        var stream = new FileStream(CurrentFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var header = new byte[64];
        var headerLen = RecFileFormat.WriteHeader(header, _options.DeviceId, _options.ShiftId, shiftStart.ToUnixTimeMilliseconds());
        _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _sha.AppendData(header.AsSpan(0, headerLen));
        stream.Write(header, 0, headerLen);
        Interlocked.Add(ref _totalBytes, headerLen);
        _fileSlot[0] = stream;
        _frameCount = 0;
    }

    private string ResolveFilePath(DateTimeOffset shiftStart)
    {
        var basePath = Path.Combine(
            _options.Directory,
            $"{_options.DeviceId}_{shiftStart:yyyyMMdd_HHmm}{RecConstants.FileExtension}");
        if (!File.Exists(basePath))
        {
            return basePath;
        }

        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(
                _options.Directory,
                $"{_options.DeviceId}_{shiftStart:yyyyMMdd_HHmm}-{n}{RecConstants.FileExtension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private void FinalizeCurrentFile()
    {
        var stream = _fileSlot[0];
        if (stream is null)
        {
            return;
        }

        _fileSlot[0] = null;
        try
        {
            Span<byte> footer = stackalloc byte[RecFileFormat.FooterSize];
            RecFileFormat.FooterMagic.CopyTo(footer);
            BinaryPrimitives.WriteInt64LittleEndian(footer[8..], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            BinaryPrimitives.WriteUInt64LittleEndian(footer[16..], (ulong)_frameCount);
            _sha!.GetCurrentHash(footer[24..]);
            stream.Write(footer);
            Interlocked.Add(ref _totalBytes, footer.Length);
        }
        finally
        {
            _sha?.Dispose();
            _sha = null;
            _frameCount = 0;
            stream.Dispose();
        }
    }
}

public static class RecConstants
{
    public const string FileExtension = ".aotrec";
}
