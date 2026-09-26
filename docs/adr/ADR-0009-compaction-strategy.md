# ADR-0009: LSM Compaction Strategies — Leveled vs Size-Tiered

## Status
Accepted

## Context
In an LSM-tree storage engine, background compaction merges multiple SSTables into new SSTables to reclaim disk space occupied by deleted records (tombstones) and overwritten keys, while preventing read amplification from growing unboundedly.

Two classical compaction strategies exist in distributed databases:
1. **Size-Tiered Compaction (STCS)** (used in Apache Cassandra): Groups SSTables of roughly equal size and merges them into one larger SSTable.
2. **Leveled Compaction (LCS)** (used in LevelDB, RocksDB, TiKV): Organizes SSTables into discrete levels ($L_0, L_1, L_2, \dots$), where each level $L_{i \ge 1}$ has strictly non-overlapping key ranges and grows exponentially ($L_{i+1} = 10 \times L_i$).

## Decision
Hyperion implements **both Leveled Compaction and Size-Tiered Compaction**, with a pluggable `ICompactionStrategy` interface, defaulting to **Leveled Compaction**.

1. **Leveled Compaction (Default)**:
   - $L_0$ SSTables have overlapping key ranges (created directly from MemTable flushes).
   - Once $L_0$ reaches 4 SSTables, compaction merges them with overlapping SSTables in $L_1$.
   - For all levels $L_1 \dots L_k$, SSTables within the same level have mutually disjoint key ranges.
   - Guaranteed bounded read amplification: at most 1 SSTable read per level for any point query.
2. **Size-Tiered Compaction (Configurable)**:
   - Provided for high-write-throughput append workloads where write amplification must be minimized at the expense of higher read and space amplification.
3. **Atomic Manifest Commit**:
   - Compaction writes new SSTables to temporary files.
   - Once complete, an atomic `VersionEdit` is committed to the `Manifest` file. Only after the Manifest is durably `fsync`ed are old SSTable files deleted.

## Alternatives Considered
- **Universal Compaction (RocksDB)**: Rejected for the initial implementation due to higher complexity and less predictable space amplification spikes.
- **FIFO Compaction**: Suitable only for time-to-live (TTL) caches, rejected for general database usage.

## Tradeoffs
- **Positives**: Leveled compaction bounds space amplification to $\approx 1.1\times$ and guarantees fast point lookups; Size-Tiered provides optimal ingestion for write-heavy benchmarks.
- **Negatives**: Leveled compaction exhibits higher write amplification ($\approx 10\times - 30\times$) during intensive overwrites.

## Invariants
- In Leveled Compaction, no two SSTables in the same level $L \ge 1$ may have overlapping key ranges.
- A tombstone must not be dropped during compaction if an older version of the key may exist in a deeper level.

## Failure Consequences
Improper tombstone dropping results in the resurrection of previously deleted records ("ghost records"). Crashes during compaction must never corrupt active reads.
