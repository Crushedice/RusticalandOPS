# RusticalandOPS

An event-driven AI operations stack for managing Rust Dedicated game servers on a Linux host. Four layers communicate over JSON files and a REST API; everything runs as systemd services under `/opt/rust-manager/`.

---

## Architecture

```
Steam chat
    │
    ▼
SteamBot (SteamKit2)           ← Layer 4: transport adapter
    │  chat-inbox / message-outbox (JSON files)
    ▼
Agent / RustOpsAgent (C#)      ← Layer 3: reasoning engine (Semantic Kernel + LLM)
    │  REST calls
    ▼
API / rustmgrapi (ASP.NET 8)   ← Layer 2: deterministic control plane
    │  subprocess
    ▼
rustmgr.sh (Bash)              ← Layer 1: server lifecycle authority
    │
    ▼
Rust Dedicated (game servers)
```

---

## Layer 1 — `rustmgr.sh`

Bash authority for server lifecycle. Subcommands:

| Command | Behaviour |
|---------|-----------|
| `start <name>` | Enable autorestart, launch server inside tmux |
| `stop <name>` | Disable autorestart, SIGTERM all matching processes |
| `restart <name>` | Keep supervisor alive, SIGTERM matching processes |
| `kill <name>` | SIGKILL + disable autorestart |
| `update <name>` | SteamCMD update, then restart |
| `wipe <name>` | Map/procedural wipe, then restart |
| `umod <name>` | Install/update uMod (Oxide) |
| `logs <name>` | Tail the server log |
| `query <name> <cmd>` | Send a raw RCON command via mcrcon |
| `commands <name> [n]`| Last N RCON commands sent |
| `status [name]` | Running/stopped state (all or one server) |
| `cron` | Managed cron task runner |

---

## Layer 2 — `api/` (ASP.NET 8)

REST control plane wrapping `rustmgr.sh`. ~45 endpoints. Auth: `X-Api-Key` header.

### Key endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/servers` | List known servers |
| GET | `/servers/{s}/status` | Server running state |
| POST | `/servers/{s}/start` | Start server |
| POST | `/servers/{s}/stop` | Stop server |
| POST | `/servers/{s}/restart` | Restart server |
| POST | `/servers/{s}/update` | Update game files |
| POST | `/servers/{s}/wipe` | Map wipe |
| GET | `/servers/{s}/console` | `{ server, lines, content }` — recent console output |
| GET | `/servers/{s}/logs/tail` | Structured log entries. Params: `lines`, `since` (ISO8601), `offset` |
| GET | `/servers/{s}/logs/read` | Rolling byte-offset log reader |
| GET | `/servers/{s}/commands` | `{ server, lines, content }` — RCON command trace |
| GET | `/servers/{s}/events` | Structured RCON command trace |
| GET | `/servers/{s}/players` | Live playerlist (RCON) |
| GET | `/servers/{s}/serverinfo` | Live serverinfo (RCON) |
| GET | `/servers/{s}/bans` | Ban list (RCON) |
| POST | `/servers/{s}/rcon` | Execute arbitrary RCON command |
| GET | `/servers/{s}/config` | Read server config JSON |
| PUT | `/servers/{s}/config` | Write + validate server config |
| GET | `/servers/{s}/plugins` | Installed Oxide plugins + uMod metadata |
| GET | `/host/network/interfaces` | Host network interface list (JSON) |
| GET | `/agent/status` | Agent runtime status snapshot |
| GET | `/agent/incidents` | Open + recently resolved incidents |
| PUT | `/agent/log-rules` | Update log importance rules |
| GET | `/ui` | Built-in HTML dashboard |

All error responses use `{ "code": "...", "message": "..." }` with appropriate HTTP status codes.

---

## Layer 3 — `agent/RustOpsAgent/` (C# + Semantic Kernel)

The reasoning engine. Runs as a long-lived process, polling inboxes and maintaining memory.

### Core loop

```
ProcessChatInboxAsync      → per-admin SemaphoreSlim → classify → execute → compose → outbox
ProcessDecisionInboxAsync  → approve/reject pending proposals
ProcessFeedbackInboxAsync  → store ignore rules / importance adjustments
AutoPullService.TickAsync  → git pull + optional build + optional service restart
ReviewIncidentsAsync       → periodic LLM incident analysis + GitOps PR proposal
```

### Modules

| Namespace | Purpose |
|-----------|---------|
| `Core.Contracts` | Config, memory, and exchange types |
| `Core.Interaction` | `AdminIntentClassifier`, `ActionExecutor`, `ResponseComposer` |
| `Domains.Rust` | Tool handlers — one per intent domain |
| `Infrastructure` | `NeoCortexStore`, `GitOpsService`, `AutoPullService`, `RustOpsApiClient` |

### Tool handlers

| Handler | Intent | Function |
|---------|--------|----------|
| `RustServerControlToolHandler` | `ServerControl` | start / stop / restart / update / wipe |
| `RustStatusToolHandler` | `StatusCheck` | aggregate status across servers |
| `RustPlayerLookupToolHandler` | `PlayerLookup` | find player by SteamID or name |
| `RustRconToolHandler` | `RconCommand` | run RCON command with policy gating |
| `RustLogsToolHandler` | `Troubleshooting` | fetch and summarise log entries |
| `RustPluginToolHandler` | `StatusCheck` | check + stage plugin updates from uMod |
| `RustNetworkToolHandler` | `StatusCheck` | report traffic on configured interfaces |
| `RustFileEditToolHandler` | `FileEdit` | read / diff / commit server config files via GitOps |
| `RustChatToolHandler` | `Chat` | conversational reply with memory context |

### Memory — `NeoCortexStore`

Flat-file JSON/JSONL store under `neoCortexRoot/`:

```
operations/active-state.json      — recent actions, LLM interaction log
selection/session-state.json      — per-admin conversation state + recent messages
logs/log-knowledge.json           — ignore patterns, importance rules, observations
evolution/incidents.jsonl         — one incident record per line (JSONL)
policy/ignore-feedback.json       — user-supplied ignore phrases
policy/command-policy.json        — adaptive RCON command allow/deny tracking
cache/domain-cache.json           — server name cache
```

### Adaptive command policy

Each RCON command is tracked. A command auto-allows after `autoAllowAfterSuccesses` consecutive successes (default 5) and requires explicit admin approval after `requireApprovalAfterFailures` consecutive failures (default 2). Thresholds are configurable in `agentsettings.json`.

### Conversation memory

The last 12 messages per admin are kept in `ConversationSelectionState.RecentMessages`. The 6 most recent are injected into every LLM compose prompt so the agent maintains context across turns.

### LLM architecture

Three Semantic Kernel instances are maintained:

| Kernel | Role |
|--------|------|
| Fast kernel | Intent classification — cheap, low-latency |
| Deep kernel | Incident analysis, log summarisation — higher context budget |
| Compose kernel | Response generation — balances quality and speed |

Providers are pluggable: OpenAI-compatible endpoints (LM Studio, Ollama, or hosted).

---

## Layer 4 — `SteamBot/OpsSteamBot/` (SteamKit2)

Pure transport adapter. Whitelist-gated; direct commands are handled locally.

| Steam message | Action |
|---------------|--------|
| `approve` / `reject` | Write to `decision-inbox` |
| `feedback <text>` | Write to `feedback-inbox` |
| `ping` | Reply "pong" inline |
| `help` | List available commands |
| Anything else | Forward to `chat-inbox` |

Replies from `message-outbox` are chunked to ≤ 350 characters before sending.

---

## Dashboard — built-in Web UI

The API hosts a full ops dashboard at `/ui` — no separate frontend deployment needed.

- Dark-theme responsive grid layout
- Live status cards with colour-coded indicators (running, offline, pending)
- Tabbed views: Servers, Agent, Host, Admin Console
- Server control buttons (start/stop/restart/update/wipe)
- Live player lists, incident browser, RCON console
- LLM configuration panel
- All data fetched from the API via polling — no external JS framework

---

## Configuration

### Agent — `agentsettings.json`

Key sections:

```jsonc
{
  "api":      { "baseUrl": "...", "apiKey": "..." },
  "llm":      { "enabled": true, "provider": "lmstudio", "baseUrl": "...", "model": "..." },
  "gitOps":   { "enabled": true, "repoPath": "...", "pushBranchPrefix": "agent/" },
  "autoPull": { "enabled": true, "intervalMinutes": 60, "buildEnabled": true, "restartEnabled": true },
  "network":  { "trackedInterfaces": ["eth0", "wg0"] },
  "pluginUpdates": { "downloadEnabled": false, "stagingPath": "/tmp/plugin-staging" },
  "commandExecution": { "autoAllowAfterSuccesses": 5, "requireApprovalAfterFailures": 2 },
  "monitor":  { "incidentReviewIntervalMinutes": 30 },
  "memory":   { "neoCortexRoot": "/opt/rust-manager/neocortex", "statePath": "..." },
  "inbox":    { "chatInboxPath": "...", "decisionInboxPath": "...", "feedbackInboxPath": "..." },
  "outbox":   { "messageOutboxPath": "..." }
}
```

### Environment — `config.env`

Secrets and overrides loaded at startup from `/opt/rust-manager/config.env` (or `config.env` next to the binary):

```
RUSTOPS_API_KEY=...
RUSTOPS_LLM_BASE_URL=http://localhost:1234/v1
RUSTOPS_LLM_MODEL=lmstudio-community/...
RUSTOPS_GIT_REMOTE_URL=...
SENTRY_DSN=...
```

---

## CI

GitHub Actions (`ci.yml`) runs on every push to `main` and `agent/**` branches:

1. Restore all projects
2. Build API, Agent, SteamBot in Release mode
3. Run Agent unit tests (`RustOpsAgent.Tests`)
4. Shellcheck `rustmgr.sh`

---

## Current Capabilities

What is fully working right now:

- **Server lifecycle** — start, stop, restart, kill, update, wipe via both the shell script and the API
- **RCON execution** — arbitrary RCON commands with adaptive allow/deny policy and Steam `approve`/`reject` flow
- **Status aggregation** — live status across all servers with player counts and server info
- **Player lookup** — find a player by SteamID or name fragment via RCON
- **Log inspection** — structured log tail with time-based filtering, byte-offset rolling reader, and LLM-assisted summarisation
- **Plugin listing** — installed Oxide plugin inventory with uMod metadata
- **Network monitoring** — live traffic stats on configured host interfaces
- **Config read/write** — server config JSON via API with validation; GitOps-backed file editing from the agent
- **Incident tracking** — periodic LLM review of log events, incident creation and resolution stored in JSONL
- **Conversation memory** — per-admin message history injected into every LLM compose call (last 6 of 12)
- **Feedback learning** — admins can send `feedback <text>` via Steam to teach the agent ignore rules and importance adjustments
- **GitOps** — agent proposes config changes as git branches with safety checks (rejects pushes to `main`)
- **Auto-pull** — agent polls the git remote, rebuilds, and restarts services on new commits
- **Web dashboard** — full ops view at `/ui` covering server state, players, incidents, RCON console, and LLM settings
- **Steam adapter** — whitelist-gated Steam chat with local command handling and inbox/outbox forwarding
- **Sentry integration** — error reporting with breadcrumb logging across all services

---

## Not Yet Working / Known Issues

- **Plugin version-aware updates** — `RustPluginToolHandler` can stage plugin files to a path but has no logic to compare installed vs. available uMod versions or auto-download updates. `pluginUpdates.downloadEnabled` defaults to `false`.
- **Dashboard real-time updates** — the `/ui` dashboard polls endpoints on a timer. There are no WebSocket or SSE feeds, so state can lag and rapid events (bursts of players joining, RCON floods) are not reflected instantly.
- **Log pagination for old entries** — `/servers/{s}/logs/tail` always works from the most recent N lines. Reaching entries older than the tail window requires `/logs/read` with manual byte-offset tracking; there is no cursor-based pagination API.
- **SteamBot reconnection reliability** — the bot's reconnect-on-disconnect path exists but has not been stress-tested against Steam network drops or long idle periods.
- **Autonomous player replies** — the architecture supports it but the policy module is not implemented. Any in-game RCON/admin relay for player-facing messages is blocked pending an auditable approval gate.
- **GitOps merge / conflict resolution** — agent-created branches must be manually reviewed and merged. There is no automatic merge strategy or conflict resolver.
- **NeoCortex semantic search** — memory lookup is flat-file key matching. Despite the "cortex" branding there are no vector embeddings; related-incident retrieval is purely chronological.
- **API test coverage** — there are no tests for the ~45 REST endpoints. The xUnit suite covers agent internals only (GitOps safety, NeoCortex migration, command policy, incident recording).
- **No container/Docker packaging** — deployment is systemd-only. There is no `Dockerfile` or compose file for containerised environments.
- **CI coverage reporting** — the pipeline builds and runs tests but produces no coverage report or badge.
- **LLM provider hot-swap** — changing the LLM model or base URL requires restarting the agent; there is no live reload path for kernel configuration.

---

## Needs Work

Short-to-medium term items that would meaningfully improve reliability or usability:

- **API refactor** — `api/Program.cs` is 4,600+ lines of inline route handlers. Splitting into controller classes and a service layer would make it maintainable and testable.
- **API endpoint tests** — add an integration test project using `WebApplicationFactory` to cover the most critical lifecycle and RCON endpoints.
- **Plugin manager** — build a dedicated version-comparison and download pipeline for Oxide plugins so `pluginUpdates.downloadEnabled` can be safely turned on.
- **Dashboard WebSocket feed** — replace or supplement the polling loop with a server-sent events or WebSocket channel so the UI reflects console output and player changes in real time.
- **SteamBot hardening** — add exponential back-off reconnect, dead-letter queue flushing on reconnect, and message dedup to prevent double-sends after reconnects.
- **Log cursor API** — expose a cursor-based log pagination endpoint so the agent and dashboard can page through historical entries without manual byte offsets.
- **Feedback loop for classification** — when an admin corrects the agent ("I meant X not Y"), write the correction back into `NeoCortexStore` as a learned rule rather than only applying it to the current turn.
- **CI deployment step** — add a deploy job (rsync + systemctl restart) gated on passing tests so `main` always reflects what is running on the host.
- **Coverage gate** — enforce a minimum coverage threshold in CI so regressions in agent logic are caught before merge.

---

## Future Plans

Longer-horizon features that are architecturally planned but not started:

- **Multi-server incident correlation** — cross-server log analysis to detect coordinated attacks, shared crash causes, or wipe-timing conflicts.
- **Autonomous maintenance windows** — let the agent schedule and execute low-risk maintenance (restarts, updates, wipes) during configured quiet hours without admin approval.
- **In-game admin relay** — optional RCON-based pipeline so the agent can respond to in-game admin chat, gated by a strict policy layer and full audit log.
- **Player behaviour monitoring** — opt-in mode where the agent tracks unusual RCON events (mass kills, rapid connects/disconnects) and flags or acts on them.
- **Vector memory** — replace flat-file incident lookup in `NeoCortexStore` with an embedded vector store (e.g. Chroma, sqlite-vss) for semantic incident retrieval and "has this happened before?" queries.
- **Web UI as a standalone SPA** — extract the dashboard from the API binary into a proper frontend project (React or Svelte) with a build pipeline, allowing richer interactivity without bloating the API.
- **CLI adapter** — a local `rustops` CLI that speaks to the API, giving operators a terminal interface alongside the Steam bot and dashboard.
- **Multiple transport adapters** — Discord bot adapter as an alternative or complement to Steam chat, sharing the same inbox/outbox plumbing.
- **Provisioning wizard** — guided new-server provisioning flow through the dashboard: pick map seed, port, plugins, generate and validate config, commit to git, and start.
- **Plugin marketplace browser** — integrated uMod plugin search and install UI within the dashboard, replacing manual file staging.
