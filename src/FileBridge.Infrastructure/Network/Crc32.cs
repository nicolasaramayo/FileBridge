namespace FileBridge.Infrastructure.Network;

using System.IO;
using System.Runtime.CompilerServices;

public sealed class Crc32
{
    private static readonly uint[] Table;

    static Crc32()
    {
        Table = new uint[256];
        const uint polynomial = 0xEDB88320;
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 8; j > 0; j--)
            {
                if ((crc & 1) == 1)
                    crc = (crc >> 1) ^ polynomial;
                else
                    crc >>= 1;
            }
            Table[i] = crc;
        }
    }

    public static uint Compute(byte[] data)
    {
        return Compute(data.AsSpan());
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        }
        return crc ^ 0xFFFFFFFF;
    }
}
