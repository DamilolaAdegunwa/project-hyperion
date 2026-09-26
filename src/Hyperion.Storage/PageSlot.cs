using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hyperion.Storage;

/// <summary>
/// 4-byte slot descriptor stored in the slotted page header array.
/// Points to a record payload at [Offset..Offset+Length].
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
public readonly record struct PageSlot(ushort Offset, ushort Length)
{
    public const int Size = 4;

    public bool IsDeleted => Length == 0 || Offset == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PageSlot ReadFrom(ReadOnlySpan<byte> buffer)
    {
        ushort offset = BinaryPrimitives.ReadUInt16LittleEndian(buffer[0..2]);
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..4]);
        return new PageSlot(offset, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTo(Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[0..2], Offset);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..4], Length);
    }
}
