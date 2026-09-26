namespace Hyperion.Lsm;

public enum CompactionType
{
    Leveled,
    SizeTiered
}

public sealed class CompactionTask
{
    public int SourceLevel { get; set; }
    public int TargetLevel { get; set; }
    public List<FileMetadata> SourceFiles { get; set; } = new();
    public List<FileMetadata> TargetOverlappingFiles { get; set; } = new();
    public bool DropTombstones { get; set; }
}

public interface ICompactionStrategy
{
    CompactionTask? PickCompaction(ManifestState state);
}

public sealed class LeveledCompactionStrategy : ICompactionStrategy
{
    private const int L0Threshold = 4;

    public CompactionTask? PickCompaction(ManifestState state)
    {
        // 1. Check Level 0 file count trigger
        if (state.Levels.TryGetValue(0, out var l0Files) && l0Files.Count >= L0Threshold)
        {
            var task = new CompactionTask
            {
                SourceLevel = 0,
                TargetLevel = 1,
                SourceFiles = new List<FileMetadata>(l0Files),
                DropTombstones = !state.Levels.ContainsKey(2) || state.Levels[2].Count == 0
            };

            // Find overlapping files in Level 1
            if (state.Levels.TryGetValue(1, out var l1Files))
            {
                byte[] minKey = l0Files.Select(f => f.SmallestKey).Min(ByteArrayComparer.Instance)!;
                byte[] maxKey = l0Files.Select(f => f.LargestKey).Max(ByteArrayComparer.Instance)!;

                foreach (var f1 in l1Files)
                {
                    if (ByteArrayComparer.Instance.Compare(f1.LargestKey, minKey) >= 0 &&
                        ByteArrayComparer.Instance.Compare(f1.SmallestKey, maxKey) <= 0)
                    {
                        task.TargetOverlappingFiles.Add(f1);
                    }
                }
            }

            return task;
        }

        // 2. Check higher levels size ratio trigger (10x fanout)
        foreach (var kvp in state.Levels)
        {
            int level = kvp.Key;
            if (level == 0) continue;

            long totalBytes = kvp.Value.Sum(f => f.FileSizeBytes);
            long maxBytes = (long)Math.Pow(10, level) * 1024 * 1024; // 10MB, 100MB, etc.

            if (totalBytes > maxBytes && kvp.Value.Count > 0)
            {
                var victim = kvp.Value[0];
                var task = new CompactionTask
                {
                    SourceLevel = level,
                    TargetLevel = level + 1,
                    SourceFiles = new List<FileMetadata> { victim },
                    DropTombstones = !state.Levels.ContainsKey(level + 2) || state.Levels[level + 2].Count == 0
                };

                if (state.Levels.TryGetValue(level + 1, out var nextLevelFiles))
                {
                    foreach (var fn in nextLevelFiles)
                    {
                        if (ByteArrayComparer.Instance.Compare(fn.LargestKey, victim.SmallestKey) >= 0 &&
                            ByteArrayComparer.Instance.Compare(fn.SmallestKey, victim.LargestKey) <= 0)
                        {
                            task.TargetOverlappingFiles.Add(fn);
                        }
                    }
                }

                return task;
            }
        }

        return null;
    }
}

public sealed class SizeTieredCompactionStrategy : ICompactionStrategy
{
    private const int MinFilesThreshold = 4;

    public CompactionTask? PickCompaction(ManifestState state)
    {
        foreach (var kvp in state.Levels)
        {
            if (kvp.Value.Count >= MinFilesThreshold)
            {
                return new CompactionTask
                {
                    SourceLevel = kvp.Key,
                    TargetLevel = kvp.Key + 1,
                    SourceFiles = new List<FileMetadata>(kvp.Value.Take(MinFilesThreshold)),
                    DropTombstones = false
                };
            }
        }
        return null;
    }
}
