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

        LogRepeatedFailureHints(context);

        // Multi-step sequence: execute steps in order, stop on first failure unless the step
        // is non-blocking. Each step runs as its own ToolExecutionContext with a synthesised
        // route so the existing per-handler logic doesn't need to know about sequencing.
        //
        // EXCEPTION: ScheduleTask carries its action steps as a payload to be stored, NOT
        // executed now. The scheduler handler reads route.Steps to persist them.
        if (context.Route.Steps is { Count: > 1 } steps &&
            context.Route.Intent != AdminIntentType.ScheduleTask)
        {
            return await ExecuteSequenceAsync(context, steps, cancellationToken);
        }

        var handler = _registry.ResolveSingle(context);
        if (handler is null)
        {
            return new ToolExecutionResult(false, "No eligible tool found for this intent.", context.SelectionState.LastServerName, false, "no_tool");
        }

        return await handler.ExecuteAsync(context, cancellationToken);
    }

    private async Task<ToolExecutionResult> ExecuteSequenceAsync(
        ToolExecutionContext context,
        IReadOnlyList<AdminIntentStep> steps,
        CancellationToken cancellationToken)
    {
        var summaries = new List<string>();
        var allSucceeded = true;
        string? lastServer = context.SelectionState.LastServerName;
        ToolExecutionResult? last = null;

        Console.WriteLine($"[executor] running sequence of {steps.Count} steps for admin {context.AdminId}.");

        for (var i = 0; i < steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = steps[i];
            var stepRoute = new AdminIntentRoute(
                step.Intent,
                step.Slots,
                context.Route.Confidence,
                NeedsClarification: false,
                ClarificationQuestion: null,
                step.TargetRef,
                context.Route.PlanningMemoryContext,
                context.Route.ClassifierSource,
                context.Route.LlmAttempted,
                context.Route.LlmSucceeded,
                Steps: null);

            var stepContext = context with
            {
                Route = stepRoute,
                ExecutionMemoryContext = null
            };

            var handler = _registry.ResolveSingle(stepContext);
            if (handler is null)
            {
                allSucceeded = false;
                summaries.Add($"step {i + 1}/{steps.Count} ({step.Intent}): no eligible tool — sequence aborted.");
                last = new ToolExecutionResult(false, summaries[^1], lastServer, false, "no_tool");
                break;
            }

            ToolExecutionResult result;
            try
            {
                result = await handler.ExecuteAsync(stepContext, cancellationToken);
            }
            catch (Exception ex)
            {
                allSucceeded = false;
                summaries.Add($"step {i + 1}/{steps.Count} ({step.Intent}): exception — {ex.Message}");
                last = new ToolExecutionResult(false, summaries[^1], lastServer, false, "exception");
                break;
            }

            last = result;
            lastServer = result.SelectedServer ?? lastServer;
            summaries.Add($"step {i + 1}/{steps.Count} ({step.Intent}{(step.Slots.ServerName is null ? string.Empty : $" → {step.Slots.ServerName}")}): {(result.Success ? "ok" : "FAILED")} — {result.Message}");
            if (!result.Success)
            {
                allSucceeded = false;
                Console.WriteLine($"[executor] step {i + 1} failed; aborting remaining {steps.Count - i - 1} step(s).");
                break;
            }
        }

        var summary = string.Join("\n", summaries);
        return new ToolExecutionResult(
            allSucceeded,
            summary,
            lastServer,
            MutatedState: last?.MutatedState ?? false,
            ErrorCode: allSucceeded ? null : (last?.ErrorCode ?? "sequence_failed"),
            Payload: summary,
            SelectedServers: last?.SelectedServers,
            ScopeKind: last?.ScopeKind ?? ServerScopeKind.Unspecified);
    }

    private static void LogRepeatedFailureHints(ToolExecutionContext context)
    {
        var executionMemory = context.ExecutionMemoryContext;
        if (executionMemory is null || !executionMemory.HasResults)
        {
            return;
        }

        var repeatedFailures = executionMemory.Results
            .Where(item => item.MemoryRecord.Type == MemoryRecordType.Failure && item.FinalScore >= 0.82)
            .Take(2)
            .ToList();

        if (repeatedFailures.Count >= 2)
        {
            Console.WriteLine($"[memory] repeated failure hints present; continuing execution. Top hint: {repeatedFailures[0].MemoryRecord.Summary}");
        }
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
