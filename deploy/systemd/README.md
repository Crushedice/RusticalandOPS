# systemd setup

These unit files assume a flat published deployment on the Linux host.

## Expected layout

- `/opt/rust-manager/rustmgr.sh`
- `/opt/rust-manager/config`
- `/opt/rust-manager/runtime`
- `/opt/rust-manager/tasks`
- `/opt/rust-manager/api/rustmgrapi.dll`
- `/opt/rust-manager/agent/RustOpsAgent/RustOpsAgent.dll`
- `/opt/rust-manager/agent/RustOpsAgent/agentsettings.json`
- `/opt/rust-manager/remote-agent/RustOpsRemoteAgent/RustOpsRemoteAgent.dll`
- `/opt/rust-manager/SteamBot/OpsSteamBot/OpsSteamBot.dll`
- `/opt/rust-manager/SteamBot/OpsSteamBot/botsettings.json`

## Recommended service account

Create a dedicated service user:

```bash
sudo useradd --system --home /opt/rust-manager --shell /usr/sbin/nologin rustmgr
sudo chown -R rustmgr:rustmgr /opt/rust-manager
```

## Install the units

Copy the service files into `/etc/systemd/system/`:

```bash
sudo cp /opt/rust-manager/deploy/systemd/rustmgrapi.service /etc/systemd/system/
sudo cp /opt/rust-manager/deploy/systemd/rustopsagent.service /etc/systemd/system/
sudo cp /opt/rust-manager/deploy/systemd/rustops-remote-agent.service /etc/systemd/system/
sudo cp /opt/rust-manager/deploy/systemd/opssteambot.service /etc/systemd/system/
```

Then reload and enable them:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now rustmgrapi.service
sudo systemctl enable --now rustopsagent.service
sudo systemctl enable --now rustops-remote-agent.service
sudo systemctl enable --now opssteambot.service
```

## Important edits before enabling

Edit `rustmgrapi.service` and set:

- `RUSTMGR_API_KEY`
- `RUSTMGR_BIND` if you do not want `0.0.0.0:2077`
- `RUSTOPS_REMOTE_AGENT_API_KEY` for remote host management
- `RUSTOPS_REMOTE_AGENT_BIND` if the remote agent should not listen on `0.0.0.0:2088`

Or place the shared values in:

- `/opt/rust-manager/config.env`

Prefer putting the shared API values and agent inbox/outbox paths in:

- `/opt/rust-manager/config.env`

The deployment-facing example configs already point at those env vars, so you do not need to hardcode Linux paths into both JSON files.

For the Steam adapter, keep the explicit environment values in the unit:

- `HOME=/opt/rust-manager`
- `DOTNET_CLI_HOME=/opt/rust-manager`
- `TMPDIR=/tmp`

These help the Steam adapter behave the same way under `systemd` as it does during a successful manual run as `rustmgr`.

## Manual dry-run before systemd

The API, agent, and Steam adapter will all try to read a shared `config.env` file from the current working tree or `/opt/rust-manager/config.env`.

If you want to test the published files first, run:

```bash
cd /opt/rust-manager/api
dotnet /opt/rust-manager/api/rustmgrapi.dll
```

```bash
cd /opt/rust-manager/agent/RustOpsAgent
dotnet /opt/rust-manager/agent/RustOpsAgent/RustOpsAgent.dll /opt/rust-manager/agent/RustOpsAgent/agentsettings.json
```

```bash
cd /opt/rust-manager/remote-agent/RustOpsRemoteAgent
dotnet /opt/rust-manager/remote-agent/RustOpsRemoteAgent/RustOpsRemoteAgent.dll
```

```bash
cd /opt/rust-manager/SteamBot/OpsSteamBot
dotnet /opt/rust-manager/SteamBot/OpsSteamBot/OpsSteamBot.dll /opt/rust-manager/SteamBot/OpsSteamBot/botsettings.json
```

## Logs

Use `journalctl`:

```bash
journalctl -u rustmgrapi.service -f
journalctl -u rustopsagent.service -f
journalctl -u rustops-remote-agent.service -f
journalctl -u opssteambot.service -f
```

## Notes

- The Steam adapter may need a first interactive login to enter a Steam Guard code.
- If the Steam adapter works manually as `rustmgr` but not under `systemd`, check the service environment first before changing bot code.
- If you later switch to self-contained binaries, replace the `ExecStart=/usr/bin/dotnet ...dll` lines with the executable paths.
- If you want the local LLM runtime managed by `systemd` too, add a separate unit for it and order `rustopsagent.service` after it.
