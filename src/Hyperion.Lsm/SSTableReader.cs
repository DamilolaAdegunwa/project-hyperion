using System.Buffers.Binary;
using Hyperion.Core.Checksum;

namespace Hyperion.Lsm;

public sealed class SSTableReader : IDisposable
{
    private readonly string _filePath;
    private readonly FileStream _fileStream;
    private readonly SSTableFooter _footer;
    private readonly BloomFilter _bloomFilter;
    private readonly List<BlockIndexEntry> _blockIndex;
    private bool _disposed;

    public string FilePath => _filePath;
    public ulong TotalKeyCount => _footer.TotalKeyCount;
    public byte[]? SmallestKey => _blockIndex.Count > 0 ? _blockIndex[0].SeparatorKey : null;
    public byte[]? LargestKey => _blockIndex.Count > 0 ? _blockIndex[^1].SeparatorKey : null;

    public SSTableReader(string filePath)
    {
        _filePath = filePath;
        _fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (_fileStream.Length < SSTableFooter.Size)
            throw new InvalidOperationException("File too small to be a valid SSTable");

        // 1. Read Footer
        Span<byte> footerBuffer = stackalloc byte[SSTableFooter.Size];
        _fileStream.Seek(-SSTableFooter.Size, SeekOrigin.End);
        _fileStream.ReadExactly(footerBuffer);
        _footer = SSTableFooter.ReadFrom(footerBuffer);

        // 2. Read Bloom Filter
        byte[] bloomBuffer = new byte[_footer.BloomLength];
        _fileStream.Seek(_footer.BloomOffset, SeekOrigin.Begin);
        _fileStream.ReadExactly(bloomBuffer);
        _bloomFilter = BloomFilter.Deserialize(bloomBuffer);

        // 3. Read Block Index
        byte[] indexBuffer = new byte[_footer.IndexLength];
        _fileStream.Seek(_footer.IndexOffset, SeekOrigin.Begin);
        _fileStream.ReadExactly(indexBuffer);

        int indexPayloadLen = _footer.IndexLength - 4;
        uint storedIndexCrc = BinaryPrimitives.ReadUInt32LittleEndian(indexBuffer.AsSpan(indexPayloadLen, 4));
        uint actualIndexCrc = Crc32C.Compute(indexBuffer.AsSpan(0, indexPayloadLen));
        if (storedIndexCrc != actualIndexCrc)
            throw new InvalidOperationException("SSTable block index checksum verification failed");

        _blockIndex = new List<BlockIndexEntry>();
        int offset = 0;
        while (offset < indexPayloadLen)
        {
            ushort keyLen = BinaryPrimitives.ReadUInt16LittleEndian(indexBuffer.AsSpan(offset, 2));
            long blockOffset = BinaryPrimitives.ReadInt64LittleEndian(indexBuffer.AsSpan(offset + 2, 8));
            int blockLength = BinaryPrimitives.ReadInt32LittleEndian(indexBuffer.AsSpan(offset + 10, 4));
            byte[] sepKey = indexBuffer.AsSpan(offset + 14, keyLen).ToArray();

            _blockIndex.Add(new BlockIndexEntry(sepKey, blockOffset, blockLength));
            offset += 14 + keyLen;
        }
    }

    public bool TryGet(ReadOnlySpan<byte> key, out LsmEntry? entry)
    {
        entry = null;

        // Step 1: Bloom filter probe (In-memory, zero disk reads on negative hit)
        if (!_bloomFilter.MayContain(key))
            return false;

        // Step 2: Binary search in block index for candidate block
        int blockIdx = FindBlockIndex(key);
        if (blockIdx < 0 || blockIdx >= _blockIndex.Count)
            return false;

        // Step 3: Read candidate block and search within block
        var block = _blockIndex[blockIdx];
        byte[] blockBytes = new byte[block.Length];
        _fileStream.Seek(block.Offset, SeekOrigin.Begin);
        _fileStream.ReadExactly(blockBytes);

        // Verify block checksum
        int payloadLen = block.Length - 6;
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(blockBytes.AsSpan(payloadLen + 2, 4));
        uint actualCrc = Crc32C.Compute(blockBytes.AsSpan(0, payloadLen));
        if (storedCrc != actualCrc)
            throw new InvalidOperationException($"Data block checksum verification failed at offset {block.Offset}");

        ushort entryCount = BinaryPrimitives.ReadUInt16LittleEndian(blockBytes.AsSpan(payloadLen, 2));
        int readOffset = 0;

        for (int i = 0; i < entryCount; i++)
        {
            ushort keyLen = BinaryPrimitives.ReadUInt16LittleEndian(blockBytes.AsSpan(readOffset, 2));
            ushort valLen = BinaryPrimitives.ReadUInt16LittleEndian(blockBytes.AsSpan(readOffset + 2, 2));
            ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(blockBytes.AsSpan(readOffset + 4, 8));
            bool isTombstone = blockBytes[readOffset + 12] == 1;

            var entryKey = blockBytes.AsSpan(readOffset + 13, keyLen);
            int cmp = ByteArrayComparer.CompareSpans(entryKey, key);

            if (cmp == 0)
            {
                byte[] valBytes = valLen > 0 ? blockBytes.AsSpan(readOffset + 13 + keyLen, valLen).ToArray() : Array.Empty<byte>();
                entry = new LsmEntry(entryKey.ToArray(), valBytes, seq, isTombstone);
                return true;
            }

            if (cmp > 0)
            {
                break; // Entries are sorted, cannot be found further
            }

            readOffset += 13 + keyLen + valLen;
        }

        return false;
    }

    public List<LsmEntry> Scan(ReadOnlySpan<byte> startKey, ReadOnlySpan<byte> endKey)
    {
        var results = new List<LsmEntry>();

        foreach (var block in _blockIndex)
        {
            byte[] blockBytes = new byte[block.Length];
            _fileStream.Seek(block.Offset, SeekOrigin.Begin);
            _fileStream.ReadExactly(blockBytes);

            int payloadLen = block.Length - 6;
            ushort entryCount = BinaryPrimitives.ReadUInt16LittleEndian(blockBytes.AsSpan(payloadLen, 2));
            int readOffset = 0;

            for (int i = 0; i < entryCount; i++)
            {
                ushort keyLen = BinaryPrimitives.ReadUInt16LittleEndian(blockBytes.AsSpan(readOffset, 2));
                ushort valLen = BinaryPrimitives.ReadUInt16LittleEndian(blockBytes.AsSpan(readOffset + 2, 2));
                ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(blockBytes.AsSpan(readOffset + 4, 8));
                bool isTombstone = blockBytes[readOffset + 12] == 1;

                var entryKey = blockBytes.AsSpan(readOffset + 13, keyLen);

                if (startKey.IsEmpty || ByteArrayComparer.CompareSpans(entryKey, startKey) >= 0)
                {
                    if (!endKey.IsEmpty && ByteArrayComparer.CompareSpans(entryKey, endKey) >= 0)
                    {
                        return results;
                    }

                    byte[] valBytes = valLen > 0 ? blockBytes.AsSpan(readOffset + 13 + keyLen, valLen).ToArray() : Array.Empty<byte>();
                    results.Add(new LsmEntry(entryKey.ToArray(), valBytes, seq, isTombstone));
                }

                readOffset += 13 + keyLen + valLen;
            }
        }

        return results;
    }

    private int FindBlockIndex(ReadOnlySpan<byte> key)
    {
        int low = 0;
        int high = _blockIndex.Count - 1;
        int candidate = -1;

        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            int cmp = ByteArrayComparer.CompareSpans(_blockIndex[mid].SeparatorKey, key);

            if (cmp <= 0)
            {
                candidate = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return candidate;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _fileStream.Dispose();
            _disposed = true;
        }
    }
}
