# Infrastructure

Rusticaland operational facts in this folder are curated manual seed knowledge.

## VPS Routing

The VPS public endpoint forwards traffic to the Windows server through the configured private overlay route.

The VPS acts as a public-facing gateway only. The actual Rust servers run on the Windows host behind a private overlay network (NetBird / WireGuard equivalent). All inbound traffic is DNATed from the VPS to the internal host.

## Architecture

Flow:
Internet → VPS (public IPs) → Overlay tunnel → Windows server

Key roles:
- VPS: routing, firewall, reverse proxy, public exposure
- Windows server: Rust servers, APIs, services
- Overlay network: private routing (no direct public exposure)

Important:
- Windows server MUST NOT be directly exposed to the internet
- All routing goes through VPS
- Overlay must always be healthy

## Ports and Routing

Typical ports:
- Game: 28015 UDP
- RCON: 28016 TCP
- Query: 27015 UDP
- WebRCON: optional TCP

Rules:
- Only game + query ports are public
- RCON is restricted to admin IPs
- All ports forwarded via VPS → overlay → Windows

## Firewall Rules (concept)

- Allow public:
  - 28015 UDP/TCP
  - 27015 UDP

- Restrict:
  - 28016 (RCON) → admin IP only

- Drop everything else

## Resource Model

Rust is CPU-bound (single-thread heavy):

- CPU: high clock > many cores
- RAM:
  - small: 8 GB
  - medium: 12–16 GB
  - modded: 16–32 GB
- Disk: NVMe required
- Network:
  - ~1 Mbps per player (rough rule)

## Storage

- Map + saves grow over time
- Logs + plugins increase disk usage

Minimum:
- Vanilla: ~20–30 GB
- Modded: 50+ GB

## Security

Critical rules:

- NEVER expose RCON publicly
- Strong RCON password
- Firewall whitelist for admin access
- Keep overlay updated
- Regular backups of:
  - configs
  - saves
  - plugin data

Failure mode:
If RCON is exposed → full server compromise possible (wipe, bans, shutdown)