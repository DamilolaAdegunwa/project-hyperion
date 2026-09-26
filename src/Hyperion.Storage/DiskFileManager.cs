using Microsoft.Win32.SafeHandles;
using Hyperion.Core;

namespace Hyperion.Storage;

public interface IFileManager : IDisposable
{
    uint TotalPages { get; }
    PageId AllocatePage();
    void ReadPage(PageId pageId, Span<byte> destination);
    void WritePage(PageId pageId, ReadOnlySpan<byte> source);
    void Sync();
}

public sealed class DiskFileManager : IFileManager
{
    private readonly string _filePath;
    private readonly SafeFileHandle _fileHandle;
    private long _fileLength;
    private readonly object _allocationLock = new();
    private bool _disposed;

    public DiskFileManager(string filePath)
    {
        _filePath = filePath;
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var fileStream = new FileStream(
            filePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.RandomAccess);

        _fileHandle = fileStream.SafeFileHandle;
        _fileLength = fileStream.Length;
    }

    public uint TotalPages
    {
        get
        {
            lock (_allocationLock)
            {
                return (uint)(_fileLength / Page.PageSize);
            }
        }
    }

    public PageId AllocatePage()
    {
        lock (_allocationLock)
        {
            uint newPageNumber = (uint)(_fileLength / Page.PageSize);
            _fileLength += Page.PageSize;
            RandomAccess.SetLength(_fileHandle, _fileLength);
            return new PageId(newPageNumber);
        }
    }

    public void ReadPage(PageId pageId, Span<byte> destination)
    {
        if (destination.Length < Page.PageSize)
            throw new ArgumentException($"Destination span must be at least {Page.PageSize} bytes", nameof(destination));

        long offset = (long)pageId.Value * Page.PageSize;
        int bytesRead = RandomAccess.Read(_fileHandle, destination[..Page.PageSize], offset);

        if (bytesRead < Page.PageSize)
        {
            // Pad remainder with zeros if page was allocated but not yet written
            destination[bytesRead..Page.PageSize].Clear();
        }
    }

    public void WritePage(PageId pageId, ReadOnlySpan<byte> source)
    {
        if (source.Length < Page.PageSize)
            throw new ArgumentException($"Source span must be at least {Page.PageSize} bytes", nameof(source));

        long offset = (long)pageId.Value * Page.PageSize;
        RandomAccess.Write(_fileHandle, source[..Page.PageSize], offset);
    }

    public void Sync()
    {
        // Flush operating system file cache to durable disk media (fsync)
        RandomAccess.FlushToDisk(_fileHandle);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Sync();
            _fileHandle.Dispose();
            _disposed = true;
        }
    }
}
