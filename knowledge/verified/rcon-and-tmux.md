# RCON And tmux

Use RCON for live server commands when the WebRCON endpoint is healthy.

## RCON

Primary control interface.

Capabilities:
- Execute commands
- Manage players
- Monitor server
- Automate tasks

Required config:
- rcon.web 1
- rcon.password <strong password>
- rcon.port 28016

Security:
- NEVER expose publicly
- Restrict to admin IP

## Core Commands

- server.save
- server.writecfg
- players
- kick <id>
- ban <id>
- say "message"
- server.stop

## Restart (Preferred)

Use:
restart <seconds> <message>

Behavior:
- Notifies players
- Saves world
- Prevents rollback

## tmux

Use tmux inspection only for process-level diagnostics when API or RCON status is not enough.

Use cases:
- Server won’t respond to RCON
- Startup debugging
- Crash investigation

Commands:
- start: tmux new -s rust ./start.sh
- attach: tmux attach -t rust
- detach: Ctrl+B then D

## Rule of Separation

Use:
- RCON → gameplay / admin actions
- API → automation / orchestration
- tmux → low-level debugging only

Failure mode:
Using tmux for normal ops = unsafe + no state tracking