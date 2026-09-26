using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hyperion.Core;

namespace Hyperion.Storage;

public static class PageType
{
    public const byte Data = 1;
    public const byte IndexInterior = 2;
    public const byte IndexLeaf = 3;
    public const byte Overflow = 4;
}

[Flags]
public enum PageState : byte
{
    None = 0,
    Dirty = 1 << 0,
    Compressed = 1 << 1,
}

/// <summary>
/// 32-byte header for a 4096-byte database page.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
public readonly struct PageHeader
{
    public const int Size = 32;

    [FieldOffset(0)]  public readonly uint Checksum;          // CRC32C over bytes 4..4095
    [FieldOffset(4)]  public readonly ulong PageLsn;          // LSN of last modification
    [FieldOffset(12)] public readonly uint PageId;           // Page identifier
    [FieldOffset(16)] public readonly uint PrevPageId;       // Previous page link
    [FieldOffset(20)] public readonly uint NextPageId;       // Next page link
    [FieldOffset(24)] public readonly ushort SlotCount;       // Number of slots allocated
    [FieldOffset(26)] public readonly ushort FreeSpaceOffset; // Byte offset where free space ends
    [FieldOffset(28)] public readonly byte PageType;          // PageType constant
    [FieldOffset(29)] public readonly byte Flags;             // PageFlags
    [FieldOffset(30)] public readonly ushort FragmentedBytes; // Reclaimable fragmented space

    public PageHeader(
        uint checksum,
        ulong pageLsn,
        uint pageId,
        uint prevPageId,
        uint nextPageId,
        ushort slotCount,
        ushort freeSpaceOffset,
        byte pageType,
        byte flags,
        ushort fragmentedBytes)
    {
        Checksum = checksum;
        PageLsn = pageLsn;
        PageId = pageId;
        PrevPageId = prevPageId;
        NextPageId = nextPageId;
        SlotCount = slotCount;
        FreeSpaceOffset = freeSpaceOffset;
        PageType = pageType;
        Flags = flags;
        FragmentedBytes = fragmentedBytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PageHeader ReadFrom(ReadOnlySpan<byte> buffer)
    {
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..4]);
        ulong pageLsn = BinaryPrimitives.ReadUInt64LittleEndian(buffer[4..12]);
        uint pageId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..16]);
        uint prevPageId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..20]);
        uint nextPageId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[20..24]);
        ushort slotCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer[24..26]);
        ushort freeSpaceOffset = BinaryPrimitives.ReadUInt16LittleEndian(buffer[26..28]);
        byte pageType = buffer[28];
        byte flags = buffer[29];
        ushort fragmentedBytes = BinaryPrimitives.ReadUInt16LittleEndian(buffer[30..32]);

        return new PageHeader(
            checksum, pageLsn, pageId, prevPageId, nextPageId,
            slotCount, freeSpaceOffset, pageType, flags, fragmentedBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTo(Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0..4], Checksum);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[4..12], PageLsn);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..16], PageId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[16..20], PrevPageId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[20..24], NextPageId);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[24..26], SlotCount);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[26..28], FreeSpaceOffset);
        buffer[28] = PageType;
        buffer[29] = Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[30..32], FragmentedBytes);
    }
}
