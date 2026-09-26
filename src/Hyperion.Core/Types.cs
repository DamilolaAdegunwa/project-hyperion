using System.Runtime.CompilerServices;

namespace Hyperion.Core;

/// <summary>
/// Represents a unique 32-bit logical page identifier in the storage engine.
/// </summary>
public readonly record struct PageId(uint Value) : IComparable<PageId>
{
    public static readonly PageId Invalid = new(uint.MaxValue);

    public bool IsValid => Value != uint.MaxValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(PageId other) => Value.CompareTo(other.Value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(PageId left, PageId right) => left.Value < right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(PageId left, PageId right) => left.Value <= right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(PageId left, PageId right) => left.Value > right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(PageId left, PageId right) => left.Value >= right.Value;

    public override string ToString() => IsValid ? $"Page({Value})" : "Page(Invalid)";

    public static implicit operator uint(PageId id) => id.Value;
    public static explicit operator PageId(uint value) => new(value);
}

/// <summary>
/// Uniquely identifies a slotted record by its PageId and SlotIndex within that page.
/// </summary>
public readonly record struct RecordId(PageId PageId, ushort SlotIndex) : IComparable<RecordId>
{
    public static readonly RecordId Invalid = new(PageId.Invalid, ushort.MaxValue);

    public bool IsValid => PageId.IsValid && SlotIndex != ushort.MaxValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(RecordId other)
    {
        int cmp = PageId.CompareTo(other.PageId);
        return cmp != 0 ? cmp : SlotIndex.CompareTo(other.SlotIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(RecordId left, RecordId right) => left.CompareTo(right) < 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(RecordId left, RecordId right) => left.CompareTo(right) <= 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(RecordId left, RecordId right) => left.CompareTo(right) > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(RecordId left, RecordId right) => left.CompareTo(right) >= 0;

    public override string ToString() => IsValid ? $"RID({PageId.Value}:{SlotIndex})" : "RID(Invalid)";
}

/// <summary>
/// Represents a 64-bit monotonically increasing Log Sequence Number (LSN) corresponding
/// to physical byte offset in the Write-Ahead Log.
/// </summary>
public readonly record struct Lsn(ulong Value) : IComparable<Lsn>
{
    public static readonly Lsn Invalid = new(0);

    public bool IsValid => Value > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(Lsn other) => Value.CompareTo(other.Value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(Lsn left, Lsn right) => left.Value < right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(Lsn left, Lsn right) => left.Value <= right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(Lsn left, Lsn right) => left.Value > right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(Lsn left, Lsn right) => left.Value >= right.Value;

    public override string ToString() => $"LSN({Value})";

    public static implicit operator ulong(Lsn lsn) => lsn.Value;
    public static explicit operator Lsn(ulong value) => new(value);
}

/// <summary>
/// Monotonically increasing Transaction Identifier.
/// </summary>
public readonly record struct TxId(ulong Value) : IComparable<TxId>
{
    public static readonly TxId Invalid = new(0);

    public bool IsValid => Value > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(TxId other) => Value.CompareTo(other.Value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(TxId left, TxId right) => left.Value < right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(TxId left, TxId right) => left.Value <= right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(TxId left, TxId right) => left.Value > right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(TxId left, TxId right) => left.Value >= right.Value;

    public override string ToString() => $"TxId({Value})";

    public static implicit operator ulong(TxId txId) => txId.Value;
    public static explicit operator TxId(ulong value) => new(value);
}

/// <summary>
/// Node identifier in a distributed cluster.
/// </summary>
public readonly record struct NodeId(uint Value) : IComparable<NodeId>
{
    public static readonly NodeId Invalid = new(0);

    public bool IsValid => Value > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(NodeId other) => Value.CompareTo(other.Value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(NodeId left, NodeId right) => left.Value < right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(NodeId left, NodeId right) => left.Value <= right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(NodeId left, NodeId right) => left.Value > right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(NodeId left, NodeId right) => left.Value >= right.Value;

    public override string ToString() => $"Node({Value})";
}

/// <summary>
/// Partition identifier for sharding / Raft groups.
/// </summary>
public readonly record struct PartitionId(uint Value) : IComparable<PartitionId>
{
    public static readonly PartitionId Invalid = new(0);

    public bool IsValid => Value > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(PartitionId other) => Value.CompareTo(other.Value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(PartitionId left, PartitionId right) => left.Value < right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(PartitionId left, PartitionId right) => left.Value <= right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(PartitionId left, PartitionId right) => left.Value > right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(PartitionId left, PartitionId right) => left.Value >= right.Value;

    public override string ToString() => $"Partition({Value})";
}

/// <summary>
/// Raft logical election term.
/// </summary>
public readonly record struct Term(ulong Value) : IComparable<Term>
{
    public static readonly Term Zero = new(0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(Term other) => Value.CompareTo(other.Value);

    public override string ToString() => $"Term({Value})";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(Term left, Term right) => left.Value < right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(Term left, Term right) => left.Value <= right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(Term left, Term right) => left.Value > right.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(Term left, Term right) => left.Value >= right.Value;
}
