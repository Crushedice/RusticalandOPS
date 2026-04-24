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
