# ADR-0005: Transaction Isolation Models & Anomaly Guarantees

## Status
Accepted

## Context
ANSI SQL-92 defines four standard isolation levels (`Read Uncommitted`, `Read Committed`, `Repeatable Read`, `Serializable`) based on three phenomena (Dirty Read, Non-Repeatable Read, Phantom Read). However, Berenson et al. (1995) demonstrated that ANSI definitions are incomplete, failing to address Snapshot Isolation anomalies such as Write Skew and Lost Updates. Hyperion must provide explicit, mathematically verifiable isolation guarantees.

## Decision
Hyperion implements three explicit isolation levels:

1. **`Read Committed`**:
   - Each statement within the transaction takes a fresh snapshot at statement start.
   - Prevents: Dirty Reads.
   - Permits: Non-Repeatable Reads, Phantoms, Write Skew.
2. **`Repeatable Read` (Snapshot Isolation)**:
   - The transaction takes a single immutable snapshot at transaction start.
   - Enforces the **First-Committer-Wins** rule on concurrent write conflicts: if two transactions attempt to update/delete the same key concurrently, the second committer is aborted with a serialization failure.
   - Prevents: Dirty Reads, Non-Repeatable Reads, Phantom Reads, Lost Updates.
   - Permits: Write Skew.
3. **`Serializable` (Serializable Snapshot Isolation / Strict 2PL)**:
   - Tracks read-write dependency conflicts (rw-antidependencies / $T_1 \xrightarrow{rw} T_2$) using `SIREAD` lock flags on examined keys and index ranges.
   - If a cycle of two consecutive $rw$-antidependency edges ($T_1 \xrightarrow{rw} T_2 \xrightarrow{rw} T_3 \dots$) is detected, the transaction is aborted.
   - Prevents: All concurrency anomalies including Write Skew.
   - Guarantees: Full serializable execution order.

## Alternatives Considered
- **Pure Strict 2PL (Pessimistic Locking)**: Rejected for general queries due to severe read-write lock contention. Retained as an optional locking mode via `SELECT ... FOR UPDATE`.
- **Read Uncommitted**: Deliberately omitted; dirty reads violate basic relational integrity and corrupt database invariants.

## Tradeoffs
- **Positives**: Balances maximum throughput under Read Committed / Repeatable Read with provable mathematical correctness under Serializable.
- **Negatives**: Serializable SSI tracking adds small memory overhead for SIREAD lock tracking in the lock manager.

## Invariants
- An uncommitted write cannot be seen by any other transaction under any isolation level.
- Under Serializable, concurrent execution must be equivalent to some serial order.

## Failure Consequences
Improper dependency tracking could allow write skew anomalies under Serializable isolation, violating database business invariants (e.g. negative bank balances or overlapping doctor on-call shifts).
