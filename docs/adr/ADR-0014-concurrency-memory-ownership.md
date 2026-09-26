# ADR-0014: Concurrency Model, Memory Ownership, and Allocation Discipline

## Status
Accepted

## Context
High-performance database engines written in managed runtimes (.NET / Java) frequently suffer from performance degradation due to Garbage Collection (GC) pauses, excessive lock contention, thread-pool starvation, and uncoordinated asynchronous state machines. A database engine requires mechanical sympathy, explicit resource ownership, and strict separation between CPU-bound, I/O-bound, and synchronization work.

## Decision
Hyperion establishes clear systems-level engineering standards across the codebase:

### 1. Clear Workload Segregation
- **CPU-Bound Work** (e.g. Parsing, Sorting, Hashing, Checksumming, Expression Evaluation): Executed synchronously on dedicated compute threads or pooled task threads. Does not yield via `async/await` in tight loops.
- **I/O-Bound Work** (e.g. Asynchronous disk reads, WAL fsync, TCP socket I/O): Uses `ValueTask` / `Task` with `ConfigureAwait(false)`. Never blocks threads synchronously with `.Result` or `.Wait()`.
- **Background Engine Workers** (e.g. WAL Flusher, LSM Compaction, Vacuum Cleaner, Deadlock Detector, Raft Tickers): Dedicated long-running threads (`ThreadPriority.AboveNormal` for WAL/Raft) executing non-blocking event loops over `Channel<T>`.

### 2. Memory Ownership & Zero-Allocation Hot Paths
- **`ReadOnlySpan<byte>` and `Span<byte>`**: Hot paths (record parsing, slot decoding, page checksum calculation, frame serialization) accept spans and return zero allocations on the managed heap.
- **ArrayPool & MemoryPool**: Byte buffers for temporary network frames, WAL batches, and SSTable compression blocks are rented from `ArrayPool<byte>.Shared` and returned in `finally` blocks.
- **Buffer Pool Native Memory**: Fixed 4KB page frames are managed in pre-allocated unmanaged memory or pinned byte arrays, completely insulated from .NET GC Gen 0/1/2 sweeps.
- **`ref struct` & Value Types**: Iterators and tuple accessors are structured as value types where practical to avoid heap allocations.

### 3. Synchronization & Concurrency Primitives
- **Page Latching**: Managed via lightweight `ReaderWriterLockSlim` or atomic spin-locks with backoff, ensuring multi-threaded read access to shared buffer frames.
- **Lock-Free Rings**: The WAL submission queue utilizes lock-free ring buffers (`Interlocked.Increment`) for thread-safe multi-producer single-consumer batching.
- **Bounded Channels**: Inter-thread and network communication utilizes `System.Threading.Channels.Channel<T>` with `BoundedChannelOptions` and `BoundedChannelFullMode.Wait` to enforce deterministic backpressure.

## Alternatives Considered
- **Blind Async/Await everywhere**: Rejected because async state machine allocations (`<MoveNext>d__State` objects) in tight database iterator loops degrade throughput by 40–60%.
- **Unchecked Unsafe Memory / Raw Pointers everywhere**: Rejected except where strictly justified for memory-mapped I/O or native interop, to retain .NET's memory safety guarantees.

## Tradeoffs
- **Positives**: Predictable sub-millisecond latencies; eliminates GC pause spikes (STW); predictable CPU cache behavior.
- **Negatives**: Requires disciplined engineering; manual memory lifetime management (renting/returning to pools) requires strict adherence to `IDisposable` patterns to avoid memory leaks.

## Invariants
- Memory rented from `ArrayPool<byte>` must be returned in a `finally` block or disposable wrapper.
- No heap-allocating LINQ queries or boxing operations are permitted inside hot-path query execution or WAL flusher loops.

## Failure Consequences
Buffer pool frame leaks or unreturned pooled arrays cause memory leaks, eventually triggering `OutOfMemoryException` and node failure.
