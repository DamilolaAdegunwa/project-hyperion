# Project Hyperion — Observability Architecture

## 1. The Three Pillars of Observability

Observability in Hyperion is treated as a first-class architectural invariant, not an afterthought. A distributed database that cannot be inspected in real time is unusable in production.

```
       [ OBSERVABILITY SUBSYSTEM ]
       /            |            \
      v             v             v
 [ METRICS ]     [ LOGS ]     [ TRACES ]
 - Throughput    - Structured - Distributed Span
 - Latency Hist  - JSON Lines - Correlation IDs
 - Buffer Pool   - Diagnostic - Cross-Partition
 - Raft States   - Error Dumps- Causality Graph
```

---

## 2. Metric Registry & Performance Counters

Hyperion embeds an in-memory, lock-free `MetricsRegistry` collecting high-resolution telemetry:

### 2.1 Storage & WAL Metrics
- `storage_wal_append_bytes_total`: Counter of bytes written to WAL.
- `storage_wal_fsync_latency_microseconds`: Histogram (p50, p95, p99, p999) of `fsync` system calls.
- `storage_buffer_pool_hits_total` / `misses_total`: Hit ratio of in-memory 4KB frames.
- `storage_buffer_pool_evictions_total`: Number of pages evicted under memory pressure.
- `storage_lsm_compaction_bytes_read` / `written`: Write amplification telemetry.

### 2.2 Consensus & Distributed Metrics
- `raft_current_term`: Gauge tracking the current election term.
- `raft_is_leader`: Binary gauge (1 = leader, 0 = follower/candidate).
- `raft_commit_index`: Monotonic index of last committed state machine log.
- `raft_replication_lag_entries`: Difference between leader's last log index and followers' `matchIndex`.
- `raft_election_timeout_count`: Number of election timeouts triggered.

### 2.3 Transaction & Query Metrics
- `tx_active_count`: Gauge of currently active transactions.
- `tx_committed_total` / `aborted_total`: Transaction outcome counters.
- `tx_deadlock_detected_total`: Counter of cycles identified in the wait-for graph.
- `query_execution_duration_microseconds`: Histogram of query latency from parse to final tuple emit.

---

## 3. Structured Diagnostic Logging

Hyperion outputs all log events formatted as single-line JSON with mandatory contextual correlation fields:

```json
{
  "timestamp": "2026-09-26T22:18:42.045123Z",
  "level": "INFO",
  "subsystem": "Hyperion.Raft",
  "event": "LEADER_ELECTED",
  "node_id": 3,
  "partition_id": 1,
  "term": 4,
  "last_log_index": 1284,
  "trace_id": "c1a90e38-4e89-40ad-bc17-578b84d12e84",
  "message": "Node 3 elected as leader for partition 1 in term 4 with 2 of 3 votes."
}
```

### Contextual Fields
- `TraceId`: Globally unique request identifier traversing client, coordinator, and storage nodes.
- `RequestId`: Per-hop RPC identifier.
- `NodeId`: Physical node identity.
- `PartitionId`: Shard/Raft partition identifier.
- `TransactionId`: Active transaction ID (if executing within a transaction context).
- `RaftTerm` & `LogIndex`: Current consensus metadata.

---

## 4. Distributed Tracing

For distributed queries and cross-partition transactions:
1. The Client generates a root `TraceId` upon initiating a request.
2. The Gateway extracts the `TraceId` and attaches a span ID (`SpanId`).
3. Inter-node binary RPC frames propagate the trace context across TCP connections in the frame header.
4. Each subsystem creates nested spans:
   - `Client.ExecuteQuery`
     - `Gateway.ParseAndBind`
     - `Coordinator.ScatterGather`
       - `Node1.RaftPropose`
         - `Node1.WalAppend`
         - `Node1.BufferPoolWrite`
       - `Node2.RaftPropose`
         - `Node2.WalAppend`
5. Enables sub-millisecond diagnosis of stragglers and tail-latency outliers across the cluster.
