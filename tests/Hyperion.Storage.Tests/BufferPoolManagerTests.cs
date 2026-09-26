using System.Text;
using Hyperion.Core;
using Hyperion.Storage;
using Xunit;

namespace Hyperion.Storage.Tests;

public class BufferPoolManagerTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _testDbFile;

    public BufferPoolManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "hyperion_storage_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _testDbFile = Path.Combine(_testDir, "test.hdb");
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
    public void BufferPool_NewPageAndFetch_PersistsDataAcrossEviction()
    {
        using var fileManager = new DiskFileManager(_testDbFile);
        using var bpm = new BufferPoolManager(poolSize: 2, fileManager);

        // Allocate Page 0 and write data
        var frame0 = bpm.NewPage(out var page0);
        Assert.NotNull(frame0);
        Assert.Equal(0u, page0.Value);

        Assert.True(Page.TryInsertRecord(frame0.Data, Encoding.UTF8.GetBytes("Record On Page 0"), out _));
        bpm.UnpinPage(page0, isDirty: true);

        // Allocate Page 1
        var frame1 = bpm.NewPage(out var page1);
        Assert.NotNull(frame1);
        Assert.Equal(1u, page1.Value);
        Assert.True(Page.TryInsertRecord(frame1.Data, Encoding.UTF8.GetBytes("Record On Page 1"), out _));
        bpm.UnpinPage(page1, isDirty: true);

        // Allocate Page 2 (Must evict one of the previous unpinned pages!)
        var frame2 = bpm.NewPage(out var page2);
        Assert.NotNull(frame2);
        Assert.Equal(2u, page2.Value);
        Assert.True(Page.TryInsertRecord(frame2.Data, Encoding.UTF8.GetBytes("Record On Page 2"), out _));
        bpm.UnpinPage(page2, isDirty: true);

        // Now re-fetch Page 0 (which was evicted to disk and flushed)
        var refetched0 = bpm.FetchPage(page0);
        Assert.NotNull(refetched0);
        Assert.True(Page.TryGetRecord(refetched0.Data, 0, out var rec0));
        Assert.Equal("Record On Page 0", Encoding.UTF8.GetString(rec0));
        bpm.UnpinPage(page0, isDirty: false);
    }

    [Fact]
    public void BufferPool_SaturatedPinnedPages_ReturnsNull()
    {
        using var fileManager = new DiskFileManager(_testDbFile);
        using var bpm = new BufferPoolManager(poolSize: 2, fileManager);

        var frame0 = bpm.NewPage(out var p0);
        var frame1 = bpm.NewPage(out var p1);
        Assert.NotNull(frame0);
        Assert.NotNull(frame1);

        // Both frames are pinned (PinCount = 1). Allocating a 3rd should fail gracefully (return null)
        var frame2 = bpm.NewPage(out var p2);
        Assert.Null(frame2);
        Assert.False(p2.IsValid);

        // Unpin one page, now allocation should succeed
        bpm.UnpinPage(p0, isDirty: false);
        frame2 = bpm.NewPage(out p2);
        Assert.NotNull(frame2);
        Assert.True(p2.IsValid);
        bpm.UnpinPage(p2, isDirty: false);
        bpm.UnpinPage(p1, isDirty: false);
    }

    [Fact]
    public void BufferPool_EnforcesWalInvariant_BeforeDirtyPageFlush()
    {
        using var fileManager = new DiskFileManager(_testDbFile);
        using var bpm = new BufferPoolManager(poolSize: 1, fileManager);

        Lsn flushedLsn = Lsn.Invalid;
        bpm.WalFlushCallback = lsn =>
        {
            flushedLsn = lsn;
        };

        var frame = bpm.NewPage(out var pageId);
        Assert.NotNull(frame);

        var modificationLsn = new Lsn(100500);
        frame.PageLsn = modificationLsn;
        Page.SetLsn(frame.Data, modificationLsn);

        bpm.UnpinPage(pageId, isDirty: true);

        // Explicitly flush page
        bpm.FlushPage(pageId);

        // Verify WAL flush callback was invoked with the exact PageLSN
        Assert.Equal(modificationLsn, flushedLsn);
    }
}
