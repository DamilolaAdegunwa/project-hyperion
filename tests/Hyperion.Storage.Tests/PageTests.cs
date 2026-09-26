using System.Text;
using Hyperion.Core;
using Hyperion.Storage;
using Xunit;

namespace Hyperion.Storage.Tests;

public class PageTests
{
    [Fact]
    public void Page_Initialize_CreatesValidEmptyPage()
    {
        byte[] buffer = new byte[Page.PageSize];
        var pageId = new PageId(42);

        Page.Initialize(buffer, pageId, PageType.Data);

        var header = Page.GetHeader(buffer);
        Assert.Equal(42u, header.PageId);
        Assert.Equal(PageType.Data, header.PageType);
        Assert.Equal(0, header.SlotCount);
        Assert.Equal(Page.PageSize, header.FreeSpaceOffset);
        Assert.Equal(0, header.FragmentedBytes);
        Assert.True(Page.VerifyChecksum(buffer, out _, out _));
    }

    [Fact]
    public void Page_InsertAndGetRecord_Succeeds()
    {
        byte[] buffer = new byte[Page.PageSize];
        Page.Initialize(buffer, new PageId(1));

        byte[] payload1 = Encoding.UTF8.GetBytes("Hyperion Storage Engine Record 1");
        byte[] payload2 = Encoding.UTF8.GetBytes("Hyperion Storage Engine Record 2 - Longer Record");

        Assert.True(Page.TryInsertRecord(buffer, payload1, out ushort slot0));
        Assert.Equal(0, slot0);

        Assert.True(Page.TryInsertRecord(buffer, payload2, out ushort slot1));
        Assert.Equal(1, slot1);

        Assert.True(Page.TryGetRecord(buffer, 0, out var read1));
        Assert.Equal("Hyperion Storage Engine Record 1", Encoding.UTF8.GetString(read1));

        Assert.True(Page.TryGetRecord(buffer, 1, out var read2));
        Assert.Equal("Hyperion Storage Engine Record 2 - Longer Record", Encoding.UTF8.GetString(read2));

        Assert.False(Page.TryGetRecord(buffer, 2, out _));
    }

    [Fact]
    public void Page_UpdateRecord_InPlaceAndShrinkAndExpand()
    {
        byte[] buffer = new byte[Page.PageSize];
        Page.Initialize(buffer, new PageId(1));

        byte[] original = Encoding.UTF8.GetBytes("ABCDEF"); // 6 bytes
        Assert.True(Page.TryInsertRecord(buffer, original, out ushort slot));

        // 1. Same size update
        byte[] sameSize = Encoding.UTF8.GetBytes("123456");
        Assert.True(Page.TryUpdateRecord(buffer, slot, sameSize));
        Assert.True(Page.TryGetRecord(buffer, slot, out var read1));
        Assert.Equal("123456", Encoding.UTF8.GetString(read1));

        // 2. Shrink update
        byte[] smaller = Encoding.UTF8.GetBytes("XYZ"); // 3 bytes
        Assert.True(Page.TryUpdateRecord(buffer, slot, smaller));
        Assert.True(Page.TryGetRecord(buffer, slot, out var read2));
        Assert.Equal("XYZ", Encoding.UTF8.GetString(read2));
        var header = Page.GetHeader(buffer);
        Assert.Equal(3, header.FragmentedBytes);

        // 3. Expand update
        byte[] larger = Encoding.UTF8.GetBytes("SuperLongExpandedStringDataHere");
        Assert.True(Page.TryUpdateRecord(buffer, slot, larger));
        Assert.True(Page.TryGetRecord(buffer, slot, out var read3));
        Assert.Equal("SuperLongExpandedStringDataHere", Encoding.UTF8.GetString(read3));
    }

    [Fact]
    public void Page_DeleteAndReuseSlot_WorksCorrectly()
    {
        byte[] buffer = new byte[Page.PageSize];
        Page.Initialize(buffer, new PageId(1));

        Assert.True(Page.TryInsertRecord(buffer, Encoding.UTF8.GetBytes("Item 0"), out ushort s0));
        Assert.True(Page.TryInsertRecord(buffer, Encoding.UTF8.GetBytes("Item 1"), out ushort s1));
        Assert.True(Page.TryInsertRecord(buffer, Encoding.UTF8.GetBytes("Item 2"), out ushort s2));

        Assert.True(Page.DeleteRecord(buffer, s1));
        Assert.False(Page.TryGetRecord(buffer, s1, out _));

        // Next insert should reuse slot 1
        Assert.True(Page.TryInsertRecord(buffer, Encoding.UTF8.GetBytes("Reused Item 1"), out ushort reusedSlot));
        Assert.Equal(s1, reusedSlot);

        Assert.True(Page.TryGetRecord(buffer, reusedSlot, out var read));
        Assert.Equal("Reused Item 1", Encoding.UTF8.GetString(read));
    }

    [Fact]
    public void Page_Defragmentation_ConsolidatesFreeSpace()
    {
        byte[] buffer = new byte[Page.PageSize];
        Page.Initialize(buffer, new PageId(1));

        // Insert 10 records
        for (int i = 0; i < 10; i++)
        {
            Assert.True(Page.TryInsertRecord(buffer, Encoding.UTF8.GetBytes($"Record {i}"), out _));
        }

        // Delete even records
        for (ushort i = 0; i < 10; i += 2)
        {
            Assert.True(Page.DeleteRecord(buffer, i));
        }

        var headerBefore = Page.GetHeader(buffer);
        Assert.True(headerBefore.FragmentedBytes > 0);

        Page.Defragment(buffer);

        var headerAfter = Page.GetHeader(buffer);
        Assert.Equal(0, headerAfter.FragmentedBytes);

        // Verify remaining odd records are intact
        for (ushort i = 1; i < 10; i += 2)
        {
            Assert.True(Page.TryGetRecord(buffer, i, out var rec));
            Assert.Equal($"Record {i}", Encoding.UTF8.GetString(rec));
        }
    }

    [Fact]
    public void Page_Checksum_DetectsCorruption()
    {
        byte[] buffer = new byte[Page.PageSize];
        Page.Initialize(buffer, new PageId(1));
        Page.TryInsertRecord(buffer, Encoding.UTF8.GetBytes("Intact Data"), out _);
        Page.UpdateChecksum(buffer);

        Assert.True(Page.VerifyChecksum(buffer, out _, out _));

        // Corrupt single bit
        buffer[100] ^= 0xFF;
        Assert.False(Page.VerifyChecksum(buffer, out var expected, out var actual));
        Assert.NotEqual(expected, actual);
    }
}
