using System.Collections.Concurrent;
using Hyperion.Core;

namespace Hyperion.Storage;

public interface IBufferPoolManager : IDisposable
{
    int PoolSize { get; }
    BufferFrame? FetchPage(PageId pageId);
    BufferFrame? NewPage(out PageId pageId);
    bool UnpinPage(PageId pageId, bool isDirty);
    bool FlushPage(PageId pageId);
    void FlushAllPages();
    bool DeletePage(PageId pageId);
    Action<Lsn>? WalFlushCallback { get; set; }
    void SimulateCrash();
}

public sealed class BufferPoolManager : IBufferPoolManager
{
    private readonly int _poolSize;
    private readonly IFileManager _fileManager;
    private readonly IReplacer _replacer;
    private readonly BufferFrame[] _frames;
    private readonly ConcurrentDictionary<PageId, int> _pageTable;
    private readonly ConcurrentQueue<int> _freeList;
    private readonly object _globalLock = new();
    private bool _crashed;
    private bool _disposed;

    public Action<Lsn>? WalFlushCallback { get; set; }

    public int PoolSize => _poolSize;

    public void SimulateCrash()
    {
        lock (_globalLock)
        {
            _crashed = true;
        }
    }

    public BufferPoolManager(int poolSize, IFileManager fileManager, IReplacer? replacer = null)
    {
        if (poolSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(poolSize), "Pool size must be positive");

        _poolSize = poolSize;
        _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
        _replacer = replacer ?? new ClockReplacer(poolSize);

        _frames = new BufferFrame[poolSize];
        _freeList = new ConcurrentQueue<int>();
        _pageTable = new ConcurrentDictionary<PageId, int>();

        for (int i = 0; i < poolSize; i++)
        {
            _frames[i] = new BufferFrame(i);
            _freeList.Enqueue(i);
        }
    }

    public BufferFrame? FetchPage(PageId pageId)
    {
        if (!pageId.IsValid)
            return null;

        lock (_globalLock)
        {
            if (_pageTable.TryGetValue(pageId, out int frameId))
            {
                var cachedFrame = _frames[frameId];
                cachedFrame.PinCount++;
                _replacer.Pin(frameId);
                return cachedFrame;
            }

            if (!TryGetAvailableFrame(out int targetFrameId))
                return null; // All frames pinned, buffer pool saturated

            var frame = _frames[targetFrameId];
            EvictAndFlushIfDirty(frame);

            frame.Reset();
            frame.PageId = pageId;
            frame.PinCount = 1;

            _fileManager.ReadPage(pageId, frame.Data);

            // Check if page on disk is raw/unformatted (all zeros from file allocation)
            var header = Page.GetHeader(frame.Data);
            if (header.FreeSpaceOffset == 0)
            {
                Page.Initialize(frame.Data, pageId);
                header = Page.GetHeader(frame.Data);
            }
            else if (header.PageId != 0 || header.SlotCount > 0)
            {
                if (!Page.VerifyChecksum(frame.Data, out uint expected, out uint actual))
                {
                    throw new PageCorruptedException(pageId, expected, actual);
                }
            }

            frame.PageLsn = new Lsn(header.PageLsn);
            _pageTable[pageId] = targetFrameId;
            _replacer.Pin(targetFrameId);

            return frame;
        }
    }

    public BufferFrame? NewPage(out PageId pageId)
    {
        lock (_globalLock)
        {
            pageId = PageId.Invalid;

            if (!TryGetAvailableFrame(out int targetFrameId))
                return null;

            pageId = _fileManager.AllocatePage();
            var frame = _frames[targetFrameId];
            EvictAndFlushIfDirty(frame);

            frame.Reset();
            frame.PageId = pageId;
            frame.PinCount = 1;
            frame.IsDirty = true;

            Page.Initialize(frame.Data, pageId);
            _pageTable[pageId] = targetFrameId;
            _replacer.Pin(targetFrameId);

            return frame;
        }
    }

    public bool UnpinPage(PageId pageId, bool isDirty)
    {
        lock (_globalLock)
        {
            if (!_pageTable.TryGetValue(pageId, out int frameId))
                return false;

            var frame = _frames[frameId];
            if (frame.PinCount <= 0)
                return false;

            if (isDirty)
            {
                frame.IsDirty = true;
            }

            frame.PinCount--;
            if (frame.PinCount == 0)
            {
                _replacer.Unpin(frameId);
            }

            return true;
        }
    }

    public bool FlushPage(PageId pageId)
    {
        lock (_globalLock)
        {
            if (!_pageTable.TryGetValue(pageId, out int frameId))
                return false;

            var frame = _frames[frameId];
            FlushFrame(frame);
            return true;
        }
    }

    public void FlushAllPages()
    {
        lock (_globalLock)
        {
            foreach (var kvp in _pageTable)
            {
                var frame = _frames[kvp.Value];
                FlushFrame(frame);
            }
            _fileManager.Sync();
        }
    }

    public bool DeletePage(PageId pageId)
    {
        lock (_globalLock)
        {
            if (!_pageTable.TryGetValue(pageId, out int frameId))
                return true;

            var frame = _frames[frameId];
            if (frame.PinCount > 0)
                return false; // Cannot delete pinned page

            _pageTable.TryRemove(pageId, out _);
            _replacer.Pin(frameId); // Remove from replacer
            frame.Reset();
            _freeList.Enqueue(frameId);

            return true;
        }
    }

    private bool TryGetAvailableFrame(out int frameId)
    {
        if (_freeList.TryDequeue(out frameId))
            return true;

        return _replacer.Victim(out frameId);
    }

    private void EvictAndFlushIfDirty(BufferFrame frame)
    {
        if (frame.PageId.IsValid)
        {
            if (frame.IsDirty)
            {
                FlushFrame(frame);
            }
            _pageTable.TryRemove(frame.PageId, out _);
        }
    }

    private void FlushFrame(BufferFrame frame)
    {
        if (!frame.PageId.IsValid || !frame.IsDirty)
            return;

        // WAL Invariant: FlushedLSN >= PageLSN before writing page to disk
        if (frame.PageLsn.IsValid && WalFlushCallback != null)
        {
            WalFlushCallback(frame.PageLsn);
        }

        Page.UpdateChecksum(frame.Data);
        _fileManager.WritePage(frame.PageId, frame.Data);
        frame.IsDirty = false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (!_crashed)
            {
                FlushAllPages();
            }
            _fileManager.Dispose();
            foreach (var frame in _frames)
            {
                frame.Dispose();
            }
            _disposed = true;
        }
    }
}
