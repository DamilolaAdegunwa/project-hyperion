using System.Text;
using Hyperion.Lsm;
using Xunit;

namespace Hyperion.Lsm.Tests;

public class SSTableTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _sstPath;

    public SSTableTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "hyperion_sstable_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _sstPath = Path.Combine(_testDir, "test.sst");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SSTable_WriteAndRead_PointLookupsAndScans()
    {
        int keyCount = 200;
        using (var writer = new SSTableWriter(_sstPath, keyCount))
        {
            for (int i = 0; i < keyCount; i++)
            {
                byte[] key = Encoding.UTF8.GetBytes($"key_{i:D4}");
                byte[] val = Encoding.UTF8.GetBytes($"value_payload_{i}");
                writer.Add(LsmEntry.CreatePut(key, val, (ulong)i));
            }
            writer.Finish();
        }

        using (var reader = new SSTableReader(_sstPath))
        {
            Assert.Equal((ulong)keyCount, reader.TotalKeyCount);

            // 1. Verify point lookups
            for (int i = 0; i < keyCount; i++)
            {
                byte[] key = Encoding.UTF8.GetBytes($"key_{i:D4}");
                Assert.True(reader.TryGet(key, out var entry));
                Assert.NotNull(entry);
                Assert.Equal($"value_payload_{i}", Encoding.UTF8.GetString(entry.Value));
            }

            // 2. Missing key should be eliminated
            Assert.False(reader.TryGet(Encoding.UTF8.GetBytes("key_9999"), out _));

            // 3. Range scan
            byte[] start = Encoding.UTF8.GetBytes("key_0050");
            byte[] end = Encoding.UTF8.GetBytes("key_0060");
            var scan = reader.Scan(start, end);

            Assert.Equal(10, scan.Count);
            Assert.Equal("key_0050", Encoding.UTF8.GetString(scan[0].Key));
            Assert.Equal("key_0059", Encoding.UTF8.GetString(scan[^1].Key));
        }
    }
}
