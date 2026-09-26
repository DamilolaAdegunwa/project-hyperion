# Project Hyperion — Deployment & Topology Model

## 1. Local and Multi-Node Topologies

Hyperion is architected to operate identically in a single local developer process, in a multi-process local cluster, or across containerized nodes in Docker / Kubernetes.

### 1.1 Standard 3-Node Topology
The recommended development and production cluster comprises 3 physical or container nodes:

```
[ Client / Hyperion CLI ]
         |
         v
+-------------------+      +-------------------+      +-------------------+
| Node 1 (Port 7001)|      | Node 2 (Port 7002)|      | Node 3 (Port 7003)|
| - Partition 1 (L) |<---->| - Partition 1 (F) |<---->| - Partition 1 (F) |
| - Partition 2 (F) |<---->| - Partition 2 (L) |<---->| - Partition 2 (F) |
| - Partition 3 (F) |<---->| - Partition 3 (F) |<---->| - Partition 3 (L) |
| /data/node1/      |      | /data/node2/      |      | /data/node3/      |
+-------------------+      +-------------------+      +-------------------+
```

---

## 2. Node Filesystem Layout

Each Hyperion storage node manages its own dedicated directory structure:

```
/var/lib/hyperion/data/
├── node.json              # Node identity (NodeId, ClusterId, GossipSeeds)
├── catalog/
│   └── schema.cat         # Relational schema catalog
├── wal/
│   ├── wal-00000001.log   # Active Write-Ahead Log segments
│   ├── wal-00000002.log
│   └── checkpoint.meta    # Last valid checkpoint record & ATT/DPT state
├── storage/
│   ├── tables.hdb         # Fixed 4KB Slotted page tables
│   └── indexes.hidx       # B+ Tree persistent index pages
├── lsm/
│   ├── MANIFEST-000001    # LSM version manifest
│   ├── L0/                # Level 0 SSTables
│   │   └── 000101.sst
│   └── L1/                # Level 1 SSTables
│       └── 000102.sst
└── raft/
    ├── raft.state         # Raft currentTerm & votedFor
    ├── raft.log           # Raft replicated log segments
    └── snapshots/         # State machine snapshots
```

---

## 3. Configuration & Startup

Nodes are bootstrapped via JSON configuration or CLI arguments:

```json
{
  "nodeId": 1,
  "clusterId": "hyperion-prod-alpha",
  "listenAddress": "0.0.0.0:7001",
  "dataDirectory": "./data/node-1",
  "peers": [
    { "nodeId": 1, "address": "127.0.0.1:7001" },
    { "nodeId": 2, "address": "127.0.0.1:7002" },
    { "nodeId": 3, "address": "127.0.0.1:7003" }
  ],
  "storage": {
    "pageSizeBytes": 4096,
    "bufferPoolCapacityPages": 16384,
    "walFlushIntervalMs": 5,
    "walSyncMode": "Fsync"
  },
  "raft": {
    "heartbeatIntervalMs": 50,
    "electionTimeoutMinMs": 150,
    "electionTimeoutMaxMs": 300,
    "snapshotThresholdEntries": 10000
  }
}
```

---

## 4. Docker & Orchestration

Hyperion includes a production `Dockerfile` leveraging multi-stage builds (`mcr.microsoft.com/dotnet/sdk:9.0` build container and `mcr.microsoft.com/dotnet/runtime:9.0-alpine` production runner) and a `docker-compose.yml` defining an isolated multi-node test network with simulated latency.
