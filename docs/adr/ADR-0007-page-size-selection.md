# ADR-0007: Page Size Selection (4096-Byte Slotted Pages)

## Status
Accepted

## Context
In a page-based relational storage engine, the choice of page size determines:
1. Disk I/O granularity and write amplification.
2. Buffer pool memory utilization and cache fragmentation.
3. B+ Tree fanout and tree height.
4. Concurrency contention (page latching).

Common database page sizes range from 4KB (InnoDB standard default 16KB, Postgres default 8KB, SQLite default 4KB).

## Decision
Hyperion sets the standard database page size to **4096 bytes (4KB)**.

Reasons:
1. **OS Page Alignment**: Linux and macOS virtual memory subsystems use 4096-byte pages. Aligning database page buffers to 4KB guarantees that a single page read or write maps to exactly one OS memory page, avoiding split-page double page faults.
2. **NVMe / SSD Physical Sector Alignment**: Modern Advanced Format SSDs and NVMe drives employ native 4096-byte physical sectors (4Kn). Writing 4KB ensures atomicity at the drive controller boundary and avoids Read-Modify-Write (RMW) write amplification in the flash translation layer (FTL).
3. **Reduced Latch Contention**: Smaller pages (4KB vs 16KB or 64KB) contain fewer concurrent records per page, drastically reducing lock and latch contention during concurrent inserts and updates.
4. **Fast Transfer**: 4KB pages fit neatly into network packet MTU clusters when transmitting state snapshots or page images across nodes.

## Alternatives Considered
- **8192 bytes (8KB - PostgreSQL default)**: Higher B+ Tree fanout, but increases write amplification on small point updates.
- **16384 bytes (16KB - MySQL InnoDB default)**: Great for table scans and high-fanout indexes, but suffers from high page latch contention and massive write amplification for small random writes.
- **Variable-size Pages**: Rejected due to catastrophic free-space external fragmentation on disk.

## Tradeoffs
- **Positives**: Minimal write amplification; zero RMW penalty on 4Kn SSDs; lower latch contention.
- **Negatives**: Slightly taller B+ Tree heights for multi-million row tables (e.g. height 4 instead of 3), requiring an additional internal node lookup (mitigated by buffer caching of root/interior nodes).

## Invariants
- `PageSize == 4096` bytes across all data, index, and overflow pages.
- Page offsets on disk must be exact multiples of 4096 (`Offset % 4096 == 0`).

## Failure Consequences
Misaligned page writes result in disk controller write amplification, severe performance penalties, and increased vulnerability to torn page writes during power loss.
