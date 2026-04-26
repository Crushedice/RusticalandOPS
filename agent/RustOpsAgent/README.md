# RustOpsAgent

Passive Linux-side operations agent foundation for Rust server management.

## Role

This process is the always-running orchestrator:

- polls the local API for server and host state
- records incidents and outcomes into persistent memory
- ingests admin feedback and approval decisions from inbox folders
- writes natural-language admin messages to an outbox folder
- optionally asks the configured local LLM runtime for summaries/classification
- prepares the ground for later interface adapters like Steam chat

## Run

1. Copy `agentsettings.example.json` to `agentsettings.json`.
2. Optional: copy `agent-log-rules.example.json` to `agent-log-rules.json` and add ignore/incident phrases for your server console noise.
3. Copy `config.env.example` to `config.env` (at the repo root) and fill in the shared values.
4. Point it at the running API if you are not using the shared env file.
5. Start the daemon:

```powershell
dotnet run --project H:\RUSTICALANDPROJECTS\AIProject\agent\RustOpsAgent\RustOpsAgent.csproj
```

## Current behavior

- polls `/servers/summary`
- polls `/servers/{server}/health`
- polls `/servers/{server}/logs/read` with a rolling file offset for new console/log output
- creates incident memory entries for repeated errors and state changes
- filters startup noise and known irrelevant console lines with deterministic log rules
- stores server-specific notes and action outcomes in a JSON memory file
- creates policy-gated action proposals
- auto-executes only explicitly allowed safe actions
- records approvals, rejections, and action outcomes
- learns simple action preferences from negative/positive admin feedback
- can call LM Studio or another OpenAI-compatible local runtime for incident summaries and action recommendations when enabled
- can run a bounded self-repair loop that detects recurring runtime gaps and writes corrective artifacts in `data/self-repair`
- can optionally rewrite project files inside `selfRepair.scopeRootPath` (for Linux deploys this is intended to be `/opt/rust-manager`)
- can update runtime log-rule patterns and reply-style guidance through LLM tool calls inside agent scope
- reads shared settings from `config.env` (repo root) or `RUSTOPS_ENV_FILE` when present
- normalizes mixed Windows/Linux path separators before resolving inbox and outbox folders

## Inbox files

Adapters can drop JSON files into the configured inbox folders:

- `feedback-inbox`
  Used for admin feedback like "good call", "bad restart", or preference notes.
- `decision-inbox`
  Used to approve or reject pending actions.
- `chat-inbox`
  Used by adapters like Steam chat to forward natural-language admin requests to the agent.

- `message-outbox`
  The agent writes JSON messages here for adapters like Steam chat to relay naturally.

## Next intended extensions

- action policy and approval gates
- plugin inventory/update knowledge
- Steam adapter forwarding into this agent
