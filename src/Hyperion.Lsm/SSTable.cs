using System.Buffers.Binary;
using Hyperion.Core.Checksum;

namespace Hyperion.Lsm;

public readonly record struct BlockIndexEntry(byte[] SeparatorKey, long Offset, int Length);

public sealed class SSTableFooter
{
    public const int Size = 48;
    public const uint MagicNumber = 0x53535442; // "SSTB"

    public long IndexOffset { get; }
    public int IndexLength { get; }
    public long BloomOffset { get; }
    public int BloomLength { get; }
    public ulong TotalKeyCount { get; }

    public SSTableFooter(long indexOffset, int indexLength, long bloomOffset, int bloomLength, ulong totalKeys)
    {
        IndexOffset = indexOffset;
        IndexLength = indexLength;
        BloomOffset = bloomOffset;
        BloomLength = bloomLength;
        TotalKeyCount = totalKeys;
    }

    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], MagicNumber);
        BinaryPrimitives.WriteInt64LittleEndian(destination[4..12], IndexOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..16], IndexLength);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..24], BloomOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[24..28], BloomLength);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[28..36], TotalKeyCount);
        BinaryPrimitives.WriteInt64LittleEndian(destination[36..44], 0); // Padding

        uint crc = Crc32C.Compute(destination[0..44]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[44..48], crc);
    }

    public static SSTableFooter ReadFrom(ReadOnlySpan<byte> source)
    {
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]);
        if (magic != MagicNumber)
            throw new InvalidOperationException($"Invalid SSTable magic header: 0x{magic:X8}");

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(source[44..48]);
        uint actualCrc = Crc32C.Compute(source[0..44]);
        if (storedCrc != actualCrc)
            throw new InvalidOperationException("SSTable footer checksum verification failed");

        long indexOffset = BinaryPrimitives.ReadInt64LittleEndian(source[4..12]);
        int indexLength = BinaryPrimitives.ReadInt32LittleEndian(source[12..16]);
        long bloomOffset = BinaryPrimitives.ReadInt64LittleEndian(source[16..24]);
        int bloomLength = BinaryPrimitives.ReadInt32LittleEndian(source[24..28]);
        ulong totalKeys = BinaryPrimitives.ReadUInt64LittleEndian(source[28..36]);

        return new SSTableFooter(indexOffset, indexLength, bloomOffset, bloomLength, totalKeys);
    }
}
