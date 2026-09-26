# ADR-0002: Write-Ahead Logging (WAL) Before Data Mutation and ARIES Recovery

## Status
Accepted

## Context
In a database utilizing an in-memory buffer pool, dirty pages reside in volatile RAM. If the system loses power or the process crashes, dirty pages that have not yet been flushed to disk are lost. If dirty pages were flushed to disk before the transaction committed (STEAL policy), uncommitted dirty data would pollute the disk. 

To maintain the fundamental ACID property of Durability without requiring an immediate, random synchronous disk write of every modified 4KB page on commit (which would collapse write throughput), a logging protocol is required.

## Decision
Hyperion adopts strict **Write-Ahead Logging (WAL)** with physiological logging and the classical **ARIES (Algorithms for Recovery and Isolation Exploiting Semantics)** recovery protocol.

1. **WAL Invariant**: No dirty page is ever written to disk until the WAL record describing the mutation has been durably synced to disk (`FlushedLSN >= PageLSN`).
2. **Physiological Logging**: Log records contain physical page addresses (`PageId`, `SlotId`) with logical redo/undo operations, balancing compact log size with deterministic re-execution.
3. **No-Force / Steal Policy**:
   - *No-Force*: Commit does not force dirty data pages to disk (only the sequential WAL record is forced).
   - *Steal*: The buffer pool is permitted to evict and write dirty pages of active transactions to disk to free memory, provided the WAL record is flushed first.
4. **ARIES 3-Phase Recovery**:
   - *Analysis*: Scan forward from the latest checkpoint to reconstruct the Active Transaction Table (ATT) and Dirty Page Table (DPT).
   - *Redo ("Repeating History")*: Scan forward from the lowest `RecLSN` in the DPT to replay all logged operations, returning the database to the exact state at the moment of crash.
   - *Undo*: Scan backward from the crash point, rolling back changes made by uncommitted active transactions, and logging Compensation Log Records (CLRs).

## Alternatives Considered
- **Shadow Paging (Copy-on-Write)**: Rejected because it causes extreme physical page fragmentation, destroys spatial locality for sequential scans, and requires updating entire parent pointer paths up the B+ Tree.
- **Force / No-Steal**: Rejected because it forces random I/O at commit time and limits maximum transaction size to available buffer pool RAM.

## Tradeoffs
- **Positives**: Maximizes sequential write performance through group commit; provides provably correct recovery even if repeated crashes occur during the recovery process itself.
- **Negatives**: High implementation complexity; requires careful bookkeeping of LSNs across pages, frames, and log records.

## Invariants
- `FlushedLSN >= PageLSN` before any page write.
- Every WAL record must contain a 32-bit CRC32C checksum verified prior to processing.
- ARIES Undo must write Compensation Log Records (CLRs) with `UndoNextLSN` pointers to guarantee bounded recovery time.

## Failure Consequences
Violating the WAL invariant allows uncommitted data to become permanent on disk without undo records, causing permanent database corruption after a crash.
