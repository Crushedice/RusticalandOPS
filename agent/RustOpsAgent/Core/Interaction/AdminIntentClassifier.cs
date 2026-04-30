using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Infrastructure.Memory;

namespace RustOpsAgent.Core.Interaction;

internal sealed class AdminIntentClassifier : IIntentClassifier
{
    private readonly Kernel? _kernel;
    private readonly LlmSettings _settings;
    private readonly NeoCortexStore? _neoCortex;
    private readonly ISemanticMemoryService? _semanticMemory;

    // Config file keys live in the rustmgr JSON config and are edited via file_edit.
    // Everything else that looks like a dotted identifier is a live RCON convar.
    private static readonly HashSet<string> ConfigFileKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "server.worldsize", "server.seed", "server.maxplayers", "server.hostname",
        "server.port", "server.identity",
        "rcon.port", "rcon.password", "rcon.ip",
        "app.port",
        "serverdir", "logfile", "additionalargs"
    };

    public AdminIntentClassifier(
        Kernel? kernel,
        LlmSettings? settings = null,
        NeoCortexStore? neoCortex = null,
        ISemanticMemoryService? semanticMemory = null)
    {
        _kernel = kernel;
        _settings = settings ?? new LlmSettings();
        _neoCortex = neoCortex;
        _semanticMemory = semanticMemory;
    }

    public async Task<AdminIntentRoute> ClassifyAsync(
        string message,
        ConversationSelectionState state,
        IReadOnlyList<string> knownServers,
        CancellationToken cancellationToken)
    {
        var planningMemory = _semanticMemory is null
            ? WorkflowMemoryContext.Empty
            : await _semanticMemory.RecallForPlanningAsync(message, state, knownServers, cancellationToken);
        if (planningMemory.RetrievalSkipped)
        {
            Console.WriteLine($"[memory] planning recall skipped: {planningMemory.SkipReason}");
        }
        else if (planningMemory.HasResults)
        {
            Console.WriteLine($"[memory] planning recall retrieved {planningMemory.Results.Count} record(s)");
        }

        if (_kernel is null)
            return HeuristicFallback(message, state, knownServers, planningMemory, "heuristic_no_kernel", false, false);

        var prompt = BuildPrompt(message, state, knownServers, planningMemory);

        string raw;
        try
        {
            var response = await _kernel.InvokePromptAsync(prompt, cancellationToken: cancellationToken);
            raw = response.GetValue<string>() ?? string.Empty;
        }
        catch
        {
            return HeuristicFallback(message, state, knownServers, planningMemory, "heuristic_after_llm_error", true, false);
        }

        var json = TryExtractJson(raw);
        if (json is null)
            return HeuristicFallback(message, state, knownServers, planningMemory, "heuristic_after_llm_parse_failure", true, false);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var lowered = message.ToLowerInvariant();

            var intentText = root.TryGetProperty("intent", out var intentNode)
                ? intentNode.GetString() ?? "clarification"
                : "clarification";
            var intent = ParseIntent(intentText);

            if (ShouldPromoteToStatusIntent(intent, lowered))
                intent = AdminIntentType.StatusCheck;

            if (intent is AdminIntentType.Troubleshooting or AdminIntentType.StatusCheck &&
                LooksLikeServerlessReferenceQuestion(lowered, knownServers))
            {
                intent = AdminIntentType.Chat;
            }

            var correctionFollowUp = IsCorrectionFollowUp(lowered);
            if (correctionFollowUp &&
                !HasExplicitIntentSignal(lowered) &&
                TryParseStateIntent(state.LastIntent, out var previousIntent) &&
                previousIntent is not (AdminIntentType.Chat or AdminIntentType.Clarification))
            {
                intent = previousIntent;
            }

            var confidence = root.TryGetProperty("confidence", out var confidenceNode) && confidenceNode.ValueKind == JsonValueKind.Number
                ? confidenceNode.GetDouble()
                : 0.4;
            var llmNeedsClarification = root.TryGetProperty("needsClarification", out var needsNode) && needsNode.ValueKind == JsonValueKind.True;
            var clarification = root.TryGetProperty("clarificationQuestion", out var questionNode) ? questionNode.GetString() : null;
            var targetRef = root.TryGetProperty("targetRef", out var targetNode) ? targetNode.GetString() : null;

            string? serverName = null;
            string? playerName = null;
            string? commandText = null;
            string? timeRange = null;
            string? severity = null;
            string? configKey = null;
            string? configValue = null;
            var scopeKind = ServerScopeKind.Unspecified;
            List<string>? serverNames = null;

            if (root.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Object)
            {
                serverName = slots.TryGetProperty("serverName", out var sn) ? sn.GetString() : null;
                playerName = slots.TryGetProperty("playerName", out var pn) ? pn.GetString() : null;
                commandText = slots.TryGetProperty("commandText", out var cn) ? cn.GetString() : null;
                timeRange = slots.TryGetProperty("timeRange", out var tn) ? tn.GetString() : null;
                severity = slots.TryGetProperty("severity", out var sv) ? sv.GetString() : null;
                configKey = slots.TryGetProperty("configKey", out var ck) ? ck.GetString() : null;
                configValue = slots.TryGetProperty("configValue", out var cv) ? cv.GetString() : null;
                scopeKind = slots.TryGetProperty("scopeKind", out var scopeNode)
                    ? ParseScopeKind(scopeNode.GetString())
                    : ServerScopeKind.Unspecified;

                if (slots.TryGetProperty("serverNames", out var namesNode) && namesNode.ValueKind == JsonValueKind.Array)
                {
                    serverNames = namesNode
                        .EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item!.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }

            var serverlessCatalogQuestion =
                (intent == AdminIntentType.Chat || intent == AdminIntentType.RconCommand || intent == AdminIntentType.Troubleshooting) &&
                LooksLikeServerlessCatalogQuestion(lowered, knownServers);

            if (string.IsNullOrWhiteSpace(serverName) && !serverlessCatalogQuestion)
                serverName = ExtractServerHint(message);

            var allowPluralDefaultAll = intent is AdminIntentType.StatusCheck or AdminIntentType.Troubleshooting;
            var scope = ServerScopeResolver.Resolve(
                message, knownServers, state,
                scopeKind, serverNames, serverName,
                allowPluralDefaultAll: allowPluralDefaultAll,
                allowLastScopeFallback: !serverlessCatalogQuestion);

            serverNames = scope.Servers.ToList();
            serverName = serverNames.Count == 1 ? serverNames[0] : null;
            scopeKind = scope.ScopeKind;

            targetRef = NormalizeTargetRef(targetRef) ?? InferTargetRef(intent, lowered);
            if (intent == AdminIntentType.Chat && LooksLikeExplicitWebLookup(lowered))
            {
                targetRef = "web.search";
            }

            // Bug #3 fix: LLM's needsClarification is respected only when it also provides a
            // clarification question (avoids false-positive blocking when scope is already resolved).
            var scopeNeedsClarification = RequiresServerScope(intent, lowered, knownServers) && scope.RequiresClarification;
            var needsClarification = scopeNeedsClarification
                || (llmNeedsClarification && !scopeNeedsClarification && !string.IsNullOrWhiteSpace(clarification));

            clarification = needsClarification
                ? BuildClarificationQuestion(intent, knownServers, clarification)
                : null;

            return new AdminIntentRoute(
                intent,
                new AdminIntentSlots(serverName, playerName, commandText, timeRange, severity, scopeKind, serverNames, configKey, configValue),
                Math.Clamp(confidence, 0.0, 1.0),
                needsClarification,
                clarification,
                targetRef,
                planningMemory,
                "llm",
                true,
                true);
        }
        catch
        {
            return HeuristicFallback(message, state, knownServers, planningMemory, "heuristic_after_llm_json_error", true, false);
        }
    }

    // Static template — no C# interpolation, so braces in JSON examples are unambiguous.
    private const string PromptTemplate =
        "You are classifying admin messages for a Rust game server operations agent.\n" +
        "Return ONLY strict JSON — no markdown, no explanation, nothing else.\n\n" +
        "Required JSON keys: intent, confidence, needsClarification, clarificationQuestion, targetRef, slots\n\n" +
        "══ INTENT VALUES ══\n" +
        "chat              General conversation, questions about the agent, git/build/code operations\n" +
        "server_control    Rust game server lifecycle ONLY: start, stop, restart, kill, update, wipe\n" +
        "player_lookup     Player lists, ban lists, kick queries\n" +
        "rcon_command      Live RCON commands sent to a running server; server convars get/set/explain\n" +
        "file_edit         Read or edit rustmgr JSON config files; query or change a config file key\n" +
        "status_check      Server health, online/offline, network interfaces, logs overview\n" +
        "troubleshooting   Plugin errors, oxide/umod issues, compile failures, crash investigation\n" +
        "server_management Add, remove, register, provision server connections; update RCON credentials\n" +
        "clarification     Cannot determine intent\n\n" +
        "══ TARGETREF VALUES ══\n" +
        "rust.server.control   rust.player.lookup    rust.rcon.command    rust.file.edit\n" +
        "rust.status.check     rust.logs.inspect     rust.plugins.verify  rust.network.inspect\n" +
        "rust.chat.reply       rust.server.management  web.search\n\n" +
        "══ SLOTS ══\n" +
        "serverName   string  – single server (match from Known servers list when possible)\n" +
        "serverNames  array   – multiple server names\n" +
        "scopeKind    enum    – unspecified | single | all | subset\n" +
        "playerName   string\n" +
        "commandText  string  – raw RCON command text; or RCON IP when registering a server\n" +
        "configKey    string  – specific JSON config key being read or changed (e.g. server.maxplayers)\n" +
        "configValue  string  – new value for a config key mutation (e.g. \"200\", \"true\")\n" +
        "timeRange    string\n" +
        "severity     string\n\n" +
        "══ ROUTING RULES ══\n\n" +
        "1. CONVAR vs CONFIG FILE — the most important distinction:\n" +
        "   Config file keys are a FIXED SET stored in rustmgr JSON files:\n" +
        "     server.worldsize  server.seed  server.maxplayers  server.hostname\n" +
        "     server.port  server.identity  rcon.port  rcon.password  rcon.ip\n" +
        "     app.port  serverDir  logFile  additionalArgs\n" +
        "   Aliases for config keys: worldsize, seed, maxplayers, hostname, serverdir, logfile\n" +
        "   -> intent=file_edit, targetRef=rust.file.edit\n" +
        "   -> put the key in slots.configKey, the new value (if any) in slots.configValue\n\n" +
        "   All OTHER dotted identifiers (ai.move, decay.scale, fps.limit, env.time, server.fps,\n" +
        "   craft.instant, etc.) are RCON convars — live runtime variables.\n" +
        "   -> intent=rcon_command, targetRef=rust.rcon.command\n" +
        "   -> put the identifier (and any value) in slots.commandText\n\n" +
        "2. server_control is ONLY for lifecycle: start, stop, restart, kill, update, wipe.\n" +
        "   NEVER use server_control for raw commands, convars, or config changes.\n\n" +
        "3. rcon_command for any message with: \"rcon\", \"run\", \"execute\", \"send command\", quoted\n" +
        "   command text, \"what does X.Y do\", \"explain X.Y\", \"get X.Y on server\", \"set X.Y to V\"\n" +
        "   where X.Y is NOT in the config file key list above.\n\n" +
        "4. troubleshooting + rust.plugins.verify for: \"compile error/s\", \"plugin error/s\",\n" +
        "   \"oxide issue/s\", \"umod issue/s\", \"cs error/s\". NEVER treat \"compile\" as a server name.\n\n" +
        "5. status_check + rust.network.inspect for: \"network\", \"throughput\", \"latency\",\n" +
        "   \"bandwidth\", \"eth0\", \"wg0\", \"wg1\", \"wt1\", \"interface\".\n\n" +
        "6. status_check + rust.logs.inspect for: \"log/s\", \"exception\", \"traceback\", \"crash log\".\n\n" +
        "7. server_management for: add/register/remove/delete/provision a server, update RCON\n" +
        "   credentials, \"edit server connection\". serverName and commandText (=RCON IP) go in slots.\n\n" +
        "8. chat for: git operations, pull, rebuild, build, questions about the agent software itself.\n" +
        "   Memory/admin commands such as \"memory stats\", \"/memory search\", \"memory migrate\" are ALSO chat.\n" +
        "   Plugin reference lookup commands such as \"/plugin-index search\", \"what commands does Backpacks have\",\n" +
        "   \"what permission is needed for /kit\", and \"which plugin owns /remove\" are ALSO chat.\n" +
        "   General questions about plugins, server convars, or server commands do NOT need a server.\n" +
        "   Only set serverName or ask for a server when the admin names a server or asks about live server state.\n" +
        "   Examples of serverless catalog questions: \"what convars are there for stability\", \"what does ai.move do\",\n" +
        "   \"what commands does Backpacks have\", \"which permission does /kit need\".\n" +
        "   CRITICAL: \"git pull\", \"rebuild the agent\", \"can you pull?\" -> ALWAYS intent=chat.\n\n" +
        "9. scopeKind=all when admin says \"all servers\", \"every server\", \"all N servers\".\n" +
        "   For general status/health questions with NO specific server name, default scopeKind=all.\n\n" +
        "10. Correction follow-ups (\"no\", \"actually\", \"I meant\"): preserve previous intent unless\n" +
        "    the message contains an unambiguous new intent signal.\n\n" +
        "══ EXAMPLES ══\n" +
        "\"set ai.move false on cotton\"\n" +
        "  -> intent=rcon_command, targetRef=rust.rcon.command, slots.commandText=\"ai.move false\", slots.serverName=\"cotton\"\n\n" +
        "\"set maxplayers to 200 on cotton\"\n" +
        "  -> intent=file_edit, targetRef=rust.file.edit, slots.configKey=\"server.maxplayers\", slots.configValue=\"200\", slots.serverName=\"cotton\"\n\n" +
        "\"what does ai.move do\"\n" +
        "  -> intent=rcon_command, targetRef=rust.rcon.command, needsClarification=false, slots.commandText=\"ai.move\", serverName=null\n\n" +
        "\"what convars are there for stability\"\n" +
        "  -> intent=chat, targetRef=rust.chat.reply, needsClarification=false, serverName=null\n\n" +
        "\"give me the config for the Vanish plugin from modded server\"\n" +
        "  -> intent=file_edit, targetRef=rust.file.edit, slots.serverName=\"modded\"\n\n" +
        "\"which plugins use the /kit permission\"\n" +
        "  -> intent=chat, targetRef=rust.chat.reply, needsClarification=false, serverName=null\n\n" +
        "\"how do I reload oxide\"\n" +
        "  -> intent=chat, targetRef=rust.chat.reply, needsClarification=false, serverName=null\n\n" +
        "\"what does vanish do\"\n" +
        "  -> intent=chat, targetRef=rust.chat.reply, needsClarification=false, serverName=null\n\n" +
        "\"docs for Vanish plugin\"\n" +
        "  -> intent=chat, targetRef=web.search, needsClarification=false, serverName=null\n\n" +
        "\"what's the worldsize on monthly\"\n" +
        "  -> intent=file_edit, targetRef=rust.file.edit, slots.configKey=\"server.worldsize\", slots.serverName=\"monthly\"\n\n" +
        "\"show cotton config\" / \"open cotton.json\"\n" +
        "  -> intent=file_edit, targetRef=rust.file.edit, slots.serverName=\"cotton\"\n\n" +
        "\"compile errors on monthly\"\n" +
        "  -> intent=troubleshooting, targetRef=rust.plugins.verify, slots.serverName=\"monthly\"\n\n" +
        "\"restart cotton in 5 minutes\"\n" +
        "  -> intent=server_control, targetRef=rust.server.control, slots.serverName=\"cotton\"\n\n" +
        "\"run status on cotton\" / \"rcon say Hello on cotton\"\n" +
        "  -> intent=rcon_command, targetRef=rust.rcon.command, slots.commandText=\"status\", slots.serverName=\"cotton\"\n\n" +
        "\"git pull\" / \"rebuild the agent\" / \"can you pull from main?\"\n" +
        "  -> intent=chat, targetRef=rust.chat.reply\n\n" +
        "\"memory stats\" / \"memory search restart failure\"\n" +
        "  -> intent=chat, targetRef=rust.chat.reply\n\n" +
        "\"add remote server MyServer at 1.2.3.4:28016 password abc\"\n" +
        "  -> intent=server_management, targetRef=rust.server.management, slots.serverName=\"MyServer\", slots.commandText=\"1.2.3.4\"\n";

    private string BuildPrompt(
        string message,
        ConversationSelectionState state,
        IReadOnlyList<string> knownServers,
        WorkflowMemoryContext planningMemory)
    {
        var sb = new StringBuilder();

        var systemPrefix = BuildSystemPrefix();
        if (!string.IsNullOrWhiteSpace(systemPrefix))
            sb.AppendLine(systemPrefix);

        var learnedRules = BuildLearnedRulesSection();
        if (!string.IsNullOrWhiteSpace(learnedRules))
            sb.AppendLine(learnedRules);

        sb.Append(PromptTemplate);
        sb.AppendLine();
        sb.AppendLine("══ CONVERSATION CONTEXT ══");
        sb.AppendLine($"lastServer={state.LastServerName ?? string.Empty}");
        sb.AppendLine($"lastIntent={state.LastIntent ?? string.Empty}");
        sb.AppendLine($"lastScopeKind={state.LastScopeKind}");
        sb.AppendLine($"lastResolvedServers={string.Join(", ", state.LastResolvedServers)}");
        sb.AppendLine($"lastCommand={state.LastCommandText ?? string.Empty}");
        sb.AppendLine($"pendingClarificationIntent={state.PendingClarification?.Intent ?? string.Empty}");
        sb.AppendLine($"pendingClarificationQuestion={state.PendingClarification?.Question ?? string.Empty}");
        sb.AppendLine($"lastUserSummary={state.LastUserMessageSummary ?? string.Empty}");
        sb.AppendLine();
        sb.AppendLine($"Known servers: {string.Join(", ", knownServers)}");
        sb.AppendLine();
        var recentConversation = BuildRecentConversationSection(state);
        if (!string.IsNullOrWhiteSpace(recentConversation))
        {
            sb.AppendLine(recentConversation);
        }

        if (planningMemory.HasResults)
        {
            sb.AppendLine("══ RELEVANT MEMORY ══");
            sb.AppendLine(planningMemory.CompactContext);
            sb.AppendLine();
        }
        else if (planningMemory.RetrievalSkipped && !string.IsNullOrWhiteSpace(planningMemory.SkipReason))
        {
            sb.AppendLine($"Memory status: {planningMemory.SkipReason}");
            sb.AppendLine();
        }

        sb.Append($"Admin message: {message}");

        return sb.ToString();
    }

    private static string BuildRecentConversationSection(ConversationSelectionState state)
    {
        if (state.RecentMessages.Count == 0)
            return string.Empty;

        var sb = new StringBuilder("══ RECENT CONVERSATION ══\n");
        foreach (var msg in state.RecentMessages.TakeLast(6))
        {
            var label = msg.Role == "assistant" ? "RustOps" : "Admin";
            sb.AppendLine($"[{label}]: {msg.Text}");
        }

        return sb.ToString();
    }

    private static AdminIntentRoute HeuristicFallback(
        string message,
        ConversationSelectionState state,
        IReadOnlyList<string> knownServers,
        WorkflowMemoryContext planningMemory,
        string source,
        bool llmAttempted,
        bool llmSucceeded)
    {
        var lowered = message.ToLowerInvariant();
        var intent = InferHeuristicIntent(lowered, knownServers);

        if (IsCorrectionFollowUp(lowered) &&
            !HasExplicitIntentSignal(lowered) &&
            TryParseStateIntent(state.LastIntent, out var previousIntent) &&
            previousIntent is not (AdminIntentType.Chat or AdminIntentType.Clarification))
        {
            intent = previousIntent;
        }

        var serverlessCatalogQuestion =
            (intent == AdminIntentType.Chat || intent == AdminIntentType.RconCommand || intent == AdminIntentType.Troubleshooting) &&
            LooksLikeServerlessCatalogQuestion(lowered, knownServers);
        var hintedServer = serverlessCatalogQuestion ? null : ExtractServerHint(message);
        var scope = ServerScopeResolver.Resolve(
            message, knownServers, state,
            ServerScopeKind.Unspecified, null, hintedServer,
            allowPluralDefaultAll: intent is AdminIntentType.StatusCheck or AdminIntentType.Troubleshooting,
            allowLastScopeFallback: !serverlessCatalogQuestion);

        var selectedServer = scope.Servers.Count == 1 ? scope.Servers[0] : null;
        var needsClarification = RequiresServerScope(intent, lowered, knownServers) && scope.RequiresClarification;
        var targetRef = InferTargetRef(intent, lowered);

        return new AdminIntentRoute(
            intent,
            new AdminIntentSlots(selectedServer, null, null, null, null, scope.ScopeKind, scope.Servers),
            0.4,
            needsClarification,
            needsClarification ? BuildClarificationQuestion(intent, knownServers, null) : null,
            targetRef,
            planningMemory,
            source,
            llmAttempted,
            llmSucceeded);
    }

    private static AdminIntentType InferHeuristicIntent(string lowered, IReadOnlyList<string> knownServers)
    {
        if (lowered.StartsWith("memory ", StringComparison.Ordinal) ||
            lowered.StartsWith("/memory ", StringComparison.Ordinal) ||
            lowered.StartsWith("plugin-index ", StringComparison.Ordinal) ||
            lowered.StartsWith("/plugin-index ", StringComparison.Ordinal) ||
            LooksLikeExplicitWebLookup(lowered) ||
            LooksLikeGeneralServerCatalogQuestion(lowered, knownServers) ||
            LooksLikeServerlessReferenceQuestion(lowered, knownServers) ||
            LooksLikePluginReferenceQuestion(lowered))
            return AdminIntentType.Chat;
        if (lowered.Contains("add server") || lowered.Contains("add remote") || lowered.Contains("register server") ||
            lowered.Contains("remove server") || lowered.Contains("delete server") || lowered.Contains("provision server") ||
            lowered.Contains("new server") || lowered.Contains("update rcon") || lowered.Contains("edit server") ||
            lowered.Contains("rcon credential") || lowered.Contains("connect server"))
            return AdminIntentType.ServerManagement;
        if (LooksLikeServerVariableIntent(lowered))
            return AdminIntentType.RconCommand;
        if (LooksLikeFileOrConfigIntent(lowered) || LooksLikeServerConfigValueIntent(lowered))
            return AdminIntentType.FileEdit;
        if (lowered.Contains("pull") || lowered.Contains("git") || lowered.Contains("rebuild") || lowered.Contains("build"))
            return AdminIntentType.Chat;
        if (lowered.Contains("network") || lowered.Contains("throughput") || lowered.Contains("latency") || lowered.Contains("eth0") || lowered.Contains("wg1") || lowered.Contains("wt1"))
            return AdminIntentType.StatusCheck;
        if (lowered.Contains("plugin") || lowered.Contains("umod") || lowered.Contains("oxide") || lowered.Contains("compile") || lowered.Contains("compilation"))
            return AdminIntentType.Troubleshooting;
        if (lowered.Contains("restart") || lowered.Contains("wipe") || IsLifecycleVerb(lowered))
            return AdminIntentType.ServerControl;
        if (lowered.Contains("player") || lowered.Contains("ban"))
            return AdminIntentType.PlayerLookup;
        if (lowered.Contains("rcon") || lowered.Contains("command") || lowered.Contains("say ") || lowered.Contains("global."))
            return AdminIntentType.RconCommand;
        if (lowered.Contains("status") || lowered.Contains("health") || lowered.Contains("logs") || lowered.Contains("online"))
            return AdminIntentType.StatusCheck;
        if (lowered.Contains("fix") || lowered.Contains("error") || lowered.Contains("fail"))
            return AdminIntentType.Troubleshooting;
        return AdminIntentType.Chat;
    }

    private static bool LooksLikePluginReferenceQuestion(string lowered) =>
        ((lowered.Contains("plugin", StringComparison.Ordinal) &&
          (lowered.Contains("permission", StringComparison.Ordinal) ||
           lowered.Contains("command", StringComparison.Ordinal) ||
           lowered.Contains("what does", StringComparison.Ordinal))) ||
         lowered.Contains("what command", StringComparison.Ordinal) ||
         lowered.Contains("which command", StringComparison.Ordinal) ||
         lowered.Contains("commands can players", StringComparison.Ordinal) ||
         lowered.Contains("permission is needed", StringComparison.Ordinal) ||
         (lowered.Contains("permission", StringComparison.Ordinal) && lowered.Contains("/", StringComparison.Ordinal)) ||
         lowered.Contains("which plugin owns", StringComparison.Ordinal) ||
         lowered.Contains("which plugins use", StringComparison.Ordinal) ||
         lowered.Contains("does this plugin use", StringComparison.Ordinal) ||
         lowered.Contains("hook", StringComparison.Ordinal) ||
         lowered.Contains("config key", StringComparison.Ordinal)) &&
        !lowered.Contains("run ", StringComparison.Ordinal) &&
        !lowered.Contains("execute ", StringComparison.Ordinal);

    private static bool LooksLikeExplicitWebLookup(string lowered) =>
        (lowered.StartsWith("search ", StringComparison.Ordinal) ||
         lowered.StartsWith("search for ", StringComparison.Ordinal) ||
         lowered.StartsWith("look up ", StringComparison.Ordinal) ||
         lowered.StartsWith("lookup ", StringComparison.Ordinal) ||
         lowered.StartsWith("find docs", StringComparison.Ordinal) ||
         lowered.StartsWith("docs for ", StringComparison.Ordinal) ||
         lowered.StartsWith("documentation for ", StringComparison.Ordinal) ||
         lowered.StartsWith("what is ", StringComparison.Ordinal) ||
         lowered.StartsWith("how does ", StringComparison.Ordinal)) &&
        (lowered.Contains("rust", StringComparison.Ordinal) ||
         lowered.Contains("oxide", StringComparison.Ordinal) ||
         lowered.Contains("umod", StringComparison.Ordinal) ||
         lowered.Contains("plugin", StringComparison.Ordinal) ||
         lowered.Contains("convar", StringComparison.Ordinal) ||
         lowered.Contains("permission", StringComparison.Ordinal));

    private static bool LooksLikeServerlessReferenceQuestion(string lowered, IReadOnlyList<string> knownServers)
    {
        if (MentionsKnownServerWithScope(lowered, knownServers))
            return false;

        return LooksLikePluginReferenceQuestion(lowered) ||
               lowered.Contains("how do i reload oxide", StringComparison.Ordinal) ||
               lowered.Contains("reload oxide", StringComparison.Ordinal) ||
               lowered.Contains("what does vanish do", StringComparison.Ordinal);
    }

    private static bool LooksLikeGeneralServerCatalogQuestion(string lowered, IReadOnlyList<string> knownServers)
    {
        var mentionsCatalog =
            lowered.Contains("convar", StringComparison.Ordinal) ||
            lowered.Contains("convars", StringComparison.Ordinal) ||
            lowered.Contains("server variable", StringComparison.Ordinal) ||
            lowered.Contains("server variables", StringComparison.Ordinal) ||
            lowered.Contains("server command", StringComparison.Ordinal) ||
            lowered.Contains("server commands", StringComparison.Ordinal);

        if (!mentionsCatalog)
            return false;

        var asksGeneral =
            lowered.Contains("what", StringComparison.Ordinal) ||
            lowered.Contains("which", StringComparison.Ordinal) ||
            lowered.Contains("list", StringComparison.Ordinal) ||
            lowered.Contains("show", StringComparison.Ordinal) ||
            lowered.Contains("are there", StringComparison.Ordinal) ||
            lowered.Contains("available", StringComparison.Ordinal);

        return asksGeneral && !MentionsKnownServerWithScope(lowered, knownServers);
    }

    private static bool LooksLikeServerlessRconCatalogQuestion(string lowered, IReadOnlyList<string> knownServers)
    {
        if (MentionsKnownServerWithScope(lowered, knownServers))
            return false;

        if (LooksLikeGeneralServerCatalogQuestion(lowered, knownServers))
            return true;

        var hasDottedIdentifier = Regex.IsMatch(lowered, @"\b[a-z][a-z0-9_-]*\.[a-z0-9_.-]+\b", RegexOptions.IgnoreCase);
        if (!hasDottedIdentifier)
            return false;

        return
            lowered.Contains("what does", StringComparison.Ordinal) ||
            lowered.Contains("description", StringComparison.Ordinal) ||
            lowered.Contains("explain", StringComparison.Ordinal) ||
            (lowered.Contains(" do", StringComparison.Ordinal) && !lowered.Contains("how do", StringComparison.Ordinal));
    }

    private static bool LooksLikeServerlessCatalogQuestion(string lowered, IReadOnlyList<string> knownServers) =>
        LooksLikeServerlessRconCatalogQuestion(lowered, knownServers) ||
        LooksLikeServerlessReferenceQuestion(lowered, knownServers) ||
        LooksLikeExplicitWebLookup(lowered);

    private static bool MentionsKnownServerWithScope(string lowered, IReadOnlyList<string> knownServers)
    {
        foreach (var server in knownServers)
        {
            if (string.IsNullOrWhiteSpace(server))
                continue;

            var escaped = Regex.Escape(server.Trim().ToLowerInvariant());
            if (Regex.IsMatch(lowered, $@"\b(?:on|for|in)\s+(?:the\s+)?{escaped}\b", RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }

    // Standalone lifecycle verbs — only match when NOT part of "update rcon" etc.
    private static bool IsLifecycleVerb(string lowered) =>
        (lowered.Contains("start ") || lowered.Contains(" start") || lowered == "start") ||
        (lowered.Contains("stop ") || lowered.Contains(" stop") || lowered == "stop") ||
        lowered.Contains("kill ");

    private static bool ShouldPromoteToStatusIntent(AdminIntentType intent, string loweredMessage)
    {
        if (intent is not (AdminIntentType.Chat or AdminIntentType.Clarification))
            return false;

        return loweredMessage.Contains("online", StringComparison.Ordinal) &&
               loweredMessage.Contains("server", StringComparison.Ordinal);
    }

    private static bool HasExplicitIntentSignal(string loweredMessage) =>
        loweredMessage.Contains("restart", StringComparison.Ordinal) ||
        loweredMessage.Contains("start ", StringComparison.Ordinal) ||
        loweredMessage.Contains("stop ", StringComparison.Ordinal) ||
        loweredMessage.Contains("kill ", StringComparison.Ordinal) ||
        loweredMessage.Contains("wipe ", StringComparison.Ordinal) ||
        loweredMessage.Contains("player", StringComparison.Ordinal) ||
        loweredMessage.Contains("ban", StringComparison.Ordinal) ||
        loweredMessage.Contains("rcon", StringComparison.Ordinal) ||
        loweredMessage.Contains("command", StringComparison.Ordinal) ||
        loweredMessage.Contains("convar", StringComparison.Ordinal) ||
        loweredMessage.Contains("variable", StringComparison.Ordinal) ||
        loweredMessage.Contains("pull", StringComparison.Ordinal) ||
        loweredMessage.Contains("rebuild", StringComparison.Ordinal) ||
        loweredMessage.Contains("build", StringComparison.Ordinal) ||
        loweredMessage.Contains("git", StringComparison.Ordinal) ||
        loweredMessage.Contains("compile", StringComparison.Ordinal) ||
        loweredMessage.Contains("serverconfig", StringComparison.Ordinal) ||
        loweredMessage.Contains("server config", StringComparison.Ordinal) ||
        LooksLikeServerVariableIntent(loweredMessage) ||
        LooksLikeServerConfigValueIntent(loweredMessage);

    private static bool IsCorrectionFollowUp(string loweredMessage) =>
        loweredMessage.StartsWith("no ", StringComparison.Ordinal) ||
        loweredMessage.StartsWith("no,", StringComparison.Ordinal) ||
        loweredMessage.StartsWith("nah", StringComparison.Ordinal) ||
        loweredMessage.StartsWith("actually", StringComparison.Ordinal) ||
        loweredMessage.Contains("i meant", StringComparison.Ordinal);

    private static bool TryParseStateIntent(string? intentText, out AdminIntentType intent)
    {
        intent = AdminIntentType.Chat;
        if (string.IsNullOrWhiteSpace(intentText))
            return false;

        var normalized = intentText.Trim();
        if (Enum.TryParse(normalized, true, out intent))
            return true;

        intent = ParseIntent(normalized.Replace(" ", "_", StringComparison.Ordinal));
        return intent != AdminIntentType.Clarification || normalized.Contains("clarification", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresServerScope(AdminIntentType intent, string loweredMessage, IReadOnlyList<string> knownServers)
    {
        if ((intent == AdminIntentType.RconCommand || intent == AdminIntentType.Chat || intent == AdminIntentType.Troubleshooting) &&
            LooksLikeServerlessCatalogQuestion(loweredMessage, knownServers))
            return false;

        // FileEdit and ServerManagement handle their own scope internally.
        return intent is
            AdminIntentType.ServerControl or
            AdminIntentType.PlayerLookup or
            AdminIntentType.RconCommand or
            AdminIntentType.StatusCheck or
            AdminIntentType.Troubleshooting;
    }

    private static string BuildClarificationQuestion(AdminIntentType intent, IReadOnlyList<string> knownServers, string? preferredQuestion)
    {
        if (!string.IsNullOrWhiteSpace(preferredQuestion))
            return preferredQuestion.Trim();

        var known = knownServers.Count == 0
            ? "No configured servers are currently available."
            : $"Known servers: {string.Join(", ", knownServers)}.";

        return intent switch
        {
            AdminIntentType.ServerControl => $"Which single server should I target? {known}",
            AdminIntentType.PlayerLookup => $"Which server should I query for players? {known}",
            AdminIntentType.RconCommand => $"Which server should receive the RCON command? {known}",
            AdminIntentType.FileEdit => $"Which server's config should I access? {known}",
            _ => $"Which server should I check? You can name one server or say 'all servers'. {known}"
        };
    }

    private static string? InferTargetRef(AdminIntentType intent, string loweredMessage) =>
        intent switch
        {
            AdminIntentType.ServerControl => "rust.server.control",
            AdminIntentType.PlayerLookup => "rust.player.lookup",
            AdminIntentType.RconCommand => "rust.rcon.command",
            AdminIntentType.FileEdit => "rust.file.edit",
            AdminIntentType.ServerManagement => "rust.server.management",
            AdminIntentType.Chat or AdminIntentType.Clarification => LooksLikeExplicitWebLookup(loweredMessage) ? "web.search" : "rust.chat.reply",
            AdminIntentType.StatusCheck or AdminIntentType.Troubleshooting => InferDiagnosticsTarget(loweredMessage),
            _ => null
        };

    private static string InferDiagnosticsTarget(string loweredMessage)
    {
        if (loweredMessage.Contains("network") || loweredMessage.Contains("latency") || loweredMessage.Contains("throughput") ||
            loweredMessage.Contains("eth0") || loweredMessage.Contains("wg1") || loweredMessage.Contains("wt1"))
            return "rust.network.inspect";

        if (loweredMessage.Contains("compile") || loweredMessage.Contains("compilation") ||
            loweredMessage.Contains("plugin") || loweredMessage.Contains("umod") || loweredMessage.Contains("oxide"))
            return "rust.plugins.verify";

        if (loweredMessage.Contains("log") || loweredMessage.Contains("error") ||
            loweredMessage.Contains("exception") || loweredMessage.Contains("fail"))
            return "rust.logs.inspect";

        return "rust.status.check";
    }

    private static string? NormalizeTargetRef(string? targetRef)
    {
        if (string.IsNullOrWhiteSpace(targetRef))
            return null;

        return targetRef.Trim().ToLowerInvariant() switch
        {
            "network" or "network.inspect" => "rust.network.inspect",
            "plugins" or "plugins.verify" or "plugin" => "rust.plugins.verify",
            "logs" or "logs.inspect" => "rust.logs.inspect",
            "status" or "status.check" => "rust.status.check",
            "server_control" => "rust.server.control",
            "player_lookup" => "rust.player.lookup",
            "rcon_command" => "rust.rcon.command",
            "file_edit" or "file" or "config" => "rust.file.edit",
            "server_management" or "server.management" => "rust.server.management",
            "web" or "web.search" or "search" => "web.search",
            "chat" or "clarification" => "rust.chat.reply",
            _ => targetRef
        };
    }

    private static bool LooksLikeFileOrConfigIntent(string lowered)
    {
        var mentionsConfig =
            lowered.Contains("config", StringComparison.Ordinal) ||
            lowered.Contains("serverconfig", StringComparison.Ordinal) ||
            lowered.Contains(".json", StringComparison.Ordinal) ||
            lowered.Contains(".cfg", StringComparison.Ordinal);

        var readVerb =
            lowered.Contains("show", StringComparison.Ordinal) ||
            lowered.Contains("give", StringComparison.Ordinal) ||
            lowered.Contains("read", StringComparison.Ordinal) ||
            lowered.Contains("view", StringComparison.Ordinal) ||
            lowered.Contains("open", StringComparison.Ordinal) ||
            lowered.Contains("display", StringComparison.Ordinal) ||
            lowered.Contains("contents", StringComparison.Ordinal) ||
            lowered.Contains("print", StringComparison.Ordinal);

        var editVerb =
            lowered.Contains("set ", StringComparison.Ordinal) ||
            lowered.Contains("change ", StringComparison.Ordinal) ||
            lowered.Contains("update ", StringComparison.Ordinal) ||
            lowered.Contains("edit ", StringComparison.Ordinal) ||
            lowered.Contains("modify ", StringComparison.Ordinal);

        return mentionsConfig && (readVerb || editVerb);
    }

    private static bool LooksLikeServerVariableIntent(string lowered)
    {
        var hasDottedIdentifier = Regex.IsMatch(lowered, @"\b[a-z][a-z0-9_-]*\.[a-z0-9_.-]+\b", RegexOptions.IgnoreCase);
        if (!hasDottedIdentifier)
            return false;

        // Don't classify config file keys as convars
        if (ConfigFileKeys.Any(k => lowered.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return false;

        return
            lowered.Contains("what does", StringComparison.Ordinal) ||
            lowered.Contains("description", StringComparison.Ordinal) ||
            lowered.Contains("explain", StringComparison.Ordinal) ||
            lowered.Contains("value", StringComparison.Ordinal) ||
            lowered.Contains("current", StringComparison.Ordinal) ||
            lowered.Contains("fetch", StringComparison.Ordinal) ||
            lowered.Contains("get ", StringComparison.Ordinal) ||
            lowered.Contains("read ", StringComparison.Ordinal) ||
            lowered.Contains("what is", StringComparison.Ordinal) ||
            lowered.Contains("set ", StringComparison.Ordinal) ||
            lowered.Contains("change ", StringComparison.Ordinal) ||
            lowered.Contains("update ", StringComparison.Ordinal) ||
            lowered.Contains("server variable", StringComparison.Ordinal) ||
            lowered.Contains("convar", StringComparison.Ordinal);
    }

    private static bool LooksLikeServerConfigValueIntent(string lowered)
    {
        var mentionsServerOrConfig =
            lowered.Contains(" server", StringComparison.Ordinal) ||
            lowered.Contains("servers", StringComparison.Ordinal) ||
            lowered.Contains("config", StringComparison.Ordinal);
        if (!mentionsServerOrConfig)
            return false;

        var asksValue =
            lowered.Contains("what is", StringComparison.Ordinal) ||
            lowered.Contains("what's", StringComparison.Ordinal) ||
            lowered.Contains("whats", StringComparison.Ordinal) ||
            lowered.Contains("value", StringComparison.Ordinal) ||
            lowered.Contains("show", StringComparison.Ordinal) ||
            lowered.Contains("get ", StringComparison.Ordinal) ||
            lowered.Contains("read ", StringComparison.Ordinal);
        if (!asksValue)
            return false;

        return
            lowered.Contains("worldsize", StringComparison.Ordinal) ||
            lowered.Contains("world size", StringComparison.Ordinal) ||
            lowered.Contains("maxplayers", StringComparison.Ordinal) ||
            lowered.Contains("max players", StringComparison.Ordinal) ||
            lowered.Contains("hostname", StringComparison.Ordinal) ||
            lowered.Contains("server name", StringComparison.Ordinal) ||
            lowered.Contains("server.seed", StringComparison.Ordinal) ||
            lowered.Contains("seed", StringComparison.Ordinal) ||
            lowered.Contains("rcon.port", StringComparison.Ordinal) ||
            lowered.Contains("rcon.password", StringComparison.Ordinal) ||
            lowered.Contains("app.port", StringComparison.Ordinal) ||
            lowered.Contains("server.port", StringComparison.Ordinal);
    }

    private static ServerScopeKind ParseScopeKind(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "single" => ServerScopeKind.Single,
        "all" => ServerScopeKind.All,
        "subset" => ServerScopeKind.Subset,
        _ => ServerScopeKind.Unspecified
    };

    private static AdminIntentType ParseIntent(string value) => value.ToLowerInvariant() switch
    {
        "chat" => AdminIntentType.Chat,
        "server_control" => AdminIntentType.ServerControl,
        "player_lookup" => AdminIntentType.PlayerLookup,
        "rcon_command" => AdminIntentType.RconCommand,
        "file_edit" => AdminIntentType.FileEdit,
        "status_check" => AdminIntentType.StatusCheck,
        "troubleshooting" => AdminIntentType.Troubleshooting,
        "server_management" => AdminIntentType.ServerManagement,
        _ => AdminIntentType.Clarification
    };

    private static string? TryExtractJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return raw[start..(end + 1)];
    }

    private static readonly HashSet<string> ServerHintExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "your", "the", "this", "that", "my", "our", "their", "its",
        "a", "an", "any", "some", "server", "servers"
    };

    private static string? ExtractServerHint(string message)
    {
        var match = Regex.Match(
            message,
            @"\b(?:from|on|for|in)\s+(?<server>[a-zA-Z0-9][a-zA-Z0-9._-]{2,})\b",
            RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var server = match.Groups["server"].Value.Trim();
            return ServerHintExclusions.Contains(server) ? null : server;
        }

        return null;
    }

    private string BuildLearnedRulesSection()
    {
        if (_neoCortex is null) return string.Empty;
        try
        {
            var knowledge = _neoCortex.LoadClassifierKnowledge();
            if (knowledge.LearnedRules.Count == 0) return string.Empty;

            var sb = new StringBuilder("Learned from admin corrections (highest priority):\n");
            foreach (var rule in knowledge.LearnedRules.TakeLast(20))
                sb.AppendLine($"- {rule.Rule}");
            sb.AppendLine();
            return sb.ToString();
        }
        catch { return string.Empty; }
    }

    private string BuildSystemPrefix()
    {
        if (!_settings.UseChatSystemPrompt || string.IsNullOrWhiteSpace(_settings.ChatSystemPrompt))
            return string.Empty;

        return $"System guidance:\n{_settings.ChatSystemPrompt!.Trim()}\n\n";
    }
}
