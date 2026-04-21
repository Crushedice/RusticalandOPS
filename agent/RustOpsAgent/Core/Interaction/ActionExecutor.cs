using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Core.Interaction;

internal sealed class ActionExecutor : IActionExecutor
{
    private readonly ToolRegistry _registry;

    public ActionExecutor(ToolRegistry registry)
    {
        _registry = registry;
    }

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Route.NeedsClarification)
        {
            return new ToolExecutionResult(
                false,
                context.Route.ClarificationQuestion ?? "Please clarify the target server or command.",
                context.SelectionState.LastServerName,
                false,
                "clarification_required");
        }

        var handler = _registry.ResolveSingle(context);
        if (handler is null)
        {
            return new ToolExecutionResult(false, "No eligible tool found for this intent.", context.SelectionState.LastServerName, false, "no_tool");
        }

        return await handler.ExecuteAsync(context, cancellationToken);
    }
}
