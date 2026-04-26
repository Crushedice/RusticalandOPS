# Deploy Checklist

Use this on the Linux host as the bring-up sequence for the full stack.

This checklist assumes you already published the apps and copied the final Linux files into a flat layout under `/opt/rust-manager`.

## 1. Base layout

Expected root:

- `/opt/rust-manager/rustmgr.sh`
- `/opt/rust-manager/config`
- `/opt/rust-manager/runtime`
- `/opt/rust-manager/tasks`
- `/opt/rust-manager/api/rustmgrapi.dll`
- `/opt/rust-manager/agent/RustOpsAgent/RustOpsAgent.dll`
- `/opt/rust-manager/agent/RustOpsAgent/agentsettings.json`
- `/opt/rust-manager/SteamBot/OpsSteamBot/OpsSteamBot.dll`
- `/opt/rust-manager/SteamBot/OpsSteamBot/botsettings.json`
- `/opt/rust-manager/deploy/systemd`

## 2. Service account

Create the dedicated service account if it does not exist:

```bash
sudo useradd --system --home /opt/rust-manager --shell /usr/sbin/nologin rustmgr || true
sudo chown -R rustmgr:rustmgr /opt/rust-manager
```

## 3. Permissions and directories

Make sure the runtime directories exist:

```bash
sudo mkdir -p /opt/rust-manager/config
sudo mkdir -p /opt/rust-manager/runtime
sudo mkdir -p /opt/rust-manager/tasks
sudo mkdir -p /opt/rust-manager/agent/RustOpsAgent/data/chat-inbox
sudo mkdir -p /opt/rust-manager/agent/RustOpsAgent/data/feedback-inbox
sudo mkdir -p /opt/rust-manager/agent/RustOpsAgent/data/decision-inbox
sudo mkdir -p /opt/rust-manager/agent/RustOpsAgent/data/message-outbox
sudo mkdir -p /opt/rust-manager/agent/RustOpsAgent/data/message-outbox-sent
sudo mkdir -p /opt/rust-manager/agent/RustOpsAgent/data/message-outbox-deadletter
sudo chown -R rustmgr:rustmgr /opt/rust-manager
```

## 4. API config

Edit the API service environment in:

- `/opt/rust-manager/deploy/systemd/rustmgrapi.service`

Set at minimum:

- `RUSTMGR_API_KEY`
- `RUSTMGR_BIND`
- `RUSTMGR_PATH`
- `RUSTMGR_RUNTIME`
- `RUSTMGR_CONFIG`
- `RUSTMGR_TASKS_DIR`

Recommended:

- create `/opt/rust-manager/config.env` from `config.env.example`
- let the API, agent, and Steam adapter all read their shared values from that one file

## 5. Agent config

Create:

- `/opt/rust-manager/agent/RustOpsAgent/agentsettings.json`

Based on:

- `/opt/rust-manager/agent/RustOpsAgent/agentsettings.example.json`

Check:

- API URL is correct
- API key matches the API service
- inbox/outbox paths point to the Linux data folders
- LLM access stays disabled until you finish the local runtime setup

## 6. Steam adapter config

Create:

- `/opt/rust-manager/SteamBot/OpsSteamBot/botsettings.json`

Based on:

- `/opt/rust-manager/SteamBot/OpsSteamBot/botsettings.example.json`

Check:

- Steam bot username/password are correct
- admin Steam IDs are correct
- API URL/key match the API
- if you keep paths in JSON, they must use Linux separators or absolute Linux paths
- preferred: keep the shared agent data paths in `/opt/rust-manager/config.env`

## 7. Published file check

Before enabling services, verify the deployed files exist:

```bash
ls -l /opt/rust-manager/api/rustmgrapi.dll
ls -l /opt/rust-manager/agent/RustOpsAgent/RustOpsAgent.dll
ls -l /opt/rust-manager/SteamBot/OpsSteamBot/OpsSteamBot.dll
```

## 8. First manual smoke test

API:

```bash
cd /opt/rust-manager/api
dotnet /opt/rust-manager/api/rustmgrapi.dll
```

Agent:

```bash
cd /opt/rust-manager/agent/RustOpsAgent
dotnet /opt/rust-manager/agent/RustOpsAgent/RustOpsAgent.dll /opt/rust-manager/agent/RustOpsAgent/agentsettings.json
```

Steam adapter:

```bash
cd /opt/rust-manager/SteamBot/OpsSteamBot
dotnet /opt/rust-manager/SteamBot/OpsSteamBot/OpsSteamBot.dll /opt/rust-manager/SteamBot/OpsSteamBot/botsettings.json
```

Then verify:

- `curl http://127.0.0.1:2077/health`
- `agent-state.json` gets created
- Steam bot account logs in
- Steam chat responds to `help` and `pending`

## 9. Steam Guard note

If the bot account asks for a Steam Guard code, do the first login manually before relying on `systemd`.

If the Steam adapter works manually as `rustmgr` but not as a service, verify that `opssteambot.service` includes:

- `Environment=HOME=/opt/rust-manager`
- `Environment=DOTNET_CLI_HOME=/opt/rust-manager`
- `Environment=TMPDIR=/tmp`

and that `ExecStart` uses the full path to `dotnet`.

## 10. Install services

Use:

- `/opt/rust-manager/deploy/systemd/install-services.sh`

Or install manually from the `deploy/systemd/README.md`.

## 10a. One-line install

```bash
sudo bash /opt/rust-manager/deploy/systemd/install-services.sh
```

## 11. Post-start checks

```bash
sudo systemctl status rustmgrapi.service
sudo systemctl status rustopsagent.service
sudo systemctl status opssteambot.service
sudo bash /opt/rust-manager/deploy/systemd/check-services.sh
```

Logs:

```bash
journalctl -u rustmgrapi.service -f
journalctl -u rustopsagent.service -f
journalctl -u opssteambot.service -f
```

## 12. When enabling the local LLM runtime

Only after the stack is stable:

- install/run LM Studio on the host and make sure LM Link is active
- set the `llm` section in `agentsettings.json`
- verify the configured model is present and reachable on the local server endpoint
- restart `rustopsagent.service`
