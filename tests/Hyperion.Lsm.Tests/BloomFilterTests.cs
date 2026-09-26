using System.Text;
using Hyperion.Lsm;
using Xunit;

namespace Hyperion.Lsm.Tests;

public class BloomFilterTests
{
    [Fact]
    public void BloomFilter_AddedKeys_AlwaysReturnTrue_ZeroFalseNegatives()
    {
        var filter = new BloomFilter(expectedEntries: 1000, falsePositiveRate: 0.01);

        for (int i = 0; i < 1000; i++)
        {
            byte[] key = Encoding.UTF8.GetBytes($"user_{i:D5}");
            filter.Add(key);
        }

        // Bloom filter fundamental property: NO FALSE NEGATIVES
        for (int i = 0; i < 1000; i++)
        {
            byte[] key = Encoding.UTF8.GetBytes($"user_{i:D5}");
            Assert.True(filter.MayContain(key));
        }
    }

    [Fact]
    public void BloomFilter_FalsePositiveRate_WithinAcceptableBound()
    {
        int entries = 2000;
        var filter = new BloomFilter(entries, 0.01);

        for (int i = 0; i < entries; i++)
        {
            filter.Add(Encoding.UTF8.GetBytes($"present_key_{i}"));
        }

        int falsePositives = 0;
        int testCount = 5000;
        for (int i = 0; i < testCount; i++)
        {
            byte[] absent = Encoding.UTF8.GetBytes($"absent_key_{i}");
            if (filter.MayContain(absent))
            {
                falsePositives++;
            }
        }

        double observedFpr = falsePositives / (double)testCount;
        // Observed FPR should be close to 1% (allowing up to 3% statistical variance)
        Assert.True(observedFpr < 0.03, $"Observed FPR too high: {observedFpr:P2}");
    }

    [Fact]
    public void BloomFilter_SerializationRoundTrip_PreservesFilter()
    {
        var filter = new BloomFilter(100, 0.01);
        filter.Add(Encoding.UTF8.GetBytes("Alpha"));
        filter.Add(Encoding.UTF8.GetBytes("Beta"));

        byte[] buffer = new byte[12 + filter.ByteSize];
        filter.Serialize(buffer);

        var restored = BloomFilter.Deserialize(buffer);
        Assert.True(restored.MayContain(Encoding.UTF8.GetBytes("Alpha")));
        Assert.True(restored.MayContain(Encoding.UTF8.GetBytes("Beta")));
        Assert.False(restored.MayContain(Encoding.UTF8.GetBytes("GammaNonExistent")));
    }
}
