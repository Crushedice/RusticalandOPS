# RusticalandOPS

RusticalandOPS is a local-first operations stack for managing Rust Dedicated servers. It combines a Bash control layer, an ASP.NET API, a long-running C# agent, and a Steam chat transport adapter. The system is designed to run on a Debian/Linux host under `systemd`, with local file inbox/outbox boundaries and optional local or OpenAI-compatible LLM/embedding endpoints.

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

```text
Steam chat
    │
    ▼
SteamBot (SteamKit2)           ← transport adapter
    │  chat-inbox / decision-inbox / feedback-inbox
    ▼
Agent / RustOpsAgent (C#)      ← reasoning + workflow engine
    │  REST calls
    ▼
API / rustmgrapi (ASP.NET 8)   ← deterministic control plane
    │  subprocess
    ▼
rustmgr.sh (Bash)              ← lifecycle authority
    │
    ▼
Rust Dedicated servers
```

## Layer Status

### Layer 1: `rustmgr.sh`

Still the lifecycle authority for start/stop/restart/update/wipe/status/log/query operations. This remains the lowest-level deterministic control path and is still the final authority for host-side server operations.

### Layer 2: `api/`

The ASP.NET API remains the main deterministic interface over `rustmgr.sh`. It exposes server lifecycle, logs, players, RCON, config, plugin, network, and agent-state endpoints, plus the built-in `/ui` dashboard.

This layer is still functional, but it remains a large inline route surface and still needs broader endpoint test coverage.

### Layer 3: `agent/RustOpsAgent/`

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

### Layer 4: `SteamBot/OpsSteamBot/`

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

Main agent configuration lives in:

- `agent/RustOpsAgent/agentsettings.json`
- `agent/RustOpsAgent/agentsettings.example.json`

Shared environment examples:

- `config.env.example`
- `rustops.env.example`

Important memory settings now include:

- `memory.provider`
- `memory.databasePath`
- `memory.searchEnabled`
- `memory.writeEnabled`
- `memory.similarityThreshold`
- `memory.maxRetrievedMemoriesPerStep`
- `memory.maxSearchCandidates`
- `memory.maxInjectedMemoryCharacters`
- `memory.maxWritesPerWorkflowStep`
- `memory.pruneLowImportanceThreshold`
- `memory.pruneLowConfidenceThreshold`
- `memory.pruneOlderThanDays`
- `memory.embedding.baseUrl`
- `memory.embedding.model`
- `memory.embedding.requireApiKey`
- `memory.embedding.apiKey`

Separate LLM config blocks still exist for:

- `llm`
- `llmDeep`
- `llmCompose`

## Running

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
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-rebuild-embeddings
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-prune
```

The Steam/admin chat path also exposes memory inspection and maintenance commands through `RustChatToolHandler`.

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

## Recommended Next Cleanup

The next cleanup candidates are:

- reduce or remove classifier learned-rule dependence on NeoCortex
- reduce or remove command-policy dependence on NeoCortex
- decide whether incident and log knowledge should move more fully into semantic memory
- add stronger API integration tests
- split the large API route surface into a more maintainable service/controller structure

## Additional Docs

For deeper agent-specific details, see:

- `agent/RustOpsAgent/README.md`
- `deploy/systemd/README.md`
- `SteamBot/OpsSteamBot/README.md`
