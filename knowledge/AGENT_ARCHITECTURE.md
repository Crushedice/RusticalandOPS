# Local Ops Agent Architecture

## Premise

The product is a local AI operations agent running on the Linux host.
Steam chat is only one interface adapter for admins, not the core system.

## Core layers

1. `rustmgr.sh`
The authority for Rust server lifecycle actions.

2. `api/`
The deterministic control plane around `rustmgr.sh`.
It exposes safe, structured endpoints for:
- server lifecycle
- log and event inspection
- config validation and provisioning
- host network inspection
- managed scheduled tasks
- Oxide config/plugin validation

3. `remote-agent/`
The Debian-side remote host control service.
Install `remote-agent/RustOpsRemoteAgent` on a different Rust host when the main API should manage that host as if it were local.
It exposes authenticated endpoints for:
- server lifecycle through the host's local `rustmgr.sh`
- config read/write/validation
- log, command trace, and event reads
- WebRCON command/query/moderation

The main API stores agent-backed remote entries in `remote-servers.json` with `agentBaseUrl`, `agentApiKey`, and optional `agentServerName`, then proxies normal `/servers/{server}/...` calls to the remote agent.

4. `agent/` or future agent service
The reasoning layer.
This service should:
- call a local LLM runtime such as LM Studio via OpenAI-compatible endpoints
- decide when to inspect, summarize, or act
- keep policy boundaries for destructive actions
- choose the proper interface adapter

5. Interface adapters
- Steam chat bot
- web dashboard
- optional CLI
- optional in-game RCON/admin relay

## Capability map

- Steam chatbot:
Implemented as an adapter candidate in `SteamBot/OpsSteamBot`.

- Monitor server console:
Use `/servers/{server}/logs/tail`, `/servers/{server}/health`, `/servers/{server}/events`.

- Monitor root server network interfaces:
Use `/host/network/interfaces`.

- Create new server instances with configs:
Use `/servers/provision` plus `/servers/{server}/config/validate`.

- Create delayed or repeating tasks:
Use `/tasks`.

- Validate `rustmgr` JSON configs:
Use `/servers/{server}/config/validate`.

- Validate Oxide configs/plugins:
Use `/servers/{server}/oxide/validate`.

- Evaluate console and reason about action:
Agent responsibility, using log/event/health endpoints as tools.

- Check and update uMod/Oxide plugins:
Partially present via `rustmgr.sh umod`; version-aware plugin update logic still needs a dedicated plugin manager layer.

- Stand-in admin responses to players:
Future agent policy module; this should be gated and auditable before enabling autonomous replies.

## Recommended next implementation steps

1. Build the Linux-side `agent` service with a tool-calling loop against the API.
2. Add a policy layer for approval-required actions.
3. Add a plugin inventory/update subsystem.
4. Add a memory store for incidents, prior actions, and admin preferences.
5. Rewire the Steam bot so it forwards to the agent instead of calling the API directly.
