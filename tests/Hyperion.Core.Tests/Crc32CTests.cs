using System.Text;
using Hyperion.Core.Checksum;
using Xunit;

namespace Hyperion.Core.Tests;

public class Crc32CTests
{
    [Fact]
    public void Compute_StandardVector_123456789_ReturnsExpectedChecksum()
    {
        // Standard Castagnoli CRC-32C vector for ASCII "123456789" is 0xE3069283
        byte[] input = Encoding.ASCII.GetBytes("123456789");
        uint crc = Crc32C.Compute(input);
        Assert.Equal(0xE3069283u, crc);
    }

    [Fact]
    public void Compute_EmptySpan_ReturnsZero()
    {
        uint crc = Crc32C.Compute(ReadOnlySpan<byte>.Empty);
        Assert.Equal(0u, crc);
    }

    [Fact]
    public void Compute_SoftwareAndHardware_YieldIdenticalResults()
    {
        var random = new Random(42);
        for (int length = 0; length <= 4096; length += 17)
        {
            byte[] data = new byte[length];
            random.NextBytes(data);

            uint hw = Crc32C.Compute(data);
            uint sw = ~Crc32C.ComputeSoftware(~0u, data);

            Assert.Equal(hw, sw);
        }
    }
}
