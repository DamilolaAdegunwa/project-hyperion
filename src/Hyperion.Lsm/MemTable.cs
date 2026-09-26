namespace Hyperion.Lsm;

public sealed class MemTable : IDisposable
{
    private readonly SkipList _skipList;
    public ulong Id { get; }
    public long ApproximateSizeBytes => _skipList.ApproximateSizeBytes;
    public int Count => _skipList.Count;

    public MemTable(ulong id)
    {
        Id = id;
        _skipList = new SkipList();
    }

    public void Put(byte[] key, byte[] value, ulong sequenceNumber)
    {
        var entry = LsmEntry.CreatePut(key, value, sequenceNumber);
        _skipList.Insert(entry);
    }

    public void Delete(byte[] key, ulong sequenceNumber)
    {
        var entry = LsmEntry.CreateTombstone(key, sequenceNumber);
        _skipList.Insert(entry);
    }

    public bool TryGet(ReadOnlySpan<byte> key, out LsmEntry? entry)
    {
        return _skipList.TryFind(key, out entry);
    }

    public List<LsmEntry> Scan(ReadOnlySpan<byte> startKey, ReadOnlySpan<byte> endKey)
    {
        return _skipList.Scan(startKey, endKey);
    }

    public IEnumerable<LsmEntry> GetAllEntries() => _skipList;

    public void Dispose()
    {
        _skipList.Dispose();
    }
}
