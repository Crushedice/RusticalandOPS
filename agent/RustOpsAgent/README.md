# RustOpsAgent

RustOpsAgent is the long-running operations agent for this project. It sits beside the Rust server management API, watches operational state, processes admin requests from inbox files, executes bounded actions through tool handlers, and writes admin-facing replies to an outbox.

The current implementation is not just a prototype router anymore. It has a live workflow, a semantic memory layer, legacy operational state, policy-aware command execution, optional local LLM integration, and background review loops for incidents and classifier evolution.

## Current state

Today the agent can:

- process admin chat requests from `chat-inbox`
- process feedback and approval/decision inbox items
- classify intent with heuristics or an LLM
- route requests to Rust-specific tool handlers
- execute server control, status, player lookup, RCON, logs, plugin, network, file-edit, chat, and server-management actions
- compose admin replies with a dedicated response-composition model or a non-LLM fallback
- observe servers and logs in the background
- review incidents and repeated failures
- persist operational state in NeoCortex/legacy JSON stores
- persist semantic memory in SQLite with embeddings and vector similarity scoring
- migrate old flat-file memory into the semantic store
- continue running when embeddings or the vector store are unavailable

## Runtime architecture

The live runtime is built in `Program.cs` and starts one `AgentRuntime` with these main collaborators:

- `AdminIntentClassifier`
- `ActionExecutor`
- `ResponseComposer`
- `NeoCortexStore`
- `LegacyAgentStateStore`
- `SemanticMemoryService`
- `RustOpsApiClient`
- `GitOpsService`
- `AutoPullService`

Tool handlers currently registered in the live path:

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

## Live workflow

The real admin request path is:

1. adapter drops a `ChatInboxItem` JSON file into `chat-inbox`
2. `AgentRuntime` loads conversation state from NeoCortex
3. `AdminIntentClassifier` classifies the message and performs planning-time semantic recall
4. `AgentRuntime` performs execution-time semantic recall and attaches it to `ToolExecutionContext`
5. `ActionExecutor` selects and runs the matching tool handler
6. `AgentRuntime` records structured success/failure memory
7. `ResponseComposer` generates the reply
8. `AgentRuntime` writes the reply JSON to `message-outbox`

There is an executor fallback recall path, but the primary execution-memory handoff is owned by `AgentRuntime`.

## Memory model

The project currently uses two memory layers.

### 1. Operational / legacy state

These are still active and still matter:

- `memory.statePath`
- `memory.neoCortexRoot`

They store non-semantic operational state such as:

- conversation selection state
- classifier learned rules and pending misclassifications
- command policy state
- incident history
- log knowledge and ignore patterns
- recent operations and runtime status

### 2. Semantic memory

The semantic layer lives in `memory.databasePath` and is the primary retrieval path for planning and execution guidance.

It stores structured `MemoryRecord` rows with:

- type and scope
- summary and full text
- tags and related entity ids
- timestamps, access counts, importance, confidence
- metadata
- content hash deduplication
- optional embeddings and embedding model

Current semantic memory behavior:

- planning recall runs in `AdminIntentClassifier`
- execution recall runs in `AgentRuntime`
- write-back runs after both successful and failed actions
- migration can import records even if embeddings are unavailable
- rebuild can later fill missing embeddings
- secrets are sanitized before persistence

## Semantic memory status

The semantic memory system is wired into the live pipeline, not just registered on the side.

- `Program.cs` constructs exactly one `SemanticMemoryService`
- the same instance is passed into `AdminIntentClassifier`, `ActionExecutor`, `RustChatToolHandler`, and `AgentRuntime`
- planning retrieval happens before intent routing decisions are finalized
- execution retrieval happens before tool execution
- repeated failure memory can change live executor behavior
- fix/procedure memory can influence reply composition
- post-action success and failure outcomes are written back as structured memories

If embeddings are down, the agent keeps running. Retrieval and writes are skipped with logs instead of crashing the workflow.

## Embedding support

The current embedding implementation is OpenAI-compatible HTTP only.

Supported deployment style:

- LM Studio OpenAI-compatible embeddings endpoint
- Ollama OpenAI-compatible endpoint
- other OpenAI-compatible local or remote embedding gateways

Current behavior:

- base URLs with or without trailing slash are handled
- API keys are optional unless config explicitly requires them
- HTTP 400/401/404/500 are surfaced clearly
- timeouts and batch mismatches are handled
- empty vectors and dimension mismatches are rejected
- transient failures do not take down the agent

## SQLite store status

The semantic memory store is SQLite-backed and local-first.

Current guardrails:

- schema creation is idempotent
- DB directory is created automatically
- schema version metadata exists
- WAL mode is enabled
- candidate scanning is capped by config
- prompt memory context is capped by config
- per-step writes are capped by config
- expired records are excluded by default
- corrupted rows are skipped with logs
- zero vectors do not break search
- content-hash dedup applies to normal writes and migration

This is not an ANN/Qdrant setup. Search is still SQLite plus in-process cosine scoring.

## Configuration

Primary config file:

- `agentsettings.json` copied from `agentsettings.example.json`

Shared environment examples:

- `config.env.example`
- `rustops.env.example`

Important memory settings:

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

There are also separate LLM configs for:

- `llm`
- `llmDeep`
- `llmCompose`

`llmDeep` is used for deeper background analysis. `llmCompose` is used for reply composition when configured.

## Running

1. Copy `agentsettings.example.json` to `agentsettings.json`.
2. Optional: copy `config.env.example` to `config.env` and fill in the shared values.
3. Configure the API endpoint and any LLM/embedding endpoints you want to use.
4. Start the agent:

```powershell
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj
```

## Inbox and outbox

The runtime uses file-based adapter boundaries.

- `feedback-inbox`
  Admin feedback such as corrections, preferences, and ignore directives.
- `decision-inbox`
  Approvals or rejections for proposed actions.
- `chat-inbox`
  Natural-language admin requests.
- `message-outbox`
  Agent replies for the external adapter to deliver.

## Memory maintenance commands

CLI:

```powershell
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-migrate
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-migrate --dry-run
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-stats
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-search "restart monthly timeout"
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-rebuild-embeddings
dotnet run --project H:\RUSTICALANDPROJECTS\RusticalandOPS\agent\RustOpsAgent\RustOpsAgent.csproj -- --memory-prune
```

Admin chat commands:

- `memory stats`
- `memory search <query>`
- `memory show <id>`
- `memory delete <id>`
- `memory recent`
- `memory repeated failures`
- `memory migrate`
- `memory migrate dry-run`
- `memory rebuild`
- `memory prune`
- `memory add <summary> :: <detail>`

## Current limitations

- legacy operational state is still required for several live subsystems
- semantic memory is primary for planning/execution recall, but not yet a full replacement for NeoCortex
- response composition only uses memory when the composed reply goes through the LLM path
- no ANN/vector-native SQLite extension is used yet
- embedding support is OpenAI-compatible only; there is no separate native Ollama protocol

## Recommended next cleanup

The next cleanup candidates, after the current stabilization pass, are:

- reduce or remove classifier learned-rule dependence on NeoCortex
- reduce or remove command-policy dependence on NeoCortex
- decide whether incident-review history should move fully into semantic memory
- document the external API endpoints expected by each tool handler

## Verification status

The current verification pass added runtime-path and hardening coverage for:

- live semantic-memory wiring
- execution/planning recall
- failure and success write-back
- memory-disabled workflow behavior
- embedding-provider failure behavior
- SQLite guardrails and corruption handling
- migration dry-run, repeated migration, and metadata-only import
- memory admin/debug command routing

At the time of the last verification run:

- `dotnet build` passed
- `dotnet test` passed
- `dotnet format --verify-no-changes` passed
