using System.Runtime.CompilerServices;

namespace Hyperion.Lsm;

public sealed class ByteArrayComparer : IComparer<byte[]>
{
    public static readonly ByteArrayComparer Instance = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return x.AsSpan().SequenceCompareTo(y.AsSpan());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CompareSpans(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);
}

public sealed class LsmEntry : IComparable<LsmEntry>, IEquatable<LsmEntry>
{
    public byte[] Key { get; }
    public byte[] Value { get; }
    public ulong SequenceNumber { get; }
    public bool IsTombstone { get; }

    public int Size => Key.Length + Value.Length + sizeof(ulong) + sizeof(bool);

    public LsmEntry(byte[] key, byte[]? value, ulong sequenceNumber, bool isTombstone)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Value = value ?? Array.Empty<byte>();
        SequenceNumber = sequenceNumber;
        IsTombstone = isTombstone;
    }

    public static LsmEntry CreatePut(byte[] key, byte[] value, ulong seq) =>
        new(key, value, seq, isTombstone: false);

    public static LsmEntry CreateTombstone(byte[] key, ulong seq) =>
        new(key, null, seq, isTombstone: true);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(LsmEntry? other)
    {
        if (other is null) return 1;
        int cmp = ByteArrayComparer.Instance.Compare(Key, other.Key);
        if (cmp != 0) return cmp;
        return other.SequenceNumber.CompareTo(SequenceNumber);
    }

    public bool Equals(LsmEntry? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return SequenceNumber == other.SequenceNumber &&
               IsTombstone == other.IsTombstone &&
               ByteArrayEqualityComparer.Instance.Equals(Key, other.Key) &&
               ByteArrayEqualityComparer.Instance.Equals(Value, other.Value);
    }

    public override bool Equals(object? obj) => Equals(obj as LsmEntry);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(ByteArrayEqualityComparer.Instance.GetHashCode(Key));
        hash.Add(SequenceNumber);
        hash.Add(IsTombstone);
        return hash.ToHashCode();
    }

    public static bool operator ==(LsmEntry? left, LsmEntry? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    public static bool operator !=(LsmEntry? left, LsmEntry? right) => !(left == right);

    public static bool operator <(LsmEntry? left, LsmEntry? right)
    {
        if (left is null) return right is not null;
        return left.CompareTo(right) < 0;
    }

    public static bool operator <=(LsmEntry? left, LsmEntry? right)
    {
        if (left is null) return true;
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >(LsmEntry? left, LsmEntry? right)
    {
        if (left is null) return false;
        return left.CompareTo(right) > 0;
    }

    public static bool operator >=(LsmEntry? left, LsmEntry? right)
    {
        if (left is null) return right is null;
        return left.CompareTo(right) >= 0;
    }
}
