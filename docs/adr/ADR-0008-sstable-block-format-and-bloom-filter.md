# ADR-0008: SSTable Format, Block Indexes, and Bloom Filters

## Status
Accepted

## Context
In Hyperion's Log-Structured Merge-Tree (LSM) engine, active MemTables are flushed to disk as immutable Sorted String Tables (SSTables). If SSTables are simply flat arrays of sorted keys, point lookups would require reading the entire SSTable from disk or performing binary searches that trigger dozens of random disk I/O operations per read. To minimize read amplification, an on-disk layout with block-level indexing and probabilistic filtering is necessary.

## Decision
Hyperion implements a structured **SSTable file format** incorporating:
1. **4KB Data Blocks**: Keys and values are partitioned into contiguous 4KB blocks. Within each block, keys are stored in sorted lexicographical order with delta compression.
2. **Block Index**: Located at the end of the SSTable file. Maps each data block's first key (separator key) to its file offset and length. The block index is loaded into RAM upon SSTable open, allowing binary search to identify the exact block containing a key with zero disk reads.
3. **Bloom Filter**: A bit-array filter utilizing MurmurHash3 / XXHash64 hashing with $k$ hash functions and 10 bits per key ($\approx 1\%$ false positive rate). Probing the Bloom filter in RAM avoids reading the SSTable entirely if the key is not present.
4. **SSTable Footer**: Fixed 48-byte trailer containing magic number (`0x53535442` - "SSTB"), index block offset, bloom filter offset, total key count, and CRC32C checksum.

```
+-------------------------------------------------------------------------+
| DATA BLOCK 0 (4KB, Sorted Keys/Values + CRC32C)                         |
+-------------------------------------------------------------------------+
| DATA BLOCK 1 (4KB, Sorted Keys/Values + CRC32C)                         |
+-------------------------------------------------------------------------+
| ...                                                                     |
+-------------------------------------------------------------------------+
| BLOOM FILTER BLOCK (Bit array + CRC32C)                                 |
+-------------------------------------------------------------------------+
| BLOCK INDEX (Array of [BlockKey, FileOffset, BlockSize] + CRC32C)       |
+-------------------------------------------------------------------------+
| SSTABLE FOOTER (48 bytes: Magic, Offsets, KeyCount, FooterCRC)          |
+-------------------------------------------------------------------------+
```

## Alternatives Considered
- **Unindexed SSTable**: Rejected because point lookups would degenerate to linear scans of the file.
- **Key-level B-Tree index per SSTable**: Rejected because the block index is compact enough to remain pinned in memory, making deeper B-Tree structures within an SSTable redundant.

## Tradeoffs
- **Positives**: Reduces read amplification for point lookups from $O(N)$ disk reads to $\le 1$ disk read (99% of negative lookups terminate in RAM via Bloom filter); block indexing minimizes memory footprint.
- **Negatives**: Adding Bloom filters and block indexes increases SSTable file size by $\approx 10\%$.

## Invariants
- Keys across all blocks within an SSTable must be strictly sorted: $K_i < K_{i+1}$.
- Every data block and index block must end with a valid CRC32C checksum.

## Failure Consequences
Corrupted block indexes or Bloom filters would result in missed keys (silent data loss) or unhandled reading exceptions.
