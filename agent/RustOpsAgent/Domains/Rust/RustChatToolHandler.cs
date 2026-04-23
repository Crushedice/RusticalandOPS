using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Infrastructure.Memory;

namespace RustOpsAgent.Domains.Rust;

internal sealed class RustChatToolHandler : IToolHandler
{
    private readonly NeoCortexStore _memory;

    public RustChatToolHandler(NeoCortexStore memory)
    {
        _memory = memory;
    }

    public string Name => "rust.chat.reply";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.Chat, AdminIntentType.Clarification };

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Route.Intent == AdminIntentType.Clarification)
        {
            var question = context.Route.ClarificationQuestion ?? "Please clarify what action you want and which server it targets.";
            return Task.FromResult(new ToolExecutionResult(true, question, context.SelectionState.LastServerName));
        }

        // Build a context payload so the LLM composer has real operational data to work with.
        object? payload = null;
        try
        {
            var ops = _memory.LoadOperations();
            var recentActions = ops.RecentActions
                .TakeLast(5)
                .Select(a => $"{a.Intent} on {a.ServerName ?? "?"}: {a.Result}")
                .ToList();

            payload = new
            {
                recentActions,
                lastServer = context.SelectionState.LastServerName ?? "none",
                lastIntent = context.SelectionState.LastIntent ?? "none",
                llmEnabled = ops.RuntimeStatus?.LlmEnabled ?? false,
                lastLlmInteractionAtUtc = ops.RuntimeStatus?.LastLlmInteractionAtUtc?.ToString("O")
            };
        }
        catch
        {
            // If memory load fails, compose with no payload — LLM will still work without it.
        }

        return Task.FromResult(new ToolExecutionResult(
            true,
            "Ready to assist with Rust server operations.",
            context.SelectionState.LastServerName,
            Payload: payload));
    }
}
