#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
BASE_DIR="/opt/rust-manager"
CONFIG_DIR="$BASE_DIR/config"
GENERATED_DIR="$BASE_DIR/generated"
RUNTIME_DIR="$BASE_DIR/runtime"

STEAMCMD_BIN="${STEAMCMD_BIN:-/usr/games/steamcmd}"
STEAM_USER="${STEAM_USER:-anonymous}"
RUST_APP_ID="${RUST_APP_ID:-258550}"

QUERY_TIMEOUT_SECONDS="${QUERY_TIMEOUT_SECONDS:-8}"
STOP_TIMEOUT_SECONDS="${STOP_TIMEOUT_SECONDS:-30}"
START_WAIT_SECONDS="${START_WAIT_SECONDS:-15}"
RESTART_LOOP_DELAY_SECONDS="${RESTART_LOOP_DELAY_SECONDS:-5}"

mkdir -p "$CONFIG_DIR" "$GENERATED_DIR" "$RUNTIME_DIR"

die() {
    echo "ERROR: $*" >&2
    exit 1
}

require_bin() {
    command -v "$1" >/dev/null 2>&1 || die "Missing required binary: $1"
}

require_core_deps() {
    require_bin tmux
    require_bin jq
    require_bin python3
}

require_update_deps() {
    require_bin "$STEAMCMD_BIN"
}

json_get() {
    local file="$1"
    local expr="$2"
    jq -er "$expr" "$file"
}

json_get_or_empty() {
    local file="$1"
    local expr="$2"
    jq -er "$expr // empty" "$file" 2>/dev/null || true
}

server_config_path() {
    local server="$1"
    echo "$CONFIG_DIR/$server.json"
}

generated_cmd_path() {
    local server="$1"
    echo "$GENERATED_DIR/$server.cmd"
}

runner_script_path() {
    local server="$1"
    echo "$GENERATED_DIR/$server.runner.sh"
}

runtime_meta_path() {
    local server="$1"
    echo "$RUNTIME_DIR/$server.meta"
}

restart_flag_path() {
    local server="$1"
    echo "$RUNTIME_DIR/$server.autorestart"
}

command_trace_path() {
    local server="$1"
    echo "$RUNTIME_DIR/$server.commands.log"
}

session_name() {
    local server="$1"
    echo "rust-$server"
}

has_config() {
    local server="$1"
    [[ -f "$(server_config_path "$server")" ]]
}

list_servers() {
    shopt -s nullglob
    local files=("$CONFIG_DIR"/*.json)
    shopt -u nullglob

    local found=0
    for f in "${files[@]}"; do
        found=1
        basename "$f" .json
    done

    if [[ "$found" -eq 0 ]]; then
        return 1
    fi
}

server_exists() {
    local server="$1"
    has_config "$server"
}

tmux_has_session() {
    local server="$1"
    tmux has-session -t "$(session_name "$server")" 2>/dev/null
}

shell_escape() {
    printf '%q' "$1"
}

quote_arg() {
    local val="$1"
    val="${val//\\/\\\\}"
    val="${val//\"/\\\"}"
    printf '"%s"' "$val"
}

regex_escape() {
    printf '%s' "$1" | sed -e 's/[][(){}.^$+*?|\\/]/\\&/g'
}

server_identity() {
    local server="$1"
    local cfg
    cfg="$(server_config_path "$server")"
    if [[ ! -f "$cfg" ]]; then
        echo "$server"
        return 0
    fi

    local identity
    identity="$(json_get_or_empty "$cfg" '.["server.identity"]')"
    if [[ -n "$identity" ]]; then
        echo "$identity"
    else
        echo "$server"
    fi
}

load_config() {
    local server="$1"
    local cfg
    cfg="$(server_config_path "$server")"
    [[ -f "$cfg" ]] || die "Config not found: $cfg"

    CFG_NAME="$(json_get "$cfg" '.name')"
    CFG_SERVER_HOSTNAME="$(json_get "$cfg" '."server.hostname"')"
    CFG_SERVER_DESCRIPTION="$(json_get_or_empty "$cfg" '."server.description"')"
    CFG_SERVER_URL="$(json_get_or_empty "$cfg" '."server.url"')"
    CFG_SERVER_LOGOIMAGE="$(json_get_or_empty "$cfg" '."server.logoimage"')"
    CFG_SERVER_HEADERIMAGE="$(json_get_or_empty "$cfg" '."server.headerimage"')"
    CFG_SERVER_TAGS="$(json_get_or_empty "$cfg" '."server.tags"')"
    CFG_SERVER_IDENTITY="$(json_get "$cfg" '."server.identity"')"
    CFG_SERVER_PORT="$(json_get "$cfg" '."server.port"')"
    CFG_RCON_PORT="$(json_get "$cfg" '."rcon.port"')"
    CFG_APP_PORT="$(json_get "$cfg" '."app.port"')"
    CFG_SERVER_WORLDSIZE="$(json_get "$cfg" '."server.worldsize"')"
    CFG_SERVER_SEED="$(json_get "$cfg" '."server.seed"')"
    CFG_SERVER_MAXPLAYERS="$(json_get "$cfg" '."server.maxplayers"')"
    CFG_SERVER_LEVEL="$(json_get "$cfg" '."server.level"')"
    CFG_SERVER_LEVELURL="$(json_get_or_empty "$cfg" '."server.levelurl"')"
    CFG_RCON_PASSWORD="$(json_get "$cfg" '."rcon.password"')"
    CFG_SERVER_REPORTS_ENDPOINT="$(json_get_or_empty "$cfg" '."server.reportsserverendpoint"')"
    CFG_LOG_FILE="$(json_get "$cfg" '.logFile')"
    CFG_SERVER_ENCRYPTION="$(json_get_or_empty "$cfg" '."server.encryption"')"
    CFG_BOOMBOX_SERVERURLLIST="$(json_get_or_empty "$cfg" '."boombox.serverurllist"')"
    CFG_ADDITIONAL_ARGS="$(json_get_or_empty "$cfg" '.additionalArgs')"
    CFG_SERVER_DIR="$(json_get "$cfg" '.serverDir')"

    [[ -n "$CFG_NAME" ]] || die "Invalid config: name"
    [[ -n "$CFG_SERVER_HOSTNAME" ]] || die "Invalid config: server.hostname"
    [[ -n "$CFG_SERVER_IDENTITY" ]] || die "Invalid config: server.identity"
    [[ -n "$CFG_SERVER_DIR" ]] || die "Invalid config: serverDir"
    [[ -n "$CFG_RCON_PASSWORD" ]] || die "Invalid config: rcon.password"

    [[ "$CFG_SERVER_PORT" =~ ^[0-9]+$ ]] || die "Invalid server.port in $cfg"
    [[ "$CFG_RCON_PORT" =~ ^[0-9]+$ ]] || die "Invalid rcon.port in $cfg"
    [[ "$CFG_APP_PORT" =~ ^[0-9]+$ ]] || die "Invalid app.port in $cfg"
    [[ "$CFG_SERVER_WORLDSIZE" =~ ^[0-9]+$ ]] || die "Invalid server.worldsize in $cfg"
    [[ "$CFG_SERVER_SEED" =~ ^[0-9]+$ ]] || die "Invalid server.seed in $cfg"
    [[ "$CFG_SERVER_MAXPLAYERS" =~ ^[0-9]+$ ]] || die "Invalid server.maxplayers in $cfg"

    [[ -d "$CFG_SERVER_DIR" ]] || die "ServerDir does not exist: $CFG_SERVER_DIR"
    [[ -x "$CFG_SERVER_DIR/RustDedicated" ]] || die "RustDedicated not found or not executable: $CFG_SERVER_DIR/RustDedicated"
}

trace_server_command() {
    local server="$1"
    local line="$2"
    local trace
    trace="$(command_trace_path "$server")"
    printf '[%s] %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$line" >> "$trace"
}

build_start_command() {
    local server="$1"
    load_config "$server"

    local cmd
    cmd="cd $(shell_escape "$CFG_SERVER_DIR") && "
    cmd+="./RustDedicated "
    cmd+="-batchmode "
    cmd+="-nographics "
    cmd+="-silent-crashes "

    cmd+="+server.hostname $(quote_arg "$CFG_SERVER_HOSTNAME") "

    if [[ -n "$CFG_SERVER_DESCRIPTION" ]]; then
        cmd+="+server.description $(quote_arg "$CFG_SERVER_DESCRIPTION") "
    fi

    if [[ -n "$CFG_SERVER_URL" ]]; then
        cmd+="+server.url $(quote_arg "$CFG_SERVER_URL") "
    fi

    if [[ -n "$CFG_SERVER_LOGOIMAGE" ]]; then
        cmd+="+server.logoimage $(quote_arg "$CFG_SERVER_LOGOIMAGE") "
    fi

    if [[ -n "$CFG_SERVER_HEADERIMAGE" ]]; then
        cmd+="+server.headerimage $(quote_arg "$CFG_SERVER_HEADERIMAGE") "
    fi

    if [[ -n "$CFG_SERVER_TAGS" ]]; then
        cmd+="+server.tags $(quote_arg "$CFG_SERVER_TAGS") "
    fi

    cmd+="+server.identity $(quote_arg "$CFG_SERVER_IDENTITY") "
    cmd+="+server.port $CFG_SERVER_PORT "
    cmd+="+rcon.port $CFG_RCON_PORT "
    cmd+="+app.port $CFG_APP_PORT "
    cmd+="+server.worldsize $CFG_SERVER_WORLDSIZE "
    cmd+="+server.seed $CFG_SERVER_SEED "
    cmd+="+server.maxplayers $CFG_SERVER_MAXPLAYERS "
    cmd+="+server.level $(quote_arg "$CFG_SERVER_LEVEL") "

    if [[ -n "$CFG_SERVER_LEVELURL" ]]; then
        cmd+="+server.levelurl $(quote_arg "$CFG_SERVER_LEVELURL") "
    fi

    cmd+="+rcon.password $(quote_arg "$CFG_RCON_PASSWORD") "

    if [[ -n "$CFG_SERVER_REPORTS_ENDPOINT" ]]; then
        cmd+="+server.reportsserverendpoint $(quote_arg "$CFG_SERVER_REPORTS_ENDPOINT") "
    fi

    if [[ -n "$CFG_SERVER_ENCRYPTION" ]]; then
        cmd+="+server.encryption $(quote_arg "$CFG_SERVER_ENCRYPTION") "
    fi

    if [[ -n "$CFG_BOOMBOX_SERVERURLLIST" ]]; then
        cmd+="+boombox.serverurllist $(quote_arg "$CFG_BOOMBOX_SERVERURLLIST") "
    fi

    cmd+="-logFile $(quote_arg "$CFG_LOG_FILE") "

    if [[ -n "${CFG_ADDITIONAL_ARGS:-}" ]]; then
        cmd+="${CFG_ADDITIONAL_ARGS} "
    fi

    echo "$cmd"
}

write_runtime_meta() {
    local server="$1"
    load_config "$server"

    cat > "$(runtime_meta_path "$server")" <<EOF
NAME=$CFG_NAME
SERVER_HOSTNAME=$CFG_SERVER_HOSTNAME
SERVER_DESCRIPTION=$CFG_SERVER_DESCRIPTION
SERVER_URL=$CFG_SERVER_URL
SERVER_LOGOIMAGE=$CFG_SERVER_LOGOIMAGE
SERVER_HEADERIMAGE=$CFG_SERVER_HEADERIMAGE
SERVER_TAGS=$CFG_SERVER_TAGS
SERVER_IDENTITY=$CFG_SERVER_IDENTITY
SERVER_PORT=$CFG_SERVER_PORT
RCON_PORT=$CFG_RCON_PORT
APP_PORT=$CFG_APP_PORT
SERVER_WORLDSIZE=$CFG_SERVER_WORLDSIZE
SERVER_SEED=$CFG_SERVER_SEED
SERVER_MAXPLAYERS=$CFG_SERVER_MAXPLAYERS
SERVER_LEVEL=$CFG_SERVER_LEVEL
SERVER_LEVELURL=$CFG_SERVER_LEVELURL
SERVER_REPORTS_ENDPOINT=$CFG_SERVER_REPORTS_ENDPOINT
LOG_FILE=$CFG_LOG_FILE
SERVER_ENCRYPTION=$CFG_SERVER_ENCRYPTION
BOOMBOX_SERVERURLLIST=$CFG_BOOMBOX_SERVERURLLIST
SERVER_DIR=$CFG_SERVER_DIR
EOF
}

write_runner_script() {
    local server="$1"
    local runner_path control_path trace_path command_path
    runner_path="$(runner_script_path "$server")"
    control_path="$(restart_flag_path "$server")"
    trace_path="$(command_trace_path "$server")"
    command_path="$(generated_cmd_path "$server")"

    cat > "$runner_path" <<EOF
#!/usr/bin/env bash
set -uo pipefail

CONTROL_PATH=$(quote_arg "$control_path")
TRACE_PATH=$(quote_arg "$trace_path")
COMMAND_PATH=$(quote_arg "$command_path")
RESTART_DELAY=$(quote_arg "$RESTART_LOOP_DELAY_SECONDS")

trace() {
    printf '[%s] %s\n' "\$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "\$1" >> "\$TRACE_PATH"
}

while [[ -f "\$CONTROL_PATH" ]]; do
    if [[ ! -f "\$COMMAND_PATH" ]]; then
        trace "supervisor: waiting for command file"
        sleep "\$RESTART_DELAY"
        continue
    fi

    cmd="\$(cat "\$COMMAND_PATH")"
    trace "process start: RustDedicated"
    bash -lc "\$cmd"
    exit_code=\$?
    trace "process exit: code=\$exit_code"

    if [[ ! -f "\$CONTROL_PATH" ]]; then
        break
    fi

    sleep "\$RESTART_DELAY"
done

trace "supervisor: stopped"
EOF

    chmod +x "$runner_path"
}

sync_config_one() {
    local server="$1"
    local cmd
    cmd="$(build_start_command "$server")"

    printf '%s\n' "$cmd" > "$(generated_cmd_path "$server")"
    write_runtime_meta "$server"
    write_runner_script "$server"

    echo "synced $server"
}

log_path() {
    local server="$1"
    load_config "$server"
    echo "$CFG_SERVER_DIR/$CFG_LOG_FILE"
}

server_pid() {
    local server="$1"
    local cfg identity server_port rcon_port app_port
    cfg="$(server_config_path "$server")"
    identity="$(json_get_or_empty "$cfg" '.["server.identity"]')"
    server_port="$(json_get_or_empty "$cfg" '.["server.port"]')"
    rcon_port="$(json_get_or_empty "$cfg" '.["rcon.port"]')"
    app_port="$(json_get_or_empty "$cfg" '.["app.port"]')"

    if [[ -z "$identity" ]]; then
        identity="$server"
    fi

    python3 - "$identity" "$server_port" "$rcon_port" "$app_port" <<'PY'
import os
import sys

identity, server_port, rcon_port, app_port = sys.argv[1:5]
candidates = []

for name in os.listdir("/proc"):
    if not name.isdigit():
        continue

    cmdline_path = f"/proc/{name}/cmdline"
    try:
        raw = open(cmdline_path, "rb").read()
    except OSError:
        continue

    if not raw:
        continue

    parts = [part.decode("utf-8", errors="ignore") for part in raw.split(b"\0") if part]
    if not parts:
        continue

    executable = os.path.basename(parts[0])
    if "RustDedicated" not in executable:
        continue

    def arg_value(flag: str) -> str | None:
        for index, part in enumerate(parts[:-1]):
            if part == flag:
                return parts[index + 1]
        return None

    score = 0
    if arg_value("+server.identity") == identity:
        score += 10
    if server_port and arg_value("+server.port") == server_port:
        score += 5
    if rcon_port and arg_value("+rcon.port") == rcon_port:
        score += 3
    if app_port and arg_value("+app.port") == app_port:
        score += 3

    if score > 0:
        candidates.append((score, int(name)))

if candidates:
    candidates.sort(key=lambda item: (item[0], item[1]), reverse=True)
    print(candidates[0][1])
PY
}

status_one() {
    local server="$1"

    if ! server_exists "$server"; then
        echo "$server: missing-config"
        return 1
    fi

    local sess="no"
    local pid=""
    local state="offline"
    local autorestart="no"

    if tmux_has_session "$server"; then
        sess="yes"
    fi

    if [[ -f "$(restart_flag_path "$server")" ]]; then
        autorestart="yes"
    fi

    pid="$(server_pid "$server" || true)"
    if [[ -n "$pid" ]]; then
        state="running"
    elif [[ "$autorestart" == "yes" ]]; then
        state="starting"
    elif [[ "$sess" == "yes" ]]; then
        state="session-only"
    fi

    echo "name: $server"
    echo "state: $state"
    echo "session: $sess"
    echo "autorestart: $autorestart"
    if [[ -n "$pid" ]]; then
        echo "pid: $pid"
    fi
}

start_one() {
    local server="$1"
    local runner_path control_path pid waited trace_path recent_trace

    server_exists "$server" || die "Unknown server: $server"
    sync_config_one "$server" >/dev/null

    control_path="$(restart_flag_path "$server")"
    runner_path="$(runner_script_path "$server")"
    pid="$(server_pid "$server" || true)"

    if [[ -n "$pid" ]] && ! tmux_has_session "$server"; then
        die "$server is already running outside the managed tmux session (pid $pid)"
    fi

    if tmux_has_session "$server"; then
        pid="$(server_pid "$server" || true)"
        if [[ -n "$pid" ]]; then
            touch "$control_path"
            echo "$server already running"
            return 0
        fi

        tmux kill-session -t "$(session_name "$server")" 2>/dev/null || true
        sleep 1
    fi

    touch "$control_path"
    trace_path="$(command_trace_path "$server")"
    tmux new-session -d -s "$(session_name "$server")" "bash -lc $(shell_escape "$runner_path")"

    waited=0
    while (( waited < START_WAIT_SECONDS )); do
        pid="$(server_pid "$server" || true)"
        if [[ -n "$pid" ]]; then
            sleep 4
            local final_pid
            final_pid="$(server_pid "$server" || true)"
            if [[ -n "$final_pid" ]]; then
                echo "started $server"
                return 0
            fi
        fi

        if ! tmux_has_session "$server"; then
            break
        fi

        sleep 1
        waited=$((waited + 1))
    done

    recent_trace=""
    if [[ -f "$trace_path" ]]; then
        recent_trace="$(tail -n 6 "$trace_path" | tr '\n' ' ' | sed 's/[[:space:]]\+/ /g' | sed 's/^ //; s/ $//')"
    fi

    if [[ -n "$recent_trace" ]]; then
        die "Start did not produce a RustDedicated pid for $server within ${START_WAIT_SECONDS}s. Recent trace: $recent_trace"
    fi

    die "Start did not produce a RustDedicated pid for $server within ${START_WAIT_SECONDS}s"
}

stop_one() {
    local server="$1"
    local pid control_path

    control_path="$(restart_flag_path "$server")"
    rm -f "$control_path"

    if ! tmux_has_session "$server"; then
        pid="$(server_pid "$server" || true)"
        if [[ -n "$pid" ]]; then
            kill -TERM "$pid" 2>/dev/null || true
            echo "stopped $server"
            return 0
        fi

        echo "$server already stopped"
        return 0
    fi

    pid="$(server_pid "$server" || true)"
    if [[ -n "$pid" ]]; then
        kill -TERM "$pid" 2>/dev/null || true
        local waited=0
        while kill -0 "$pid" 2>/dev/null; do
            sleep 1
            waited=$((waited + 1))
            if (( waited >= STOP_TIMEOUT_SECONDS )); then
                echo "graceful stop timed out for $server, killing process"
                kill -9 "$pid" 2>/dev/null || true
                break
            fi
        done
    fi

    if tmux_has_session "$server"; then
        tmux kill-session -t "$(session_name "$server")" 2>/dev/null || true
    fi

    echo "stopped $server"
}

kill_one() {
    local server="$1"

    local pid
    pid="$(server_pid "$server" || true)"
    if [[ -n "$pid" ]]; then
        kill -9 "$pid" 2>/dev/null || true
    fi

    if tmux_has_session "$server"; then
        tmux kill-session -t "$(session_name "$server")" 2>/dev/null || true
    fi

    echo "killed $server"
}

restart_one() {
    local server="$1"
    local old_pid new_pid waited control_path

    server_exists "$server" || die "Unknown server: $server"
    sync_config_one "$server" >/dev/null

    if ! tmux_has_session "$server"; then
        old_pid="$(server_pid "$server" || true)"
        if [[ -n "$old_pid" ]]; then
            kill -TERM "$old_pid" 2>/dev/null || true
            waited=0
            while kill -0 "$old_pid" 2>/dev/null; do
                sleep 1
                waited=$((waited + 1))
                if (( waited >= STOP_TIMEOUT_SECONDS )); then
                    kill -9 "$old_pid" 2>/dev/null || true
                    break
                fi
            done
        fi

        start_one "$server"
        return 0
    fi

    control_path="$(restart_flag_path "$server")"
    touch "$control_path"

    old_pid="$(server_pid "$server" || true)"
    if [[ -z "$old_pid" ]]; then
        tmux kill-session -t "$(session_name "$server")" 2>/dev/null || true
        start_one "$server"
        return 0
    fi

    trace_server_command "$server" "restart requested"
    kill -TERM "$old_pid" 2>/dev/null || true

    waited=0
    while (( waited < STOP_TIMEOUT_SECONDS + 30 )); do
        sleep 1
        new_pid="$(server_pid "$server" || true)"
        if [[ -n "$new_pid" && "$new_pid" != "$old_pid" ]]; then
            echo "restarted $server"
            return 0
        fi
        waited=$((waited + 1))
    done

    die "Timed out waiting for $server to restart"
}

update_one() {
    local server="$1"

    server_exists "$server" || die "Unknown server: $server"
    load_config "$server"
    require_update_deps

    echo "updating $server in $CFG_SERVER_DIR"
    local steam_out rc
    set +e
    steam_out="$(
        LC_ALL=C "$STEAMCMD_BIN" \
            +force_install_dir "$CFG_SERVER_DIR" \
            +login "$STEAM_USER" \
            +app_update "$RUST_APP_ID" validate \
            +quit 2>&1
    )"
    rc=$?
    set -e

    printf '%s\n' "$steam_out"

    if (( rc != 0 )); then
        # SteamCMD may return exit 8 with app state 0x6 despite install being present.
        if [[ "$steam_out" == *"state is 0x6 after update job"* ]]; then
            echo "steamcmd returned exit $rc with state 0x6; treating as non-fatal"
            trace_server_command "$server" "update: steamcmd exit $rc state 0x6 (treated non-fatal)"
            echo "updated $server"
            return 0
        fi

        die "steamcmd update failed for $server (exit $rc)"
    fi

    trace_server_command "$server" "update completed"
    echo "updated $server"
}

umod_one() {
    local server="$1"
    server_exists "$server" || die "Unknown server: $server"
    load_config "$server"

    require_bin curl
    require_bin unzip

    local tmp_zip=""
    tmp_zip="$(mktemp /tmp/umod-rust.XXXXXX.zip)"
    trap 'if [[ -n "${tmp_zip:-}" ]]; then rm -f "$tmp_zip"; fi' RETURN

    echo "downloading uMod for $server"
    curl -fsSL "https://umod.org/games/rust/download/develop" -o "$tmp_zip"
    unzip -o "$tmp_zip" -d "$CFG_SERVER_DIR" >/dev/null
    trace_server_command "$server" "umod updated from develop channel"
    echo "updated umod for $server"
}

logs_one() {
    local server="$1"
    local lp
    lp="$(log_path "$server")"

    if [[ ! -f "$lp" ]]; then
        echo ""
        return 0
    fi

    cat "$lp"
}

console_one() {
    local server="$1"
    local lp
    lp="$(log_path "$server")"

    touch "$lp"
    tail -n 120 -f "$lp"
}

rcon_send() {
    local server="$1"
    local command="$2"
    local return_output="${3:-0}"

    local cfg rcon_port rcon_password
    cfg="$(server_config_path "$server")"
    if [[ ! -f "$cfg" ]]; then
        return 1
    fi

    rcon_port="$(json_get_or_empty "$cfg" '.["rcon.port"]')"
    rcon_password="$(json_get_or_empty "$cfg" '.["rcon.password"]')"

    if [[ -z "$rcon_port" || -z "$rcon_password" ]]; then
        return 1
    fi

    python3 - "$rcon_port" "$rcon_password" "$command" "$return_output" <<'PY'
import sys, socket, base64, json

port = int(sys.argv[1])
password = sys.argv[2]
command = sys.argv[3]
return_output = sys.argv[4] == "1"

key = base64.b64encode(b"0123456789ABCDEF").decode('utf-8')
req = (f"GET /{password} HTTP/1.1\r\n"
       f"Host: 127.0.0.1:{port}\r\n"
       f"Upgrade: websocket\r\n"
       f"Connection: Upgrade\r\n"
       f"Sec-WebSocket-Key: {key}\r\n"
       f"Sec-WebSocket-Version: 13\r\n\r\n")

s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
s.settimeout(10.0)
try:
    s.connect(("127.0.0.1", port))
    s.sendall(req.encode('utf-8'))
    resp = b""
    while b"\r\n\r\n" not in resp:
        chunk = s.recv(4096)
        if not chunk: sys.exit(1)
        resp += chunk
    
    if b"101 Switching Protocols" not in resp:
        sys.exit(1)

    payload = json.dumps({"Identifier": 1, "Message": command, "Name": "WebRcon"}).encode('utf-8')
    frame = bytearray([0x81])
    if len(payload) <= 125:
        frame.append(len(payload) | 0x80)
    elif len(payload) <= 65535:
        frame.append(126 | 0x80)
        frame.extend(len(payload).to_bytes(2, 'big'))
    else:
        frame.append(127 | 0x80)
        frame.extend(len(payload).to_bytes(8, 'big'))

    mask = b'\x00\x00\x00\x00'
    frame.extend(mask)
    for i in range(len(payload)):
        frame.append(payload[i] ^ mask[i % 4])

    s.sendall(frame)

    if not return_output:
        sys.exit(0)

    response_text = ""
    while True:
        header = s.recv(2)
        if not header: break
        op = header[0] & 0x0f
        if op == 8: break
        
        plen = header[1] & 0x7f
        if plen == 126:
            ext = s.recv(2)
            plen = int.from_bytes(ext, 'big')
        elif plen == 127:
            ext = s.recv(8)
            plen = int.from_bytes(ext, 'big')
            
        data = b""
        while len(data) < plen:
            chunk = s.recv(min(4096, plen - len(data)))
            if not chunk: break
            data += chunk
            
        if op == 1:
            try:
                msg = json.loads(data.decode('utf-8'))
                if msg.get("Identifier") == 1:
                    response_text = msg.get("Message", "")
                    break
            except Exception:
                pass
    
    if response_text:
        print(response_text)
        sys.exit(0)
    else:
        sys.exit(1)
        
except Exception as e:
    sys.exit(1)
finally:
    s.close()
PY
}

send_one() {
    local server="$1"
    shift || true
    local cmd="$*"

    [[ -n "$cmd" ]] || die "Missing command"
    tmux_has_session "$server" || die "Server not running: $server"
    local pid
    pid="$(server_pid "$server" || true)"
    [[ -n "$pid" ]] || die "tmux session exists but RustDedicated pid is missing for $server"

    rcon_send "$server" "$cmd" "0" || tmux send-keys -t "$(session_name "$server")" "$cmd" C-m
    trace_server_command "$server" "send: $cmd"
    echo "sent to $server: $cmd"
}

commands_one() {
    local server="$1"
    local lines="${2:-80}"
    local trace
    trace="$(command_trace_path "$server")"

    if [[ ! -f "$trace" ]]; then
        echo ""
        return 0
    fi

    tail -n "$lines" "$trace"
}

query_extract_json_from_chunk() {
    local chunk_file="$1"

    python3 - "$chunk_file" <<'PY'
import json
import sys
from pathlib import Path

text = Path(sys.argv[1]).read_text(encoding="utf-8", errors="ignore")
decoder = json.JSONDecoder()

for i, ch in enumerate(text):
    if ch not in "[{":
        continue
    try:
        obj, end = decoder.raw_decode(text[i:])
        print(json.dumps(obj, ensure_ascii=False))
        sys.exit(0)
    except Exception:
        pass

sys.exit(1)
PY
}

query_one() {
    local server="$1"
    local query="$2"

    case "$query" in
        serverinfo|playerlist|bans) ;;
        *)
            die "Unsupported query: $query"
            ;;
    esac

    tmux_has_session "$server" || die "Server not running: $server"

    local out
    if out="$(rcon_send "$server" "$query" "1")"; then
        printf '%s\n' "$out"
        return 0
    fi

    die "Failed to query '$query' on $server via RCON"
}

config_show_one() {
    local server="$1"
    server_exists "$server" || die "Unknown server: $server"
    cat "$(server_config_path "$server")"
}

wipe_one() {
    local server="$1"
    server_exists "$server" || die "Unknown server: $server"
    load_config "$server"

    if tmux_has_session "$server"; then
        die "Refusing to wipe while server is running: $server"
    fi

    local target="$CFG_SERVER_DIR/$CFG_SERVER_IDENTITY"
    if [[ ! -d "$target" ]]; then
        echo "identity dir not found, nothing to wipe: $target"
        return 0
    fi

    find "$target" -maxdepth 1 -type f \
        \( -name '*.map' -o -name '*.sav*' -o -name '*.db' -o -name '*.dat' -o -name '*.bak' \) \
        -print -delete

    echo "wiped save/map data for $server"
}

run_for_target() {
    local action="$1"
    local target="$2"
    shift 2 || true

    if [[ "$target" == "all" ]]; then
        local s
        while IFS= read -r s; do
            [[ -n "$s" ]] || continue
            case "$action" in
                status) status_one "$s" ;;
                sync-config) sync_config_one "$s" ;;
                start) start_one "$s" ;;
                stop) stop_one "$s" ;;
                restart) restart_one "$s" ;;
                kill) kill_one "$s" ;;
                update) update_one "$s" ;;
                umod) umod_one "$s" ;;
                *)
                    die "Action '$action' does not support target 'all'"
                    ;;
            esac
            echo
        done < <(list_servers)
    else
        case "$action" in
            status) status_one "$target" ;;
            sync-config) sync_config_one "$target" ;;
            start) start_one "$target" ;;
            stop) stop_one "$target" ;;
            restart) restart_one "$target" ;;
            kill) kill_one "$target" ;;
            update) update_one "$target" ;;
            umod) umod_one "$target" ;;
            logs) logs_one "$target" ;;
            console) console_one "$target" ;;
            commands) commands_one "$target" "$@" ;;
            config-show) config_show_one "$target" ;;
            wipe) wipe_one "$target" ;;
            send) send_one "$target" "$@" ;;
            query) query_one "$target" "$@" ;;
            *)
                die "Unknown action: $action"
                ;;
        esac
    fi
}

usage() {
    cat <<'EOF'
Usage:
  rustmgr.sh list
  rustmgr.sh status <server|all>
  rustmgr.sh start <server|all>
  rustmgr.sh stop <server|all>
  rustmgr.sh restart <server|all>
  rustmgr.sh kill <server|all>
  rustmgr.sh update <server|all>
  rustmgr.sh umod <server|all>
  rustmgr.sh logs <server>
  rustmgr.sh console <server>
  rustmgr.sh commands <server> [lines]
  rustmgr.sh send <server> "<command>"
  rustmgr.sh query <server> <serverinfo|playerlist|bans>
  rustmgr.sh sync-config <server|all>
  rustmgr.sh config-show <server>
  rustmgr.sh wipe <server>

Config files:
  /opt/rust-manager/config/<server>.json
EOF
}

require_core_deps

ACTION="${1:-}"
TARGET="${2:-}"

case "$ACTION" in
    list)
        list_servers || true
        ;;
    status|start|stop|restart|kill|update|umod|sync-config)
        [[ -n "$TARGET" ]] || die "Missing target"
        run_for_target "$ACTION" "$TARGET"
        ;;
    logs|console|commands|config-show|wipe)
        [[ -n "$TARGET" ]] || die "Missing target"
        shift 2
        run_for_target "$ACTION" "$TARGET" "$@"
        ;;
    query)
        [[ -n "$TARGET" ]] || die "Missing target"
        QUERY="${3:-}"
        [[ -n "$QUERY" ]] || die "Missing query"
        run_for_target "$ACTION" "$TARGET" "$QUERY"
        ;;
    # send keeps command payload together.
    send)
        [[ -n "$TARGET" ]] || die "Missing target"
        shift 2
        [[ $# -gt 0 ]] || die "Missing command"
        run_for_target "$ACTION" "$TARGET" "$*"
        ;;
    ""|-h|--help|help)
        usage
        ;;
    *)
        usage
        exit 1
        ;;
esac
