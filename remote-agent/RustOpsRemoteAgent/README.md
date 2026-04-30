# RustOps Remote Agent

`RustOpsRemoteAgent` is the Debian-side host agent for managing a Rust server on another machine as if it were local to the main RusticalandOPS API.

Install it on the machine that owns `rustmgr.sh`, the Rust server files, configs, logs, and RCON credentials. The main API then registers this host as an agent-backed remote server and proxies lifecycle, logs, config, and RCON calls through it.

## Environment

```bash
RUSTOPS_REMOTE_AGENT_BIND=http://0.0.0.0:2088
RUSTOPS_REMOTE_AGENT_API_KEY=replace-with-a-long-random-token
RUSTMGR_PATH=/opt/rust-manager/rustmgr.sh
RUSTMGR_CONFIG=/opt/rust-manager/config
RUSTMGR_RUNTIME=/opt/rust-manager/runtime
RUSTMGR_TASKS_DIR=/opt/rust-manager/tasks
```

The agent also accepts `RUSTMGR_API_KEY` or `RUSTOPS_API_KEY` as a fallback API key, but a dedicated `RUSTOPS_REMOTE_AGENT_API_KEY` is preferred.

## Core endpoints

- `GET /health`
- `GET /servers`
- `GET /servers/summary`
- `GET /servers/{server}/status`
- `GET /servers/{server}/health`
- `POST /servers/{server}/start`
- `POST /servers/{server}/stop`
- `POST /servers/{server}/restart`
- `POST /servers/{server}/kill`
- `POST /servers/{server}/update`
- `POST /servers/{server}/umod`
- `POST /servers/{server}/sync-config`
- `POST /servers/{server}/wipe`
- `GET /servers/{server}/config`
- `PUT /servers/{server}/config`
- `POST /servers/{server}/config/validate`
- `GET /servers/{server}/console`
- `GET /servers/{server}/logs/tail`
- `GET /servers/{server}/logs/read`
- `GET /servers/{server}/commands`
- `GET /servers/{server}/events`
- `POST /servers/{server}/command`
- `POST /servers/{server}/command/exec`
- `GET /servers/{server}/serverinfo`
- `GET /servers/{server}/players`
- `GET /servers/{server}/bans`
- `POST /servers/{server}/kick`
- `POST /servers/{server}/ban`
- `POST /servers/{server}/unban`

All endpoints except `/` and `/health` require `X-Api-Key`.

## Main API registration

Register a remote server with `agentBaseUrl`, `agentApiKey`, and optionally `agentServerName`.

```json
{
  "name": "remote-vanilla",
  "displayName": "Remote Vanilla",
  "rconIp": "10.10.0.22",
  "rconPort": 28016,
  "rconPassword": "legacy-rcon-fallback",
  "gamePort": 28015,
  "agentBaseUrl": "http://10.10.0.22:2088",
  "agentApiKey": "replace-with-a-long-random-token",
  "agentServerName": "vanilla"
}
```
