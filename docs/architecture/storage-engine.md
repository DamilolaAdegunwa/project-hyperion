# Project Hyperion — Storage Engine Architecture

## 1. Physical Page Architecture

Hyperion's relational engine organizes disk storage into fixed-size **4096-byte (4KB)** pages. The 4KB page size matches modern OS virtual memory page boundaries and NVMe/SSD physical sector sizes (4Kn), preventing write amplification caused by unaligned Read-Modify-Write cycles.

### 1.1 Page Layout (Slotted Page Format)

Hyperion employs the classical slotted-page architecture (pioneered by System R and refined in Postgres/Aries). This decouples internal record offsets from external `RecordId`s (`PageId` + `SlotIndex`), allowing in-place compaction within the page without invalidating external secondary index pointers.

```
+-----------------------------------------------------------------------+
| PAGE HEADER (32 bytes)                                                |
|  - Checksum (CRC32C)       : 4 bytes                                  |
|  - PageLSN                 : 8 bytes                                  |
|  - PageId (FileId + PageNo): 8 bytes                                  |
|  - PrevPageId              : 4 bytes                                  |
|  - NextPageId              : 4 bytes                                  |
|  - SlotCount               : 2 bytes                                  |
|  - FreeSpaceOffset         : 2 bytes                                  |
|  - PageType & Flags        : 2 bytes                                  |
+-----------------------------------------------------------------------+
| SLOT ARRAY (grows downward)                                           |
|  - Slot 0: [Offset: 2B, Length: 2B]                                   |
|  - Slot 1: [Offset: 2B, Length: 2B]                                   |
|  - Slot N: ...                                                        |
+-----------------------------------------------------------------------+
| FREE SPACE REGION                                                     |
|                                                                       |
| (Compacted dynamically when space is fragmented)                      |
|                                                                       |
+-----------------------------------------------------------------------+
| TUPLE STORAGE AREA (grows upward from bottom)                         |
|  - Record N ...                                                       |
|  - Record 1                                                           |
|  - Record 0                                                           |
+-----------------------------------------------------------------------+
```

### 1.2 Binary Layout Specification

```csharp
[StructLayout(LayoutKind.Explicit, Size = 32)]
public readonly struct PageHeader
{
    [FieldOffset(0)]  public readonly uint Checksum;          // CRC32C of bytes 4..4095
    [FieldOffset(4)]  public readonly ulong PageLsn;          // Last LSN that modified this page
    [FieldOffset(12)] public readonly uint PageId;           // Logical Page Number in File
    [FieldOffset(16)] public readonly uint PrevPageId;       // Doubly-linked page list (for scans)
    [FieldOffset(20)] public readonly uint NextPageId;       // Doubly-linked page list
    [FieldOffset(24)] public readonly ushort SlotCount;       // Number of record slots allocated
    [FieldOffset(26)] public readonly ushort FreeSpaceOffset; // Byte offset where next record payload starts
    [FieldOffset(28)] public readonly byte PageType;          // DataPage, BTreeInterior, BTreeLeaf, Overflow
    [FieldOffset(29)] public readonly byte Flags;             // Dirty, Compressed, Fragmented
    [FieldOffset(30)] public readonly ushort Reserved;        // 2-byte alignment padding
}
```

### 1.3 Slot Entry
Each slot is a compact 4-byte descriptor:
```csharp
[StructLayout(LayoutKind.Sequential, Pack = 2)]
public readonly record struct PageSlot(ushort Offset, ushort Length);
```
- A slot length of `0` or offset `0` indicates a deleted tombstone slot that can be reclaimed during defragmentation.
- When an update shrinks a record, the slot length is modified and the free space pointer is adjusted or marked fragmented.
- When a page becomes fragmented, `DefragmentPage()` compacts all active tuples to the bottom of the page, resetting `FreeSpaceOffset` sequentially.

---

## 2. Checksum and Integrity Verification

Integrity verification is non-negotiable. Every page read from disk must be verified before decoding:
1. When writing a page, `Checksum` in the header is set to `0`.
2. A hardware-accelerated CRC32C (`Castagnoli`) hash is computed over all 4096 bytes.
3. The computed 32-bit checksum is stored into bytes 0..3 of the header.
4. On read, the checksum in bytes 0..3 is extracted, temporarily zeroed in memory, and recomputed over the 4096 bytes. If `computed != stored`, a `CorruptedPageException` is thrown, aborting the read and alerting the recovery manager.

---

## 3. Buffer Pool Manager

The `BufferPoolManager` mediates between disk storage and in-memory query execution. It prevents arbitrary I/O by caching fixed-size 4KB frames in a pre-allocated unmanaged memory pool.

```mermaid
graph TD
    subgraph Execution Layer
        WORKER["Worker Thread / Query Operator"]
    end

    subgraph Buffer Pool Manager
        PT["Page Table (Concurrent Dictionary PageId -> FrameId)"]
        FT["Frame Table (Array of BufferFrame structs)"]
        REPL["Replacer (Clock-Pro / 2Q)"]
        FREE["Free Frame Queue"]
    end

    subgraph Storage Layer
        DISK["Disk File Manager (Random Access 4KB Blocks)"]
        WAL["Write-Ahead Log (fsync)"]
    end

    WORKER -->|FetchPage(PageId)| PT
    PT -->|Hit| FT
    PT -->|Miss| FREE
    FREE -->|No free frames| REPL
    REPL -->|Evict victim frame| FT
    FT -->|If Dirty: Check WAL Flush LSN >= PageLSN| WAL
    WAL -->|Flushed| DISK
    DISK -->|Read 4KB into Frame| FT
```

### 3.1 Buffer Frame Lifecycle
Each frame holds:
- **`Memory<byte>`**: Exactly 4096 bytes aligned to memory page boundaries.
- **`PageId`**: The currently cached page (or `InvalidPageId`).
- **`PinCount`**: Number of concurrent active readers/writers. A frame with `PinCount > 0` cannot be evicted.
- **`IsDirty`**: Boolean flag set when memory is mutated.
- **`ReaderWriterLockSlim` / SpinLock**: Fine-grained page latching to support concurrent shared reads and exclusive writes.

### 3.2 Eviction Policy: Clock-Pro
To prevent sequential table scans from polluting the buffer cache and evicting hot index pages ("cache thrashing"), Hyperion implements a Clock-Pro / 2Q hybrid algorithm distinguishing:
- **Cold Pages**: Pages referenced once (probationary queue).
- **Hot Pages**: Pages referenced multiple times (promoted to protected ring).
- **Test Pages**: Tracking metadata for recently evicted pages to dynamically adapt hot vs. cold capacity.

---

## 4. Write-Ahead Logging (WAL) and Durability

Hyperion guarantees the Write-Ahead Logging invariant:
> **The WAL Invariant:** No dirty data page is written to permanent storage until the WAL record describing that change has been durably persisted to disk.
> $$\text{FlushedLSN} \ge \text{PageLSN}$$

### 4.1 Monotonic Log Sequence Numbers (LSN)
Every WAL record has a 64-bit monotonically increasing `LogSequenceNumber` (LSN). The LSN corresponds to the physical byte offset in the append-only WAL stream, ensuring $LSN_a < LSN_b \iff a \text{ occurred before } b$.

### 4.2 WAL Record Binary Format
```
+-------------------------------------------------------------------------+
| WAL RECORD HEADER (24 bytes)                                            |
|  - Checksum (CRC32C)       : 4 bytes                                    |
|  - LSN                     : 8 bytes (Physical stream offset)           |
|  - PrevLSN                 : 8 bytes (LSN of previous record in this tx)|
|  - TxId                    : 8 bytes (Transaction identifier)           |
|  - RecordType              : 1 byte  (Begin, Update, Commit, Abort, CLR)|
|  - PayloadLength           : 4 bytes (Length of Redo/Undo bytes)        |
+-------------------------------------------------------------------------+
| PAYLOAD (Variable length)                                               |
|  - Redo Data (e.g. PageId, SlotIndex, NewBytes)                         |
|  - Undo Data (e.g. PageId, SlotIndex, OldBytes)                         |
+-------------------------------------------------------------------------+
```

### 4.3 Group Commit
To achieve high write throughput without sacrificing durability:
- Incoming transactions append their `LogRecord` to a concurrent lock-free ring buffer (`WalRingBuffer`).
- A dedicated background `WalFlusher` worker thread uses `Channel<TaskCompletionSource>` to batch concurrent commit requests.
- The flusher writes all accumulated records sequentially in a single contiguous I/O operation and invokes `FileStream.Flush(flushToDisk: true)` (`fsync`).
- All waiting transactions in the batch are notified concurrently upon return from `fsync`.

---

## 5. Crash Recovery: ARIES-Style Algorithm

Hyperion implements the gold-standard ARIES (Algorithms for Recovery and Isolation Exploiting Semantics) recovery protocol across three phases:

```
        CRASH
          |
          v
   +--------------+
   |   PHASE 1    |  Scan forward from last Checkpoint to End of Log.
   |   ANALYSIS   |  Reconstruct Active Transaction Table (ATT) & Dirty Page Table (DPT).
   +-------+------+
           |
           v
   +--------------+
   |   PHASE 2    |  Scan forward from minimum RecLSN in DPT.
   |     REDO     |  Replay all logged changes (committed & uncommitted).
   |              |  Brings database to exact state at moment of crash.
   +-------+------+
           |
           v
   +--------------+
   |   PHASE 3    |  Scan backward through WAL for all active (uncommitted) txns.
   |     UNDO     |  Rollback uncommitted changes using Undo payloads.
   |              |  Write Compensation Log Records (CLRs) to prevent undo loops.
   +--------------+
           |
           v
   RECOVERY COMPLETE (Ready for new transactions)
```

### 5.1 Compensation Log Records (CLRs)
During Phase 3 (Undo), every undone operation logs a `CompensationLogRecord` containing an `UndoNextLSN` pointer. If the database crashes *during recovery*, it will not attempt to undo already-undone actions, ensuring strictly bounded recovery time.
