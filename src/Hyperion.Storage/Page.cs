using System.Runtime.CompilerServices;
using Hyperion.Core;
using Hyperion.Core.Checksum;

namespace Hyperion.Storage;

/// <summary>
/// Implements a 4096-byte slotted page structure from first principles.
/// 
/// Page Layout:
/// [0..32)     : PageHeader (Checksum, LSN, PageId, SlotCount, FreeSpaceOffset, etc.)
/// [32..Slots) : Slot Array (PageSlot structs: Offset, Length) - grows downward
/// [Slots..Free): Contiguous Free Space
/// [Free..4096): Tuple Storage Area - records grow upward from bottom
/// </summary>
public static class Page
{
    public const int PageSize = 4096;
    public const int HeaderSize = PageHeader.Size;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Initialize(Span<byte> buffer, PageId pageId, byte pageType = PageType.Data)
    {
        if (buffer.Length < PageSize)
            throw new ArgumentException($"Buffer size must be at least {PageSize} bytes", nameof(buffer));

        buffer.Clear();
        var header = new PageHeader(
            checksum: 0,
            pageLsn: 0,
            pageId: pageId.Value,
            prevPageId: PageId.Invalid.Value,
            nextPageId: PageId.Invalid.Value,
            slotCount: 0,
            freeSpaceOffset: PageSize,
            pageType: pageType,
            flags: (byte)PageState.None,
            fragmentedBytes: 0);

        header.WriteTo(buffer);
        UpdateChecksum(buffer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PageHeader GetHeader(ReadOnlySpan<byte> buffer) => PageHeader.ReadFrom(buffer);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetHeader(Span<byte> buffer, in PageHeader header) => header.WriteTo(buffer);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLsn(Span<byte> buffer, Lsn lsn)
    {
        var header = PageHeader.ReadFrom(buffer);
        var updated = new PageHeader(
            header.Checksum,
            lsn.Value,
            header.PageId,
            header.PrevPageId,
            header.NextPageId,
            header.SlotCount,
            header.FreeSpaceOffset,
            header.PageType,
            (byte)(header.Flags | (byte)PageState.Dirty),
            header.FragmentedBytes);
        updated.WriteTo(buffer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetContiguousFreeSpace(ReadOnlySpan<byte> buffer)
    {
        var header = PageHeader.ReadFrom(buffer);
        int slotArrayEnd = HeaderSize + (header.SlotCount * PageSlot.Size);
        return Math.Max(0, header.FreeSpaceOffset - slotArrayEnd);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetUsableFreeSpace(ReadOnlySpan<byte> buffer)
    {
        var header = PageHeader.ReadFrom(buffer);
        return GetContiguousFreeSpace(buffer) + header.FragmentedBytes;
    }

    public static bool TryInsertRecord(Span<byte> buffer, ReadOnlySpan<byte> recordData, out ushort slotIndex)
    {
        slotIndex = 0;
        int recordLen = recordData.Length;
        if (recordLen > PageSize - HeaderSize - PageSlot.Size)
            return false;

        var header = PageHeader.ReadFrom(buffer);

        // Find reusable deleted slot or allocate new one
        int targetSlot = -1;
        for (int i = 0; i < header.SlotCount; i++)
        {
            var slot = GetSlot(buffer, (ushort)i);
            if (slot.IsDeleted)
            {
                targetSlot = i;
                break;
            }
        }

        bool isNewSlot = targetSlot == -1;
        int neededContiguous = recordLen + (isNewSlot ? PageSlot.Size : 0);
        int contiguousFree = GetContiguousFreeSpace(buffer);

        if (contiguousFree < neededContiguous)
        {
            // Try defragmentation if usable free space is sufficient
            int totalUsable = contiguousFree + header.FragmentedBytes;
            if (totalUsable >= neededContiguous)
            {
                Defragment(buffer);
                header = PageHeader.ReadFrom(buffer);
                contiguousFree = GetContiguousFreeSpace(buffer);
            }
            else
            {
                return false; // Out of space on page
            }
        }

        ushort assignedSlotIndex = isNewSlot ? header.SlotCount : (ushort)targetSlot;
        ushort newFreeOffset = (ushort)(header.FreeSpaceOffset - recordLen);

        // Copy record payload to end of free space
        recordData.CopyTo(buffer.Slice(newFreeOffset, recordLen));

        // Write slot entry
        var newSlot = new PageSlot(newFreeOffset, (ushort)recordLen);
        SetSlot(buffer, assignedSlotIndex, newSlot);

        // Update header
        ushort newSlotCount = isNewSlot ? (ushort)(header.SlotCount + 1) : header.SlotCount;
        var updatedHeader = new PageHeader(
            header.Checksum,
            header.PageLsn,
            header.PageId,
            header.PrevPageId,
            header.NextPageId,
            newSlotCount,
            newFreeOffset,
            header.PageType,
            (byte)(header.Flags | (byte)PageState.Dirty),
            header.FragmentedBytes);
        updatedHeader.WriteTo(buffer);

        slotIndex = assignedSlotIndex;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetRecord(ReadOnlySpan<byte> buffer, ushort slotIndex, out ReadOnlySpan<byte> record)
    {
        record = ReadOnlySpan<byte>.Empty;
        var header = PageHeader.ReadFrom(buffer);
        if (slotIndex >= header.SlotCount)
            return false;

        var slot = GetSlot(buffer, slotIndex);
        if (slot.IsDeleted)
            return false;

        record = buffer.Slice(slot.Offset, slot.Length);
        return true;
    }

    public static bool TryUpdateRecord(Span<byte> buffer, ushort slotIndex, ReadOnlySpan<byte> newRecordData)
    {
        var header = PageHeader.ReadFrom(buffer);
        if (slotIndex >= header.SlotCount)
            return false;

        var slot = GetSlot(buffer, slotIndex);
        if (slot.IsDeleted)
            return false;

        int newLen = newRecordData.Length;
        int oldLen = slot.Length;

        if (newLen == oldLen)
        {
            // Exact same size: in-place overwrite
            newRecordData.CopyTo(buffer.Slice(slot.Offset, newLen));
            MarkDirty(buffer);
            return true;
        }

        if (newLen < oldLen)
        {
            // Shrinking: overwrite prefix, mark difference as fragmented
            newRecordData.CopyTo(buffer.Slice(slot.Offset, newLen));
            var newSlot = new PageSlot(slot.Offset, (ushort)newLen);
            SetSlot(buffer, slotIndex, newSlot);

            ushort shrinkFragmented = (ushort)(header.FragmentedBytes + (oldLen - newLen));
            var updatedHeader = new PageHeader(
                header.Checksum,
                header.PageLsn,
                header.PageId,
                header.PrevPageId,
                header.NextPageId,
                header.SlotCount,
                header.FreeSpaceOffset,
                header.PageType,
                (byte)(header.Flags | (byte)PageState.Dirty),
                shrinkFragmented);
            updatedHeader.WriteTo(buffer);
            return true;
        }

        // Growing: mark old space as fragmented, allocate new space at FreeSpaceOffset
        ushort fragmented = (ushort)(header.FragmentedBytes + oldLen);
        SetSlot(buffer, slotIndex, new PageSlot(0, 0));

        int contiguousFree = GetContiguousFreeSpace(buffer);
        if (contiguousFree < newLen)
        {
            if (contiguousFree + fragmented >= newLen)
            {
                var tempHeader = new PageHeader(
                    header.Checksum, header.PageLsn, header.PageId, header.PrevPageId, header.NextPageId,
                    header.SlotCount, header.FreeSpaceOffset, header.PageType, header.Flags, fragmented);
                tempHeader.WriteTo(buffer);

                Defragment(buffer);
                header = PageHeader.ReadFrom(buffer);
                fragmented = 0;
            }
            else
            {
                // Restore old slot if expansion fails
                SetSlot(buffer, slotIndex, slot);
                return false;
            }
        }

        ushort newFreeOffset = (ushort)(header.FreeSpaceOffset - newLen);
        newRecordData.CopyTo(buffer.Slice(newFreeOffset, newLen));
        SetSlot(buffer, slotIndex, new PageSlot(newFreeOffset, (ushort)newLen));

        var finalHeader = new PageHeader(
            header.Checksum,
            header.PageLsn,
            header.PageId,
            header.PrevPageId,
            header.NextPageId,
            header.SlotCount,
            newFreeOffset,
            header.PageType,
            (byte)(header.Flags | (byte)PageState.Dirty),
            fragmented);
        finalHeader.WriteTo(buffer);
        return true;
    }

    public static bool DeleteRecord(Span<byte> buffer, ushort slotIndex)
    {
        var header = PageHeader.ReadFrom(buffer);
        if (slotIndex >= header.SlotCount)
            return false;

        var slot = GetSlot(buffer, slotIndex);
        if (slot.IsDeleted)
            return false;

        // Mark slot deleted (tombstone)
        SetSlot(buffer, slotIndex, new PageSlot(0, 0));

        ushort newFragmented = (ushort)(header.FragmentedBytes + slot.Length);
        var updatedHeader = new PageHeader(
            header.Checksum,
            header.PageLsn,
            header.PageId,
            header.PrevPageId,
            header.NextPageId,
            header.SlotCount,
            header.FreeSpaceOffset,
            header.PageType,
            (byte)(header.Flags | (byte)PageState.Dirty),
            newFragmented);
        updatedHeader.WriteTo(buffer);
        return true;
    }

    /// <summary>
    /// Defragments the page by compacting all active records to the high end of the page,
    /// eliminating fragmentation and consolidating free space.
    /// </summary>
    public static void Defragment(Span<byte> buffer)
    {
        var header = PageHeader.ReadFrom(buffer);
        if (header.SlotCount == 0 || header.FragmentedBytes == 0)
            return;

        // Allocate temporary scratch space on stack/heap
        Span<byte> temp = stackalloc byte[PageSize];
        buffer.CopyTo(temp);

        ushort currentOffset = PageSize;

        for (ushort i = 0; i < header.SlotCount; i++)
        {
            var slot = GetSlot(temp, i);
            if (slot.IsDeleted)
                continue;

            currentOffset -= slot.Length;
            temp.Slice(slot.Offset, slot.Length).CopyTo(buffer.Slice(currentOffset, slot.Length));
            SetSlot(buffer, i, new PageSlot(currentOffset, slot.Length));
        }

        var defragHeader = new PageHeader(
            header.Checksum,
            header.PageLsn,
            header.PageId,
            header.PrevPageId,
            header.NextPageId,
            header.SlotCount,
            currentOffset,
            header.PageType,
            (byte)(header.Flags | (byte)PageState.Dirty),
            fragmentedBytes: 0);
        defragHeader.WriteTo(buffer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PageSlot GetSlot(ReadOnlySpan<byte> buffer, ushort slotIndex)
    {
        int slotOffset = HeaderSize + (slotIndex * PageSlot.Size);
        return PageSlot.ReadFrom(buffer.Slice(slotOffset, PageSlot.Size));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetSlot(Span<byte> buffer, ushort slotIndex, PageSlot slot)
    {
        int slotOffset = HeaderSize + (slotIndex * PageSlot.Size);
        slot.WriteTo(buffer.Slice(slotOffset, PageSlot.Size));
    }

    public static uint ComputeChecksum(ReadOnlySpan<byte> buffer)
    {
        // CRC32C over page body excluding the first 4 bytes (the Checksum field itself)
        return Crc32C.Compute(buffer[4..PageSize]);
    }

    public static void UpdateChecksum(Span<byte> buffer)
    {
        uint crc = ComputeChecksum(buffer);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer[0..4], crc);
    }

    public static bool VerifyChecksum(ReadOnlySpan<byte> buffer, out uint expected, out uint actual)
    {
        expected = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..4]);
        actual = ComputeChecksum(buffer);
        return expected == actual;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MarkDirty(Span<byte> buffer)
    {
        buffer[29] |= (byte)PageState.Dirty;
    }
}
