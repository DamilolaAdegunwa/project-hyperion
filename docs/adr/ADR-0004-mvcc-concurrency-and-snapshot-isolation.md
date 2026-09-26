# ADR-0004: Multi-Version Concurrency Control (MVCC) and Snapshot Isolation

## Status
Accepted

## Context
Under high-concurrency relational workloads, read-heavy queries (e.g. analytics, aggregations, reporting) compete with frequent write-heavy transactions (e.g. balance updates, order insertions). Under pure two-phase locking (2PL), shared read locks block exclusive write locks, and exclusive write locks block shared read locks. This causes severe lock contention, query latency spikes, and deadlocks.

## Decision
Hyperion implements **Multi-Version Concurrency Control (MVCC)** using timestamp-ordered version chains and snapshot descriptors:
1. Updates append new versions rather than overwriting in place.
2. Every version contains `Xmin` (creating transaction ID) and `Xmax` (deleting/superseding transaction ID).
3. Read operations execute against a point-in-time `Snapshot` descriptor containing the reading transaction's ID, highest allocated ID, and the set of concurrent uncommitted transaction IDs.
4. **Non-blocking Reads**: Readers never block writers; writers never block readers.
5. **Epoch Vacuuming**: An asynchronous background cleaner removes obsolete tuple versions whose `Xmax` precedes the lowest active snapshot transaction in the cluster.

## Alternatives Considered
- **Strict Two-Phase Locking (S2PL) with In-Place Updates**: Rejected due to catastrophic throughput degradation under mixed read/write workloads and susceptibility to deadlocks on long-running queries.
- **Rollback Segments / Undo Logs (MySQL InnoDB / Oracle style)**: Reconstructs historical versions dynamically from undo logs. Rejected in favor of PostgreSQL-style versioned tuples in data pages and LSMs because it simplifies snapshot traversal and allows direct garbage collection without chasing long undo chains.

## Tradeoffs
- **Positives**: Maximizes read concurrency; eliminates read-write deadlocks; naturally provides repeatable reads and point-in-time snapshots.
- **Negatives**: Causes space bloat until vacuuming reclaims dead versions; requires version chain traversal for frequently updated keys.

## Invariants
- A transaction never observes a version created by an uncommitted transaction (unless it created the version itself).
- A transaction never observes a version created after its snapshot was established.
- Dead version reclamation must never delete a version visible to any active snapshot.

## Failure Consequences
A flaw in the visibility rule could expose uncommitted dirty data or produce non-repeatable reads under snapshot isolation.
