using Hyperion.Core;

namespace Hyperion.Storage;

/// <summary>
/// Represents an in-memory frame in the Buffer Pool caching a single 4096-byte database page.
/// </summary>
public sealed class BufferFrame : IDisposable
{
    public int FrameId { get; }
    public byte[] Data { get; }
    public PageId PageId { get; internal set; }
    public int PinCount { get; internal set; }
    public bool IsDirty { get; internal set; }
    public Lsn PageLsn { get; set; }
    public ReaderWriterLockSlim Latch { get; }

    public BufferFrame(int frameId)
    {
        FrameId = frameId;
        Data = new byte[Page.PageSize];
        PageId = PageId.Invalid;
        PinCount = 0;
        IsDirty = false;
        PageLsn = Lsn.Invalid;
        Latch = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
    }

    public void Reset()
    {
        PageId = PageId.Invalid;
        PinCount = 0;
        IsDirty = false;
        PageLsn = Lsn.Invalid;
        Array.Clear(Data);
    }

    public void EnterReadLatch() => Latch.EnterReadLock();
    public void ExitReadLatch() => Latch.ExitReadLock();

    public void EnterWriteLatch() => Latch.EnterWriteLock();
    public void ExitWriteLatch() => Latch.ExitWriteLock();

    public void Dispose()
    {
        Latch.Dispose();
    }
}
