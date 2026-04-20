---

### Root problem 1 — Web UI is empty

The API's `LoadAgentMemorySnapshot` reads directly from `agent-state.json` via `ResolveAgentRuntimePaths()`. The web UI is only empty for **one of two reasons:**

**A) The agent-state.json path is misresolved** — the API is looking in a different path than where the agent is actually writing. The `ResolveAgentRuntimePaths` function chains through: env var override → agentsettings.json `memory.statePath` → fallback path. If any of those are slightly off or the agent isn't writing yet, the file simply doesn't exist where the API looks.

**B) The agent isn't actually accumulating data** — it needs actual server incidents, LLM calls, and feedback before those lists are non-empty. If LLM is not enabled or not reachable, most of those lists stay empty.

The web UI code itself is correctly wired — `ReadRecentIncidents`, `ReadRecentActions`, `ReadLlmInteractions`, `ReadPendingActions`, `ReadRecentFeedback` are all implemented and read the right JSON keys from the state file.

---

### Root problem 2 — Self-repair is not the "agent evolution" you described

What's currently implemented is: **the agent detects existing errors/failures/gaps and asks the LLM for a repair plan**. The LLM can write workspace files, merge log rules, update reply style, etc.

What's **missing** is your vision of the **failure-filing pipeline**:
- When a chat request fails or is unresolvable → file it as a `LearningIncidentRecord` (the class *exists* but is never written to the learning inbox in the chat failure path)
- The self-repair cycle should pick those up, reason through them with the LLM, classify each failure, and queue an improvement
- A dedicated `CapabilityGap` bucket exists in memory but `RecordCapabilityGap` is only called from the self-repair plan itself — it's never triggered from chat failures

---

### Root problem 3 — Chat failures never become learning incidents

Looking at `ProcessChatInboxAsync`: if `HandleChatRequestAsync` throws, the error goes to `RecordAgentError` — a plain string list. It never becomes a `LearningIncidentRecord`. The `LearningIncidentRecord` class exists and `_learningInboxPath` is set up, but **nothing ever writes a file to it from the chat pipeline**.

---

### The fix plan

Here's exactly what we need to change, in order of priority:

**Step 1 — Wire chat failures into the learning inbox**
When a chat request fails (tool error, unknown intent, LLM unavailable, command blocked) → write a `LearningIncidentRecord` JSON file to `learning/inbox/` with the message, intent, what was tried, and why it failed.

**Step 2 — Wire the self-repair cycle to reason over those learning incidents**
The `BuildSelfRepairContext` already reads `_learningInboxPath`. The `ShouldAttemptRepair` gate already checks `learningIncidents.Count > 0`. But the LLM prompt for self-repair needs to explicitly instruct the model to treat each learning incident as a failure to analyze — not just "fix errors in the system".

**Step 3 — Add `self-reflection` / `failed-task` incident categories**
In `DetectIncidentAsync` and in the chat failure path, tag incidents as `"self-reflection"` or `"failed-task"` so the web UI can show them distinctly.

**Step 4 — Confirm the state path alignment between agent and API**
We need to verify your actual `agentsettings.json` and `rustops.env` to confirm the agent is writing where the API reads.

**Step 5 — Add `CapabilityGap` recording to the chat failure path**
After failed tool calls or unresolvable intent, call `_memory.RecordCapabilityGap("chat-intent", ...)` so it surfaces on the dashboard.

---


## Confirmed findings & what to fix

**1. `RecordLearningIncident` exists and works — but is only called in 2 places:**
- When intent is `"unknown"` in the deterministic fallback path ✅
- Inside `_memory.RecordCapabilityGap("chat-learning", ...)` that it calls internally

**Missing call sites:**
- When `TryHandleChatWithLlmToolsAsync` returns `null` (LLM failure, capability denial, 3 rounds exhausted) — **nothing records why it failed**
- When `ProcessChatInboxAsync` catches an exception — only `RecordAgentError` is called, no learning file written
- When the server name can't be resolved despite the admin mentioning it by name

**2. `LearningIncidentRecord` has no `category` field** — your "self-reflection" / "failed-task" buckets can't be distinguished

**3. The self-repair LLM prompt is generic** — it lists the learning incidents as a flat list but doesn't instruct the model to analyze each one as a failure case, reason over what went wrong, and propose a concrete improvement

**4. Web UI is empty** because the `AgentMemoryStore` is only non-empty after actual chat activity, incidents, LLM calls. The field names in the state JSON already match what the API reads — so once the agent has real data, it'll show. The issue is confirming paths and getting data flowing.

**5. `CapabilityGaps` and `SelfRepairHistory` are in the memory store but never sent to the `/dashboard/summary` endpoint and never shown in the UI**

---