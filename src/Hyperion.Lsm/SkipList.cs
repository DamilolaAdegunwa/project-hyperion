using System.Collections;

namespace Hyperion.Lsm;

public sealed class SkipList : IEnumerable<LsmEntry>, IDisposable
{
    private const int MaxLevel = 16;
    private const double Probability = 0.5;

    private sealed class Node
    {
        public LsmEntry Entry { get; }
        public Node?[] Next { get; }

        public Node(LsmEntry entry, int level)
        {
            Entry = entry;
            Next = new Node?[level];
        }
    }

    private readonly Node _head;
    private int _currentLevel;
    private int _count;
    private long _approximateSize;
    private readonly Random _random;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public long ApproximateSizeBytes
    {
        get
        {
            _lock.EnterReadLock();
            try { return _approximateSize; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public SkipList(int? seed = null)
    {
        _head = new Node(new LsmEntry(Array.Empty<byte>(), null, 0, false), MaxLevel);
        _currentLevel = 1;
        _count = 0;
        _approximateSize = 0;
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    private int GenerateRandomLevel()
    {
        int level = 1;
        while (_random.NextDouble() < Probability && level < MaxLevel)
        {
            level++;
        }
        return level;
    }

    public void Insert(LsmEntry entry)
    {
        _lock.EnterWriteLock();
        try
        {
            Node?[] update = new Node?[MaxLevel];
            Node current = _head;

            for (int i = _currentLevel - 1; i >= 0; i--)
            {
                while (current.Next[i] != null && ByteArrayComparer.Instance.Compare(current.Next[i]!.Entry.Key, entry.Key) < 0)
                {
                    current = current.Next[i]!;
                }
                update[i] = current;
            }

            int newLevel = GenerateRandomLevel();
            if (newLevel > _currentLevel)
            {
                for (int i = _currentLevel; i < newLevel; i++)
                {
                    update[i] = _head;
                }
                _currentLevel = newLevel;
            }

            var newNode = new Node(entry, newLevel);
            for (int i = 0; i < newLevel; i++)
            {
                newNode.Next[i] = update[i]!.Next[i];
                update[i]!.Next[i] = newNode;
            }

            _count++;
            _approximateSize += entry.Size;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool TryFind(ReadOnlySpan<byte> key, out LsmEntry? result)
    {
        _lock.EnterReadLock();
        try
        {
            Node current = _head;
            for (int i = _currentLevel - 1; i >= 0; i--)
            {
                while (current.Next[i] != null && ByteArrayComparer.CompareSpans(current.Next[i]!.Entry.Key, key) < 0)
                {
                    current = current.Next[i]!;
                }
            }

            current = current.Next[0]!;
            if (current != null && ByteArrayComparer.CompareSpans(current.Entry.Key, key) == 0)
            {
                result = current.Entry;
                return true;
            }

            result = null;
            return false;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public List<LsmEntry> Scan(ReadOnlySpan<byte> startKey, ReadOnlySpan<byte> endKey)
    {
        _lock.EnterReadLock();
        try
        {
            var results = new List<LsmEntry>();
            Node current = _head;

            if (!startKey.IsEmpty)
            {
                for (int i = _currentLevel - 1; i >= 0; i--)
                {
                    while (current.Next[i] != null && ByteArrayComparer.CompareSpans(current.Next[i]!.Entry.Key, startKey) < 0)
                    {
                        current = current.Next[i]!;
                    }
                }
            }

            current = current.Next[0]!;
            while (current != null)
            {
                if (!endKey.IsEmpty && ByteArrayComparer.CompareSpans(current.Entry.Key, endKey) >= 0)
                {
                    break;
                }

                results.Add(current.Entry);
                current = current.Next[0]!;
            }

            return results;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IEnumerator<LsmEntry> GetEnumerator()
    {
        _lock.EnterReadLock();
        try
        {
            var list = new List<LsmEntry>(_count);
            Node? current = _head.Next[0];
            while (current != null)
            {
                list.Add(current.Entry);
                current = current.Next[0];
            }
            return list.GetEnumerator();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        _lock.Dispose();
    }
}
