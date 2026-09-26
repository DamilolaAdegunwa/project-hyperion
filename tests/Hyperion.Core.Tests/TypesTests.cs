using Hyperion.Core;
using Xunit;

namespace Hyperion.Core.Tests;

public class TypesTests
{
    [Fact]
    public void PageId_ValidationAndOrdering()
    {
        var p1 = new PageId(1);
        var p2 = new PageId(2);
        var inv = PageId.Invalid;

        Assert.True(p1.IsValid);
        Assert.False(inv.IsValid);
        Assert.True(p1.CompareTo(p2) < 0);
        Assert.Equal("Page(1)", p1.ToString());
    }

    [Fact]
    public void RecordId_ValidationAndOrdering()
    {
        var r1 = new RecordId(new PageId(1), 0);
        var r2 = new RecordId(new PageId(1), 1);
        var r3 = new RecordId(new PageId(2), 0);

        Assert.True(r1.IsValid);
        Assert.True(r1.CompareTo(r2) < 0);
        Assert.True(r2.CompareTo(r3) < 0);
        Assert.Equal("RID(1:0)", r1.ToString());
    }

    [Fact]
    public void Lsn_MonotonicComparison()
    {
        var l1 = new Lsn(100);
        var l2 = new Lsn(200);

        Assert.True(l1 < l2);
        Assert.True(l1 <= l2);
        Assert.False(l1 > l2);
        Assert.False(l1 >= l2);
        Assert.Equal("LSN(100)", l1.ToString());
    }
}
