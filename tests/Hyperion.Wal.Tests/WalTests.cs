using System.Text;
using Hyperion.Core;
using Hyperion.Wal;
using Xunit;

namespace Hyperion.Wal.Tests;

public class WalTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _walPath;

    public WalTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "hyperion_wal_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _walPath = Path.Combine(_testDir, "test.wal");
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
    public void Wal_AppendAndRead_PreservesOrderAndData()
    {
        using (var writer = new LogWriter(_walPath))
        {
            var r1 = LogRecord.CreateBegin(new TxId(1), Lsn.Invalid);
            var lsn1 = writer.Append(r1);

            byte[] afterData = Encoding.UTF8.GetBytes("Tuple value 1");
            var r2 = LogRecord.CreateInsert(new TxId(1), lsn1, new PageId(10), 0, afterData);
            var lsn2 = writer.Append(r2);

            var r3 = LogRecord.CreateCommit(new TxId(1), lsn2);
            var lsn3 = writer.Append(r3);

            writer.Flush();

            Assert.True(lsn1 < lsn2);
            Assert.True(lsn2 < lsn3);
            Assert.Equal(writer.CurrentLsn, writer.FlushedLsn);
        }

        using (var reader = new LogReader(_walPath))
        {
            var read1 = reader.ReadNext();
            Assert.NotNull(read1);
            Assert.Equal(LogRecordType.Begin, read1.Type);
            Assert.Equal(1u, read1.TxId.Value);

            var read2 = reader.ReadNext();
            Assert.NotNull(read2);
            Assert.Equal(LogRecordType.Insert, read2.Type);
            Assert.Equal(10u, read2.PageId.Value);
            Assert.Equal("Tuple value 1", Encoding.UTF8.GetString(read2.AfterImage));

            var read3 = reader.ReadNext();
            Assert.NotNull(read3);
            Assert.Equal(LogRecordType.Commit, read3.Type);

            Assert.Null(reader.ReadNext());
        }
    }

    [Fact]
    public void Wal_CorruptedRecordInMiddle_ThrowsWalCorruptedException()
    {
        using (var writer = new LogWriter(_walPath))
        {
            writer.Append(LogRecord.CreateBegin(new TxId(1), Lsn.Invalid));
            writer.Append(LogRecord.CreateCommit(new TxId(1), new Lsn(1)));
            writer.Flush();
        }

        // Corrupt byte in first record body
        byte[] bytes = File.ReadAllBytes(_walPath);
        bytes[20] ^= 0xAA; // flip bits
        File.WriteAllBytes(_walPath, bytes);

        using var reader = new LogReader(_walPath, tolerateTailCorruption: false);
        Assert.Throws<WalCorruptedException>(() => reader.ReadNext());
    }

    [Fact]
    public void Wal_TornWriteAtTail_ToleratedWhenConfigured()
    {
        using (var writer = new LogWriter(_walPath))
        {
            writer.Append(LogRecord.CreateBegin(new TxId(1), Lsn.Invalid));
            writer.Flush();
        }

        // Append 10 bytes of truncated garbage representing a crashed partial write
        using (var fs = new FileStream(_walPath, FileMode.Append, FileAccess.Write))
        {
            fs.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
            fs.Flush();
        }

        using var reader = new LogReader(_walPath, tolerateTailCorruption: true);
        var r1 = reader.ReadNext();
        Assert.NotNull(r1);
        Assert.Equal(LogRecordType.Begin, r1.Type);

        // Next read encounters truncated garbage at tail, should return null safely without throwing
        var r2 = reader.ReadNext();
        Assert.Null(r2);
    }

    [Fact]
    public void CheckpointManager_SaveAndLoad_PreservesMetadata()
    {
        var mgr = new CheckpointManager(_testDir);
        var info = new CheckpointInfo
        {
            CheckpointLsn = new Lsn(4096),
            TimestampUtc = DateTime.UtcNow,
            ActiveTransactionTable = new() { [100] = 500 },
            DirtyPageTable = new() { [42] = 200 }
        };

        mgr.SaveCheckpoint(info);
        var loaded = mgr.LoadCheckpoint();

        Assert.NotNull(loaded);
        Assert.Equal(new Lsn(4096), loaded.CheckpointLsn);
        Assert.Equal(500u, loaded.ActiveTransactionTable[100]);
        Assert.Equal(200u, loaded.DirtyPageTable[42]);
    }
}
