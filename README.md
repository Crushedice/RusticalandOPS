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

### Environment — `rustops.env`

Secrets and overrides loaded at startup from `/etc/rustops.env` (or `rustops.env` next to the binary):

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

## Known limitations

- **LLM is required for rich responses.** When disabled, the agent falls back to template messages. All core operations (start/stop/RCON/logs) still work without LLM.
- **GitOps branch push requires `pushBranchPrefix = "agent/"`** — enforced at startup.
- **Plugin download is disabled by default** (`pluginUpdates.downloadEnabled = false`). Enable and set `stagingPath` to allow automatic `.cs` staging.
- **RCON commands outside the allow-list require admin approval** via Steam `approve` command before execution.
- **Log pagination** uses the `offset` parameter on `/servers/{s}/logs/tail`; the underlying `rustmgr.sh logs` command always fetches the most recent N lines, so very old entries are not addressable without `/logs/read` (byte-offset reader).
