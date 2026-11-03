using System.Buffers.Binary;
using System.Text;

namespace AoiTwin.Core.Recording;

/// <summary>
/// .aotrec 分帧容器常量与帧头布局（PRD-AOI-TWIN-M1 §5.5）。
/// 格式（小端）：
///   文件头 = magic(8B "AOTREC\0") + version(u32) + deviceId(u16len+utf8) + shiftId(u16len+utf8) + startUnixMs(i64)
///   帧     = payloadLen(u32) + crc32(u32, 对 payload) + topic(u8) + typeId(u8) + reserved(u16) + timestampUnixMs(i64) + payload(utf8 JSON)
///   Footer = magic(8B "AOTRECF\0") + endUnixMs(i64) + frameCount(u64) + sha256(32B, 覆盖 Footer 前全部字节)
/// 断电安全说明：结束时间与帧计数存于 Footer 而非文件头——进程硬杀时头部无机会更新，
/// 无 Footer 即视为未正常关闭，可扫描修复（见 RecordingReader.Repair）。
/// 大数组混合二进制（DieMap 走裸字节）：M1 暂以 JSON Base64 承载，typeId 空间与 version 字段已预留升级路径，
/// 出现 WaferResult 大数据量性能问题时按契约变更评审启用。
/// </summary>
public static class RecFileFormat
{
    public const int Version = 1;

    public static ReadOnlySpan<byte> FileMagic => "AOTREC\0\0"u8;
    public static ReadOnlySpan<byte> FooterMagic => "AOTRECF\0"u8;

    /// <summary>帧头固定长度：len(4) + crc(4) + topic(1) + typeId(1) + reserved(2) + ts(8)。</summary>
    public const int FrameHeaderSize = 20;

    /// <summary>Footer 固定长度：endTs(8) + count(8) + sha(32) + magic(8)。</summary>
    public const int FooterSize = 8 + 8 + 32 + 8;

    /// <summary>单帧 payload 合理上限（64MB），防御损坏文件的越界分配。</summary>
    public const int MaxPayloadLength = 64 * 1024 * 1024;

    /// <summary>帧类型 ID（契约冻结：只追加不修改）。</summary>
    public static class MessageType
    {
        public const byte AxisSnapshot = 1;
        public const byte StatusEvent = 2;
        public const byte OpticsEvent = 3;
        public const byte WaferResult = 4;
    }

    public static byte TypeIdOf(Bus.ITwinMessage message) => message switch
    {
        Bus.AxisSnapshot => MessageType.AxisSnapshot,
        Bus.StatusEvent => MessageType.StatusEvent,
        Bus.OpticsEvent => MessageType.OpticsEvent,
        Bus.WaferResult => MessageType.WaferResult,
        _ => throw new ArgumentException($"未登记的录制消息类型: {message.GetType().Name}", nameof(message)),
    };

    public static int WriteHeader(Span<byte> dst, string deviceId, string shiftId, long startUnixMs)
    {
        var dev = Encoding.UTF8.GetBytes(deviceId);
        var shift = Encoding.UTF8.GetBytes(shiftId);
        if (dev.Length > ushort.MaxValue || shift.Length > ushort.MaxValue)
        {
            throw new ArgumentException("deviceId/shiftId 过长");
        }

        int offset = 0;
        FileMagic.CopyTo(dst);
        offset += FileMagic.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[offset..], Version);
        offset += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[offset..], (ushort)dev.Length);
        offset += 2;
        dev.CopyTo(dst[offset..]);
        offset += dev.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[offset..], (ushort)shift.Length);
        offset += 2;
        shift.CopyTo(dst[offset..]);
        offset += shift.Length;
        BinaryPrimitives.WriteInt64LittleEndian(dst[offset..], startUnixMs);
        offset += 8;
        return offset;
    }
}
