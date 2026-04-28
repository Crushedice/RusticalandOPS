using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Core.Interaction;

internal sealed class ActionExecutor : IActionExecutor
{
    private readonly ToolRegistry _registry;
    private readonly ISemanticMemoryService? _semanticMemory;

    public ActionExecutor(ToolRegistry registry, ISemanticMemoryService? semanticMemory = null)
    {
        _registry = registry;
        _semanticMemory = semanticMemory;
    }

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (_semanticMemory is not null && context.ExecutionMemoryContext is null)
        {
            Console.WriteLine("[memory] executor fallback recall invoked because execution context was missing");
            var executionMemory = await _semanticMemory.RecallForExecutionAsync(context, cancellationToken);
            executionMemory = executionMemory with { RetrievalOrigin = "executor-fallback" };
            context = context with { ExecutionMemoryContext = executionMemory };
        }

        var avoidRepeatResult = ShouldAvoidRepeatedFailure(context);
        if (avoidRepeatResult is not null)
        {
            return avoidRepeatResult;
        }

        if (context.Route.NeedsClarification && IsBlockingClarification(context.Route))
        {
            return new ToolExecutionResult(
                false,
                context.Route.ClarificationQuestion ?? "Please clarify the target server or command.",
                context.SelectionState.LastServerName,
                false,
                "clarification_required",
                SelectedServers: context.SelectionState.LastResolvedServers,
                ScopeKind: context.Route.Slots.ScopeKind);
        }

        var handler = _registry.ResolveSingle(context);
        if (handler is null)
        {
            return new ToolExecutionResult(false, "No eligible tool found for this intent.", context.SelectionState.LastServerName, false, "no_tool");
        }

        return await handler.ExecuteAsync(context, cancellationToken);
    }

    private static ToolExecutionResult? ShouldAvoidRepeatedFailure(ToolExecutionContext context)
    {
        var executionMemory = context.ExecutionMemoryContext;
        if (executionMemory is null || !executionMemory.HasResults)
        {
            return null;
        }

        var repeatedFailures = executionMemory.Results
            .Where(item => item.MemoryRecord.Type == MemoryRecordType.Failure && item.FinalScore >= 0.82)
            .Take(2)
            .ToList();
        if (repeatedFailures.Count < 2)
        {
            return null;
        }

        var summary = repeatedFailures[0].MemoryRecord.Summary;
        return new ToolExecutionResult(
            false,
            $"I’m avoiding a repeated failure for now. Similar attempts have failed before: {summary}",
            context.Route.Slots.ServerName ?? context.SelectionState.LastServerName,
            false,
            "known_failure_repeated",
            SelectedServers: context.Route.Slots.ServerNames ?? context.SelectionState.LastResolvedServers,
            ScopeKind: context.Route.Slots.ScopeKind);
    }

    private static bool IsBlockingClarification(AdminIntentRoute route)
    {
        if (route.Intent is AdminIntentType.ServerControl or AdminIntentType.RconCommand or AdminIntentType.PlayerLookup)
        {
            return true;
        }

        if (route.Intent is AdminIntentType.StatusCheck or AdminIntentType.Troubleshooting or AdminIntentType.FileEdit)
        {
            return false;
        }

        return route.Intent == AdminIntentType.Clarification;
    }
}
