## RusticalandOPS — Project Briefing

### What the system is

A three-service, event-driven AI operations stack for managing Rust game servers on a Linux host. Everything runs as systemd services deployed to `/opt/rust-manager/`.

**Layer 1 — `rustmgr.sh`:** The bash authority for server lifecycle (start/stop/restart/kill/update/wipe/umod).

**Layer 2 — `api/` (ASP.NET, ~3,866 lines):** The deterministic control plane. Wraps rustmgr.sh and exposes ~40 REST endpoints covering lifecycle, RCON, console logs (with rolling offset), config read/write/validate, Oxide plugin validation, managed cron tasks, host network inspection, and a built-in `/ui` HTML dashboard. Auth via `X-Api-Key`.

**Layer 3 — `agent/RustOpsAgent/` (~321KB):** The reasoning engine. It continuously polls the API, maintains a JSON memory store (incidents, action history, feedback, LLM interaction log), runs an LLM tool-calling loop (LM Studio / OpenRouter, supports fallback and race strategy with a secondary endpoint), enforces a policy layer (auto-allowed vs. approval-required actions), runs a self-repair loop (writes corrective artifacts, can trigger source builds and service restarts within a scoped root path), handles GitOps (auto-pull, push branches), monitors plugin updates via the uMod search API, and reads/writes log rules and reply-style guidance.

**Layer 4 — `SteamBot/OpsSteamBot/`:** A pure transport adapter. Logs into a Steam account via SteamKit2, forwards any unrecognized natural-language message from whitelisted admins to the agent's `chat-inbox`, and continuously polls the agent's `message-outbox` to relay replies back via Steam. Direct commands (`approve`, `reject`, `feedback`, `ping`, `help`) are handled locally. Message chunking keeps everything under Steam's 350-char limit.

**Shared utilities:** `RustMgrExecutor` (subprocess + timeout + verification), `RustOpsEnv` (env file loading, `${PLACEHOLDER}` resolution, path normalization), `RustOpsSentry` (Sentry error tracking, all three services).

---

### Feature map — what's implemented vs. what needs solidifying

All the features exist in code. The goal is making sure each one actually runs, is exercised, and produces the right outcomes. Here's how I'd categorize them:

**The core flow (highest priority — nothing else works without this):**
The pipeline is: Steam message → chat-inbox JSON file → agent picks it up → LLM tool loop runs → agent writes reply to message-outbox → Steam bot delivers it. Every break point in that chain needs to be verified and hardened.

**Approval flow:** Policy-gated proposals (restart, stop, wipe, etc.) need to correctly serialize to the `decision-inbox`, get picked up by the agent, and execute (or be rejected) with the outcome recorded.

**Self-repair loop:** This is the most complex and potentially fragile piece — it needs to reliably identify runtime gaps, write valid corrective artifacts, and not break things outside its scope root.

**GitOps:** Auto-pull + rebuild + optional restart. Needs to work correctly with the clean-worktree check and the branch prefix conventions.

**Plugin update notifications:** uMod search polling needs to compare installed plugin versions against what's available and surface meaningful diffs to admins.

**Web UI (`/ui` dashboard):** Needs to reflect live server state, pending actions, inbox/outbox counts, and LLM status accurately.

**Log rule and reply-style updates:** When the agent or self-repair writes updates to `agent-log-rules.json`, those changes need to be reloaded and actually affect filtering behavior.

---
