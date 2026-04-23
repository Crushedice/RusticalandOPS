using System.Text.RegularExpressions;
using System.Text.Json;
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

    public async Task<AdminIntentRoute> ClassifyAsync(string message, ConversationSelectionState state, CancellationToken cancellationToken)
    {
        if (_kernel is null)
        {
            return HeuristicFallback(message, state, "heuristic_no_kernel", false, false);
        }

        var prompt = $$"""
{{BuildSystemPrefix()}}
Return strict JSON only with keys:
intent, confidence, needsClarification, clarificationQuestion, targetRef, slots

intent enum:
chat, status_check, troubleshooting, file_edit, clarification

targetRef enum:
integrations.connector.status, integrations.logs.inspect, agent.chat.reply

slots object keys:
serverName, playerName, commandText, timeRange, severity

Conversation context:
lastServer={{state.LastServerName ?? ""}}
lastIntent={{state.LastIntent ?? ""}}
lastCommand={{state.LastCommandText ?? ""}}

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
            return HeuristicFallback(message, state, "heuristic_after_llm_error", true, false);
        }

        var json = TryExtractJson(raw);
        if (json is null)
        {
            return HeuristicFallback(message, state, "heuristic_after_llm_parse_failure", true, false);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var lowered = message.ToLowerInvariant();
            var intentText = root.TryGetProperty("intent", out var intentNode) ? intentNode.GetString() ?? "clarification" : "clarification";
            var intent = ParseIntent(intentText);
            var confidence = root.TryGetProperty("confidence", out var confidenceNode) && confidenceNode.ValueKind == JsonValueKind.Number
                ? confidenceNode.GetDouble()
                : 0.4;
            var needsClarification = root.TryGetProperty("needsClarification", out var needsNode) && needsNode.ValueKind == JsonValueKind.True;
            var clarification = root.TryGetProperty("clarificationQuestion", out var questionNode) ? questionNode.GetString() : null;
            var targetRef = root.TryGetProperty("targetRef", out var targetNode) ? targetNode.GetString() : null;

            string? serverName = null;
            string? playerName = null;
            string? commandText = null;
            string? timeRange = null;
            string? severity = null;

            if (root.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Object)
            {
                serverName = slots.TryGetProperty("serverName", out var sn) ? sn.GetString() : null;
                playerName = slots.TryGetProperty("playerName", out var pn) ? pn.GetString() : null;
                commandText = slots.TryGetProperty("commandText", out var cn) ? cn.GetString() : null;
                timeRange = slots.TryGetProperty("timeRange", out var tn) ? tn.GetString() : null;
                severity = slots.TryGetProperty("severity", out var sv) ? sv.GetString() : null;
            }

            if (string.IsNullOrWhiteSpace(serverName) && ShouldUseLastServer(message))
            {
                serverName = state.LastServerName;
            }
            if (string.IsNullOrWhiteSpace(serverName))
            {
                serverName = ExtractServerHint(message);
            }

            targetRef = NormalizeTargetRef(targetRef) ?? InferTargetRef(intent, lowered);

            return new AdminIntentRoute(
                intent,
                new AdminIntentSlots(serverName, playerName, commandText, timeRange, severity),
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
            return HeuristicFallback(message, state, "heuristic_after_llm_json_error", true, false);
        }
    }

    private static AdminIntentRoute HeuristicFallback(string message, ConversationSelectionState state, string source, bool llmAttempted, bool llmSucceeded)
    {
        var lowered = message.ToLowerInvariant();
        AdminIntentType intent;
        if (lowered.Contains("log") ||
            lowered.Contains("error") ||
            lowered.Contains("exception") ||
            lowered.Contains("alert") ||
            lowered.Contains("incident") ||
            lowered.Contains("failed"))
            intent = AdminIntentType.Troubleshooting;
        else if (lowered.Contains("status") ||
                 lowered.Contains("health") ||
                 lowered.Contains("connector") ||
                 lowered.Contains("autotask") ||
                 lowered.Contains("datto") ||
                 lowered.Contains("rmm"))
            intent = AdminIntentType.StatusCheck;
        else
            intent = AdminIntentType.Chat;

        var serverName = ShouldUseLastServer(message) ? state.LastServerName : ExtractServerHint(message);
        var targetRef = InferTargetRef(intent, lowered);

        return new AdminIntentRoute(
            intent,
            new AdminIntentSlots(serverName, null, null, null, null),
            0.4,
            false,
            null,
            targetRef,
            source,
            llmAttempted,
            llmSucceeded);
    }

    private static bool ShouldUseLastServer(string message)
    {
        var lowered = message.ToLowerInvariant();
        // Only reuse the last context slot for explicit follow-up phrasing.
        return lowered.Contains("that one") ||
               lowered.Contains("same source") ||
               lowered.Contains("same connector") ||
               lowered.Contains("same one") ||
               lowered.Contains("again") ||
               lowered.Contains("check it");
    }

    private static string? InferTargetRef(AdminIntentType intent, string loweredMessage) =>
        intent switch
        {
            AdminIntentType.Chat or AdminIntentType.Clarification => "agent.chat.reply",
            AdminIntentType.StatusCheck => "integrations.connector.status",
            AdminIntentType.Troubleshooting => "integrations.logs.inspect",
            _ => null
        };

    private static string? NormalizeTargetRef(string? targetRef)
    {
        if (string.IsNullOrWhiteSpace(targetRef))
        {
            return null;
        }

        return targetRef.Trim().ToLowerInvariant() switch
        {
            "connector" or "connectors" or "status" or "status.check" => "integrations.connector.status",
            "log" or "logs" or "logs.inspect" => "integrations.logs.inspect",
            "chat" or "clarification" => "agent.chat.reply",
            _ => targetRef
        };
    }

    private static AdminIntentType ParseIntent(string value) => value.ToLowerInvariant() switch
    {
        "chat" => AdminIntentType.Chat,
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
