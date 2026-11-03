using System.Buffers.Binary;

namespace AoiTwin.Core.Recording;

/// <summary>CRC-32（IEEE 802.3，多项式 0xEDB88320），与 zlib/标准帧校验一致。</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public const uint Initial = 0xFFFFFFFFu;

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Update(Initial, data);

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    public static uint Final(uint crc) => ~crc;
}
