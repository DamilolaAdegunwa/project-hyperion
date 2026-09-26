using System.Text.Json;
using Hyperion.Core.Checksum;

namespace Hyperion.Lsm;

public sealed class FileMetadata
{
    public ulong FileNumber { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string SmallestKeyBase64 { get; set; } = string.Empty;
    public string LargestKeyBase64 { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    public byte[] SmallestKey => Convert.FromBase64String(SmallestKeyBase64);
    public byte[] LargestKey => Convert.FromBase64String(LargestKeyBase64);
}

public sealed class VersionEdit
{
    public ulong NextFileNumber { get; set; }
    public List<(int Level, FileMetadata Metadata)> AddedFiles { get; set; } = new();
    public List<(int Level, ulong FileNumber)> DeletedFiles { get; set; } = new();
}

public sealed class ManifestState
{
    public ulong NextFileNumber { get; set; } = 1;
    public Dictionary<int, List<FileMetadata>> Levels { get; set; } = new();
}

public sealed class Manifest
{
    private readonly string _manifestPath;
    private readonly object _lock = new();
    private ManifestState _state;

    public ManifestState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public Manifest(string dbDirectory)
    {
        _manifestPath = Path.Combine(dbDirectory, "MANIFEST");
        _state = LoadOrCreate();
    }

    public ulong AllocateFileNumber()
    {
        lock (_lock)
        {
            return _state.NextFileNumber++;
        }
    }

    public void ApplyVersionEdit(VersionEdit edit)
    {
        lock (_lock)
        {
            if (edit.NextFileNumber > _state.NextFileNumber)
            {
                _state.NextFileNumber = edit.NextFileNumber;
            }

            // Remove deleted files
            foreach (var (level, fileNum) in edit.DeletedFiles)
            {
                if (_state.Levels.TryGetValue(level, out var list))
                {
                    list.RemoveAll(f => f.FileNumber == fileNum);
                }
            }

            // Add new files
            foreach (var (level, meta) in edit.AddedFiles)
            {
                if (!_state.Levels.TryGetValue(level, out var list))
                {
                    list = new List<FileMetadata>();
                    _state.Levels[level] = list;
                }
                list.Add(meta);
            }

            // Sort Level 1+ by smallest key
            foreach (var kvp in _state.Levels)
            {
                if (kvp.Key > 0)
                {
                    kvp.Value.Sort((a, b) => ByteArrayComparer.Instance.Compare(a.SmallestKey, b.SmallestKey));
                }
            }

            SaveManifest();
        }
    }

    private void SaveManifest()
    {
        string json = JsonSerializer.Serialize(_state);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);

        byte[] payload = new byte[8 + bytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), bytes.Length);
        bytes.CopyTo(payload.AsSpan(8));

        uint crc = Crc32C.Compute(payload.AsSpan(4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), crc);

        string tempPath = _manifestPath + ".tmp";
        File.WriteAllBytes(tempPath, payload);
        File.Move(tempPath, _manifestPath, overwrite: true);
    }

    private ManifestState LoadOrCreate()
    {
        if (!File.Exists(_manifestPath))
        {
            var initial = new ManifestState();
            _state = initial;
            SaveManifest();
            return initial;
        }

        byte[] payload = File.ReadAllBytes(_manifestPath);
        if (payload.Length < 8)
            return new ManifestState();

        uint storedCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
        uint actualCrc = Crc32C.Compute(payload.AsSpan(4));
        if (storedCrc != actualCrc)
            throw new InvalidOperationException("MANIFEST checksum mismatch");

        int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        string json = System.Text.Encoding.UTF8.GetString(payload, 8, len);
        return JsonSerializer.Deserialize<ManifestState>(json) ?? new ManifestState();
    }
}
