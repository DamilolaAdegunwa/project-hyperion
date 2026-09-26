# ADR-0011: Custom Binary TCP Protocol with Multiplexing and Backpressure

## Status
Accepted

## Context
Inter-node communication in distributed databases (Raft log replication, heartbeat monitoring, 2PC prepare/commit phases, distributed scatter-gather queries) demands extreme throughput, minimal serialization overhead, deterministic timeouts, and backpressure. Utilizing HTTP/1.1 or generic REST APIs introduces unacceptable latency spikes, heavy HTTP header parsing overhead, and head-of-line blocking.

## Decision
Hyperion implements a custom, asynchronous binary RPC transport over long-lived TCP sockets (`System.Net.Sockets.Socket` using async `SocketAsyncEventArgs` or `ValueTask<int>` over `NetworkStream`):

1. **Multiplexing**: Multiple concurrent client or inter-node requests share a single underlying TCP connection using 64-bit `CorrelationId`s in the 20-byte frame header.
2. **Fixed Framing & Hardware Checksumming**: Every frame begins with magic `0x48595052` ("HYPR"), length prefix, message code, correlation ID, and ends with a 32-bit CRC32C checksum.
3. **Bounded Backpressure via Channels**: Network read/write queues are backed by bounded `System.Threading.Channels.Channel<T>`. When the outbound channel is full, upstream producers are asynchronously suspended rather than buffering unboundedly in RAM until an `OutOfMemoryException` occurs.
4. **Idempotency Tokens**: Every mutating RPC includes a client-generated UUID `RequestId`. If a network timeout occurs and the client retries, the server checks its recent `IdempotencyCache` and returns the previously computed result instead of re-executing mutations.

## Alternatives Considered
- **gRPC (HTTP/2 + Protobuf)**: High-quality industry standard, but adds third-party package dependencies, complex C-core bindings, and restricts granular low-level buffer manipulation with native Spans.
- **ASP.NET Core Kestrel WebSockets**: Heavy dependency chain unsuited for embedded systems-level database node networking.

## Tradeoffs
- **Positives**: Minimal latency (sub-millisecond inter-node round-trip); zero unnecessary allocations; complete control over socket buffer sizing, TCP_NODELAY, and backpressure policies.
- **Negatives**: Requires writing custom framing parsers, connection pool lifecycles, and reconnection state machines.

## Invariants
- Frame length must never exceed maximum configured frame size (default 16MB) to prevent malicious memory exhaustion attacks.
- Outbound socket write channels must be bounded.

## Failure Consequences
Unbounded network queues under backpressure would exhaust node memory during slow network links or disk I/O stalls, killing the process.
