#!/usr/bin/env bash
# setup-remote-node.sh — drop this on a fresh Debian system and run as root.
# Sets up a RustOps remote node: steamcmd, rustmgr, .NET runtime, and the
# remote-agent service.  The main RusticalandOPS API on your primary host
# connects to this node to gain the same lifecycle control as a local server.
#
# Usage:
#   sudo bash setup-remote-node.sh
#   # or with overrides:
#   sudo INSTALL_ROOT=/opt/rust-manager RUST_USER=rustmgr bash setup-remote-node.sh
#
set -Eeuo pipefail

# ── Tunable defaults ──────────────────────────────────────────────────────────
INSTALL_ROOT="${INSTALL_ROOT:-/opt/rust-manager}"
RUST_USER="${RUST_USER:-rustmgr}"
RUST_GROUP="${RUST_GROUP:-rustmgr}"
DOTNET_VERSION="${DOTNET_VERSION:-8}"
AGENT_BIND="${AGENT_BIND:-http://0.0.0.0:2088}"
STEAMCMD_DIR="${STEAMCMD_DIR:-/opt/steamcmd}"
SERVERS_DIR="${SERVERS_DIR:-/srv/rust}"
SYSTEMD_DIR="/etc/systemd/system"

# ── Helpers ───────────────────────────────────────────────────────────────────
log()  { printf '\e[1;32m[remote-node]\e[0m %s\n' "$*"; }
warn() { printf '\e[1;33m[remote-node] WARN:\e[0m %s\n' "$*"; }
die()  { printf '\e[1;31m[remote-node] ERROR:\e[0m %s\n' "$*" >&2; exit 1; }

require_root() {
  [[ "${EUID:-$(id -u)}" -eq 0 ]] || die "Run this script as root (sudo)."
}

require_debian() {
  [[ -f /etc/debian_version ]] || die "This script targets Debian/Ubuntu only."
}

# ── Step 1: System packages ───────────────────────────────────────────────────
install_system_packages() {
  log "Updating apt and installing base packages..."
  apt-get update -qq

  # lib32gcc1 / lib32gcc-s1 required by steamcmd
  apt-get install -y --no-install-recommends \
    curl wget ca-certificates gnupg \
    lib32gcc-s1 \
    libicu-dev \
    unzip tar git \
    jq \
    2>/dev/null || apt-get install -y --no-install-recommends \
    curl wget ca-certificates gnupg \
    lib32gcc1 \
    libicu-dev \
    unzip tar git \
    jq

  # Enable i386 for steamcmd
  dpkg --add-architecture i386
  apt-get update -qq
  apt-get install -y --no-install-recommends lib32gcc-s1 2>/dev/null || true

  log "System packages installed."
}

# ── Step 2: .NET runtime ──────────────────────────────────────────────────────
install_dotnet() {
  if command -v dotnet &>/dev/null; then
    log ".NET already installed: $(dotnet --version)"
    return
  fi
  log "Installing .NET ${DOTNET_VERSION} runtime..."
  # Microsoft's install script is the most reliable cross-distro method.
  local tmp; tmp=$(mktemp)
  curl -sSfL https://dot.net/v1/dotnet-install.sh -o "$tmp"
  chmod +x "$tmp"
  bash "$tmp" --channel "${DOTNET_VERSION}.0" --runtime dotnet --install-dir /usr/share/dotnet
  rm -f "$tmp"
  ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
  log ".NET installed: $(dotnet --version)"
}

# ── Step 3: steamcmd ──────────────────────────────────────────────────────────
install_steamcmd() {
  if [[ -x "${STEAMCMD_DIR}/steamcmd.sh" ]]; then
    log "steamcmd already present at ${STEAMCMD_DIR}."
    return
  fi
  log "Installing steamcmd to ${STEAMCMD_DIR}..."
  mkdir -p "${STEAMCMD_DIR}"
  curl -sSfL "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz" \
    | tar -xz -C "${STEAMCMD_DIR}"
  chown -R "${RUST_USER}:${RUST_GROUP}" "${STEAMCMD_DIR}"
  # First-run to update
  sudo -u "${RUST_USER}" "${STEAMCMD_DIR}/steamcmd.sh" +quit || true
  log "steamcmd installed."
}

# ── Step 4: User and group ────────────────────────────────────────────────────
ensure_user() {
  if ! getent group "${RUST_GROUP}" &>/dev/null; then
    groupadd --system "${RUST_GROUP}"
    log "Created group ${RUST_GROUP}."
  fi
  if ! id -u "${RUST_USER}" &>/dev/null; then
    useradd --system \
      --gid "${RUST_GROUP}" \
      --home-dir "${INSTALL_ROOT}" \
      --shell /usr/sbin/nologin \
      --comment "RustOps managed account" \
      "${RUST_USER}"
    log "Created user ${RUST_USER}."
  fi
}

# ── Step 5: Directory layout ──────────────────────────────────────────────────
ensure_layout() {
  log "Creating directory layout under ${INSTALL_ROOT}..."
  local dirs=(
    "${INSTALL_ROOT}/remote-agent/RustOpsRemoteAgent"
    "${INSTALL_ROOT}/config"
    "${INSTALL_ROOT}/runtime"
    "${INSTALL_ROOT}/tasks"
    "${SERVERS_DIR}"
  )
  for d in "${dirs[@]}"; do
    mkdir -p "$d"
  done
  chown -R "${RUST_USER}:${RUST_GROUP}" "${INSTALL_ROOT}" "${SERVERS_DIR}"
  chmod 750 "${INSTALL_ROOT}"
  log "Directory layout created."
}

# ── Step 6: rustmgr.sh ───────────────────────────────────────────────────────
deploy_rustmgr() {
  local target="${INSTALL_ROOT}/rustmgr.sh"
  if [[ -f "$target" ]]; then
    log "rustmgr.sh already present — skipping (delete manually to replace)."
    return
  fi

  # Detect if we're running from a cloned repo
  local script_dir; script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  if [[ -f "${script_dir}/../rustmgr.sh" ]]; then
    install -m 0750 -o "${RUST_USER}" -g "${RUST_GROUP}" \
      "${script_dir}/../rustmgr.sh" "$target"
    log "Deployed rustmgr.sh from repo."
  else
    warn "rustmgr.sh not found next to this script. Copy it manually to ${target}."
  fi
}

# ── Step 7: Remote agent binary ───────────────────────────────────────────────
deploy_remote_agent() {
  local agent_dll="${INSTALL_ROOT}/remote-agent/RustOpsRemoteAgent/RustOpsRemoteAgent.dll"

  if [[ -f "$agent_dll" ]]; then
    log "Remote agent binary already present."
    return
  fi

  local script_dir; script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  local published="${script_dir}/../remote-agent/RustOpsRemoteAgent/bin/Release/net8.0/publish"
  if [[ -d "$published" ]]; then
    cp -a "$published/." "${INSTALL_ROOT}/remote-agent/RustOpsRemoteAgent/"
    chown -R "${RUST_USER}:${RUST_GROUP}" "${INSTALL_ROOT}/remote-agent"
    log "Remote agent binary deployed from repo build output."
  else
    warn "Pre-built remote agent not found. Build the solution first:"
    warn "  dotnet publish remote-agent/RustOpsRemoteAgent -c Release -o /tmp/remote-agent-publish"
    warn "Then copy /tmp/remote-agent-publish/ to ${INSTALL_ROOT}/remote-agent/RustOpsRemoteAgent/"
  fi
}

# ── Step 8: Environment file ──────────────────────────────────────────────────
write_env_file() {
  local env_file="${INSTALL_ROOT}/remote-agent.env"
  if [[ -f "$env_file" ]]; then
    log "remote-agent.env already exists — not overwriting. Edit ${env_file} as needed."
    return
  fi

  local api_key; api_key="$(openssl rand -hex 32 2>/dev/null || cat /proc/sys/kernel/random/uuid 2>/dev/null || echo "replace-me-$(date +%s)")"

  cat > "$env_file" <<ENVEOF
# RustOps Remote Node configuration
# Adjust and keep this file secure (chmod 640, owned by root:rustmgr).

RUSTOPS_REMOTE_AGENT_BIND=${AGENT_BIND}

# Generate a strong random key and share it with the primary host's
# RUSTOPS_REMOTE_AGENT_API_KEY setting or the agent registry entry.
RUSTOPS_REMOTE_AGENT_API_KEY=${api_key}

# Shared variables consumed by both rustmgr.sh and the remote agent
RUSTMGR_PATH=${INSTALL_ROOT}/rustmgr.sh
RUSTMGR_RUNTIME=${INSTALL_ROOT}/runtime
RUSTMGR_CONFIG=${INSTALL_ROOT}/config
RUSTMGR_TASKS_DIR=${INSTALL_ROOT}/tasks

# steamcmd path (used by rustmgr for update operations)
STEAMCMD_DIR=${STEAMCMD_DIR}
ENVEOF

  chmod 640 "$env_file"
  chown root:"${RUST_GROUP}" "$env_file"
  log "Wrote ${env_file} — review and set your actual keys."
  log "Generated API key: ${api_key}"
  warn "Record this key! It won't be shown again."
}

# ── Step 9: systemd service ───────────────────────────────────────────────────
install_systemd_service() {
  local service_file="${SYSTEMD_DIR}/rustops-remote-agent.service"
  local source_service
  local script_dir; script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

  if [[ -f "${script_dir}/systemd/rustops-remote-agent.service" ]]; then
    source_service="${script_dir}/systemd/rustops-remote-agent.service"
  elif [[ -f "${script_dir}/rustops-remote-agent.service" ]]; then
    source_service="${script_dir}/rustops-remote-agent.service"
  else
    log "Writing systemd unit inline..."
    cat > "$service_file" <<SVCEOF
[Unit]
Description=RustOps Remote Rust Server Agent
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${RUST_USER}
Group=${RUST_GROUP}
WorkingDirectory=${INSTALL_ROOT}/remote-agent/RustOpsRemoteAgent
EnvironmentFile=-${INSTALL_ROOT}/config.env
EnvironmentFile=-${INSTALL_ROOT}/remote-agent.env
Environment=DOTNET_CLI_HOME=${INSTALL_ROOT}
ExecStart=/usr/local/bin/dotnet ${INSTALL_ROOT}/remote-agent/RustOpsRemoteAgent/RustOpsRemoteAgent.dll
Restart=always
RestartSec=5
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
SVCEOF
    chmod 644 "$service_file"
    log "Wrote ${service_file}"
    systemctl daemon-reload
    systemctl enable rustops-remote-agent.service
    log "Service enabled. Start it with: systemctl start rustops-remote-agent"
    return
  fi

  install -m 0644 "$source_service" "$service_file"
  log "Installed ${service_file}"
  systemctl daemon-reload
  systemctl enable rustops-remote-agent.service
  log "Service enabled. Start it with: systemctl start rustops-remote-agent"
}

# ── Step 10: Firewall hint ────────────────────────────────────────────────────
print_firewall_hint() {
  local port; port="${AGENT_BIND##*:}"
  log ""
  log "Firewall: open port ${port}/tcp from your primary RusticalandOPS host."
  log "  ufw:  ufw allow from <primary-host-ip> to any port ${port} proto tcp"
  log "  iptables: iptables -A INPUT -p tcp --dport ${port} -s <primary-host-ip> -j ACCEPT"
  log ""
  log "Primary host registration: add this node to the agent registry with"
  log "  URL: http://<this-host-ip>:${port}"
  log "  API key: (see ${INSTALL_ROOT}/remote-agent.env)"
}

# ── Main ──────────────────────────────────────────────────────────────────────
main() {
  require_root
  require_debian

  log "=== RustOps Remote Node Setup ==="
  log "Install root : ${INSTALL_ROOT}"
  log "Service user : ${RUST_USER}:${RUST_GROUP}"
  log "steamcmd dir : ${STEAMCMD_DIR}"
  log "Servers dir  : ${SERVERS_DIR}"
  log "Agent bind   : ${AGENT_BIND}"
  log ""

  install_system_packages
  ensure_user
  install_dotnet
  install_steamcmd
  ensure_layout
  deploy_rustmgr
  deploy_remote_agent
  write_env_file
  install_systemd_service
  print_firewall_hint

  log ""
  log "=== Setup complete ==="
  log "Next steps:"
  log "  1. Edit ${INSTALL_ROOT}/remote-agent.env and confirm your API key."
  log "  2. systemctl start rustops-remote-agent"
  log "  3. Register this node in your primary RusticalandOPS agent registry."
  log "  4. Add server entries in ${INSTALL_ROOT}/config/ (copy from primary if applicable)."
}

main "$@"
