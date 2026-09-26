using System.Text;
using Hyperion.Lsm;
using Xunit;

namespace Hyperion.Lsm.Tests;

public class SkipListTests
{
    [Fact]
    public void SkipList_InsertAndLookup_ReturnsCorrectValues()
    {
        var list = new SkipList(seed: 42);

        for (int i = 0; i < 100; i++)
        {
            byte[] key = Encoding.UTF8.GetBytes($"key_{i:D4}");
            byte[] val = Encoding.UTF8.GetBytes($"val_{i}");
            list.Insert(LsmEntry.CreatePut(key, val, (ulong)i));
        }

        Assert.Equal(100, list.Count);

        for (int i = 0; i < 100; i++)
        {
            byte[] key = Encoding.UTF8.GetBytes($"key_{i:D4}");
            Assert.True(list.TryFind(key, out var entry));
            Assert.NotNull(entry);
            Assert.Equal($"val_{i}", Encoding.UTF8.GetString(entry.Value));
        }

        byte[] missing = Encoding.UTF8.GetBytes("key_9999");
        Assert.False(list.TryFind(missing, out _));
    }

    [Fact]
    public void SkipList_ScanRange_ReturnsSortedRange()
    {
        var list = new SkipList(seed: 42);

        for (int i = 0; i < 50; i++)
        {
            byte[] key = Encoding.UTF8.GetBytes($"item_{i:D2}");
            byte[] val = Encoding.UTF8.GetBytes($"value_{i}");
            list.Insert(LsmEntry.CreatePut(key, val, (ulong)i));
        }

        byte[] start = Encoding.UTF8.GetBytes("item_10");
        byte[] end = Encoding.UTF8.GetBytes("item_20");

        var range = list.Scan(start, end);
        Assert.Equal(10, range.Count);
        Assert.Equal("item_10", Encoding.UTF8.GetString(range[0].Key));
        Assert.Equal("item_19", Encoding.UTF8.GetString(range[^1].Key));
    }
}
