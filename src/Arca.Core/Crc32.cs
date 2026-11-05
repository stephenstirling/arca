namespace Arca.Core;

/// <summary>The standard CRC-32 (the checksum ZIP, 7z and RAR store for each file), used by "Test" to spot damaged files.</summary>
public static class Crc32
{
    private static readonly uint[] Table = MakeTable();

    public const uint Seed = 0xFFFFFFFFu;

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Finish(uint crc) => crc ^ 0xFFFFFFFFu;

    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Update(Seed, data));

    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }
}
