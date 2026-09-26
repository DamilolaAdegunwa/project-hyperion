using System.Collections.Concurrent;

namespace Hyperion.Lsm;

public sealed class LsmOptions
{
    public long MemTableSizeBytes { get; set; } = 2 * 1024 * 1024; // 2 MB
    public ICompactionStrategy CompactionStrategy { get; set; } = new LeveledCompactionStrategy();
}

public sealed class LsmEngine : IDisposable
{
    private readonly string _dbDirectory;
    private readonly LsmOptions _options;
    private readonly Manifest _manifest;
    private readonly object _writeLock = new();
    private readonly object _compactionLock = new();

    private MemTable _activeMemTable;
    private readonly ConcurrentQueue<MemTable> _immutableMemTables;
    private ulong _currentSequenceNumber;
    private bool _disposed;

    public LsmEngine(string dbDirectory, LsmOptions? options = null)
    {
        _dbDirectory = dbDirectory;
        _options = options ?? new LsmOptions();

        if (!Directory.Exists(_dbDirectory))
        {
            Directory.CreateDirectory(_dbDirectory);
        }

        _manifest = new Manifest(_dbDirectory);
        _activeMemTable = new MemTable(_manifest.AllocateFileNumber());
        _immutableMemTables = new ConcurrentQueue<MemTable>();
        _currentSequenceNumber = 1;
    }

    public void Put(byte[] key, byte[] value)
    {
        lock (_writeLock)
        {
            ulong seq = ++_currentSequenceNumber;
            _activeMemTable.Put(key, value, seq);

            if (_activeMemTable.ApproximateSizeBytes >= _options.MemTableSizeBytes)
            {
                FlushMemTableInternal();
            }
        }
    }

    public void Delete(byte[] key)
    {
        lock (_writeLock)
        {
            ulong seq = ++_currentSequenceNumber;
            _activeMemTable.Delete(key, seq);

            if (_activeMemTable.ApproximateSizeBytes >= _options.MemTableSizeBytes)
            {
                FlushMemTableInternal();
            }
        }
    }

    public byte[]? Get(byte[] key)
    {
        // 1. Search Active MemTable
        if (_activeMemTable.TryGet(key, out var activeEntry) && activeEntry != null)
        {
            return activeEntry.IsTombstone ? null : activeEntry.Value;
        }

        // 2. Search Immutable MemTables
        foreach (var immut in _immutableMemTables.Reverse())
        {
            if (immut.TryGet(key, out var immutEntry) && immutEntry != null)
            {
                return immutEntry.IsTombstone ? null : immutEntry.Value;
            }
        }

        // 3. Search On-Disk SSTables via Manifest
        var state = _manifest.State;

        // Level 0 (newest files first)
        if (state.Levels.TryGetValue(0, out var l0Files))
        {
            for (int i = l0Files.Count - 1; i >= 0; i--)
            {
                string path = Path.Combine(_dbDirectory, l0Files[i].FileName);
                if (!File.Exists(path)) continue;

                using var reader = new SSTableReader(path);
                if (reader.TryGet(key, out var entry) && entry != null)
                {
                    return entry.IsTombstone ? null : entry.Value;
                }
            }
        }

        // Level 1+ (Disjoint key ranges)
        foreach (var kvp in state.Levels.OrderBy(k => k.Key))
        {
            int level = kvp.Key;
            if (level == 0) continue;

            var files = kvp.Value;
            foreach (var file in files)
            {
                if (ByteArrayComparer.Instance.Compare(key, file.SmallestKey) >= 0 &&
                    ByteArrayComparer.Instance.Compare(key, file.LargestKey) <= 0)
                {
                    string path = Path.Combine(_dbDirectory, file.FileName);
                    if (!File.Exists(path)) continue;

                    using var reader = new SSTableReader(path);
                    if (reader.TryGet(key, out var entry) && entry != null)
                    {
                        return entry.IsTombstone ? null : entry.Value;
                    }
                }
            }
        }

        return null;
    }

    public List<LsmEntry> Scan(byte[]? startKey, byte[]? endKey)
    {
        var merged = new Dictionary<byte[], LsmEntry>(ByteArrayEqualityComparer.Instance);
        var startSpan = startKey ?? ReadOnlySpan<byte>.Empty;
        var endSpan = endKey ?? ReadOnlySpan<byte>.Empty;

        // Collect from active memtable
        foreach (var entry in _activeMemTable.Scan(startSpan, endSpan))
        {
            if (!merged.TryGetValue(entry.Key, out var existing) || existing.SequenceNumber < entry.SequenceNumber)
            {
                merged[entry.Key] = entry;
            }
        }

        // Collect from immutable memtables
        foreach (var immut in _immutableMemTables)
        {
            foreach (var entry in immut.Scan(startSpan, endSpan))
            {
                if (!merged.TryGetValue(entry.Key, out var existing) || existing.SequenceNumber < entry.SequenceNumber)
                {
                    merged[entry.Key] = entry;
                }
            }
        }

        // Collect from all SSTables
        var state = _manifest.State;
        foreach (var kvp in state.Levels)
        {
            foreach (var file in kvp.Value)
            {
                string path = Path.Combine(_dbDirectory, file.FileName);
                if (!File.Exists(path)) continue;

                using var reader = new SSTableReader(path);
                foreach (var entry in reader.Scan(startSpan, endSpan))
                {
                    if (!merged.TryGetValue(entry.Key, out var existing) || existing.SequenceNumber < entry.SequenceNumber)
                    {
                        merged[entry.Key] = entry;
                    }
                }
            }
        }

        // Filter out tombstones and sort by key
        var result = merged.Values
            .Where(e => !e.IsTombstone)
            .OrderBy(e => e.Key, ByteArrayComparer.Instance)
            .ToList();

        return result;
    }

    public void FlushMemTable()
    {
        lock (_writeLock)
        {
            FlushMemTableInternal();
        }
    }

    private void FlushMemTableInternal()
    {
        if (_activeMemTable.Count == 0)
            return;

        var entries = _activeMemTable.GetAllEntries().OrderBy(e => e).ToList();
        if (entries.Count == 0)
            return;

        ulong fileNumber = _manifest.AllocateFileNumber();
        string fileName = $"{fileNumber:D6}.sst";
        string filePath = Path.Combine(_dbDirectory, fileName);

        using (var writer = new SSTableWriter(filePath, entries.Count))
        {
            foreach (var entry in entries)
            {
                writer.Add(entry);
            }
            writer.Finish();
        }

        var fileInfo = new FileInfo(filePath);
        var meta = new FileMetadata
        {
            FileNumber = fileNumber,
            FileName = fileName,
            SmallestKeyBase64 = Convert.ToBase64String(entries[0].Key),
            LargestKeyBase64 = Convert.ToBase64String(entries[^1].Key),
            FileSizeBytes = fileInfo.Length
        };

        var edit = new VersionEdit
        {
            NextFileNumber = fileNumber + 1,
            AddedFiles = new() { (0, meta) }
        };

        _manifest.ApplyVersionEdit(edit);

        // Reset active MemTable
        _activeMemTable = new MemTable(_manifest.AllocateFileNumber());

        // Check if compaction is needed
        TriggerCompaction();
    }

    public void TriggerCompaction()
    {
        lock (_compactionLock)
        {
            var task = _options.CompactionStrategy.PickCompaction(_manifest.State);
            if (task == null) return;

            // Merge source and target files
            var allInputs = task.SourceFiles.Concat(task.TargetOverlappingFiles).ToList();
            if (allInputs.Count == 0) return;

            var mergedEntries = new Dictionary<byte[], LsmEntry>(ByteArrayEqualityComparer.Instance);

            foreach (var file in allInputs)
            {
                string path = Path.Combine(_dbDirectory, file.FileName);
                if (!File.Exists(path)) continue;

                using var reader = new SSTableReader(path);
                foreach (var entry in reader.Scan(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty))
                {
                    if (!mergedEntries.TryGetValue(entry.Key, out var existing) || existing.SequenceNumber < entry.SequenceNumber)
                    {
                        mergedEntries[entry.Key] = entry;
                    }
                }
            }

            var sortedOutputs = mergedEntries.Values
                .Where(e => !task.DropTombstones || !e.IsTombstone)
                .OrderBy(e => e)
                .ToList();

            if (sortedOutputs.Count == 0)
            {
                // Everything was dropped as tombstones! Just delete old files
                var deleteEdit = new VersionEdit
                {
                    DeletedFiles = allInputs.Select(f => (task.SourceLevel, f.FileNumber)).ToList()
                };
                _manifest.ApplyVersionEdit(deleteEdit);
                foreach (var f in allInputs)
                {
                    try { File.Delete(Path.Combine(_dbDirectory, f.FileName)); } catch { }
                }
                return;
            }

            ulong newFileNum = _manifest.AllocateFileNumber();
            string newFileName = $"{newFileNum:D6}.sst";
            string newFilePath = Path.Combine(_dbDirectory, newFileName);

            using (var writer = new SSTableWriter(newFilePath, sortedOutputs.Count))
            {
                foreach (var entry in sortedOutputs)
                {
                    writer.Add(entry);
                }
                writer.Finish();
            }

            var newMeta = new FileMetadata
            {
                FileNumber = newFileNum,
                FileName = newFileName,
                SmallestKeyBase64 = Convert.ToBase64String(sortedOutputs[0].Key),
                LargestKeyBase64 = Convert.ToBase64String(sortedOutputs[^1].Key),
                FileSizeBytes = new FileInfo(newFilePath).Length
            };

            var edit = new VersionEdit
            {
                NextFileNumber = newFileNum + 1,
                AddedFiles = new() { (task.TargetLevel, newMeta) },
                DeletedFiles = task.SourceFiles.Select(f => (task.SourceLevel, f.FileNumber))
                    .Concat(task.TargetOverlappingFiles.Select(f => (task.TargetLevel, f.FileNumber)))
                    .ToList()
            };

            _manifest.ApplyVersionEdit(edit);

            // Clean up old files
            foreach (var f in allInputs)
            {
                try { File.Delete(Path.Combine(_dbDirectory, f.FileName)); } catch { }
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            FlushMemTable();
            _disposed = true;
        }
    }
}

public sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
{
    public static readonly ByteArrayEqualityComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return x.AsSpan().SequenceEqual(y.AsSpan());
    }

    public int GetHashCode(byte[] obj)
    {
        if (obj is null) return 0;
        HashCode hash = new();
        hash.AddBytes(obj);
        return hash.ToHashCode();
    }
}
