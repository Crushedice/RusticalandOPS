# Rust Servers

Use the RustOps agent and rust manager API for bounded server lifecycle, health, log, plugin, and RCON tasks.

## Lifecycle

Prefer graceful Rust restart commands when RCON is available before using an immediate process restart.

## Control Model

Priority order:
1. RustOps API
2. RCON
3. tmux (last resort)

## Monitoring

Host level:
- htop → CPU/RAM
- ss/netstat → ports

Server level:
- status
- perf 1
- entity.stats

## Performance Characteristics

- Single-thread heavy
- CPU clock speed matters most
- Memory grows over time
- Plugins amplify load

## Tuning

Recommended:

- server.tickrate 30
- server.saveinterval 300
- gc.buffer 256

Goals:
- reduce CPU spikes
- stabilize memory
- limit entity load

## Plugins

Structure:
- oxide/plugins
- oxide/config
- oxide/data
- oxide/logs

Rules:
- Hot-load supported
- No restart required
- Permissions default deny

Commands:
- oxide.plugins
- oxide.reload <name>
- oxide.unload <name>

Critical:
Rust updates break Oxide → reinstall every update

## Wipes

Types:
- Map wipe
- Blueprint wipe
- Full wipe

Standard:
- Forced wipe: first Thursday monthly

Process:
1. Notify players
2. Save server
3. Stop gracefully
4. Clear data
5. Update server
6. Reinstall Oxide
7. Restart

## Common Issues

- Not in server list → ports wrong
- Players can't join → firewall
- Plugin broken → outdated
- Crash → not enough RAM
- RCON dead → config/firewall

## Operational Rules

- Always prefer graceful restart
- Never kill process unless stuck
- Always save before restart
- Always monitor after changes