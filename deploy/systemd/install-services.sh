#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="${ROOT_DIR:-/opt/rust-manager}"
SYSTEMD_DIR="/etc/systemd/system"
SERVICE_USER="${SERVICE_USER:-rustmgr}"
SERVICE_GROUP="${SERVICE_GROUP:-rustmgr}"

SERVICES=(
  rustmgrapi.service
  rustopsagent.service
  rustops-remote-agent.service
  opssteambot.service
)

log() {
  printf '[install-services] %s\n' "$*"
}

die() {
  printf '[install-services] ERROR: %s\n' "$*" >&2
  exit 1
}

require_root() {
  if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
    die "Run this script as root (sudo)."
  fi
}

require_file() {
  local file="$1"
  [[ -f "$file" ]] || die "Missing required file: $file"
}

install_units() {
  local source_dir="$ROOT_DIR/deploy/systemd"
  for svc in "${SERVICES[@]}"; do
    require_file "$source_dir/$svc"
    install -m 0644 "$source_dir/$svc" "$SYSTEMD_DIR/$svc"
    log "installed $svc"
  done
}

ensure_layout() {
  mkdir -p "$ROOT_DIR/config"
  mkdir -p "$ROOT_DIR/runtime"
  mkdir -p "$ROOT_DIR/tasks"
  mkdir -p "$ROOT_DIR/agent/RustOpsAgent/data/feedback-inbox"
  mkdir -p "$ROOT_DIR/agent/RustOpsAgent/data/decision-inbox"
  mkdir -p "$ROOT_DIR/agent/RustOpsAgent/data/chat-inbox"
  mkdir -p "$ROOT_DIR/agent/RustOpsAgent/data/message-outbox"
  mkdir -p "$ROOT_DIR/agent/RustOpsAgent/data/message-outbox-sent"
  mkdir -p "$ROOT_DIR/remote-agent/RustOpsRemoteAgent"
  chown -R "$SERVICE_USER:$SERVICE_GROUP" "$ROOT_DIR"
  log "ensured runtime layout under $ROOT_DIR"
}

systemd_reload_enable_start() {
  systemctl daemon-reload
  for svc in "${SERVICES[@]}"; do
    systemctl enable "$svc"
    systemctl restart "$svc"
    log "enabled and restarted $svc"
  done
}

print_status() {
  for svc in "${SERVICES[@]}"; do
    log "status for $svc"
    systemctl --no-pager --full status "$svc" || true
  done
}

main() {
  require_root
  install_units
  ensure_layout
  systemd_reload_enable_start
  print_status
  log "done"
}

main "$@"
