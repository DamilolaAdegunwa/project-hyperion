# ADR-0010: Partitioning Strategy — Deterministic Ranges and Consistent Hash Ring

## Status
Accepted

## Context
A single storage node cannot scale indefinitely in capacity or throughput. Project Hyperion must distribute tables across multiple storage nodes. The two primary strategies for partitioning distributed data are:
1. **Range Partitioning**: Maps contiguous keys to partitions.
2. **Hash Partitioning**: Hashes keys across partitions using a uniform hash function.

## Decision
Hyperion provides a hybrid, table-configurable partitioning model managed by `Hyperion.Distribution.PartitionRouter`:

1. **Range Partitioning (Default for Relational Tables)**:
   - Partitions are defined by ordered intervals $[K_{\text{start}}, K_{\text{end}})$.
   - Enables single-node localized range queries (`BETWEEN`, `>`, `<`).
   - Dynamic Partition Splitting: When a partition exceeds a size threshold (e.g., 64MB), the partition splits into two equal halves at the median key.
2. **Consistent Hash Partitioning (Default for High-Throughput Streams)**:
   - Keys are hashed using 64-bit MurmurHash3 onto a $2^{64}-1$ ring.
   - 256 virtual nodes (vnodes) per physical node prevent hash skew.
   - Eliminates write hotspots caused by monotonically increasing auto-increment primary keys.
3. **Partition Routing Invariant**:
   - The routing table maps every valid primary key deterministically to exactly one `PartitionId`.
   - The `PartitionId` maps to a specific `ReplicaGroup` governed by an independent Raft consensus instance.

## Alternatives Considered
- **Pure Centralized Directory (e.g., Google Bigtable Master)**: Master becomes a single point of failure and bottleneck for all lookups.
- **Client-Side Dumb Hashing**: Does not support dynamic range splits or transparent shard migration.

## Tradeoffs
- **Positives**: Combines optimal range query efficiency for relational models with hotspot immunity for append workloads; supports online rebalancing.
- **Negatives**: Range partitioning can lead to temporary write hotspots if all inserts have sequential keys prior to partition splitting.

## Invariants
- For any key $K$ and cluster epoch $E$, `Route(K)` must return the exact same partition ID on all nodes.
- Partition splits must be synchronized via Raft consensus proposals before activating the new routing generation.

## Failure Consequences
Routing table desynchronization could lead to split-brain writes where two different nodes believe they own the same key range.
