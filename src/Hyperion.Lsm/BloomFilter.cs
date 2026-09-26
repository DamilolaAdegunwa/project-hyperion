using System.Buffers.Binary;
using Hyperion.Core.Checksum;

namespace Hyperion.Lsm;

/// <summary>
/// Probabilistic Bloom filter using Kirsch-Mitzenmacher double hashing over 32-bit seeds.
/// Eliminates disk I/O on negative point lookups with ~1% false positive rate (10 bits/key).
/// </summary>
public sealed class BloomFilter
{
    private readonly byte[] _bits;
    private readonly int _bitCount;
    private readonly int _hashFunctions;

    public int BitCount => _bitCount;
    public int HashFunctions => _hashFunctions;
    public int ByteSize => _bits.Length;

    public BloomFilter(int expectedEntries, double falsePositiveRate = 0.01)
    {
        if (expectedEntries <= 0) expectedEntries = 1;
        if (falsePositiveRate <= 0 || falsePositiveRate >= 1) falsePositiveRate = 0.01;

        // m = - (n * ln(p)) / (ln(2)^2)
        double m = -(expectedEntries * Math.Log(falsePositiveRate)) / Math.Pow(Math.Log(2), 2);
        _bitCount = Math.Max(64, (int)Math.Ceiling(m));

        // k = (m / n) * ln(2)
        double k = (_bitCount / (double)expectedEntries) * Math.Log(2);
        _hashFunctions = Math.Clamp((int)Math.Round(k), 1, 30);

        int byteCount = (_bitCount + 7) / 8;
        _bits = new byte[byteCount];
    }

    private BloomFilter(byte[] bits, int bitCount, int hashFunctions)
    {
        _bits = bits;
        _bitCount = bitCount;
        _hashFunctions = hashFunctions;
    }

    public void Add(ReadOnlySpan<byte> key)
    {
        uint h1 = Crc32C.Compute(0x12345678, key);
        uint h2 = Crc32C.Compute(0x9ABCDEF0, key);
        if (h2 == 0) h2 = 1;

        for (int i = 0; i < _hashFunctions; i++)
        {
            uint combined = h1 + ((uint)i * h2);
            int bitIndex = (int)(combined % (uint)_bitCount);
            _bits[bitIndex / 8] |= (byte)(1 << (bitIndex % 8));
        }
    }

    public bool MayContain(ReadOnlySpan<byte> key)
    {
        uint h1 = Crc32C.Compute(0x12345678, key);
        uint h2 = Crc32C.Compute(0x9ABCDEF0, key);
        if (h2 == 0) h2 = 1;

        for (int i = 0; i < _hashFunctions; i++)
        {
            uint combined = h1 + ((uint)i * h2);
            int bitIndex = (int)(combined % (uint)_bitCount);
            if ((_bits[bitIndex / 8] & (1 << (bitIndex % 8))) == 0)
            {
                return false; // Definitely not present
            }
        }

        return true; // May be present
    }

    public void Serialize(Span<byte> destination)
    {
        // Layout: [CRC: 4B] [BitCount: 4B] [HashFunctions: 4B] [BitArray: N bytes]
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..8], _bitCount);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..12], _hashFunctions);
        _bits.CopyTo(destination[12..]);

        uint crc = Crc32C.Compute(destination[4..(12 + _bits.Length)]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], crc);
    }

    public static BloomFilter Deserialize(ReadOnlySpan<byte> source)
    {
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]);
        int bitCount = BinaryPrimitives.ReadInt32LittleEndian(source[4..8]);
        int hashFunctions = BinaryPrimitives.ReadInt32LittleEndian(source[8..12]);

        int byteLen = (bitCount + 7) / 8;
        uint actualCrc = Crc32C.Compute(source.Slice(4, 8 + byteLen));
        if (storedCrc != actualCrc)
            throw new InvalidOperationException("Bloom filter checksum mismatch");

        byte[] bits = source.Slice(12, byteLen).ToArray();
        return new BloomFilter(bits, bitCount, hashFunctions);
    }
}
