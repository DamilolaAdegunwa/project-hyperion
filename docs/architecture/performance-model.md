# Project Hyperion — Performance Model & Mechanical Sympathy

## 1. Mechanical Sympathy & Zero-Allocation Principles

Database engines must be designed with deep mechanical sympathy for hardware: CPU caches (L1/L2/L3), memory bus bandwidth, branch predictors, and storage controller I/O pipelines. Hyperion enforces zero-allocation principles on all critical paths.

### 1.1 Memory Hierarchy & Allocation Budget
- **Hot-Path Allocations = 0**: Reading pages, evaluating filter predicates, calculating checksums, and decoding binary network frames must never allocate on the managed .NET garbage-collected heap.
- **`Span<T>` and `ReadOnlySpan<T>`**: Used universally for memory slicing without buffer copying.
- **`ArrayPool<byte>.Shared`**: Reusable scratch buffers for network serialization and batch decompression.
- **Unmanaged Memory Buffers**: Buffer pool frames are allocated via `NativeMemory.AllocAligned(4096, 4096)` or pre-allocated fixed arrays, completely bypassing Gen0/Gen1 GC collections.
- **Struct Iterators**: Query operators pass `ref struct` row spans or compact unmanaged `RowBatch` handles.

---

## 2. Amplification Factors: Storage Engine Tradeoffs

A fundamental database engineering concept is the tradeoff between three amplification factors:

$$\text{Tradeoff Space} = \{\text{Write Amplification (WA)}, \text{Read Amplification (RA)}, \text{Space Amplification (SA)}\}$$

### 2.1 B+ Tree Engine (Slotted Pages)
- **Write Amplification (WA)**: High ($O(\text{PageSize} / \text{RecordSize})$). Modifying a 64-byte row requires writing an entire 4096-byte page during buffer pool flush ($\approx 64\times$).
- **Read Amplification (RA)**: Low ($O(\log_B N)$). Point lookup traverses root $\to$ interior $\to$ leaf $\to$ slotted record with typically $3$ or $4$ page reads.
- **Space Amplification (SA)**: Moderate ($1.3\times - 2.0\times$) due to internal node fill factors ($\approx 67\%$).

### 2.2 LSM-Tree Engine
- **Write Amplification (WA)**: Low on ingest ($O(1)$ sequential append to WAL and in-memory MemTable); increases during background compaction ($O(L \times \text{Fanout})$).
- **Read Amplification (RA)**: High without optimization ($O(L \times \log N)$), as multiple SSTable levels must be probed. Mitigated to $\approx 1$ disk read via **Bloom Filters** and **Block Indexes**.
- **Space Amplification (SA)**: Low to moderate ($1.1\times - 1.5\times$), as SSTables are packed sequentially without fragmentation.

```
+-------------------------------------------------------------------------+
| WORKLOAD CHARACTERISTIC      | B+ TREE ENGINE       | LSM-TREE ENGINE   |
| Point Lookup by Primary Key  | Optimal (3-4 I/Os)   | Fast (Bloom Filter)|
| High-Throughput Batch Insert | Bottlenecked by I/O  | Peak Throughput   |
| Full Range Scan              | Excellent (Linked)   | Merge-Iterator    |
| In-place Small Updates       | Direct via Page      | Append as Tombstone|
+-------------------------------------------------------------------------+
```

---

## 3. Network & Consensus Latency Model

In a distributed 3-node cluster, end-to-end write latency is modeled as:

$$T_{\text{write}} = T_{\text{client\_net}} + T_{\text{parse}} + T_{\text{raft\_propose}} + \max(T_{\text{peer1\_net}} + T_{\text{peer1\_fsync}}, T_{\text{peer2\_net}} + T_{\text{peer2\_fsync}}) + T_{\text{apply}} + T_{\text{client\_resp}}$$

Because Hyperion uses **Multi-Raft**, concurrent writes to distinct partitions execute in parallel without cross-partition lock contention, scaling write throughput linearly with cluster node count.
