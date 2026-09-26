using System.Text;
using Hyperion.Lsm;
using Xunit;

namespace Hyperion.Lsm.Tests;

public class LsmEngineTests : IDisposable
{
    private readonly string _testDir;

    public LsmEngineTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "hyperion_lsm_engine_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
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
    public void LsmEngine_PutGetDelete_AcrossFlushesAndMemory()
    {
        var options = new LsmOptions { MemTableSizeBytes = 1024 * 1024 };
        using (var engine = new LsmEngine(_testDir, options))
        {
            byte[] k1 = Encoding.UTF8.GetBytes("user:101");
            byte[] v1 = Encoding.UTF8.GetBytes("Alice");

            byte[] k2 = Encoding.UTF8.GetBytes("user:102");
            byte[] v2 = Encoding.UTF8.GetBytes("Bob");

            engine.Put(k1, v1);
            engine.Put(k2, v2);

            // Read from active MemTable
            Assert.Equal("Alice", Encoding.UTF8.GetString(engine.Get(k1)!));
            Assert.Equal("Bob", Encoding.UTF8.GetString(engine.Get(k2)!));

            // Force flush to L0 SSTable
            engine.FlushMemTable();

            // Read from L0 SSTable on disk
            Assert.Equal("Alice", Encoding.UTF8.GetString(engine.Get(k1)!));
            Assert.Equal("Bob", Encoding.UTF8.GetString(engine.Get(k2)!));

            // Delete Alice (tombstone)
            engine.Delete(k1);
            Assert.Null(engine.Get(k1));

            // Overwrite Bob in active memory
            byte[] v2Updated = Encoding.UTF8.GetBytes("Robert");
            engine.Put(k2, v2Updated);

            // Memory must mask disk!
            Assert.Equal("Robert", Encoding.UTF8.GetString(engine.Get(k2)!));
        }

        // Reopen database from disk and verify persistence
        using (var engine2 = new LsmEngine(_testDir, options))
        {
            byte[] k2 = Encoding.UTF8.GetBytes("user:102");
            Assert.Equal("Robert", Encoding.UTF8.GetString(engine2.Get(k2)!));
        }
    }

    [Fact]
    public void LsmEngine_LeveledCompaction_MergesL0FilesIntoL1()
    {
        var options = new LsmOptions
        {
            MemTableSizeBytes = 500, // Small threshold to trigger flushes quickly
            CompactionStrategy = new LeveledCompactionStrategy()
        };

        using var engine = new LsmEngine(_testDir, options);

        // Generate 4 separate flushes to trigger L0 threshold compaction
        for (int batch = 0; batch < 4; batch++)
        {
            for (int i = 0; i < 20; i++)
            {
                byte[] key = Encoding.UTF8.GetBytes($"compaction_key_{batch * 20 + i:D4}");
                byte[] val = Encoding.UTF8.GetBytes($"batch_{batch}_val_{i}");
                engine.Put(key, val);
            }
            engine.FlushMemTable();
        }

        // Verify all 80 keys are readable
        for (int i = 0; i < 80; i++)
        {
            byte[] key = Encoding.UTF8.GetBytes($"compaction_key_{i:D4}");
            var val = engine.Get(key);
            Assert.NotNull(val);
        }
    }

    [Fact]
    public void LsmEngine_Scan_MergesMemoryAndDiskVersionsCorrectly()
    {
        var options = new LsmOptions { MemTableSizeBytes = 1024 * 1024 };
        using var engine = new LsmEngine(_testDir, options);

        // Put keys on disk
        engine.Put(Encoding.UTF8.GetBytes("item:1"), Encoding.UTF8.GetBytes("V1_Disk"));
        engine.Put(Encoding.UTF8.GetBytes("item:2"), Encoding.UTF8.GetBytes("V2_Disk"));
        engine.Put(Encoding.UTF8.GetBytes("item:3"), Encoding.UTF8.GetBytes("V3_Disk"));
        engine.FlushMemTable();

        // Update item:2 in memory, delete item:3 in memory, add item:4 in memory
        engine.Put(Encoding.UTF8.GetBytes("item:2"), Encoding.UTF8.GetBytes("V2_MemoryNew"));
        engine.Delete(Encoding.UTF8.GetBytes("item:3"));
        engine.Put(Encoding.UTF8.GetBytes("item:4"), Encoding.UTF8.GetBytes("V4_MemoryNew"));

        var scan = engine.Scan(null, null);

        // Expected: item:1, item:2 (new), item:4 (item:3 deleted)
        Assert.Equal(3, scan.Count);
        Assert.Equal("item:1", Encoding.UTF8.GetString(scan[0].Key));
        Assert.Equal("V1_Disk", Encoding.UTF8.GetString(scan[0].Value));

        Assert.Equal("item:2", Encoding.UTF8.GetString(scan[1].Key));
        Assert.Equal("V2_MemoryNew", Encoding.UTF8.GetString(scan[1].Value));

        Assert.Equal("item:4", Encoding.UTF8.GetString(scan[2].Key));
        Assert.Equal("V4_MemoryNew", Encoding.UTF8.GetString(scan[2].Value));
    }
}
