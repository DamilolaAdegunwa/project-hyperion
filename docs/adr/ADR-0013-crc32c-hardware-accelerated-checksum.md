# ADR-0013: CRC32C Hardware-Accelerated Checksumming for Data Integrity

## Status
Accepted

## Context
Data corruption can occur at any stage: disk block decay (bit rot), controller firmware bugs, torn page writes during power loss, or network frame corruption. A database engine must detect corruption immediately before interpreting binary bytes as valid pointers, schema definitions, or transaction records.

The checksum algorithm must be:
1. Extremely fast (capable of processing multi-gigabytes per second per core so it does not bottleneck I/O or network pipelines).
2. Mathematically sound with excellent Hamming distance properties for detecting multi-bit burst errors.

## Decision
Hyperion standardizes universally on **CRC32C (Castagnoli)** polynomial ($0x1EDC6F41$):

1. **Hardware Intrinsics**: Uses `System.Runtime.Intrinsics.X86.Sse42.Crc32` on x86/x64 architectures and `System.Runtime.Intrinsics.Arm.Crc32` on ARM64 architectures. This processes 8 bytes per single CPU clock cycle, yielding throughput exceeding 20 GB/sec per core.
2. **Deterministic Managed Fallback**: For platforms without hardware intrinsics, a slicing-by-8 lookup table implementation is provided, guaranteeing 100% bitwise identical results across all architectures.
3. **Application Points**:
   - **4KB Pages**: Every page header contains a CRC32C over the remaining 4064 bytes.
   - **WAL Log Records**: Every log record header contains a CRC32C over its header and payload.
   - **SSTable Blocks**: Every 4KB data and index block terminates with a CRC32C.
   - **Network Frames**: Every TCP frame ends with a CRC32C.

## Alternatives Considered
- **Standard CRC32 (IEEE 802.3)**: Rejected because Castagnoli has superior error detection characteristics for data lengths up to 4KB and 64KB (Hamming distance of 6 vs 4).
- **XXHash3 / XXHash64**: Extremely fast non-cryptographic hash, but lacks native single-instruction hardware acceleration on commodity CPUs compared to SSE4.2 / ARM CRC32 instructions.
- **SHA-256**: Cryptographically secure, but orders of magnitude slower (CPU bottleneck on storage reads).

## Tradeoffs
- **Positives**: Hardware-accelerated sub-microsecond checksum calculation over 4KB pages; zero impact on write throughput.
- **Negatives**: 32-bit checksum has a theoretical 1 in $2^{32}$ collision probability for random corruption (acceptable for localized 4KB page and record verification).

## Invariants
- Any page, record, or frame with a mismatched checksum must trigger an immediate fail-stop exception. Corrupted data must never be decoded into memory structures.

## Failure Consequences
Failing to verify checksums allows corrupt disk bytes to be misinterpreted as memory offsets, resulting in memory corruption, invalid pointer dereferences, or silent persistent data corruption.
