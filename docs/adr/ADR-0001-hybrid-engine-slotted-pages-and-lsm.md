# ADR-0001: Hybrid Storage Engine — Slotted Disk Pages and Log-Structured Merge-Tree

## Status
Accepted

## Context
A modern distributed database must handle diverse workload profiles:
1. High-frequency random transactional reads and in-place row updates with complex secondary indexes (typical of OLTP financial/account records).
2. Ultra-high-throughput sequential or semi-ordered append workloads (audit logs, time-series metrics, ingest streams).

Traditional relational engines (PostgreSQL, SQLite) rely strictly on slotted disk pages managed by a buffer pool. Modern distributed NoSQL engines (Cassandra, RocksDB, TiKV) rely strictly on Log-Structured Merge-Trees (LSM). Each design makes opposing tradeoffs regarding Write Amplification (WA), Read Amplification (RA), and Space Amplification (SA).

## Decision
Hyperion will implement both engines natively from first principles within a unified abstraction:
1. A **Slotted Page B+ Tree Engine** with 4096-byte pages and ARIES crash recovery for transactional, secondary-indexed relational tables.
2. A **Log-Structured Merge-Tree (LSM) Engine** with SkipList MemTables, Immutable MemTables, SSTables, Bloom filters, and multi-strategy compaction for write-intensive key-value streams.

## Alternatives Considered
- **Pure Slotted Page / Heap File**: Rejected because random disk I/O on high-throughput appends creates severe write amplification and buffer pool thrashing.
- **Pure LSM-Tree**: Rejected because secondary indexing, complex multi-column relational updates, and deterministic in-place MVCC version pointer maintenance are significantly more complex and read-amplified in pure LSMs.
- **External Engine (e.g. RocksDB or SQLite)**: Strictly rejected by Project Hyperion's first-principles standard.

## Tradeoffs
- **Positives**: Allows direct, fair architectural comparisons and benchmarks within the same runtime; enables selecting the optimal storage structure based on table access patterns.
- **Negatives**: Doubles the storage engineering surface area, requiring two distinct persistence and recovery pipelines.

## Invariants
- Both engines must guarantee immediate durability upon acknowledgment (`fsync` before commit).
- Both engines must produce equivalent deterministic results when subjected to identical concurrent mutation sequences.

## Failure Consequences
A defect in either engine's recovery logic could lead to unrecoverable data loss or torn reads upon node crash. Strict unit and crash-injection tests are required for both.
