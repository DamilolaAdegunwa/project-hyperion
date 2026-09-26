using System.Text;
using Hyperion.Core;
using Hyperion.Storage;
using Hyperion.Wal;
using Xunit;

namespace Hyperion.Recovery.Tests;

public class AriesRecoveryTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly string _walPath;

    public AriesRecoveryTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "hyperion_recovery_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "hyperion.hdb");
        _walPath = Path.Combine(_testDir, "hyperion.wal");
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
    public void Recovery_CommittedTransaction_IsRedoneAndPersisted()
    {
        PageId targetPageId;

        // Step 1: Pre-crash execution: Transaction 1 inserts record, commits, WAL is flushed, but page in buffer pool NOT flushed to disk
        using (var fileManager = new DiskFileManager(_dbPath))
        using (var bpm = new BufferPoolManager(4, fileManager))
        using (var walWriter = new LogWriter(_walPath))
        {
            var frame = bpm.NewPage(out targetPageId);
            Assert.NotNull(frame);

            var tx1 = new TxId(101);
            var lsnBegin = walWriter.Append(LogRecord.CreateBegin(tx1, Lsn.Invalid));

            byte[] recordBytes = Encoding.UTF8.GetBytes("Durably Committed Account Balance = $10,000");
            Assert.True(Page.TryInsertRecord(frame.Data, recordBytes, out ushort slot));

            var lsnInsert = walWriter.Append(LogRecord.CreateInsert(tx1, lsnBegin, targetPageId, slot, recordBytes));
            Page.SetLsn(frame.Data, lsnInsert);
            frame.PageLsn = lsnInsert;

            var lsnCommit = walWriter.Append(LogRecord.CreateCommit(tx1, lsnInsert));
            walWriter.Flush(); // Durable WAL!

            // SIMULATE CRASH:
            bpm.SimulateCrash();
            // Do NOT unpin with flush, do NOT flush buffer pool.
            // Page on disk does NOT contain the record yet!
        }

        // Verify disk page prior to recovery does NOT have the record
        using (var verifyFileManager = new DiskFileManager(_dbPath))
        {
            byte[] diskPage = new byte[Page.PageSize];
            verifyFileManager.ReadPage(targetPageId, diskPage);
            // On disk, slotCount should be 0 because it was never flushed
            Assert.Equal(0, Page.GetHeader(diskPage).SlotCount);
        }

        // Step 2: Crash Recovery execution
        using (var fileManager = new DiskFileManager(_dbPath))
        using (var bpm = new BufferPoolManager(4, fileManager))
        using (var walWriter = new LogWriter(_walPath))
        {
            var chk = new CheckpointManager(_testDir);
            var recovery = new RecoveryManager(_walPath, chk, bpm, walWriter);

            var result = recovery.Recover();

            Assert.Equal(0, result.ActiveTransactionsFound);
            Assert.True(result.OperationsRedone >= 1);
        }

        // Step 3: Post-recovery verification: page must now contain the committed record
        using (var fileManager = new DiskFileManager(_dbPath))
        using (var bpm = new BufferPoolManager(4, fileManager))
        {
            var recoveredFrame = bpm.FetchPage(targetPageId);
            Assert.NotNull(recoveredFrame);
            Assert.True(Page.TryGetRecord(recoveredFrame.Data, 0, out var recoveredBytes));
            Assert.Equal("Durably Committed Account Balance = $10,000", Encoding.UTF8.GetString(recoveredBytes));
        }
    }

    [Fact]
    public void Recovery_UncommittedTransaction_IsUndoneAndAborted()
    {
        PageId targetPageId;

        // Step 1: Pre-crash execution: Transaction 2 mutates a page, page is evicted/flushed to disk, but transaction never commits before crash
        using (var fileManager = new DiskFileManager(_dbPath))
        using (var bpm = new BufferPoolManager(4, fileManager))
        using (var walWriter = new LogWriter(_walPath))
        {
            bpm.WalFlushCallback = lsn => walWriter.Flush();

            var frame = bpm.NewPage(out targetPageId);
            Assert.NotNull(frame);

            // First write an initial valid record
            byte[] initialBytes = Encoding.UTF8.GetBytes("Initial Valid Balance = $500");
            Assert.True(Page.TryInsertRecord(frame.Data, initialBytes, out ushort slot));
            var lsn0 = walWriter.Append(LogRecord.CreateInsert(new TxId(1), Lsn.Invalid, targetPageId, slot, initialBytes));
            walWriter.Append(LogRecord.CreateCommit(new TxId(1), lsn0));

            // Now Tx 2 starts and updates record to dirty uncommitted value
            var tx2 = new TxId(202);
            var lsnBegin2 = walWriter.Append(LogRecord.CreateBegin(tx2, Lsn.Invalid));

            byte[] dirtyBytes = Encoding.UTF8.GetBytes("HACKED Dirty Uncommitted Balance = $999,999");
            Assert.True(Page.TryUpdateRecord(frame.Data, slot, dirtyBytes));

            var lsnUpdate = walWriter.Append(LogRecord.CreateUpdate(tx2, lsnBegin2, targetPageId, slot, initialBytes, dirtyBytes));
            Page.SetLsn(frame.Data, lsnUpdate);
            frame.PageLsn = lsnUpdate;

            // Page is evicted or flushed to disk with uncommitted data (STEAL policy)
            bpm.UnpinPage(targetPageId, isDirty: true);
            bpm.FlushPage(targetPageId);

            // CRASH: Tx 2 never committed!
        }

        // Verify disk currently has the uncommitted dirty data
        using (var verifyFileManager = new DiskFileManager(_dbPath))
        {
            byte[] diskPage = new byte[Page.PageSize];
            verifyFileManager.ReadPage(targetPageId, diskPage);
            Assert.True(Page.TryGetRecord(diskPage, 0, out var dirtyOnDisk));
            Assert.Equal("HACKED Dirty Uncommitted Balance = $999,999", Encoding.UTF8.GetString(dirtyOnDisk));
        }

        // Step 2: Execute ARIES Recovery
        using (var fileManager = new DiskFileManager(_dbPath))
        using (var bpm = new BufferPoolManager(4, fileManager))
        using (var walWriter = new LogWriter(_walPath))
        {
            var chk = new CheckpointManager(_testDir);
            var recovery = new RecoveryManager(_walPath, chk, bpm, walWriter);

            var result = recovery.Recover();

            Assert.Equal(1, result.ActiveTransactionsFound);
            Assert.Equal(1, result.TransactionsRolledBack);
            Assert.True(result.OperationsUndone >= 1);
        }

        // Step 3: Post-recovery verification: dirty data has been undone back to original balance
        using (var fileManager = new DiskFileManager(_dbPath))
        using (var bpm = new BufferPoolManager(4, fileManager))
        {
            var recoveredFrame = bpm.FetchPage(targetPageId);
            Assert.NotNull(recoveredFrame);
            Assert.True(Page.TryGetRecord(recoveredFrame.Data, 0, out var restoredBytes));
            Assert.Equal("Initial Valid Balance = $500", Encoding.UTF8.GetString(restoredBytes));
        }
    }

    [Fact]
    public void Recovery_CrashDuringUndo_ClrPreventsUndoLoop()
    {
        PageId pageId;
        Lsn lsnIns;
        Lsn lsnUp1;

        // Step 1: Pre-crash setup with uncommitted transaction
        using (var fileManager = new DiskFileManager(_dbPath))
        using (var bpm = new BufferPoolManager(4, fileManager))
        using (var walWriter = new LogWriter(_walPath))
        {
            bpm.WalFlushCallback = lsn => walWriter.Flush();
            var frame = bpm.NewPage(out pageId);
            Assert.NotNull(frame);

            var tx = new TxId(303);
            var lsnBegin = walWriter.Append(LogRecord.CreateBegin(tx, Lsn.Invalid));

            byte[] b1 = Encoding.UTF8.GetBytes("Original Payload");
            Assert.True(Page.TryInsertRecord(frame.Data, b1, out ushort slot));
            lsnIns = walWriter.Append(LogRecord.CreateInsert(tx, lsnBegin, pageId, slot, b1));

            byte[] b2 = Encoding.UTF8.GetBytes("Updated Payload 1");
            Assert.True(Page.TryUpdateRecord(frame.Data, slot, b2));
            lsnUp1 = walWriter.Append(LogRecord.CreateUpdate(tx, lsnIns, pageId, slot, b1, b2));

            byte[] b3 = Encoding.UTF8.GetBytes("Updated Payload 2");
            Assert.True(Page.TryUpdateRecord(frame.Data, slot, b3));
            var lsnUp2 = walWriter.Append(LogRecord.CreateUpdate(tx, lsnUp1, pageId, slot, b2, b3));

            Page.SetLsn(frame.Data, lsnUp2);
            bpm.UnpinPage(pageId, isDirty: true);
            bpm.FlushPage(pageId);
            bpm.SimulateCrash();
        }

        // Step 2: First recovery runs, but we simulate a crash after undoing 1 operation!
        // We write a CLR for lsnUp2 manually to simulate crash midway through undo
        using (var walWriter = new LogWriter(_walPath))
        {
            var clr = LogRecord.CreateClr(
                new TxId(303),
                lsnUp1,
                pageId,
                0,
                lsnIns, // UndoNextLsn points directly to lsnIns, skipping lsnUp1!
                Encoding.UTF8.GetBytes("Updated Payload 1"));
            walWriter.Append(clr);
            walWriter.Flush();
        }

        // Step 3: Second recovery should succeed without getting stuck in an infinite undo loop
        using (var fileManager = new DiskFileManager(_dbPath))
        using (var bpm = new BufferPoolManager(4, fileManager))
        using (var walWriter = new LogWriter(_walPath))
        {
            var chk = new CheckpointManager(_testDir);
            var recovery = new RecoveryManager(_walPath, chk, bpm, walWriter);

            var result = recovery.Recover();
            Assert.True(result.TransactionsRolledBack >= 1 || result.OperationsUndone >= 1);
        }
    }
}
