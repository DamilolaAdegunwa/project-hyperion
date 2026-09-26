# Project Hyperion — Failure Model & Fault Matrix

## 1. Threat & Fault Classification

Hyperion assumes an asynchronous, crash-recovery distributed network model with crash-stop or crash-recovery nodes and fail-stop storage faults (non-Byzantine):

1. **Storage Nodes**: Nodes may crash unexpectedly at any instruction (SIGKILL, kernel panic, power loss) and restart later with their non-volatile disk intact.
2. **Network**: The network is asynchronous. Messages may be arbitrarily delayed, dropped, reordered, or duplicated. Network partitions may isolate any subset of nodes indefinitely.
3. **Storage Device**: Disks may produce torn page writes if power fails mid-write, partial WAL flushes, or latent bit corruption (bit rot). Disks do not maliciously forge data (fail-stop rather than Byzantine).
4. **Clocks**: Hardware clocks may drift. Hyperion does **not** rely on synchronized physical clocks for safety or linearizability (Raft logical terms and Lamport/Hybrid Logical Clocks are used instead).

---

## 2. Comprehensive Failure Matrix

| Failure Mode | Immediate System Behavior | Recovery Mechanism | Violated Invariant If Unhandled | Hyperion Mitigation |
|---|---|---|---|---|
| **Process Crash During WAL Flush** | Node process terminates immediately; in-flight OS page cache buffer may be lost or truncated. | Upon restart, WAL Reader verifies CRC32C of each record. The first corrupted/truncated record at tail is discarded. All prior valid records are replayed. | Durability of un-acked writes; Consistency of committed transactions. | Tail truncation safety: un-acknowledged transactions are undone during ARIES phase 3. |
| **Torn Page Write** | Power cuts out while a 4096-byte page is half-written to disk block. | The page header CRC32C fails verification on read. | Atomicity & Storage Integrity. | Double-write buffer / Full Page Write (FPW) in WAL: The first time a page is dirtied after a checkpoint, its full 4KB image is logged in the WAL. |
| **Bit Rot / Corrupted Block** | Disk block sector decays silently. | Page CRC32C fails during `FetchPage()`. | Storage Integrity. | Node marks frame invalid, aborts transaction, triggers recovery from Raft replica group via snapshot/log catchup. |
| **Raft Leader Crash** | Leader stops sending `AppendEntries` heartbeats. | Followers detect heartbeat timeout via $\Phi$-accrual failure detector, increment `currentTerm`, become Candidates, and elect new leader. | Availability (transient); Election Safety. | Randomized election timeouts (150-300ms) prevent split votes; strict majority quorum ($\lfloor N/2 \rfloor + 1$) guarantees single leader. |
| **Symmetric Network Partition (Split-Brain)** | Cluster split into $\{N_1, N_2\}$ and $\{N_3, N_4, N_5\}$. | The minority partition cannot achieve quorum (needs 3 votes). Leader in minority cannot commit entries. Majority partition elects new leader and makes progress. | Linearizability; State Machine Safety. | Quorum intersection property: Any two majorities share at least one node. |
| **Asymmetric Network Partition** | Node $A$ can send to $B$, but not receive from $B$. | Pre-vote protocol: Candidate must receive pre-votes from a majority before incrementing its term, preventing disruptive stale term increments. | Liveness / Unnecessary Leader Stepping Down. | Hyperion Raft implements Pre-Vote phase. |
| **Network Packet Duplication / Reordering** | Network repeats old RPC requests. | Every RPC contains `Term`, `LeaderId`, `LogIndex`, and unique `CorrelationId`. | At-most-once execution; Idempotency. | Receiver checks term and log index. Duplicate log appends are idempotent (`prevLogIndex` alignment). |
| **Coordinator Crash During 2PC Prepare** | 2PC Coordinator dies before writing `CommitDecided` log. | On recovery, Coordinator finds uncommitted 2PC log. Presumed Abort triggers: Coordinator broadcasts `Abort` to participants. | Transaction Atomicity. | Durable 2PC coordinator log; Participants timeout and query coordinator or abort. |
| **Coordinator Crash During 2PC Commit** | 2PC Coordinator dies after writing `CommitDecided` log. | On recovery, Coordinator reloads `CommitDecided` state from WAL and replays `Commit` to all participants until acknowledged. | Transaction Atomicity. | Coordinator commits must be durable (`fsync`) before any participant receives commit signal. |
| **Compaction Crash in LSM Engine** | Process crashes midway through merging SSTable blocks into new Level. | The Manifest file has not recorded the new SSTable. Incomplete SSTable is orphaned. | Data Loss or Duplicate Keys. | Atomic Manifest update via VersionEdit: SSTable files are only registered when atomic Manifest fsync completes. Orphan files are deleted at startup cleanup. |
| **Checkpointer Crash** | Crash while writing checkpoint record to disk. | Recovery Manager falls back to previous valid checkpoint and replays WAL from that checkpoint's start LSN. | Bounded Recovery Time. | Checkpoint record has its own CRC32C and master pointer updated atomically via rename/fsync. |
