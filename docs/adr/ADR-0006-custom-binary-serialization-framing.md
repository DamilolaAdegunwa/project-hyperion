# ADR-0006: Custom Binary Serialization & Framing Format

## Status
Accepted

## Context
A high-throughput distributed database spends a significant percentage of its CPU cycles serializing, transmitting, and deserializing messages across network sockets and disk files (WAL entries, SSTable blocks, Raft RPC messages). General-purpose serialization frameworks (JSON, XML, or reflection-heavy .NET serializers) generate massive garbage-collection pressure, unaligned memory access, and high CPU parsing overhead.

## Decision
Hyperion implements a custom, zero-allocation **Binary Serialization and Framing Engine** built directly on top of `Span<byte>`, `ReadOnlySpan<byte>`, and `BinaryPrimitives` in little-endian format.

Key properties:
1. **Zero-Allocation Deserialization**: Fixed-size structures (`PageHeader`, `FrameHeader`, `WalHeader`, `RaftState`) are decoded directly into unmanaged `readonly struct`s without heap allocations.
2. **Explicit Length-Prefixed Fields**: Variable-length strings and byte blobs are encoded with 2-byte or 4-byte length prefixes.
3. **Hardware CRC32C Frame Trailers**: Every network and on-disk frame terminates with a 4-byte CRC32C checksum verifying data integrity before deserializing payloads.
4. **Binary Compatibility**: Fields are ordered deterministically by descending byte alignment (8-byte primitives $\to$ 4-byte primitives $\to$ 2-byte primitives $\to$ 1-byte flags) to prevent unaligned memory faults.

## Alternatives Considered
- **JSON**: Rejected due to high string allocation overhead, 3–5x larger payload size, and slow CPU parsing.
- **Protocol Buffers (Google.Protobuf)**: Excellent wire format, but introduces external code generation dependencies and object allocations for every decoded message.
- **MessagePack**: Fast, but generic typemaps add boxing and heap allocations compared to pure native span decoding.

## Tradeoffs
- **Positives**: Extreme performance (tens of millions of messages decoded per second per core); zero GC allocation on hot paths; absolute control over wire layout.
- **Negatives**: Requires writing explicit, defensive binary serialization methods and unit tests for every message type.

## Invariants
- All integer and floating-point primitives are serialized in little-endian format.
- Any message whose payload length exceeds the framing header or whose CRC32C fails is rejected immediately before memory allocation.

## Failure Consequences
A buffer overflow, underflow, or miscalculated frame length could crash a node process or expose memory leaks. Defensive bounds checking on all `Span<byte>` decoders is mandatory.
