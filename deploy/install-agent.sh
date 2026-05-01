#!/usr/bin/env bash
# install-agent.sh — Install the full RusticalandOPS stack on a primary Debian host.
# Installs: .NET runtime, steamcmd, rustmgr, the API (rustmgrapi), the main agent
# (rustopsagent), and optionally the Steam bot (opssteambot).
#
# Run this ONCE on a fresh system.  Subsequent updates should go through
# autoPull (agent self-update) or manual git pull + Agent-Build.sh.
#
# Usage:
#   sudo bash install-agent.sh
#   # or with overrides:
#   sudo INSTALL_ROOT=/opt/rust-manager RUST_USER=rustmgr bash install-agent.sh
#
set -Eeuo pipefail

# ── Tunable defaults ──────────────────────────────────────────────────────────
INSTALL_ROOT="${INSTALL_ROOT:-/opt/rust-manager}"
RUST_USER="${RUST_USER:-rustmgr}"
RUST_GROUP="${RUST_GROUP:-rustmgr}"
DOTNET_VERSION="${DOTNET_VERSION:-8}"
STEAMCMD_DIR="${STEAMCMD_DIR:-/opt/steamcmd}"
SERVERS_DIR="${SERVERS_DIR:-/srv/rust}"
REPO_DIR="${REPO_DIR:-${INSTALL_ROOT}/src}"
SYSTEMD_DIR="/etc/systemd/system"
SKIP_STEAMCMD="${SKIP_STEAMCMD:-false}"
SKIP_STEAM_BOT="${SKIP_STEAM_BOT:-true}"

# ── Helpers ───────────────────────────────────────────────────────────────────
log()  { printf '\e[1;32m[install-agent]\e[0m %s\n' "$*"; }
warn() { printf '\e[1;33m[install-agent] WARN:\e[0m %s\n' "$*"; }
die()  { printf '\e[1;31m[install-agent] ERROR:\e[0m %s\n' "$*" >&2; exit 1; }

require_root() {
  [[ "${EUID:-$(id -u)}" -eq 0 ]] || die "Run this script as root (sudo)."
}

require_debian() {
  [[ -f /etc/debian_version ]] || die "This script targets Debian/Ubuntu only."
}

confirm() {
  local prompt="$1"
  local answer
  read -rp "${prompt} [y/N] " answer
  [[ "${answer,,}" == "y" ]]
}

# ── Step 1: System packages ───────────────────────────────────────────────────
install_system_packages() {
  log "Updating apt and installing base packages..."
  apt-get update -qq
  apt-get install -y --no-install-recommends \
    curl wget ca-certificates gnupg \
    lib32gcc-s1 \
    libicu-dev \
    unzip tar git \
    jq \
    2>/dev/null || apt-get install -y --no-install-recommends \
    curl wget ca-certificates gnupg lib32gcc1 libicu-dev unzip tar git jq

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
  if [[ "${SKIP_STEAMCMD}" == "true" ]]; then
    log "SKIP_STEAMCMD=true — skipping steamcmd installation."
    return
  fi
  if [[ -x "${STEAMCMD_DIR}/steamcmd.sh" ]]; then
    log "steamcmd already present at ${STEAMCMD_DIR}."
    return
  fi
  log "Installing steamcmd to ${STEAMCMD_DIR}..."
  mkdir -p "${STEAMCMD_DIR}"
  curl -sSfL "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz" \
    | tar -xz -C "${STEAMCMD_DIR}"
  chown -R "${RUST_USER}:${RUST_GROUP}" "${STEAMCMD_DIR}"
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
    "${INSTALL_ROOT}/config"
    "${INSTALL_ROOT}/runtime"
    "${INSTALL_ROOT}/tasks"
    "${INSTALL_ROOT}/api"
    "${INSTALL_ROOT}/agent/RustOpsAgent/data/feedback-inbox"
    "${INSTALL_ROOT}/agent/RustOpsAgent/data/decision-inbox"
    "${INSTALL_ROOT}/agent/RustOpsAgent/data/chat-inbox"
    "${INSTALL_ROOT}/agent/RustOpsAgent/data/message-outbox"
    "${INSTALL_ROOT}/agent/RustOpsAgent/data/message-outbox-sent"
    "${INSTALL_ROOT}/agent/RustOpsAgent/data/NeoCortex"
    "${INSTALL_ROOT}/agent/RustOpsAgent/data/plugin-staging"
    "${INSTALL_ROOT}/remote-agent/RustOpsRemoteAgent"
    "${SERVERS_DIR}"
  )
  for d in "${dirs[@]}"; do
    mkdir -p "$d"
  done
  chown -R "${RUST_USER}:${RUST_GROUP}" "${INSTALL_ROOT}" "${SERVERS_DIR}"
  chmod 750 "${INSTALL_ROOT}"
  log "Directory layout created."
}

# ── Step 6: Deploy binaries ───────────────────────────────────────────────────
deploy_binaries() {
  local script_dir; script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  local repo_root; repo_root="$(cd "${script_dir}/.." && pwd)"

  # rustmgr.sh
  local rustmgr_src="${repo_root}/rustmgr.sh"
  if [[ -f "$rustmgr_src" ]]; then
    install -m 0750 -o "${RUST_USER}" -g "${RUST_GROUP}" "$rustmgr_src" "${INSTALL_ROOT}/rustmgr.sh"
    log "Deployed rustmgr.sh"
  else
    warn "rustmgr.sh not found at ${rustmgr_src} — copy it manually to ${INSTALL_ROOT}/rustmgr.sh"
  fi

  # Build and deploy the API
  local api_src="${repo_root}/api"
  if [[ -d "$api_src" ]]; then
    log "Building API..."
    dotnet publish "${api_src}" -c Release -o "${INSTALL_ROOT}/api" --nologo -q
    chown -R "${RUST_USER}:${RUST_GROUP}" "${INSTALL_ROOT}/api"
    log "API deployed to ${INSTALL_ROOT}/api"
  else
    warn "API source not found at ${api_src}"
  fi

  # Build and deploy the main agent
  local agent_src="${repo_root}/agent/RustOpsAgent"
  if [[ -d "$agent_src" ]]; then
    log "Building agent..."
    dotnet publish "${agent_src}" -c Release -o "${INSTALL_ROOT}/agent/RustOpsAgent" --nologo -q
    chown -R "${RUST_USER}:${RUST_GROUP}" "${INSTALL_ROOT}/agent"
    log "Agent deployed to ${INSTALL_ROOT}/agent/RustOpsAgent"
  else
    warn "Agent source not found at ${agent_src}"
  fi

  # Build and deploy the remote agent
  local remote_src="${repo_root}/remote-agent/RustOpsRemoteAgent"
  if [[ -d "$remote_src" ]]; then
    log "Building remote agent..."
    dotnet publish "${remote_src}" -c Release -o "${INSTALL_ROOT}/remote-agent/RustOpsRemoteAgent" --nologo -q
    chown -R "${RUST_USER}:${RUST_GROUP}" "${INSTALL_ROOT}/remote-agent"
    log "Remote agent deployed."
  fi

  # Optionally build and deploy the Steam bot
  local bot_src="${repo_root}/steambot"
  if [[ "${SKIP_STEAM_BOT}" == "false" ]] && [[ -d "$bot_src" ]]; then
    log "Building Steam bot..."
    dotnet publish "${bot_src}" -c Release -o "${INSTALL_ROOT}/steambot" --nologo -q
    chown -R "${RUST_USER}:${RUST_GROUP}" "${INSTALL_ROOT}/steambot"
    log "Steam bot deployed."
  fi
}

# ── Step 7: Environment file ──────────────────────────────────────────────────
write_env_file() {
  local env_file="${INSTALL_ROOT}/config.env"
  if [[ -f "$env_file" ]]; then
    log "config.env already exists — not overwriting. Edit ${env_file} as needed."
    return
  fi

  local script_dir; script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  local example="${script_dir}/../config.env.example"

  if [[ -f "$example" ]]; then
    install -m 0640 "$example" "$env_file"
    chown root:"${RUST_GROUP}" "$env_file"
    log "Copied config.env.example to ${env_file}"
    log "IMPORTANT: Edit ${env_file} and set your API keys, LLM URLs, and paths."
  else
    warn "config.env.example not found. Create ${env_file} manually."
  fi
}

# ── Step 8: agentsettings.json ────────────────────────────────────────────────
write_agent_settings() {
  local settings_file="${INSTALL_ROOT}/agent/RustOpsAgent/agentsettings.json"
  if [[ -f "$settings_file" ]]; then
    log "agentsettings.json already present — not overwriting."
    return
  fi

  local script_dir; script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  local example="${script_dir}/../agent/RustOpsAgent/agentsettings.example.json"

  if [[ -f "$example" ]]; then
    install -m 0640 "$example" "$settings_file"
    chown "${RUST_USER}:${RUST_GROUP}" "$settings_file"
    log "Copied agentsettings.example.json to ${settings_file}"
    log "Review and adjust settings in ${settings_file} or set env vars in config.env."
  fi
}

# ── Step 9: systemd services ──────────────────────────────────────────────────
install_systemd_services() {
  local script_dir; script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  local systemd_src="${script_dir}/systemd"

  local services=(
    rustmgrapi.service
    rustopsagent.service
    rustops-remote-agent.service
  )
  if [[ "${SKIP_STEAM_BOT}" == "false" ]]; then
    services+=(opssteambot.service)
  fi

  for svc in "${services[@]}"; do
    local src="${systemd_src}/${svc}"
    if [[ -f "$src" ]]; then
      install -m 0644 "$src" "${SYSTEMD_DIR}/${svc}"
      log "Installed ${svc}"
    else
      warn "Service file not found: ${src}"
    fi
  done

  systemctl daemon-reload

  for svc in "${services[@]}"; do
    if [[ -f "${SYSTEMD_DIR}/${svc}" ]]; then
      systemctl enable "$svc"
      log "Enabled ${svc} (not started yet — configure env file first)"
    fi
  done
}

# ── Step 10: Summary ──────────────────────────────────────────────────────────
print_summary() {
  log ""
  log "=== Installation complete ==="
  log ""
  log "Next steps:"
  log "  1. Edit ${INSTALL_ROOT}/config.env"
  log "     Set RUSTMGR_API_KEY, LLM endpoints, and Sentry DSN."
  log ""
  log "  2. Edit ${INSTALL_ROOT}/agent/RustOpsAgent/agentsettings.json"
  log "     (or rely entirely on env vars in config.env)"
  log ""
  log "  3. Add server configs to ${INSTALL_ROOT}/config/"
  log "     Each server needs a JSON file understood by rustmgr.sh."
  log ""
  log "  4. Start services:"
  log "     systemctl start rustmgrapi"
  log "     systemctl start rustopsagent"
  log ""
  log "  5. Verify:"
  log "     systemctl status rustmgrapi rustopsagent"
  log "     journalctl -u rustopsagent -f"
}

# ── Main ──────────────────────────────────────────────────────────────────────
main() {
  require_root
  require_debian

  log "=== RusticalandOPS Primary Stack Installer ==="
  log "Install root  : ${INSTALL_ROOT}"
  log "Service user  : ${RUST_USER}:${RUST_GROUP}"
  log "steamcmd dir  : ${STEAMCMD_DIR}"
  log "Servers dir   : ${SERVERS_DIR}"
  log "Skip steamcmd : ${SKIP_STEAMCMD}"
  log "Skip Steam bot: ${SKIP_STEAM_BOT}"
  log ""

  install_system_packages
  ensure_user
  install_dotnet
  install_steamcmd
  ensure_layout
  deploy_binaries
  write_env_file
  write_agent_settings
  install_systemd_services
  print_summary
}

main "$@"
