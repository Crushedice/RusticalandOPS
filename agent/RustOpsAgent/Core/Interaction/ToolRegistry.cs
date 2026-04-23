using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Core.Interaction;

internal sealed class ToolRegistry
{
    private readonly Dictionary<string, IToolHandler> _handlers;

    public ToolRegistry(IEnumerable<IToolHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IToolHandler> ResolveEligible(AdminIntentRoute route)
    {
        return _handlers.Values
            .Where(h => h.EligibleIntents.Contains(route.Intent))
            .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IToolHandler? ResolveSingle(ToolExecutionContext context)
    {
        var route = context.Route;
        var eligible = ResolveEligible(route);
        if (eligible.Count == 0)
        {
            return null;
        }

        if (eligible.Count == 1)
        {
            return eligible[0];
        }

        if (!string.IsNullOrWhiteSpace(route.TargetRef))
        {
            var hinted = eligible.FirstOrDefault(h => string.Equals(h.Name, route.TargetRef, StringComparison.OrdinalIgnoreCase));
            if (hinted is not null)
            {
                return hinted;
            }
        }

        return route.Intent switch
        {
            AdminIntentType.Chat or AdminIntentType.Clarification => eligible.FirstOrDefault(h => h.Name == "agent.chat.reply") ?? eligible[0],
            AdminIntentType.StatusCheck => eligible.FirstOrDefault(h => h.Name == "integrations.connector.status") ?? eligible[0],
            AdminIntentType.Troubleshooting => eligible.FirstOrDefault(h => h.Name == "integrations.logs.inspect") ?? eligible[0],
            _ => eligible[0]
        };
    }
}
