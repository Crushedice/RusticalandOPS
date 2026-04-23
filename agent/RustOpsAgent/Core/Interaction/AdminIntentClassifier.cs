using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Core.Interaction;

internal sealed class AdminIntentClassifier : IIntentClassifier
{
    private readonly Kernel? _kernel;
    private readonly LlmSettings _settings;

    public AdminIntentClassifier(Kernel? kernel, LlmSettings? settings = null)
    {
        _kernel = kernel;
        _settings = settings ?? new LlmSettings();
    }

    public async Task<AdminIntentRoute> ClassifyAsync(
        string message,
        ConversationSelectionState state,
        IReadOnlyList<string> knownServers,
        CancellationToken cancellationToken)
    {
        if (_kernel is null)
        {
            return HeuristicFallback(message, state, knownServers, "heuristic_no_kernel", false, false);
        }

        var prompt = $$"""
{{BuildSystemPrefix()}}
Return strict JSON only with keys:
intent, confidence, needsClarification, clarificationQuestion, targetRef, slots

intent enum:
chat, server_control, player_lookup, rcon_command, file_edit, status_check, troubleshooting, clarification

targetRef enum:
rust.server.control, rust.player.lookup, rust.rcon.command, rust.status.check, rust.logs.inspect, rust.plugins.verify, rust.network.inspect, rust.chat.reply

slots object keys:
serverName, serverNames, scopeKind, playerName, commandText, timeRange, severity

scopeKind enum:
unspecified, single, all, subset

Rules:
- Interpret all/every/all N/all servers as scopeKind=all.
- Preserve previous intent on correction follow-ups unless user clearly switches tasks.
- For plural status/health questions with no explicit server names, default to all configured servers.

Conversation context:
lastServer={{state.LastServerName ?? ""}}
lastIntent={{state.LastIntent ?? ""}}
lastScopeKind={{state.LastScopeKind}}
lastResolvedServers={{string.Join(", ", state.LastResolvedServers)}}
lastCommand={{state.LastCommandText ?? ""}}
pendingClarificationIntent={{state.PendingClarification?.Intent ?? ""}}
pendingClarificationQuestion={{state.PendingClarification?.Question ?? ""}}
lastUserSummary={{state.LastUserMessageSummary ?? ""}}

Known servers:
{{string.Join(", ", knownServers)}}

Admin message:
{{message}}
""";

        string raw;
        try
        {
            var response = await _kernel.InvokePromptAsync(prompt, cancellationToken: cancellationToken);
            raw = response.GetValue<string>() ?? string.Empty;
        }
        catch
        {
            return HeuristicFallback(message, state, knownServers, "heuristic_after_llm_error", true, false);
        }

        var json = TryExtractJson(raw);
        if (json is null)
        {
            return HeuristicFallback(message, state, knownServers, "heuristic_after_llm_parse_failure", true, false);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var lowered = message.ToLowerInvariant();

            var intentText = root.TryGetProperty("intent", out var intentNode) ? intentNode.GetString() ?? "clarification" : "clarification";
            var intent = ParseIntent(intentText);
            if (ShouldPromoteToStatusIntent(intent, lowered))
            {
                intent = AdminIntentType.StatusCheck;
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
            var scopeKind = ServerScopeKind.Unspecified;
            List<string>? serverNames = null;

            if (root.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Object)
            {
                serverName = slots.TryGetProperty("serverName", out var sn) ? sn.GetString() : null;
                playerName = slots.TryGetProperty("playerName", out var pn) ? pn.GetString() : null;
                commandText = slots.TryGetProperty("commandText", out var cn) ? cn.GetString() : null;
                timeRange = slots.TryGetProperty("timeRange", out var tn) ? tn.GetString() : null;
                severity = slots.TryGetProperty("severity", out var sv) ? sv.GetString() : null;
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

            if (string.IsNullOrWhiteSpace(serverName))
            {
                serverName = ExtractServerHint(message);
            }

            var allowPluralDefaultAll = intent is AdminIntentType.StatusCheck or AdminIntentType.Troubleshooting;
            var scope = ServerScopeResolver.Resolve(
                message,
                knownServers,
                state,
                scopeKind,
                serverNames,
                serverName,
                allowPluralDefaultAll: allowPluralDefaultAll,
                allowLastScopeFallback: true);

            serverNames = scope.Servers.ToList();
            serverName = serverNames.Count == 1 ? serverNames[0] : null;
            scopeKind = scope.ScopeKind;

            targetRef = NormalizeTargetRef(targetRef) ?? InferTargetRef(intent, lowered);
            var needsClarification = llmNeedsClarification || (RequiresServerScope(intent) && scope.RequiresClarification);
            if (!scope.RequiresClarification)
            {
                needsClarification = false;
            }

            clarification = needsClarification
                ? BuildClarificationQuestion(intent, knownServers, clarification)
                : null;

            return new AdminIntentRoute(
                intent,
                new AdminIntentSlots(serverName, playerName, commandText, timeRange, severity, scopeKind, serverNames),
                Math.Clamp(confidence, 0.0, 1.0),
                needsClarification,
                clarification,
                targetRef,
                "llm",
                true,
                true);
        }
        catch
        {
            return HeuristicFallback(message, state, knownServers, "heuristic_after_llm_json_error", true, false);
        }
    }

    private static AdminIntentRoute HeuristicFallback(
        string message,
        ConversationSelectionState state,
        IReadOnlyList<string> knownServers,
        string source,
        bool llmAttempted,
        bool llmSucceeded)
    {
        var lowered = message.ToLowerInvariant();
        var intent = InferHeuristicIntent(lowered);

        if (IsCorrectionFollowUp(lowered) &&
            !HasExplicitIntentSignal(lowered) &&
            TryParseStateIntent(state.LastIntent, out var previousIntent) &&
            previousIntent is not (AdminIntentType.Chat or AdminIntentType.Clarification))
        {
            intent = previousIntent;
        }

        var hintedServer = ExtractServerHint(message);
        var scope = ServerScopeResolver.Resolve(
            message,
            knownServers,
            state,
            ServerScopeKind.Unspecified,
            null,
            hintedServer,
            allowPluralDefaultAll: intent is AdminIntentType.StatusCheck or AdminIntentType.Troubleshooting,
            allowLastScopeFallback: true);

        var selectedServer = scope.Servers.Count == 1 ? scope.Servers[0] : null;
        var needsClarification = RequiresServerScope(intent) && scope.RequiresClarification;
        var targetRef = InferTargetRef(intent, lowered);

        return new AdminIntentRoute(
            intent,
            new AdminIntentSlots(selectedServer, null, null, null, null, scope.ScopeKind, scope.Servers),
            0.4,
            needsClarification,
            needsClarification ? BuildClarificationQuestion(intent, knownServers, null) : null,
            targetRef,
            source,
            llmAttempted,
            llmSucceeded);
    }

    private static AdminIntentType InferHeuristicIntent(string lowered)
    {
        if (lowered.Contains("network") || lowered.Contains("throughput") || lowered.Contains("latency") || lowered.Contains("eth0") || lowered.Contains("wg1") || lowered.Contains("wt1"))
            return AdminIntentType.StatusCheck;
        if (lowered.Contains("plugin") || lowered.Contains("umod") || lowered.Contains("oxide"))
            return AdminIntentType.Troubleshooting;
        if (lowered.Contains("restart") || lowered.Contains("start") || lowered.Contains("stop") || lowered.Contains("kill") || lowered.Contains("update"))
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

    private static bool ShouldPromoteToStatusIntent(AdminIntentType intent, string loweredMessage)
    {
        if (intent is not (AdminIntentType.Chat or AdminIntentType.Clarification))
        {
            return false;
        }

        return loweredMessage.Contains("online", StringComparison.Ordinal) &&
               loweredMessage.Contains("server", StringComparison.Ordinal);
    }

    private static bool HasExplicitIntentSignal(string loweredMessage) =>
        loweredMessage.Contains("restart", StringComparison.Ordinal) ||
        loweredMessage.Contains("start ", StringComparison.Ordinal) ||
        loweredMessage.Contains("stop", StringComparison.Ordinal) ||
        loweredMessage.Contains("kill", StringComparison.Ordinal) ||
        loweredMessage.Contains("update", StringComparison.Ordinal) ||
        loweredMessage.Contains("player", StringComparison.Ordinal) ||
        loweredMessage.Contains("ban", StringComparison.Ordinal) ||
        loweredMessage.Contains("rcon", StringComparison.Ordinal) ||
        loweredMessage.Contains("command", StringComparison.Ordinal);

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
        {
            return false;
        }

        var normalized = intentText.Trim();
        if (Enum.TryParse(normalized, true, out intent))
        {
            return true;
        }

        intent = ParseIntent(normalized.Replace(" ", "_", StringComparison.Ordinal));
        return intent != AdminIntentType.Clarification || normalized.Contains("clarification", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresServerScope(AdminIntentType intent) =>
        intent is
            AdminIntentType.ServerControl or
            AdminIntentType.PlayerLookup or
            AdminIntentType.RconCommand or
            AdminIntentType.StatusCheck or
            AdminIntentType.Troubleshooting;

    private static string BuildClarificationQuestion(AdminIntentType intent, IReadOnlyList<string> knownServers, string? preferredQuestion)
    {
        if (!string.IsNullOrWhiteSpace(preferredQuestion))
        {
            return preferredQuestion.Trim();
        }

        var known = knownServers.Count == 0
            ? "No configured servers are currently available."
            : $"Known servers: {string.Join(", ", knownServers)}.";

        return intent switch
        {
            AdminIntentType.ServerControl => $"Which single server should I target? {known}",
            AdminIntentType.PlayerLookup => $"Which server should I query for players? {known}",
            AdminIntentType.RconCommand => $"Which server should receive the RCON command? {known}",
            _ => $"Which server should I check? You can name one server or say 'all servers'. {known}"
        };
    }

    private static string? InferTargetRef(AdminIntentType intent, string loweredMessage) =>
        intent switch
        {
            AdminIntentType.ServerControl => "rust.server.control",
            AdminIntentType.PlayerLookup => "rust.player.lookup",
            AdminIntentType.RconCommand => "rust.rcon.command",
            AdminIntentType.Chat or AdminIntentType.Clarification => "rust.chat.reply",
            AdminIntentType.StatusCheck or AdminIntentType.Troubleshooting => InferDiagnosticsTarget(loweredMessage),
            _ => null
        };

    private static string InferDiagnosticsTarget(string loweredMessage)
    {
        if (loweredMessage.Contains("network") || loweredMessage.Contains("latency") || loweredMessage.Contains("throughput") || loweredMessage.Contains("eth0") || loweredMessage.Contains("wg1") || loweredMessage.Contains("wt1"))
        {
            return "rust.network.inspect";
        }

        if (loweredMessage.Contains("plugin") || loweredMessage.Contains("umod") || loweredMessage.Contains("oxide"))
        {
            return "rust.plugins.verify";
        }

        if (loweredMessage.Contains("log") || loweredMessage.Contains("error") || loweredMessage.Contains("exception") || loweredMessage.Contains("fail"))
        {
            return "rust.logs.inspect";
        }

        return "rust.status.check";
    }

    private static string? NormalizeTargetRef(string? targetRef)
    {
        if (string.IsNullOrWhiteSpace(targetRef))
        {
            return null;
        }

        return targetRef.Trim().ToLowerInvariant() switch
        {
            "network" or "network.inspect" => "rust.network.inspect",
            "plugins" or "plugins.verify" or "plugin" => "rust.plugins.verify",
            "logs" or "logs.inspect" => "rust.logs.inspect",
            "status" or "status.check" => "rust.status.check",
            "server_control" => "rust.server.control",
            "player_lookup" => "rust.player.lookup",
            "rcon_command" => "rust.rcon.command",
            "chat" or "clarification" => "rust.chat.reply",
            _ => targetRef
        };
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
        _ => AdminIntentType.Clarification
    };

    private static string? TryExtractJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return raw[start..(end + 1)];
    }

    private static string? ExtractServerHint(string message)
    {
        var match = Regex.Match(
            message,
            @"\b(?:from|on|for|in)\s+(?<server>[a-zA-Z0-9][a-zA-Z0-9._-]{2,})\b",
            RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups["server"].Value.Trim();
        }

        return null;
    }

    private string BuildSystemPrefix()
    {
        if (!_settings.UseChatSystemPrompt || string.IsNullOrWhiteSpace(_settings.ChatSystemPrompt))
        {
            return string.Empty;
        }

        return $"System guidance:\n{_settings.ChatSystemPrompt!.Trim()}\n\n";
    }
}
