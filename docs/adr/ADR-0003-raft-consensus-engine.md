# ADR-0003: Raft Consensus Engine for State Machine Replication

## Status
Accepted

## Context
A distributed database must replicate data across independent nodes to survive hardware and network failures. To guarantee that all replicas agree on the exact same sequence of state mutations despite arbitrary network delays, message reordering, partitions, or node crashes, a distributed consensus algorithm is required.

## Decision
Hyperion implements the **Raft consensus algorithm** (Ongaro & Ousterhout, Stanford University, 2014) natively from first principles.

Key components implemented:
1. **Leader Election**: Randomized election timeouts (150ms–300ms) with split-vote avoidance.
2. **Log Replication**: Invariant enforcement via `AppendEntries` RPC with fast conflict backtracking.
3. **Safety**: Candidate log completeness checks ensuring that only candidates containing all committed entries can be elected.
4. **Log Compaction & Snapshots**: Truncation of applied log entries and `InstallSnapshot` RPC for catching up slow or newly added followers.
5. **Linearizable Reads**: ReadIndex protocol to verify leadership before servicing reads, avoiding stale reads during network partitions.

## Alternatives Considered
- **Multi-Paxos**: Rejected due to high conceptual complexity, ambiguous leader transition specifications, and lack of clear separation between consensus and log management.
- **Viewstamped Replication (VR)**: Viable alternative, but Raft's formal verification models, comprehensive literature, and clear state machine decompose more cleanly into decoupled C# abstractions.
- **External consensus library (e.g., DotNext.Net.Cluster or JRaft)**: Strictly rejected by Project Hyperion's anti-cheating and first-principles standard.

## Tradeoffs
- **Positives**: Understandable state machine; clear separation between leader election, log replication, and safety invariants; proven correctness in production systems (etcd, TiKV, CockroachDB).
- **Negatives**: Strong leader bottleneck per partition (mitigated in Hyperion via Multi-Raft architecture).

## Invariants
- **Election Safety**: At most one leader per term.
- **Leader Append-Only**: Leader never overwrites or truncates its own log entries.
- **Log Matching**: Same index and term implies identical log prefix.
- **Leader Completeness**: Committed entries are present in all future leaders.
- **State Machine Safety**: No two servers apply different commands at the same index.

## Failure Consequences
A bug in consensus could lead to split-brain execution, silent data divergence between replicas, or permanent cluster deadlock.
