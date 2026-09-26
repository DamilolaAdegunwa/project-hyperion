# Project Hyperion — Distributed System Architecture

## 1. Cluster Topology & Partitioning

Hyperion operates as a shared-nothing, partitioned, replicated distributed database cluster. A cluster consists of $N$ physical storage nodes hosting $M$ independent partitions (where $M \gg N$).

```mermaid
graph TD
    subgraph Cluster Routing Layer
        CLIENT["Client Application"]
        GW["Hyperion Gateway"]
        ROUTER["Partition Router (Range / Hash Map)"]
    end

    subgraph Physical Node 1
        P1_L["Partition 1 (Leader)"]
        P2_F1["Partition 2 (Follower)"]
        P3_F2["Partition 3 (Follower)"]
    end

    subgraph Physical Node 2
        P1_F1["Partition 1 (Follower)"]
        P2_L["Partition 2 (Leader)"]
        P3_F1["Partition 3 (Follower)"]
    end

    subgraph Physical Node 3
        P1_F2["Partition 1 (Follower)"]
        P2_F2["Partition 2 (Follower)"]
        P3_L["Partition 3 (Leader)"]
    end

    CLIENT --> GW
    GW --> ROUTER
    ROUTER -->|Key Range [0..1000)| P1_L
    ROUTER -->|Key Range [1000..2000)| P2_L
    ROUTER -->|Key Range [2000..Max)| P3_L
    P1_L -.->|Replication| P1_F1
    P1_L -.->|Replication| P1_F2
    P2_L -.->|Replication| P2_F1
    P2_L -.->|Replication| P2_F2
    P3_L -.->|Replication| P3_F1
    P3_L -.->|Replication| P3_F2
```

### 1.1 Multi-Raft Architecture
Rather than executing consensus over a single monolithic Raft log (which creates an extreme CPU and I/O bottleneck at scale), Hyperion implements **Multi-Raft**:
- The keyspace is divided into discrete, contiguous partitions:
  $$\text{Partitions} = \{P_1, P_2, \dots, P_M\}$$
- Each partition $P_i$ forms its own independent Raft consensus group of $2f + 1$ replicas (typically 3 or 5).
- Different partitions elect their leaders independently across physical nodes, balancing read/write load evenly across the cluster.
- Failure of a single node only triggers fast leader re-election for the subset of partitions for which that node was leader; all other partitions remain completely uninterrupted.

---

## 2. Partitioning Strategies

Hyperion supports two deterministic partitioning models:

### 2.1 Range-Based Partitioning
Partitions are defined by ordered boundaries:
$$P_i = [\text{StartKey}_i, \text{EndKey}_i)$$
- **Advantage**: High efficiency for range scans (`SELECT * WHERE id BETWEEN 100 AND 500`), requiring only 1 or 2 partition lookups.
- **Dynamic Splitting**: When an SSTable or B+ Tree partition exceeds a configurable threshold (e.g., 64 MB), the Raft leader proposes a `SplitPartitionCommand`. A boundary key is selected, creating two new child partitions $P_a$ and $P_b$ without service downtime.

### 2.2 Consistent Hash Partitioning
For keys with monotonically increasing values (e.g. auto-increment IDs or timestamps) where range partitioning would create a "hotspot" on the newest partition, Hyperion supports consistent hashing:
$$\text{PartitionId} = \text{MurmurHash3}(\text{Key}) \pmod{\text{TotalPartitions}}$$
Virtual nodes (vnodes) are used to distribute partitions uniformly across heterogeneous hardware nodes.

---

## 3. Node-to-Node RPC & Wire Protocol

Inter-node communication (Raft replication, heartbeat exchange, distributed 2PC, remote query scan) is implemented using a custom, high-performance binary TCP protocol.

### 3.1 Binary Frame Structure
Every network frame has a fixed 20-byte header:
```
+-----------------------------------------------------------------------+
| FRAME HEADER (20 bytes)                                               |
|  - Magic (0x48595052 - "HYPR") : 4 bytes                              |
|  - FrameType                   : 1 byte  (Request, Response, Heartbeat)|
|  - Flags                       : 1 byte  (Compressed, OneWay)          |
|  - MessageCode                 : 2 bytes (AppendEntries, Vote, etc.)   |
|  - CorrelationId               : 8 bytes (Uint64 multiplexing ID)      |
|  - PayloadLength               : 4 bytes (Length of payload bytes)     |
+-----------------------------------------------------------------------+
| PAYLOAD (0..PayloadLength bytes)                                      |
|  - Serialized request/response body                                   |
+-----------------------------------------------------------------------+
| FRAME CHECKSUM                 : 4 bytes (CRC32C over header+payload)  |
+-----------------------------------------------------------------------+
```

### 3.2 Multiplexing & Connection Pooling
- Nodes establish long-lived TCP connection pools (`HyperionConnectionPool`).
- A single TCP connection supports thousands of concurrent asynchronous in-flight requests via unique 64-bit `CorrelationId`s.
- Response frames are demultiplexed immediately upon receipt and dispatched to the awaiting `TaskCompletionSource<RpcResponse>` without thread blocking.

---

## 4. Failure Detection: $\Phi$-Accrual Failure Detector

Traditional fixed-timeout heartbeats (e.g. "declare dead after 3 missed pings") fail in realistic networks where GC pauses, temporary packet loss, or CPU scheduling spikes create false positives.

Hyperion implements the **$\Phi$-Accrual Failure Detector** (Hayashibara et al.):
1. Each node maintains a sliding window of historical heartbeat arrival intervals $I = \{t_1, t_2, \dots, t_k\}$.
2. The arrival intervals are modeled as a normal distribution $(\mu, \sigma^2)$.
3. For elapsed time $t$ since the last heartbeat, the suspicion level $\Phi$ is computed:
   $$\Phi = -\log_{10}(P_{\text{later}}(t))$$
   where $P_{\text{later}}(t)$ is the probability that a heartbeat will arrive more than $t$ time units after the previous one.
4. If $\Phi > \Phi_{\text{threshold}}$ (default $\Phi = 8$, meaning a false positive probability of $10^{-8}$), the node is declared suspect and election or failover is triggered.
