# RusticalandOPS

RusticalandOPS is a local-first operations stack for managing Rust Dedicated servers. It combines a Bash control layer, an ASP.NET API, a long-running C# agent, and a Steam chat transport adapter. The system is designed to run on a Debian/Linux host under `systemd`, with local file inbox/outbox boundaries and optional local or OpenAI-compatible LLM/embedding endpoints.

## Table of Contents

- [Features](#features)
- [Quick Start](#quick-start)
- [Prerequisites](#prerequisites)
- [Architecture](#architecture)
- [Deployment](#deployment)
- [Configuration](#configuration)
- [API Documentation](#api-documentation)
- [Usage Examples](#usage-examples)
- [Memory System](#memory-system)
- [Development](#development)
- [Troubleshooting](#troubleshooting)
- [Performance & Security](#performance--security)
- [Contributing](#contributing)
- [FAQ](#faq)

## Features

### Core Server Management
- **Lifecycle Control**: Start, stop, restart, update, wipe, and status monitoring for Rust servers
- **Player Management**: Real-time player lookup, inventory inspection, and moderation tools
- **Server Configuration**: Live server convar and command execution with semantic memory
- **Plugin Management**: Install, validate, update, and configure Oxide/uMod plugins
- **Log Analysis**: Centralized log aggregation with error pattern recognition
- **Network Configuration**: Port management, firewall integration, and network diagnostics

### Intelligent Agent System
- **Natural Language Processing**: Classify and route admin requests via LLM or heuristics
- **Semantic Memory**: Context-aware recall of server state, procedures, and failures
- **Autonomous Operations**: Handle routine tasks without admin intervention
- **Multi-Server Coordination**: Manage local and remote servers uniformly
- **Incident Review**: Automated analysis of errors and corrective action tracking

### Transport & Integration
- **Steam Chat Interface**: Full admin control via Steam messages
- **REST API**: Comprehensive HTTP API for external integrations
- **Remote Agent Protocol**: Manage distant servers with full parity
- **RCON Protocol**: Legacy WebRCON and source RCON support
- **File-Based Workflows**: Inbox/outbox boundaries for stateless operation

### Advanced Features
- **Plugin Config Editing**: Modify plugin JSON configurations in place with natural language
- **Plugin Reference Index**: Indexed plugin metadata, commands, hooks, and permissions
- **Multi-Model LLM Support**: Fast classification model, deep analysis model, response composition model
- **OpenAI-Compatible Embeddings**: Local or remote semantic memory backends
- **Git Integration**: Auto-pull, rebuild, and restart workflows for CI/CD pipelines
- **Error Classification & Learning**: Adaptive correction rules and incident tracking

## Quick Start

### 5-Minute Local Setup

```bash
# Clone the repository
git clone https://github.com/yourusername/RusticalandOPS.git
cd RusticalandOPS

# Copy example configs
cp config.env.example config.env
cp agent/RustOpsAgent/agentsettings.example.json agent/RustOpsAgent/agentsettings.json

# Edit config.env with your settings (API keys, endpoints, etc.)
nano config.env

# Build the project
dotnet build

# Run the agent
dotnet run --project agent/RustOpsAgent/RustOpsAgent.csproj

# In another terminal, run the API
dotnet run --project api/rustmgrapi.csproj
```

Visit `http://localhost:2077/ui` to access the dashboard.

### Production Deployment (Single Command)

On a fresh Debian host:

```bash
sudo bash deploy/install-agent.sh
# Edit /etc/rustops/config.env
systemctl start rustmgrapi rustopsagent
```

## Prerequisites

### System Requirements

- **OS**: Debian 11+ or Ubuntu 20.04+
- **CPU**: 2+ cores recommended
- **.NET Runtime**: .NET 8.0 SDK for building, .NET 8.0 runtime for deployment
- **Disk**: 10+ GB for server installations and logs
- **Memory**: 1GB+ for agent, API, and plugins
- **Network**: Stable internet for Steam authentication and optional LLM endpoints

### Required Software

- `bash` 4.0+
- `steamcmd` (installed automatically by scripts)
- `systemd` (for service management)
- Optional: `docker` (for LLM endpoints like LM Studio, Ollama)

### External Services (Optional)

- **LLM Provider**: LM Studio, Ollama, or OpenAI-compatible endpoint
- **Embedding Provider**: Same as LLM provider (OpenAI-compatible HTTP)
- **Sentry**: Error tracking (optional)
- **GitHub**: For Git integration features

## Current State

The project is no longer just a thin command router.

Current live behavior includes:

- deterministic server lifecycle control through `rustmgr.sh`
- REST control-plane access through `api/`
- Steam chat to file-inbox transport through `SteamBot/`
- long-running admin request handling through `agent/RustOpsAgent/`
- semantic memory retrieval and write-back in the live agent workflow
- legacy operational state and policy tracking through NeoCortex/JSON stores
- background incident review, feedback handling, and classifier evolution loops
- local-first deployment without a required cloud dependency

The agent now uses semantic memory before planning and before tool execution, and writes structured success/failure memories after actions complete. Legacy state still exists, but semantic memory is now the primary retrieval path for planning and execution guidance.

## Architecture

### Component Stack

```text
┌─────────────────────────────────────────────────────────────────┐
│ Admin Interfaces                                                 │
├─────────────────────────────────────────────────────────────────┤
│ Steam Chat          REST API (localhost:2077)      Direct Bash   │
│ └─ SteamBot         └─ Dashboard UI               └─ rustmgr.sh  │
│    (SteamKit2)         (AspNetCore 8)                (Deterministic)
└────────┬──────────────────────┬──────────────────────────┬──────┘
         │                      │                          │
         └──────────────────────┴──────────────────────────┘
                        │
                        ▼
         ┌──────────────────────────────────┐
         │   RustOpsAgent (C# Worker)       │
         │  ┌──────────────────────────────┤
         │  │ Intent Classifier             │
         │  │ Semantic Memory Recall        │
         │  │ Tool Executor                 │
         │  │ Response Composer             │
         │  └──────────────────────────────┤
         │                                  │
         │  Inbox: chat, decision, feedback │
         │  Outbox: messages                │
         └────────────┬─────────────────────┘
                      │
                      ▼
         ┌──────────────────────────────────┐
         │    Semantic Memory System        │
         │  ┌──────────────────────────────┤
         │  │ SQLite Vector Store           │
         │  │ Planning/Execution Recall     │
         │  │ Success/Failure Write-back    │
         │  │ OpenAI-Compatible Embeddings  │
         │  └──────────────────────────────┤
         └────────────┬─────────────────────┘
                      │
                      ▼
         ┌──────────────────────────────────┐
         │    API Layer (ASP.NET 8)         │
         │  ┌──────────────────────────────┤
         │  │ Server Lifecycle Endpoints    │
         │  │ RCON/WebRCON Interface        │
         │  │ Config Management             │
         │  │ Plugin Operations             │
         │  │ Log Aggregation               │
         │  │ Agent State Inspection        │
         │  └──────────────────────────────┤
         └────────────┬─────────────────────┘
                      │
        ┌─────────────┴─────────────┐
        │                           │
        ▼                           ▼
┌──────────────────────┐   ┌──────────────────────┐
│   Local rustmgr.sh   │   │  Remote Agent Node   │
│ (Lifecycle Control)  │   │  (Debian Install)    │
└──────────┬───────────┘   └──────────┬───────────┘
           │                          │
           │                ┌─────────┴──────────┐
           │                │                    │
           ▼                ▼                    ▼
    ┌────────────┐    ┌─────────────┐   ┌───────────────┐
    │Rust Server │    │Rust Server B│   │Rust Server C  │
    │(Local)     │    │(Remote Host)│   │(Remote Host)  │
    └────────────┘    └─────────────┘   └───────────────┘
```

### Component Responsibilities

**Layer 1: Transport Adapters**
- SteamBot: Polls Steam chat, writes to inbox, reads from outbox
- REST API: Exposes web interface and programmatic access
- Bash: Direct CLI control for system administrators

**Layer 2: Agent (Reasoning Engine)**
- Inbox polling and request classification
- Semantic memory planning and execution recall
- Tool execution and error handling
- Response composition and outbox delivery
- Failure recovery and backoff strategies

**Layer 3: Memory System**
- Semantic vector storage with SQLite backend
- Planning-time and execution-time recall
- Success/failure write-back after tool execution
- Cross-topic deduplication and importance scoring
- Optional embedding provider (local or cloud)

**Layer 4: API (Control Plane)**
- Deterministic server lifecycle management
- RCON command execution and log streaming
- Configuration file reading/writing
- Plugin validation and management
- Agent state inspection and memory management

**Layer 5: Bash (Lifecycle Authority)**
- Deterministic server start/stop/restart
- Update and wipe operations
- Log file management
- System resource monitoring
- Direct subprocess management via tmux/screen

## Layer Status

### Layer 1: `rustmgr.sh`

Still the lifecycle authority for start/stop/restart/update/wipe/status/log/query operations. This remains the lowest-level deterministic control path and is still the final authority for host-side server operations.

### Layer 2: `api/`

The ASP.NET API remains the main deterministic interface over `rustmgr.sh`. It exposes server lifecycle, logs, players, RCON, config, plugin, network, and agent-state endpoints, plus the built-in `/ui` dashboard.

This layer is still functional, but it remains a large inline route surface and still needs broader endpoint test coverage.

### Layer 3: `remote-agent/RustOpsRemoteAgent/`

The remote agent is a Debian-installable host module for managing a Rust server on another machine. It runs beside that host's `rustmgr.sh` and exposes authenticated `/servers/{server}/...` endpoints for lifecycle actions, config, logs, command traces, WebRCON queries, and moderation.

The main API remote registry supports agent-backed entries using `agentBaseUrl`, `agentApiKey`, and optional `agentServerName`. Existing RCON-only remote entries still work for command/query operations, while agent-backed entries can be started, stopped, restarted, updated, wiped, and inspected through the normal server endpoints.

Use `deploy/setup-remote-node.sh` to set up a complete remote node on a fresh Debian host automatically, including steamcmd, rustmgr, the remote agent binary, and the systemd service. This is separate from RCON-only remote control; the remote node gives the agent the same degree of control it has over local servers.

### Layer 4: `agent/RustOpsAgent/`

This is the most actively evolving part of the stack.

The agent currently:

- polls `chat-inbox`, `decision-inbox`, and `feedback-inbox`
- classifies incoming admin requests with heuristics and optional LLM support
- routes work to Rust-specific tool handlers
- composes replies with a compose model or a deterministic fallback
- tracks incidents and operational state
- uses semantic memory for reusable failures, fixes, procedures, facts, and instructions
- keeps working when the embedding provider is unavailable

Registered live tool handlers currently include:

- `RustServerControlToolHandler`
- `RustStatusToolHandler`
- `RustPlayerLookupToolHandler`
- `RustRconToolHandler`
- `RustLogsToolHandler`
- `RustPluginToolHandler`
- `RustNetworkToolHandler`
- `RustFileEditToolHandler`
- `RustChatToolHandler`
- `RustServerManagementToolHandler`

### Layer 5: `SteamBot/OpsSteamBot/`

The Steam bot remains a transport adapter, not a reasoning engine. It forwards chat to inbox files, handles `approve` / `reject` / `feedback` shortcuts, and sends replies from `message-outbox`.

## Agent Runtime Flow

The real admin request path is:

1. a transport adapter writes a `ChatInboxItem` JSON file into `chat-inbox`
2. `AgentRuntime` loads operational state and conversation context
3. `AdminIntentClassifier` classifies the request and performs planning-time semantic recall
4. `AgentRuntime` performs execution-time semantic recall and attaches it to the execution context
5. `ActionExecutor` runs the selected tool or action
6. `AgentRuntime` writes structured semantic memory for success or failure outcomes
7. `ResponseComposer` builds the final reply
8. `AgentRuntime` writes the reply to `message-outbox`

This is live wiring, not an unused side subsystem. The semantic memory service is constructed once in `Program.cs` and passed into the runtime, classifier, executor, and Rust chat handler.

## Memory Model

The project currently uses two memory layers.

### Semantic memory

Semantic memory is now the primary retrieval path for planning and execution.

It stores structured records such as:

- `Fact`
- `Procedure`
- `Failure`
- `Fix`
- `UserInstruction`
- `ServerState`
- `ToolObservation`
- `Reflection`
- `Exception`
- `ServerConvar`
- `ServerCommand`
- `PluginSummary`

Current properties include structured type/scope/source metadata, tags, related entity ids, timestamps, importance/confidence fields, metadata, content hash deduplication, and optional embeddings.

Current semantic-memory behavior:

- planning recall in `AdminIntentClassifier`
- execution recall in `AgentRuntime`
- executor fallback recall only if runtime context is missing
- post-action write-back for both success and failure
- migration from legacy flat-file memory
- rebuild support for records imported without embeddings
- secret sanitization before persistence

The default backend is local SQLite with persisted metadata and vector storage. Search uses in-process cosine scoring with configurable guardrails, not an external ANN service.

### Legacy operational state

NeoCortex and legacy JSON state are still active. They currently retain non-semantic operational roles such as:

- conversation selection state
- classifier learned rules and correction history
- command policy state
- incident history
- log knowledge and ignore patterns
- recent runtime activity

This means the project is currently in a transition state:

- semantic memory is primary for planning/execution recall
- legacy state still holds important operational and policy data

## Embeddings

The embedding integration is OpenAI-compatible HTTP only.

Supported deployment style:

- LM Studio OpenAI-compatible embeddings endpoint
- Ollama OpenAI-compatible endpoint
- other OpenAI-compatible local or remote endpoints

Current behavior:

- base URLs with or without a trailing slash are supported
- API keys can be optional when config says they are not required
- HTTP failures, timeouts, empty vectors, and dimension mismatches are handled cleanly
- embedding failures do not crash the agent
- memory retrieval or write-back is skipped with logs when embeddings are unavailable

## Configuration

### Environment Variables (config.env)

Main configuration is provided via environment variables. Copy `config.env.example` to `config.env`:

```bash
cp config.env.example config.env
nano config.env
```

#### API Configuration

```env
# REST API endpoint
RUSTOPS_API_BASE_URL=http://127.0.0.1:2077
RUSTMGR_API_KEY=your-secure-api-key-here

# API server binding
RUSTMGR_BIND=http://0.0.0.0:2077

# Rust manager paths
RUSTMGR_PATH=/opt/rust-manager/rustmgr.sh
RUSTMGR_RUNTIME=/opt/rust-manager/runtime
RUSTMGR_CONFIG=/opt/rust-manager/config
RUSTMGR_TASKS_DIR=/opt/rust-manager/tasks
```

#### Remote Agent Configuration

```env
# Remote agent (for managing servers on other hosts)
RUSTOPS_REMOTE_AGENT_BIND=http://0.0.0.0:2088
RUSTOPS_REMOTE_AGENT_API_KEY=remote-agent-key-change-me

# When this host is managed by another RusticalandOPS instance
RUSTOPS_REMOTE_AGENT_BASE_URL=http://remote-host:2088
RUSTOPS_REMOTE_AGENT_API_KEY=api-key
```

#### LLM Configuration

Three separate LLM slots for different purposes:

```env
# Fast LLM (intent classification, chat responses)
RUSTOPS_LLM_PROVIDER=lmstudio
RUSTOPS_LLM_ENABLED=true
RUSTOPS_LLM_BASE_URL=http://127.0.0.1:1234/v1
RUSTOPS_LLM_MODEL=your-fast-model
RUSTOPS_LLM_API_KEY=
RUSTOPS_LLM_REQUEST_STRATEGY=fallback  # fallback|required

# Deep LLM (analysis, incident review, classifier evolution)
RUSTOPS_LLM_DEEP_ENABLED=true
RUSTOPS_LLM_DEEP_PROVIDER=lmstudio
RUSTOPS_LLM_DEEP_BASE_URL=http://127.0.0.1:1234/v1
RUSTOPS_LLM_DEEP_MODEL=your-deep-model

# Compose LLM (response generation)
RUSTOPS_LLM_COMPOSE_ENABLED=true
RUSTOPS_LLM_COMPOSE_PROVIDER=lmstudio
RUSTOPS_LLM_COMPOSE_BASE_URL=http://127.0.0.1:1234/v1
RUSTOPS_LLM_COMPOSE_MODEL=your-compose-model
```

#### Semantic Memory Configuration

```env
# Memory backend (sqlite recommended)
RUSTOPS_MEMORY_PROVIDER=sqlite
RUSTOPS_MEMORY_DATABASE_PATH=/opt/rust-manager/agent/RustOpsAgent/data/semantic-memory.db

# Search and retrieval
RUSTOPS_MEMORY_SEARCH_ENABLED=true
RUSTOPS_MEMORY_WRITE_ENABLED=true
RUSTOPS_MEMORY_SIMILARITY_THRESHOLD=0.62        # 0.0-1.0
RUSTOPS_MEMORY_MAX_RETRIEVED_PER_STEP=6         # records per recall
RUSTOPS_MEMORY_MAX_SEARCH_CANDIDATES=400        # search depth

# Capacity and injection
RUSTOPS_MEMORY_MAX_INJECTED_CHARACTERS=2200     # context window
RUSTOPS_MEMORY_MAX_WRITES_PER_STEP=1            # writes per workflow

# Maintenance
RUSTOPS_MEMORY_PRUNE_LOW_IMPORTANCE_THRESHOLD=0.15
RUSTOPS_MEMORY_PRUNE_LOW_CONFIDENCE_THRESHOLD=0.2
RUSTOPS_MEMORY_PRUNE_OLDER_THAN_DAYS=30
```

#### Embedding Configuration

```env
# OpenAI-compatible embedding provider
RUSTOPS_EMBEDDING_PROVIDER=openai-compatible
RUSTOPS_EMBEDDING_BASE_URL=http://127.0.0.1:1234/v1
RUSTOPS_EMBEDDING_MODEL=text-embedding-nomic-embed-text-v1.5
RUSTOPS_EMBEDDING_API_KEY=
RUSTOPS_EMBEDDING_REQUIRE_API_KEY=false
RUSTOPS_EMBEDDING_TIMEOUT_SECONDS=30
RUSTOPS_EMBEDDING_BATCH_SIZE=8
```

#### Agent Inbox/Outbox Paths

```env
RUSTOPS_AGENT_STATE_PATH=/opt/rust-manager/agent/RustOpsAgent/data/agent-state.json
RUSTOPS_AGENT_NEOCORTEX_ROOT=/opt/rust-manager/agent/RustOpsAgent/data/NeoCortex

RUSTOPS_CHAT_INBOX_PATH=/opt/rust-manager/agent/RustOpsAgent/data/chat-inbox
RUSTOPS_DECISION_INBOX_PATH=/opt/rust-manager/agent/RustOpsAgent/data/decision-inbox
RUSTOPS_FEEDBACK_INBOX_PATH=/opt/rust-manager/agent/RustOpsAgent/data/feedback-inbox
RUSTOPS_MESSAGE_OUTBOX_PATH=/opt/rust-manager/agent/RustOpsAgent/data/message-outbox
RUSTOPS_MESSAGE_OUTBOX_SENT_PATH=/opt/rust-manager/agent/RustOpsAgent/data/message-outbox-sent
```

#### Git Integration (Optional)

```env
RUSTOPS_GITOPS_ENABLED=false
RUSTOPS_GITOPS_REPO_PATH=/opt/rust-manager/src
RUSTOPS_GITOPS_REMOTE=origin
RUSTOPS_GITOPS_BASE_BRANCH=main
RUSTOPS_GITOPS_PUSH_BRANCH_PREFIX=agent/
RUSTOPS_GITOPS_ALLOW_PUSH=false
GITHUB_TOKEN=

# Auto-pull on schedule
RUSTOPS_GITOPS_AUTO_PULL_ENABLED=false
RUSTOPS_GITOPS_AUTO_PULL_INTERVAL_MINUTES=60
RUSTOPS_GITOPS_AUTO_PULL_BRANCH=agent
RUSTOPS_GITOPS_AUTO_PULL_REBUILD=true
RUSTOPS_GITOPS_AUTO_RESTART_AFTER_PULL_REBUILD=false
```

#### Error Tracking (Sentry)

```env
RUSTOPS_SENTRY_DSN=
RUSTOPS_SENTRY_ENVIRONMENT=production
RUSTOPS_SENTRY_RELEASE=1.0.0
RUSTOPS_SENTRY_TRACES_SAMPLE_RATE=0.1
```

### Agent Settings (agentsettings.json)

JSON configuration file for agent-specific behavior:

```json
{
  "memory": {
    "provider": "sqlite",
    "databasePath": "./data/semantic-memory.db",
    "searchEnabled": true,
    "writeEnabled": true,
    "similarityThreshold": 0.62,
    "maxRetrievedMemoriesPerStep": 6,
    "maxSearchCandidates": 400,
    "maxInjectedMemoryCharacters": 2200,
    "maxWritesPerWorkflowStep": 1,
    "pruneLowImportanceThreshold": 0.15,
    "pruneLowConfidenceThreshold": 0.2,
    "pruneOlderThanDays": 30,
    "embedding": {
      "baseUrl": "http://127.0.0.1:1234/v1",
      "model": "text-embedding-nomic-embed-text-v1.5",
      "requireApiKey": false,
      "apiKey": ""
    }
  },
  "llm": {
    "provider": "lmstudio",
    "enabled": true,
    "baseUrl": "http://127.0.0.1:1234/v1",
    "model": "model-identifier",
    "requestStrategy": "fallback"
  },
  "llmDeep": {
    "provider": "lmstudio",
    "enabled": true,
    "baseUrl": "http://127.0.0.1:1234/v1",
    "model": "larger-model"
  },
  "llmCompose": {
    "provider": "lmstudio",
    "enabled": true,
    "baseUrl": "http://127.0.0.1:1234/v1",
    "model": "compose-model"
  }
}
```

### Server Configuration (config/servers.json)

Define Rust servers and remote agents:

```json
{
  "servers": [
    {
      "name": "main",
      "hostname": "localhost",
      "port": 28015,
      "rconPort": 28016,
      "rconPassword": "your-rcon-password"
    },
    {
      "name": "vanilla",
      "agentBaseUrl": "http://192.168.1.100:2088",
      "agentApiKey": "remote-api-key",
      "agentServerName": "vanilla"
    }
  ]
}
```

### Remote Server Configuration

Update `config/remote-servers.json` on the primary host after setting up a remote node:

```json
{
  "servers": [
    {
      "name": "remote-rust-01",
      "agentBaseUrl": "http://192.168.1.50:2088",
      "agentApiKey": "key-from-remote-agent-env",
      "agentServerName": "rust-01"
    }
  ]
}
```

## API Documentation

### Authentication

All API requests require the `X-API-Key` header:

```bash
curl -H "X-API-Key: your-api-key" http://localhost:2077/api/servers
```

### Core Endpoints

#### Server Lifecycle

```
GET    /api/servers                      # List all servers
GET    /api/servers/{server}             # Get server details
GET    /api/servers/{server}/status      # Server status
POST   /api/servers/{server}/start       # Start server
POST   /api/servers/{server}/stop        # Stop server
POST   /api/servers/{server}/restart     # Restart server
POST   /api/servers/{server}/update      # Update server
POST   /api/servers/{server}/wipe        # Wipe server (!)
GET    /api/servers/{server}/logs        # Stream server logs
```

#### Players & Moderation

```
GET    /api/servers/{server}/players     # List connected players
GET    /api/servers/{server}/players/{id} # Get player details
POST   /api/servers/{server}/players/{id}/ban    # Ban player
POST   /api/servers/{server}/players/{id}/kick   # Kick player
POST   /api/servers/{server}/players/{id}/mute   # Mute player
```

#### RCON Commands

```
POST   /api/servers/{server}/rcon        # Execute RCON command
GET    /api/servers/{server}/rcon-query  # Query server info
```

#### Configuration

```
GET    /api/servers/{server}/config      # Get server.cfg
PUT    /api/servers/{server}/config      # Update server.cfg
GET    /api/servers/{server}/config/auto # Get serverauto.cfg
```

#### Plugins

```
GET    /api/servers/{server}/oxide       # Oxide plugin list
GET    /api/servers/{server}/oxide/validate  # Validate plugins
POST   /api/servers/{server}/oxide/install   # Install plugin
POST   /api/servers/{server}/oxide/update    # Update plugins
GET    /api/servers/{server}/oxide/config/{plugin} # Get config
PUT    /api/servers/{server}/oxide/config/{plugin} # Update config
```

#### Agent State

```
GET    /api/agent/state                  # Get agent runtime state
GET    /api/agent/memory/stats           # Memory statistics
GET    /api/agent/memory/search          # Search semantic memory
POST   /api/agent/memory/import          # Import knowledge files
```

### Example Requests

#### Get Server Status

```bash
curl -H "X-API-Key: your-api-key" \
  http://localhost:2077/api/servers/main/status
```

Response:
```json
{
  "name": "main",
  "status": "running",
  "players": 42,
  "fps": 30.5,
  "uptime": "2h 15m",
  "lastUpdate": "2026-05-03T10:30:00Z"
}
```

#### Execute RCON Command

```bash
curl -X POST \
  -H "X-API-Key: your-api-key" \
  -H "Content-Type: application/json" \
  -d '{"command":"say Server restarting in 5 minutes"}' \
  http://localhost:2077/api/servers/main/rcon
```

#### List Players with Bans

```bash
curl -H "X-API-Key: your-api-key" \
  http://localhost:2077/api/servers/main/players?includeBans=true
```

#### Search Memory

```bash
curl -X POST \
  -H "X-API-Key: your-api-key" \
  -H "Content-Type: application/json" \
  -d '{"query":"restart timeout error"}' \
  http://localhost:2077/api/agent/memory/search
```

## Usage Examples

### Bash/CLI Commands

#### Start a server and tail logs

```bash
# Via API
curl -X POST -H "X-API-Key: key" http://localhost:2077/api/servers/main/start

# Direct via rustmgr
/opt/rust-manager/rustmgr.sh main start
/opt/rust-manager/rustmgr.sh main log
```

#### Update and restart

```bash
curl -X POST -H "X-API-Key: key" http://localhost:2077/api/servers/main/update
curl -X POST -H "X-API-Key: key" http://localhost:2077/api/servers/main/restart
```

### Admin Chat Commands (via Steam)

Send these via private Steam message to the bot:

```
# Server control
/start main
/stop main
/restart main
/update main
/status main

# Player management
/ban @player-name
/kick @player-name
/mute @player-name
/players

# Plugin management
/plugin-index search Kits
/plugin install Kits main
/show Kits config on main
/set Kits cooldown to 60 on main

# Memory management
/memory import ./knowledge/verified --trusted
/memory search restart timeout
/memory stats
/memory approve id-123

# Server configuration
/get server.pve from main
/set server.pve to false on main
```

### Python Integration

```python
import requests
import json

BASE_URL = "http://localhost:2077/api"
API_KEY = "your-api-key"

headers = {
    "X-API-Key": API_KEY,
    "Content-Type": "application/json"
}

# Get server status
response = requests.get(
    f"{BASE_URL}/servers/main/status",
    headers=headers
)
status = response.json()
print(f"Players: {status['players']}")

# Execute RCON command
cmd_response = requests.post(
    f"{BASE_URL}/servers/main/rcon",
    headers=headers,
    json={"command": "say Hello server!"}
)

# List all servers
servers = requests.get(
    f"{BASE_URL}/servers",
    headers=headers
).json()

for server in servers:
    print(f"{server['name']}: {server['status']}")
```

### Docker Deployment

```bash
# Run LM Studio for LLM + embeddings
docker run -d \
  --name lmstudio \
  -p 1234:1234 \
  -v lmstudio_data:/home/user/.cache/lm-studio \
  lmstudio:latest

# Run Ollama as alternative
docker run -d \
  --name ollama \
  -p 11434:11434 \
  -v ollama_data:/root/.ollama \
  ollama/ollama
```

## Deployment

### Install on a primary Debian host

```bash
sudo bash deploy/install-agent.sh
```

This script installs .NET, steamcmd, creates the `rustmgr` user/group, builds and deploys all binaries (API, agent, remote agent), writes a starter `config.env`, and registers and enables the systemd services.  Services are enabled but **not started** — edit `config.env` first, then:

```bash
systemctl start rustmgrapi rustopsagent
```

### Set up a remote node on another Debian host

A remote node lets the primary agent control a Rust server on a different machine with the same lifecycle commands it uses locally (start, stop, restart, update, wipe, logs, RCON, plugin management).

Drop `deploy/setup-remote-node.sh` on the target host and run:

```bash
sudo bash setup-remote-node.sh
```

This installs .NET, steamcmd, creates the service user, deploys the remote agent binary, writes a `remote-agent.env` with a generated API key, and enables the `rustops-remote-agent` systemd service.

After setup, open `config/remote-servers.json` on the primary host and add an entry:

```json
{
  "servers": [
    {
      "name": "server-name",
      "agentBaseUrl": "http://<remote-host-ip>:2088",
      "agentApiKey": "<key from remote-agent.env>",
      "agentServerName": "server-name"
    }
  ]
}
```

The remote agent is a passive HTTP server — it only listens. The primary API is the active side that connects to it. `RUSTOPS_REMOTE_AGENT_BIND` (on the remote host) is just the listen address; nothing on the primary host uses that variable.

### Run locally (development)

1. Copy `agent/RustOpsAgent/agentsettings.example.json` to `agent/RustOpsAgent/agentsettings.json`.
2. Optional: copy `config.env.example` to `config.env` and fill in shared values.
3. Configure the API base URL and any LLM/embedding endpoints you want to use.
4. Start the agent:

```powershell
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj
```

## Memory Maintenance Commands

The agent exposes CLI maintenance commands for semantic memory:

```powershell
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-migrate
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-migrate --dry-run
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-stats
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-search "restart timeout"
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-import H:\RUSTICALANDPROJECTS\RusticalandOPS\knowledge\verified --trusted
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-rebuild-embeddings
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-prune
```

The Steam/admin chat path also exposes memory inspection and maintenance commands through `RustChatToolHandler`.

Seed curated knowledge with:

- `/memory import <folderPath>` to recursively import `.md`, `.txt`, and `.json` files.
- `/memory import server catalog [limit N] [dry-run]` to import the local Rust server convar and server command catalogs into semantic memory.
- `/memory import convar catalog [limit N] [dry-run]` as an alias for the same server catalog import.
- `/memory pending` to list imports awaiting approval.
- `/memory approve <id>` or `/memory reject <id>` to control activation.
- `/memory search <query>` to search active semantic memory.
- `/memory forget <id>` to delete a record.

Trusted manual seed folders such as `knowledge/verified` can become active immediately. AI-generated seed folders such as `knowledge/ai-generated` default to pending unless imported with `--trusted`. Normal agent recall ignores pending and rejected memory records.

Server catalog imports read `ServerVariables.agent-readable.jsonl` and `ServerCommands.agent-readable.jsonl` by default. Override those paths with `RUSTOPS_SERVER_VARIABLES_PATH` and `RUSTOPS_SERVER_COMMANDS_PATH` if the files live elsewhere.

Example server convar JSONL row:

```json
{"convar":"server.pve","generated_on_start":true,"default_raw":"False","default_type":"boolean","description":"Enables PvE mode - players cannot damage other players."}
```

Example server command JSONL row:

```json
{"command":"server.readcfg","generated_command_metadata":true,"description":"Reads and executes serverauto.cfg then server.cfg from the server cfg folder.","risk_level_inferred":"safe","tags":["config","server"]}
```

## Plugin Config Reading and Writing

The agent can read, inspect, and modify plugin JSON config files directly.

Natural language commands that work:

```text
show the Vanish config on modded
what is the timeout in Kits on main?
set Kits cooldown to 60 on vanilla
give me the oxide/config/NTeleportation.json from modded
```

The handler locates the plugin config file in the `oxide/config` directory, reads it, and presents it. When a key-value mutation is detected and the request is clearly a write (not a read query), it applies the change in-place and writes the file back.  Successful writes are recorded in semantic memory as `Fix` records tagged with `plugin-config`.

Plugin config operations are local-only today — they require the oxide config directory to be accessible on the host running the API. Remote node servers surface the same functionality through the remote agent endpoints.

## Plugin Reference Index

Oxide/uMod plugin source is indexed separately from semantic memory. The index stores plugin metadata, commands, permissions, hooks, config keys, source path, and source hash in `pluginUpdates.referenceIndexDatabasePath`; raw source is kept in that reference database and is not imported into normal recall.

Admin commands:

```text
/plugin-index refresh
/plugin-index search <query>
/plugin-index commands
/plugin-index commands <pluginName>
/plugin-index permissions <pluginName>
/plugin-index hooks <pluginName>
```

Existing plugin verification/update checks also refresh the plugin reference index for the checked server and write only a compact `PluginSummary` semantic memory.

Plugin indexing reads the live plugin `.cs` paths returned by `/servers/{server}/oxide/validate`. A minimal plugin source that produces index entries looks like:

```csharp
[Info("Kits", "Facepunch", "1.2.3")]
[Description("Kit menu")]
class Kits : RustPlugin
{
    [ChatCommand("kit")]
    void KitCommand(BasePlayer player, string command, string[] args) {}

    [ConsoleCommand("inventory.give")]
    void Give(ConsoleSystem.Arg arg) {}

    void Init()
    {
        permission.RegisterPermission("kits.admin", this);
    }
}
```

## Verification Status

The latest agent verification pass confirmed:

- semantic memory is definitely in the live runtime path
- planning recall happens before routing
- execution recall happens before tool execution
- success and failure write-back both happen from `AgentRuntime`
- disabled memory config does not break the workflow
- embedding-provider failure does not crash the workflow
- SQLite store guardrails and migration safety were tested

At the last verification run:

- `dotnet build` passed
- `dotnet test` passed with `83/83`
- `dotnet format --verify-no-changes` passed

## Known Limitations

- legacy NeoCortex and JSON state are still required for classifier knowledge, conversation/session state, command policy, and incident/log history
- the API layer still needs stronger endpoint-level tests
- the API program remains large and would benefit from decomposition
- response composition only uses semantic memory when the reply path goes through compose/LLM context
- search is SQLite plus in-process scoring, not ANN/Qdrant
- embedding support is OpenAI-compatible only; there is no separate native Ollama protocol
- deployment is still primarily systemd-oriented rather than container-first
- plugin config reading and writing requires the oxide/config directory to be accessible on the local host (remote-node configs are exposed through the remote agent's own endpoints)

## Recommended Next Cleanup

The next cleanup candidates are:

- reduce or remove classifier learned-rule dependence on NeoCortex
- reduce or remove command-policy dependence on NeoCortex
- decide whether incident and log knowledge should move more fully into semantic memory
- add stronger API integration tests
- split the large API route surface into a more maintainable service/controller structure

## Development

### Project Structure

```
RusticalandOPS/
├── api/                          # ASP.NET 8 REST API
│   ├── Controllers/             # HTTP endpoint handlers
│   ├── Services/                # Business logic
│   ├── Models/                  # Data contracts
│   └── wwwroot/                 # Web dashboard
├── agent/                        # C# Agent Service
│   ├── RustOpsAgent/            # Agent runtime
│   │   ├── ClassifierEngine/    # Intent classification
│   │   ├── MemorySystem/        # Semantic memory
│   │   ├── ToolHandlers/        # Executable actions
│   │   ├── ResponseComposer/    # Reply generation
│   │   └── data/                # State and memory
│   └── RustOpsAgent.Tests/      # Unit tests
├── remote-agent/                 # Debian remote node agent
│   └── RustOpsRemoteAgent/      # Remote control endpoints
├── SteamBot/                     # Steam transport adapter
│   └── OpsSteamBot/             # SteamKit2 implementation
├── deploy/                       # Installation scripts
│   ├── install-agent.sh         # Primary deployment
│   ├── setup-remote-node.sh     # Remote node setup
│   └── systemd/                 # Service definitions
├── scripts/                      # Utility scripts
├── rustmgr.sh                    # Lifecycle control (Bash)
├── config/                       # Server and remote configs
├── knowledge/                    # Seed memory (verified & ai-gen)
└── README.md                     # This file
```

### Building from Source

```bash
# Install .NET 8 SDK
curl https://dot.net/v1/dotnet-install.sh | bash

# Clone and build
git clone https://github.com/yourusername/RusticalandOPS.git
cd RusticalandOPS
dotnet build
dotnet test
```

### Running Tests

```bash
# All tests
dotnet test

# Specific project
dotnet test agent/RustOpsAgent.Tests

# With coverage
dotnet test /p:CollectCoverage=true
```

### Code Style & Linting

```bash
# Format code
dotnet format

# Verify formatting
dotnet format --verify-no-changes
```

### Adding a New Tool Handler

Tool handlers are the executable actions the agent can perform. Example:

```csharp
namespace RustOpsAgent.ToolHandlers;

public class CustomToolHandler : IToolHandler
{
    private readonly ILogger<CustomToolHandler> _logger;
    
    public string Name => "custom";
    public string Description => "Custom operation";
    
    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        try
        {
            // Implementation here
            return ToolResult.Success("Operation completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool execution failed");
            return ToolResult.Failure(ex.Message);
        }
    }
}
```

Register in `Program.cs`:

```csharp
services.AddScoped<IToolHandler, CustomToolHandler>();
```

### Semantic Memory Development

Add to memory after tool execution:

```csharp
var memory = new Fact
{
    Type = "server-state",
    Scope = "main",
    Content = "Server is running with 45 players",
    Tags = new[] { "status", "main" },
    Source = "tool-execution",
    Importance = 0.8
};

await _memoryService.WriteAsync(memory);
```

Search during planning:

```csharp
var results = await _memoryService.SearchAsync(
    "restart timeout errors",
    scope: "main",
    limit: 5
);
```

### Debugging Locally

Run with verbose logging:

```powershell
$env:RUSTOPS_MEMORY_DEBUG_LOGGING_ENABLED = "true"
dotnet run --project agent/RustOpsAgent/RustOpsAgent.csproj
```

Monitor the agent workflow:

```bash
# Watch inbox directory
watch -n 1 'ls -la /path/to/chat-inbox'

# Tail outbox
tail -f /path/to/message-outbox/*.json
```

## Troubleshooting

### Agent Not Responding

**Symptoms**: Requests time out, no inbox processing

**Solution**:
```bash
# Check if service is running
systemctl status rustopsagent

# View logs
journalctl -u rustopsagent -f

# Restart service
systemctl restart rustopsagent
```

### Memory Search Not Working

**Symptoms**: `/memory search` returns no results

**Solutions**:
```bash
# Check if embeddings are available
curl http://localhost:1234/v1/models

# Rebuild embeddings
dotnet run --project agent/RustOpsAgent -- --memory-rebuild-embeddings

# Check memory stats
dotnet run --project agent/RustOpsAgent -- --memory-stats
```

### RCON Connection Timeout

**Symptoms**: `RCON timeout` errors in logs

**Solutions**:
```bash
# Test RCON connectivity manually
telnet server.example.com 28016

# Check firewall
sudo ufw allow 28016/tcp

# Verify RCON password in server config
grep rcon_password /opt/rust-manager/config/main/server.cfg
```

### Remote Agent Connection Failed

**Symptoms**: `Connection refused` when connecting to remote node

**Solutions**:
```bash
# Check remote agent status
ssh user@remote-host "systemctl status rustops-remote-agent"

# Verify firewall on remote
ssh user@remote-host "sudo ufw allow 2088/tcp"

# Test connectivity
curl http://remote-host:2088/api/status

# Check logs
ssh user@remote-host "journalctl -u rustops-remote-agent -f"
```

### LLM Provider Unreachable

**Symptoms**: `LLM request failed: connection refused`

**Solutions**:
```bash
# Start LM Studio container
docker run -d -p 1234:1234 lmstudio:latest

# Or Ollama
docker run -d -p 11434:11434 ollama/ollama

# Test endpoint
curl http://localhost:1234/v1/models

# Adjust timeout in config.env
RUSTOPS_LLM_REQUEST_TIMEOUT=60
```

### Plugin Config Editing Not Working

**Symptoms**: `/set config value` returns error

**Solutions**:
```bash
# Verify oxide config directory exists
ls -la /opt/rust-manager/servers/main/oxide/config/

# Check file permissions
sudo chown rustmgr:rustmgr -R /opt/rust-manager/servers/main/oxide/config/

# Test manual edit
nano /opt/rust-manager/servers/main/oxide/config/Kits.json
```

### High Memory Usage

**Symptoms**: Agent consuming > 1GB RAM

**Solutions**:
```env
# Reduce search depth
RUSTOPS_MEMORY_MAX_SEARCH_CANDIDATES=200

# Lower max injected memory
RUSTOPS_MEMORY_MAX_INJECTED_CHARACTERS=1500

# Prune older memories
RUSTOPS_MEMORY_PRUNE_OLDER_THAN_DAYS=7
```

Then restart:
```bash
dotnet run --project agent/RustOpsAgent -- --memory-prune
systemctl restart rustopsagent
```

## Performance & Security

### Performance Tuning

#### Memory System
- Adjust `RUSTOPS_MEMORY_MAX_RETRIEVED_PER_STEP` to limit recall context
- Enable only necessary embedding providers
- Use SQLite local storage instead of remote backends
- Batch embedding requests with `RUSTOPS_EMBEDDING_BATCH_SIZE`

#### LLM Integration
- Use a smaller, faster model for `RUSTOPS_LLM_MODEL`
- Enable request caching in LM Studio/Ollama
- Set `RUSTOPS_LLM_REQUEST_STRATEGY=fallback` to skip on timeout

#### Server Control
- Run rustmgr.sh commands in parallel where possible
- Cache server status for 30 seconds
- Use compressed log streaming

### Security Considerations

#### API Security
- Always use strong API keys (32+ characters)
- Rotate keys regularly
- Restrict API to trusted networks only
- Use HTTPS in production (`RUSTMGR_BIND=https://...`)

#### Server Access
- Limit RCON access to internal networks
- Use strong RCON passwords (32+ characters)
- Enable server firewall rules
- Monitor failed RCON attempts

#### LLM Safety
- Use local embedding providers when possible
- Don't expose LLM endpoints to public network
- Sanitize prompts before sending to LLM
- Monitor LLM request tokens for anomalies

#### File System
- Ensure proper permissions on config directories
```bash
sudo chown -R rustmgr:rustmgr /opt/rust-manager
sudo chmod -R 750 /opt/rust-manager
```

- Encrypt sensitive data at rest (API keys, RCON passwords)
- Use separate API keys per remote agent
- Rotate Steam bot credentials quarterly

#### Logging & Monitoring
- Enable audit logging for all admin actions
- Ship logs to remote syslog for archival
- Set up alerts for:
  - Failed authentication attempts
  - Server crashes or unexpected restarts
  - RCON timeouts or errors
  - Agent memory issues

### Monitoring Checklist

```bash
# Service health
systemctl status rustmgrapi rustopsagent rustops-steambot

# Disk usage
du -sh /opt/rust-manager/servers/*/

# Memory usage
ps aux | grep -E 'rustmgr|RustOpsAgent|OpsSteamBot'

# Recent errors in logs
journalctl -u rustopsagent --since "1 hour ago" | grep -i error

# API responsiveness
curl -s http://localhost:2077/api/health | jq .

# Semantic memory stats
dotnet run --project agent/RustOpsAgent -- --memory-stats
```

## Contributing

### Reporting Bugs

Submit issues with:
- Detailed reproduction steps
- Expected vs actual behavior
- Log excerpts (`journalctl -u rustopsagent`)
- Environment (OS, .NET version, server version)

### Submitting PRs

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/description`
3. Follow code style: `dotnet format`
4. Add tests for new functionality
5. Ensure all tests pass: `dotnet test`
6. Submit PR with detailed description

### Development Guidelines

- Write tests for public methods
- Use meaningful variable names
- Keep functions focused and small
- Document non-obvious logic
- Follow async/await patterns
- Use dependency injection

## FAQ

### Can I manage multiple Rust servers?

Yes! Configure servers in `config/servers.json`. You can control local servers directly and remote servers via the remote agent protocol. Scale to unlimited servers across multiple hosts.

### Does RusticalandOPS require Steam credentials?

Yes, the Steam bot adapter requires Steam credentials to connect to chat. The credentials are stored securely in `botsettings.json`. The REST API and agent can operate independently without Steam.

### How do I backup semantic memory?

Semantic memory is stored in SQLite. Backup with:
```bash
cp /opt/rust-manager/agent/RustOpsAgent/data/semantic-memory.db /backups/
```

Restore with:
```bash
cp /backups/semantic-memory.db /opt/rust-manager/agent/RustOpsAgent/data/
systemctl restart rustopsagent
```

### Can I run this in Docker?

The agent, API, and bot can be containerized. See `docker-compose.example.yml` for a complete stack. Server control (`rustmgr.sh`) should run on the host OS for best compatibility.

### What's the difference between local and remote agents?

- **Local**: Runs on same host as Rust servers, has direct filesystem access
- **Remote**: Runs on distant host, communicates via HTTP, has same operational capabilities

Remote agents are ideal for geographically distributed servers while maintaining unified control.

### How do I integrate with external tools?

Use the REST API (`http://localhost:2077/api`) from:
- Monitoring systems (Prometheus, Grafana)
- Chat platforms (Discord, Slack hooks)
- Ticketing systems (Linear, Jira)
- Custom dashboards and tooling

All endpoints require the `X-API-Key` header.

### Can I disable the semantic memory system?

Yes, set in config.env:
```env
RUSTOPS_MEMORY_SEARCH_ENABLED=false
RUSTOPS_MEMORY_WRITE_ENABLED=false
```

The agent will fall back to legacy operational state. This is not recommended for production.

### What LLM providers are supported?

- LM Studio (recommended for local)
- Ollama
- OpenAI-compatible HTTP endpoints
- Azure OpenAI
- Any service with OpenAI-compatible chat/embedding API

### How often should I prune old memories?

Default is 30 days. For active servers, prune weekly:
```bash
RUSTOPS_MEMORY_PRUNE_OLDER_THAN_DAYS=7 dotnet run --project agent/RustOpsAgent -- --memory-prune
```

### Can multiple RusticalandOPS instances manage the same servers?

Not recommended. Each instance should manage its own set of servers. For failover, use DNS round-robin with a shared API key, but ensure only one agent is active at a time.

### Where can I get help?

- Check existing [GitHub Issues](https://github.com/yourusername/RusticalandOPS/issues)
- Review logs: `journalctl -u rustopsagent -n 100`
- Run diagnostics: `dotnet run --project agent/RustOpsAgent -- --memory-stats`
- Ask in the project discussions

## Additional Docs

For deeper component-specific details, see:

- [`agent/RustOpsAgent/README.md`](agent/RustOpsAgent/README.md) — Agent architecture and extensibility
- [`deploy/systemd/README.md`](deploy/systemd/README.md) — Service configuration and management
- [`SteamBot/OpsSteamBot/README.md`](SteamBot/OpsSteamBot/README.md) — Steam chat adapter
- [`remote-agent/RustOpsRemoteAgent/README.md`](remote-agent/RustOpsRemoteAgent/README.md) — Remote node setup
- [`AGENT_ARCHITECTURE.md`](AGENT_ARCHITECTURE.md) — Deep dive into agent design
- [`usage.md`](usage.md) — Detailed usage guide

## License

[Specify your license here]

## Support & Credits

Maintained by the RusticalandOPS team. Special thanks to:
- Facepunch Studios (Rust)
- SteamKit2 contributors
- ASP.NET Core community

---

**Last Updated**: 2026-05-03  
**Stable Version**: 1.0.0  
**Status**: Production Ready
