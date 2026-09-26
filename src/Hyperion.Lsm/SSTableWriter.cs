using System.Buffers.Binary;
using Hyperion.Core.Checksum;

namespace Hyperion.Lsm;

public sealed class SSTableWriter : IDisposable
{
    private const int TargetBlockSize = 4096;
    private readonly string _filePath;
    private readonly FileStream _fileStream;
    private readonly BloomFilter _bloomFilter;
    private readonly List<BlockIndexEntry> _blockIndex;
    private readonly MemoryStream _currentBlock;
    private byte[]? _currentBlockFirstKey;
    private int _currentBlockEntries;
    private ulong _totalKeys;
    private bool _disposed;

    public SSTableWriter(string filePath, int estimatedKeyCount)
    {
        _filePath = filePath;
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        _bloomFilter = new BloomFilter(estimatedKeyCount, 0.01);
        _blockIndex = new List<BlockIndexEntry>();
        _currentBlock = new MemoryStream(TargetBlockSize);
        _currentBlockEntries = 0;
        _totalKeys = 0;
    }

    public void Add(LsmEntry entry)
    {
        _bloomFilter.Add(entry.Key);
        _totalKeys++;

        if (_currentBlockEntries == 0)
        {
            _currentBlockFirstKey = entry.Key;
        }

        // Binary entry layout:
        // [KeyLen: 2B] [ValLen: 2B] [Seq: 8B] [IsTombstone: 1B] [Key] [Val]
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt16LittleEndian(header[0..2], (ushort)entry.Key.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..4], (ushort)entry.Value.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[4..12], entry.SequenceNumber);
        header[12] = entry.IsTombstone ? (byte)1 : (byte)0;

        _currentBlock.Write(header);
        _currentBlock.Write(entry.Key);
        _currentBlock.Write(entry.Value);
        _currentBlockEntries++;

        if (_currentBlock.Length >= TargetBlockSize)
        {
            FlushCurrentBlock();
        }
    }

    private void FlushCurrentBlock()
    {
        if (_currentBlockEntries == 0 || _currentBlockFirstKey == null)
            return;

        // Block trailer: [EntryCount: 2B] [CRC32C: 4B]
        byte[] payload = _currentBlock.ToArray();
        Span<byte> trailer = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(trailer[0..2], (ushort)_currentBlockEntries);
        
        uint crc = Crc32C.Compute(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[2..6], crc);

        long blockOffset = _fileStream.Position;
        _fileStream.Write(payload);
        _fileStream.Write(trailer);

        int totalBlockLength = payload.Length + 6;
        _blockIndex.Add(new BlockIndexEntry(_currentBlockFirstKey, blockOffset, totalBlockLength));

        _currentBlock.SetLength(0);
        _currentBlockEntries = 0;
        _currentBlockFirstKey = null;
    }

    public void Finish()
    {
        FlushCurrentBlock();

        // 1. Write Bloom Filter Block
        byte[] bloomBuffer = new byte[12 + _bloomFilter.ByteSize];
        _bloomFilter.Serialize(bloomBuffer);
        long bloomOffset = _fileStream.Position;
        _fileStream.Write(bloomBuffer);
        int bloomLength = bloomBuffer.Length;

        // 2. Write Block Index Block
        long indexOffset = _fileStream.Position;
        using var indexStream = new MemoryStream();
        Span<byte> entryHeader = stackalloc byte[14];
        foreach (var entry in _blockIndex)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(entryHeader[0..2], (ushort)entry.SeparatorKey.Length);
            BinaryPrimitives.WriteInt64LittleEndian(entryHeader[2..10], entry.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(entryHeader[10..14], entry.Length);
            indexStream.Write(entryHeader);
            indexStream.Write(entry.SeparatorKey);
        }

        byte[] indexPayload = indexStream.ToArray();
        uint indexCrc = Crc32C.Compute(indexPayload);
        byte[] indexCrcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(indexCrcBytes, indexCrc);

        _fileStream.Write(indexPayload);
        _fileStream.Write(indexCrcBytes);
        int indexLength = indexPayload.Length + 4;

        // 3. Write Footer
        var footer = new SSTableFooter(indexOffset, indexLength, bloomOffset, bloomLength, _totalKeys);
        Span<byte> footerBuffer = stackalloc byte[SSTableFooter.Size];
        footer.WriteTo(footerBuffer);
        _fileStream.Write(footerBuffer);

        _fileStream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _currentBlock.Dispose();
            _fileStream.Dispose();
            _disposed = true;
        }
    }
}
