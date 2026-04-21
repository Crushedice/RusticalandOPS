using System.Text.Json;
using Microsoft.SemanticKernel;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Core.Interaction;

internal sealed class AdminIntentClassifier : IIntentClassifier
{
    private readonly Kernel? _kernel;

    public AdminIntentClassifier(Kernel? kernel)
    {
        _kernel = kernel;
    }

    public async Task<AdminIntentRoute> ClassifyAsync(string message, ConversationSelectionState state, CancellationToken cancellationToken)
    {
        if (_kernel is null)
        {
            return HeuristicFallback(message, state);
        }

        var prompt = $$"""
Return strict JSON only with keys:
intent, confidence, needsClarification, clarificationQuestion, targetRef, slots

intent enum:
chat, server_control, player_lookup, rcon_command, file_edit, status_check, troubleshooting, clarification

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
            return HeuristicFallback(message, state);
        }

        var json = TryExtractJson(raw);
        if (json is null)
        {
            return HeuristicFallback(message, state);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
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

            return new AdminIntentRoute(
                intent,
                new AdminIntentSlots(serverName, playerName, commandText, timeRange, severity),
                Math.Clamp(confidence, 0.0, 1.0),
                needsClarification,
                clarification,
                targetRef);
        }
        catch
        {
            return HeuristicFallback(message, state);
        }
    }

    private static AdminIntentRoute HeuristicFallback(string message, ConversationSelectionState state)
    {
        var lowered = message.ToLowerInvariant();
        AdminIntentType intent;
        if (lowered.Contains("network") || lowered.Contains("throughput") || lowered.Contains("latency") || lowered.Contains("eth0") || lowered.Contains("wg1") || lowered.Contains("wt1"))
            intent = AdminIntentType.StatusCheck;
        else if (lowered.Contains("plugin") || lowered.Contains("umod") || lowered.Contains("oxide"))
            intent = AdminIntentType.Troubleshooting;
        else if (lowered.Contains("restart") || lowered.Contains("start") || lowered.Contains("stop") || lowered.Contains("kill") || lowered.Contains("update"))
            intent = AdminIntentType.ServerControl;
        else if (lowered.Contains("player") || lowered.Contains("ban"))
            intent = AdminIntentType.PlayerLookup;
        else if (lowered.Contains("rcon") || lowered.Contains("command") || lowered.Contains("say ") || lowered.Contains("global."))
            intent = AdminIntentType.RconCommand;
        else if (lowered.Contains("status") || lowered.Contains("health") || lowered.Contains("logs"))
            intent = AdminIntentType.StatusCheck;
        else if (lowered.Contains("fix") || lowered.Contains("error") || lowered.Contains("fail"))
            intent = AdminIntentType.Troubleshooting;
        else
            intent = AdminIntentType.Chat;

        var serverName = ShouldUseLastServer(message) ? state.LastServerName : null;

        return new AdminIntentRoute(
            intent,
            new AdminIntentSlots(serverName, null, null, null, null),
            0.4,
            false,
            null,
            null);
    }

    private static bool ShouldUseLastServer(string message)
    {
        var lowered = message.ToLowerInvariant();
        return lowered.Contains("that one") || lowered.Contains("again") || lowered.Contains("it ") || lowered.Contains("same");
    }

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
}
