using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AoiTwin.Core.Recording;

/// <summary>.aotrec 文件检查结果。</summary>
public sealed record AotrecFileInfo
{
    public required string FilePath { get; init; }
    public required string DeviceId { get; init; }
    public required string ShiftId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public required long ValidFrameCount { get; init; }
    /// <summary>文件含合法 Footer（正常关闭）。</summary>
    public required bool IsFinalized { get; init; }
    /// <summary>Repair 后 Footer SHA-256 与帧计数已重建。</summary>
    public bool Repaired { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// .aotrec 读取器：逐帧 CRC 校验、Footer SHA-256 验证、断电文件扫描与修复（T2.2/T3.5 共用）。
/// </summary>
public static class RecordingReader
{
    /// <summary>
    /// 检查文件并逐帧回调反序列化消息（回放引擎 T3.1 由此逐条回灌）。
    /// 校验失败的帧记入 Errors 并跳过（崩溃文件尾部残帧同理）。
    /// </summary>
    public static AotrecFileInfo Inspect(string filePath, Action<Bus.ITwinMessage>? onMessage = null)
    {
        var scan = Scan(filePath, onMessage, hashValidOnly: false, out var header);
        return BuildInfo(filePath, header, scan, finalized: scan.FooterVerified, repaired: false);
    }

    /// <summary>
    /// 修复未正常关闭（崩溃/截断）的文件：截断到最后一个完整帧，重建 Footer（帧计数 + 有效前缀 SHA-256）。
    /// 已正常关闭的文件原样返回（Repaired=false）。
    /// </summary>
    public static AotrecFileInfo Repair(string filePath)
    {
        var scan = Scan(filePath, onMessage: null, hashValidOnly: true, out var header);
        if (scan.FooterVerified && scan.Errors.Count == 0)
        {
            return BuildInfo(filePath, header, scan, finalized: true, repaired: false);
        }

        if (scan.LastValidEnd <= 0)
        {
            return BuildInfo(filePath, header, scan, finalized: false, repaired: false);
        }

        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.SetLength(scan.LastValidEnd);
            stream.Seek(0, SeekOrigin.End);

            Span<byte> footer = stackalloc byte[RecFileFormat.FooterSize];
            RecFileFormat.FooterMagic.CopyTo(footer);
            BinaryPrimitives.WriteInt64LittleEndian(footer[8..], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            BinaryPrimitives.WriteUInt64LittleEndian(footer[16..], (ulong)scan.ValidFrameCount);
            scan.ValidPrefixHash!.GetCurrentHash(footer[24..]);
            stream.Write(footer);
        }

        var repaired = Scan(filePath, onMessage: null, hashValidOnly: false, out _);
        return BuildInfo(filePath, header, repaired, finalized: repaired.FooterVerified, repaired: true);
    }

    private sealed record ScanResult(
        long ValidFrameCount,
        long LastValidEnd,
        bool FooterVerified,
        ulong FooterFrameCount,
        DateTimeOffset? FooterEndedAt,
        bool FooterShaMatches,
        IncrementalHash? ValidPrefixHash,
        List<string> Errors);

    private static ScanResult Scan(
        string filePath,
        Action<Bus.ITwinMessage>? onMessage,
        bool hashValidOnly,
        out (string DeviceId, string ShiftId, DateTimeOffset StartedAt) headerInfo)
    {
        var errors = new List<string>();
        long validFrames = 0;
        long lastValidEnd = 0;
        bool footerVerified = false;
        ulong footerCount = 0;
        DateTimeOffset? footerEnded = null;
        bool footerShaMatches = false;
        var validPrefixHash = hashValidOnly ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var fileLength = stream.Length;

        // ---- 文件头 ----
        string deviceId;
        string shiftId;
        long startUnixMs;
        try
        {
            Span<byte> magic = stackalloc byte[8];
            stream.ReadExactly(magic);
            if (!magic.SequenceEqual(RecFileFormat.FileMagic))
            {
                throw new InvalidDataException("文件头 magic 不符");
            }

            var version = BinaryPrimitives.ReadUInt32LittleEndian(reader.ReadBytes(4));
            if (version != RecFileFormat.Version)
            {
                throw new InvalidDataException($"不支持的容器版本 {version}");
            }

            deviceId = Encoding.UTF8.GetString(reader.ReadBytes(BinaryPrimitives.ReadUInt16LittleEndian(reader.ReadBytes(2))));
            shiftId = Encoding.UTF8.GetString(reader.ReadBytes(BinaryPrimitives.ReadUInt16LittleEndian(reader.ReadBytes(2))));
            startUnixMs = BinaryPrimitives.ReadInt64LittleEndian(reader.ReadBytes(8));
        }
        catch (Exception ex)
        {
            errors.Add($"文件头解析失败: {ex.Message}");
            headerInfo = ("", "", default);
            return new ScanResult(0, 0, false, 0, null, false, validPrefixHash, errors);
        }

        headerInfo = (deviceId, shiftId, DateTimeOffset.FromUnixTimeMilliseconds(startUnixMs));
        var headerEnd = stream.Position;
        if (hashValidOnly)
        {
            Feed(validPrefixHash, stream, 0, headerEnd);
        }

        var footerStart = TryReadFooter(stream, fileLength, ref footerVerified, ref footerCount, ref footerEnded, ref footerShaMatches, errors);
        stream.Seek(headerEnd, SeekOrigin.Begin);

        // ---- 帧扫描 ----
        long frameIndex = 0;
        while (stream.Position < footerStart)
        {
            var frameStart = stream.Position;
            frameIndex++;
            try
            {
                if (footerStart - stream.Position < RecFileFormat.FrameHeaderSize)
                {
                    errors.Add($"帧#{frameIndex}: 尾部残缺（不足帧头），按崩溃截断处理");
                    break;
                }

                var header = reader.ReadBytes(RecFileFormat.FrameHeaderSize);
                var payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (payloadLen == 0 || payloadLen > RecFileFormat.MaxPayloadLength)
                {
                    errors.Add($"帧#{frameIndex}: 非法 payload 长度 {payloadLen}");
                    break;
                }

                var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var topic = (Bus.TwinTopic)header[8];
                var typeId = header[9];
                var tsMs = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12));
                if (footerStart - stream.Position < payloadLen)
                {
                    errors.Add($"帧#{frameIndex}: payload 截断");
                    break;
                }

                var payload = reader.ReadBytes((int)payloadLen);
                var actualCrc = Crc32.Final(Crc32.Compute(payload));
                if (actualCrc != expectedCrc)
                {
                    errors.Add($"帧#{frameIndex}: CRC 校验失败（期望 0x{expectedCrc:X8} 实际 0x{actualCrc:X8}）");
                    continue;
                }

                if (onMessage is not null)
                {
                    var message = DeserializeFrame(typeId, payload);
                    onMessage(message);
                }

                validFrames++;
                lastValidEnd = stream.Position;
                validPrefixHash?.AppendData(header);
                validPrefixHash?.AppendData(payload);
            }
            catch (EndOfStreamException)
            {
                errors.Add($"帧#{frameIndex}: 流意外结束");
                break;
            }
        }

        return new ScanResult(validFrames, lastValidEnd, footerVerified, footerCount, footerEnded, footerShaMatches, validPrefixHash, errors);
    }

    /// <summary>尝试读取 Footer；返回 Footer 起始偏移（视为帧区终点），无效时返回文件长度。</summary>
    private static long TryReadFooter(
        FileStream stream,
        long fileLength,
        ref bool footerVerified,
        ref ulong footerCount,
        ref DateTimeOffset? footerEnded,
        ref bool footerShaMatches,
        List<string> errors)
    {
        if (fileLength < RecFileFormat.FooterSize)
        {
            return fileLength;
        }

        var footerStart = fileLength - RecFileFormat.FooterSize;
        stream.Seek(footerStart, SeekOrigin.Begin);
        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(RecFileFormat.FooterMagic))
        {
            stream.Seek(0, SeekOrigin.Begin);
            return fileLength;
        }

        Span<byte> body = stackalloc byte[RecFileFormat.FooterSize - 8];
        stream.ReadExactly(body);
        footerEnded = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(body));
        footerCount = BinaryPrimitives.ReadUInt64LittleEndian(body[8..]);
        var recordedSha = body[16..48].ToArray();

        // 验证 SHA-256：覆盖 [0, footerStart)
        stream.Seek(0, SeekOrigin.Begin);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long remaining = footerStart;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                break;
            }

            sha.AppendData(buffer.AsSpan(0, read));
            remaining -= read;
        }

        Span<byte> actual = stackalloc byte[32];
        sha.GetCurrentHash(actual);
        footerShaMatches = actual.SequenceEqual(recordedSha);
        footerVerified = footerShaMatches;
        if (!footerShaMatches)
        {
            errors.Add("Footer SHA-256 与文件内容不符（文件存在静默损坏）");
        }

        stream.Seek(0, SeekOrigin.Begin);
        return fileLength - RecFileFormat.FooterSize;
    }

    private static void Feed(IncrementalHash? hash, FileStream stream, long offset, long count)
    {
        if (hash is null)
        {
            return;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[81920];
        long remaining = count;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer.AsSpan(0, read));
            remaining -= read;
        }

        stream.Seek(offset + count, SeekOrigin.Begin);
    }

    /// <summary>
    /// 构建帧索引（回放引擎 T3.1）：CRC 校验通过的帧的文件偏移/时间戳/Topic/类型。
    /// 遇到非法或 CRC 失败的帧即停止（索引只覆盖有效前缀，与 Inspect 的逐帧跳过策略不同——
    /// 回放要求序列连续可比较，中间缺失帧应整体暴露而不是静默跳过）。
    /// </summary>
    public static IReadOnlyList<FrameIndexEntry> BuildIndex(string filePath)
    {
        var entries = new List<FrameIndexEntry>();

        // 在线场景（录制中打开文件做批次索引）时文件头/尾帧可能尚未刷盘，
        // 读取越界不应抛给调用方——按「有效前缀为止」处理。
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            // ---- 跳过文件头 ----
            Span<byte> magic = stackalloc byte[8];
            stream.ReadExactly(magic);
            if (!magic.SequenceEqual(RecFileFormat.FileMagic))
            {
                throw new InvalidDataException("文件头 magic 不符");
            }

            stream.Seek(4, SeekOrigin.Current); // version
            var devLen = BinaryPrimitives.ReadUInt16LittleEndian(reader.ReadBytes(2));
            stream.Seek(devLen, SeekOrigin.Current);
            var shiftLen = BinaryPrimitives.ReadUInt16LittleEndian(reader.ReadBytes(2));
            stream.Seek(shiftLen, SeekOrigin.Current);
            stream.Seek(8, SeekOrigin.Current); // startUnixMs
            var headerEnd = stream.Position;

            // ---- 帧区终点：有合法 Footer 则为其起始，否则为文件长度 ----
            var fileLength = stream.Length;
            var framesEnd = fileLength;
            if (fileLength >= RecFileFormat.FooterSize)
            {
                stream.Seek(fileLength - RecFileFormat.FooterSize, SeekOrigin.Begin);
                Span<byte> footerMagic = stackalloc byte[8];
                stream.ReadExactly(footerMagic);
                if (footerMagic.SequenceEqual(RecFileFormat.FooterMagic))
                {
                    framesEnd = fileLength - RecFileFormat.FooterSize;
                }
            }

            // ---- 逐帧索引 ----
            stream.Seek(headerEnd, SeekOrigin.Begin);
            while (stream.Position + RecFileFormat.FrameHeaderSize <= framesEnd)
            {
                var offset = stream.Position;
                var header = reader.ReadBytes(RecFileFormat.FrameHeaderSize);
                if (header.Length < RecFileFormat.FrameHeaderSize)
                {
                    break;
                }

                var payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (payloadLen == 0 || payloadLen > RecFileFormat.MaxPayloadLength
                    || stream.Position + payloadLen > framesEnd)
                {
                    break;
                }

                var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var payload = reader.ReadBytes((int)payloadLen);
                if (payload.Length < payloadLen || Crc32.Final(Crc32.Compute(payload)) != expectedCrc)
                {
                    break;
                }

                entries.Add(new FrameIndexEntry(
                    offset,
                    BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12)),
                    (Bus.TwinTopic)header[8],
                    header[9],
                    (int)payloadLen));
            }
        }
        catch (EndOfStreamException)
        {
            // 文件仍在写入（尾部未落盘）：返回已索引的有效前缀
        }

        return entries;
    }

    /// <summary>按帧类型反序列化 payload（帧头时间戳为权威，JSON 内时间戳冗余，偏差由 T3.5 校验工具报告）。</summary>
    public static Bus.ITwinMessage DeserializeFrame(byte typeId, byte[] payload)
    {
        Bus.ITwinMessage? message = typeId switch
        {
            RecFileFormat.MessageType.AxisSnapshot => JsonSerializer.Deserialize<Bus.AxisSnapshot>(payload, Serialization.TwinJson.Options),
            RecFileFormat.MessageType.StatusEvent => JsonSerializer.Deserialize<Bus.StatusEvent>(payload, Serialization.TwinJson.Options),
            RecFileFormat.MessageType.OpticsEvent => JsonSerializer.Deserialize<Bus.OpticsEvent>(payload, Serialization.TwinJson.Options),
            RecFileFormat.MessageType.WaferResult => JsonSerializer.Deserialize<Bus.WaferResult>(payload, Serialization.TwinJson.Options),
            _ => null,
        };

        return message
            ?? throw new InvalidDataException($"帧 typeId={typeId} 反序列化失败");
    }

    private static AotrecFileInfo BuildInfo(
        string filePath,
        (string DeviceId, string ShiftId, DateTimeOffset StartedAt) header,
        ScanResult scan,
        bool finalized,
        bool repaired)
    {
        return new AotrecFileInfo
        {
            FilePath = filePath,
            DeviceId = header.DeviceId,
            ShiftId = header.ShiftId,
            StartedAt = header.StartedAt,
            EndedAt = scan.FooterEndedAt,
            ValidFrameCount = scan.ValidFrameCount,
            IsFinalized = finalized,
            Repaired = repaired,
            Errors = scan.Errors,
        };
    }
}
