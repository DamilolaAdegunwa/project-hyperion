# Project Hyperion — Distributed Consistency Model

## 1. The Consistency Spectrum in Hyperion

Distributed databases must be explicit about the exact consistency semantics they offer. Project Hyperion rejects ambiguous "eventual consistency" marketing terms and establishes rigorous, measurable consistency boundaries.

```
+-------------------------------------------------------------------------------+
| STRICT SERIALIZABILITY (External Consistency)                                 |
| - Distributed transactions execute with serializable snapshot isolation (SSI) |
| - External real-time order of transactions is strictly preserved              |
+-------------------------------------------------------------------------------+
                                        |
+---------------------------------------v---------------------------------------+
| LINEARIZABILITY (Single-Key / Single-Partition)                               |
| - Raft-replicated operations appear to execute instantaneously                |
| - Reads observe the most recent committed write in physical time              |
+-------------------------------------------------------------------------------+
                                        |
+---------------------------------------v---------------------------------------+
| SEQUENTIAL CONSISTENCY (Follower Reads with ReadIndex)                        |
| - Operations order preserves client execution sequence                        |
+-------------------------------------------------------------------------------+
                                        |
+---------------------------------------v---------------------------------------+
| BOUNDED STALENESS (Stale Follower Reads)                                      |
| - Follower reads may lag behind the leader by at most Δ LSNs or seconds       |
+-------------------------------------------------------------------------------+
```

---

## 2. Configurable Read Consistency Levels

Clients specify the desired consistency guarantee on a per-query basis:

```sql
SET CONSISTENCY = 'READ_STRONG'; -- Default
SELECT balance FROM accounts WHERE id = 42;
```

### 2.1 `READ_STRONG` (Linearizable Read)
- **Mechanism**: The request is routed to the partition's current Raft leader.
- **Protocol**: The leader executes the **ReadIndex Protocol**:
  1. Records current `commitIndex`.
  2. Confirms leadership via majority heartbeat quorum exchange.
  3. Waits until local `lastApplied >= commitIndex`.
  4. Reads state machine and returns.
- **Guarantee**: Guaranteed to observe all writes committed prior to the invocation of the read. Never returns stale data, even during leadership transitions.

### 2.2 `READ_STALE` (Bounded Stale Read)
- **Mechanism**: The request is routed directly to the nearest replica (e.g. local follower).
- **Protocol**: The follower verifies its current `lastApplied` index against the client's optional `MinimumLsn` watermark.
- **Tradeoff**: Extremely low latency (local memory read, zero network hops to leader), but may return data that lags behind the leader by the follower's replication delay.

---

## 3. Configurable Write Durability Levels

Clients specify write guarantees based on application tolerance for latency vs. durability:

```sql
SET DURABILITY = 'WRITE_DURABLE'; -- Default
INSERT INTO audit_log (msg) VALUES ('User logged in');
```

| Durability Mode | Replication Quorum | Local Fsync | Follower Fsync | Latency | Crash Resilience |
|---|---|---|---|---|---|
| **`WRITE_DURABLE`** | Majority ($\lfloor N/2 \rfloor + 1$) | Yes (`fsync`) | Yes (`fsync`) | High | Survived by entire cluster crash or simultaneous power failure across majority. |
| **`WRITE_QUORUM`** | Majority ($\lfloor N/2 \rfloor + 1$) | Yes (`fsync`) | In-Memory / OS Cache | Medium | Tolerates power loss on leader and $f-1$ followers. |
| **`WRITE_ASYNC`** | Asynchronous (Leader only) | OS Page Cache | Async | Ultra Low | Risk of data loss if leader process is killed before buffer flush. |

---

## 4. Multi-Partition Strict Serializability

For transactions spanning multiple partitions, Hyperion combines:
1. **Raft Linearizability** within each individual partition.
2. **Two-Phase Locking (S2PL)** or **Serializable Snapshot Isolation (SSI)** to prevent serialization anomalies (write skew, phantom reads).
3. **Durable Two-Phase Commit (2PC)** ensuring atomic all-or-nothing cross-partition commitment.
4. **Hybrid Logical Clocks (HLC)** to assign monotonically ordered commit timestamps across distinct physical nodes, guaranteeing external consistency without atomic GPS hardware clocks (e.g. Spanner TrueTime).
