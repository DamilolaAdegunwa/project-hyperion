# Project Hyperion — Security Architecture

## 1. Authentication & Identity

Hyperion provides a robust, native authentication framework for client and node connections without relying on external enterprise directories.

### 1.1 Password Hashing & Challenge-Response
- User credentials are stored in the system catalog table `_hyperion_users`.
- Passwords are never stored in plaintext. Hyperion uses **PBKDF2** (or salted SHA-256 with 100,000 iterations and a 32-byte cryptographic salt per user).
- **SASL / SCRAM-SHA-256** challenge-response handshake prevents password sniffing or replay attacks across untrusted networks.

---

## 2. Role-Based Access Control (RBAC)

Hyperion implements a fine-grained, hierarchical RBAC model:

### 2.1 Standard Roles
- `SUPERUSER` / `ADMIN`: Unrestricted cluster and catalog permissions.
- `DBA`: Table creation, alteration, indexing, vacuuming, and compaction triggers.
- `READ_WRITE`: `SELECT`, `INSERT`, `UPDATE`, `DELETE` operations on user tables.
- `READ_ONLY`: Restricted to `SELECT` and `EXPLAIN` queries.

### 2.2 Discrete Granular Permissions
```
CREATE_DATABASE | DROP_DATABASE
CREATE_TABLE    | DROP_TABLE    | ALTER_TABLE
CREATE_INDEX    | DROP_INDEX
SELECT          | INSERT        | UPDATE        | DELETE
EXECUTE_CHAOS   | TRIGGER_COMPACT
ADMIN
```

Every parsed SQL statement is validated by `Hyperion.Security.AuthorizationValidator` during the binding phase before query execution begins.

---

## 3. Transport Security & Audit Logging

### 3.1 Encrypted Wire Transport (TLS)
- Hyperion's custom binary protocol supports an initial TLS 1.3 handshake negotiation (`SslStream`), ensuring all node-to-node Raft replication and client-to-gateway queries are encrypted in transit.

### 3.2 Audit Logging
All security-relevant actions (authentication successes/failures, privilege escalations, DDL table drops, cluster membership changes) are emitted to a dedicated, append-only structured audit log:
```json
{
  "timestamp": "2026-09-26T22:15:00.123Z",
  "event": "AUTH_FAILURE",
  "client_ip": "192.168.1.45",
  "user": "root",
  "reason": "INVALID_CREDENTIALS",
  "trace_id": "84c50b74-3507-4a3e"
}
```

---

## 4. Explicit Security Limitations & Disclaimers

In accordance with Phase 19 requirements:
- Hyperion's security subsystem is designed for demonstration of distributed security fundamentals (RBAC, SASL, audit logging).
- Hyperion does **not** implement transparent on-disk encryption (TDE) at the page level out of the box (data files on disk are unencrypted).
- It is not certified for PCI-DSS or FIPS-140-3 compliance. Production deployment in untrusted environments requires running behind secure VPC boundaries and encrypted volume mounts (LUKS/dm-crypt).
