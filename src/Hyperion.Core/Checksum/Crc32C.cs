using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using ArmCrc = System.Runtime.Intrinsics.Arm.Crc32;

namespace Hyperion.Core.Checksum;

/// <summary>
/// Hardware-accelerated CRC32C (Castagnoli) checksum implementation with slicing-by-8 software fallback.
/// Uses SSE4.2 instructions on x86/x64 and ARM CRC32 instructions on ARM64.
/// </summary>
public static class Crc32C
{
    private const uint Poly = 0x82F63B78;
    private static readonly uint[][] Table;

    static Crc32C()
    {
        Table = new uint[8][];
        for (int i = 0; i < 8; i++)
        {
            Table[i] = new uint[256];
        }

        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Poly : crc >> 1;
            }
            Table[0][i] = crc;
        }

        for (int i = 0; i < 256; i++)
        {
            for (int j = 1; j < 8; j++)
            {
                Table[j][i] = (Table[j - 1][i] >> 8) ^ Table[0][Table[j - 1][i] & 0xFF];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compute(ReadOnlySpan<byte> data) => Compute(0, data);

    public static uint Compute(uint initialCrc, ReadOnlySpan<byte> data)
    {
        uint crc = ~initialCrc;

        if (Sse42.IsSupported)
        {
            crc = ComputeHardwareX86(crc, data);
        }
        else if (ArmCrc.IsSupported)
        {
            crc = ComputeHardwareArm(crc, data);
        }
        else
        {
            crc = ComputeSoftware(crc, data);
        }

        return ~crc;
    }

    private static uint ComputeHardwareX86(uint crc, ReadOnlySpan<byte> data)
    {
        ref byte current = ref MemoryMarshal.GetReference(data);
        int length = data.Length;

        if (Sse42.X64.IsSupported)
        {
            ulong crc64 = crc;
            while (length >= 8)
            {
                ulong val = Unsafe.ReadUnaligned<ulong>(ref current);
                crc64 = Sse42.X64.Crc32(crc64, val);
                current = ref Unsafe.Add(ref current, 8);
                length -= 8;
            }
            crc = (uint)crc64;
        }
        else
        {
            while (length >= 4)
            {
                uint val = Unsafe.ReadUnaligned<uint>(ref current);
                crc = Sse42.Crc32(crc, val);
                current = ref Unsafe.Add(ref current, 4);
                length -= 4;
            }
        }

        while (length > 0)
        {
            crc = Sse42.Crc32(crc, current);
            current = ref Unsafe.Add(ref current, 1);
            length--;
        }

        return crc;
    }

    private static uint ComputeHardwareArm(uint crc, ReadOnlySpan<byte> data)
    {
        ref byte current = ref MemoryMarshal.GetReference(data);
        int length = data.Length;

        if (ArmCrc.Arm64.IsSupported)
        {
            while (length >= 8)
            {
                ulong val = Unsafe.ReadUnaligned<ulong>(ref current);
                crc = ArmCrc.Arm64.ComputeCrc32C(crc, val);
                current = ref Unsafe.Add(ref current, 8);
                length -= 8;
            }
        }

        while (length >= 4)
        {
            uint val = Unsafe.ReadUnaligned<uint>(ref current);
            crc = ArmCrc.ComputeCrc32C(crc, val);
            current = ref Unsafe.Add(ref current, 4);
            length -= 4;
        }

        while (length > 0)
        {
            crc = ArmCrc.ComputeCrc32C(crc, current);
            current = ref Unsafe.Add(ref current, 1);
            length--;
        }

        return crc;
    }

    public static uint ComputeSoftware(uint crc, ReadOnlySpan<byte> data)
    {
        ref byte current = ref MemoryMarshal.GetReference(data);
        int length = data.Length;

        while (length >= 8)
        {
            uint one = Unsafe.ReadUnaligned<uint>(ref current) ^ crc;
            uint two = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref current, 4));

            crc = Table[7][one & 0xFF] ^
                  Table[6][(one >> 8) & 0xFF] ^
                  Table[5][(one >> 16) & 0xFF] ^
                  Table[4][one >> 24] ^
                  Table[3][two & 0xFF] ^
                  Table[2][(two >> 8) & 0xFF] ^
                  Table[1][(two >> 16) & 0xFF] ^
                  Table[0][two >> 24];

            current = ref Unsafe.Add(ref current, 8);
            length -= 8;
        }

        while (length > 0)
        {
            crc = (crc >> 8) ^ Table[0][(crc & 0xFF) ^ current];
            current = ref Unsafe.Add(ref current, 1);
            length--;
        }

        return crc;
    }
}
