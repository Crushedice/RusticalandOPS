using System.Text;
using Microsoft.SemanticKernel;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Core.Interaction;

internal sealed class ResponseComposer : IResponseComposer
{
    private readonly Kernel? _kernel;
    private readonly LlmSettings _settings;

    public ResponseComposer(Kernel? kernel, LlmSettings settings)
    {
        _kernel = kernel;
        _settings = settings;
    }

    public async Task<ComposedReply> ComposeAsync(ToolExecutionContext context, ToolExecutionResult result, CancellationToken cancellationToken)
    {
        if (string.Equals(result.ErrorCode, "clarification_required", StringComparison.OrdinalIgnoreCase))
        {
            return new ComposedReply(
                result.Message,
                "response-compose-clarification",
                false, false,
                "template_clarification",
                result.Message);
        }

        if (string.Equals(result.ErrorCode, "authoritative_catalog", StringComparison.OrdinalIgnoreCase))
        {
            return new ComposedReply(
                result.Message,
                "response-compose-catalog",
                false, false,
                "template_authoritative_catalog",
                result.Message.Length > 180 ? result.Message[..180] : result.Message);
        }

        if (result.Payload is AggregateStatusPayload aggregatePayload)
        {
            var aggregateMessage = ComposeAggregateStatusMessage(aggregatePayload);
            return new ComposedReply(
                aggregateMessage,
                "response-compose-aggregate",
                false, false,
                "template_aggregate_status",
                aggregateMessage);
        }

        // Bypass LLM for confirmed direct actions (MutatedState=true) — the tool message is
        // authoritative and a small local model hallucinates "already ongoing" style responses
        // when conversation history contains a prior action of the same type.
        if (!result.Success)
        {
            var fallback = ComposeFallback(result);
            return new ComposedReply(
                fallback,
                "response-compose-error",
                false, false,
                "template_error",
                fallback.Length > 180 ? fallback[..180] : fallback);
        }

        if (result.MutatedState)
        {
            return new ComposedReply(
                result.Message,
                "response-compose-direct",
                false, false,
                "template_direct_action",
                result.Message.Length > 180 ? result.Message[..180] : result.Message);
        }

        var payloadPreview = result.Payload?.ToString();
        if (!string.IsNullOrWhiteSpace(payloadPreview) && payloadPreview.Length > 1200)
            payloadPreview = payloadPreview[..1200];

        // If the tool succeeded but returned no payload data, do NOT let the LLM invent content.
        if (result.Success && result.Payload is null && string.IsNullOrWhiteSpace(payloadPreview))
        {
            var message = string.IsNullOrWhiteSpace(result.Message)
                ? "No data was returned."
                : result.Message;
            return new ComposedReply(
                message,
                "response-compose-no-payload",
                false, false,
                "template_no_payload",
                message.Length > 180 ? message[..180] : message);
        }

        if (_kernel is null || !_settings.Enabled)
        {
            var fallback = ComposeFallback(result);
            return new ComposedReply(
                fallback,
                "response-compose-fallback",
                false, false,
                !_settings.Enabled ? "template_llm_disabled" : "template_no_kernel",
                fallback);
        }

        var conversationHistory = BuildConversationHistory(context.SelectionState);
        var memoryContext = BuildMemoryContext(context);
        var systemPrompt = BuildSystemPrompt();

        var prompt = $$"""
{{systemPrompt}}

{{conversationHistory}}
Operational context:
- Admin said: "{{context.Message}}"
- Detected intent: {{context.Route.Intent}}
- Server targeted: {{result.SelectedServer ?? context.Route.Slots.ServerName ?? "none"}}
- Action succeeded: {{result.Success}}
- Tool result: {{result.Message}}
{{(string.IsNullOrWhiteSpace(payloadPreview) ? string.Empty : $"- Data: {payloadPreview}")}}

{{memoryContext}}

Write a direct, natural reply to the admin. Be concise (under 100 words unless details genuinely require more). Do not mention internal routing, error codes, or tool names. Speak like a knowledgeable ops colleague — not a system message.
DO NOT use future tense ("I will", "I'll", "let me check"). Only describe what the tool already did or found.
If the tool returned no data for part of the request, say "not found" for that part - do not speculate.
""";

        try
        {
            var response = await _kernel.InvokePromptAsync(prompt, cancellationToken: cancellationToken);
            var message = (response.GetValue<string>() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                var fallback = ComposeFallback(result);
                return new ComposedReply(fallback, "response-compose", true, false, "llm_empty_response", fallback);
            }

            return new ComposedReply(
                message,
                "response-compose",
                true, true,
                "llm",
                message.Length > 180 ? message[..180] : message);
        }
        catch (Exception ex)
        {
            var fallback = ComposeFallback(result);
            return new ComposedReply(
                fallback,
                "response-compose",
                true, false,
                $"llm_error:{ex.GetType().Name}",
                fallback);
        }
    }

    private string BuildSystemPrompt()
    {
        const string criticalRules = """
CRITICAL RULES - NEVER VIOLATE:
1. Never say you "will", "shall", "are going to", or "will get back to" the admin about anything.
   You are SYNCHRONOUS. If you did something, report it. If you did not do something, say so clearly.
2. Never invent server paths, file contents, config values, or plugin configurations.
   If the tool result contains data, use it. If it does not contain data for something the admin asked, say the data was not found.
3. Oxide/uMod plugin configs are ALWAYS JSON, never YAML. Never generate or describe a YAML config for an Oxide plugin.
4. Never make up file paths. Only report paths that appear verbatim in the tool result payload.
""";

        if (_settings.UseChatSystemPrompt && !string.IsNullOrWhiteSpace(_settings.ChatSystemPrompt))
            return $"{_settings.ChatSystemPrompt!.Trim()}\n\n{criticalRules}";

        return """
You are RustOps, an AI operations assistant managing Rust game servers.
You have direct knowledge of server health, logs, plugins, players, and RCON.
You remember recent context and use it — you don't ask for things you already know.
Communicate like a sharp, experienced ops person: direct answers first, relevant detail after.
Avoid jargon about your own internals. Sound like a colleague, not a status board.
When something fails, say what failed and what to try next.
When something works, confirm it clearly and note anything worth watching.
""" + "\n\n" + criticalRules;
    }

    private static string BuildConversationHistory(ConversationSelectionState state)
    {
        if (state.RecentMessages.Count == 0)
            return string.Empty;

        var sb = new StringBuilder("Recent conversation:\n");
        foreach (var msg in state.RecentMessages.TakeLast(10))
        {
            var label = msg.Role == "assistant" ? "RustOps" : "Admin";
            sb.AppendLine($"[{label}]: {msg.Text}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string BuildMemoryContext(ToolExecutionContext context)
    {
        var sections = new List<string>();
        if (context.PlanningMemoryContext?.HasResults == true)
        {
            sections.Add("Planning memory:\n" + context.PlanningMemoryContext.CompactContext);
        }

        if (context.ExecutionMemoryContext?.HasResults == true)
        {
            sections.Add("Execution memory:\n" + context.ExecutionMemoryContext.CompactContext);
        }

        if (sections.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("\n\n", sections) + "\n";
    }

    private static string ComposeFallback(ToolExecutionResult result)
    {
        if (result.Success)
            return result.Message;

        if (string.Equals(result.ErrorCode, "clarification_required", StringComparison.OrdinalIgnoreCase))
            return result.Message;

        if (string.Equals(result.ErrorCode, "not_configured", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.ErrorCode, "not_allowed", StringComparison.OrdinalIgnoreCase))
            return result.Message;

        return $"That didn't work: {result.Message}";
    }

    private static string ComposeAggregateStatusMessage(AggregateStatusPayload payload)
    {
        var total = payload.TargetServers.Count;
        var parts = new List<string> { $"{payload.OnlineCount}/{total} servers are online." };

        if (payload.OfflineServers.Count > 0)
            parts.Add($"Offline: {string.Join(", ", payload.OfflineServers)}.");

        if (payload.FailedServers.Count > 0)
            parts.Add($"Couldn't check: {string.Join(", ", payload.FailedServers)}.");

        return string.Join(" ", parts);
    }
}
