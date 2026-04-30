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

        if (route.Intent == AdminIntentType.Chat &&
            _handlers.TryGetValue(
                string.Equals(route.TargetRef, "web.search", StringComparison.OrdinalIgnoreCase)
                    ? "web.search"
                    : "rust.chat.reply",
                out var chatHandler))
        {
            return chatHandler;
        }

        // When the LLM provides a targetRef, look it up across ALL handlers first.
        // This handles the case where the LLM classifies intent as server_control but
        // targetRef as rust.rcon.command — the targetRef wins as the stronger signal.
        if (!string.IsNullOrWhiteSpace(route.TargetRef) &&
            _handlers.TryGetValue(route.TargetRef, out var targeted))
        {
            return targeted;
        }

        var eligible = ResolveEligible(route);
        if (eligible.Count == 0)
        {
            return null;
        }

        if (eligible.Count == 1)
        {
            return eligible[0];
        }

        return route.Intent switch
        {
            AdminIntentType.ServerControl => eligible.FirstOrDefault(h => h.Name == "rust.server.control") ?? eligible[0],
            AdminIntentType.RconCommand => eligible.FirstOrDefault(h => h.Name == "rust.rcon.command") ?? eligible[0],
            AdminIntentType.PlayerLookup => eligible.FirstOrDefault(h => h.Name == "rust.player.lookup") ?? eligible[0],
            AdminIntentType.Chat or AdminIntentType.Clarification => eligible.FirstOrDefault(h => h.Name == "rust.chat.reply") ?? eligible[0],
            AdminIntentType.StatusCheck => eligible.FirstOrDefault(h => h.Name == "rust.status.check") ?? eligible[0],
            AdminIntentType.Troubleshooting => eligible.FirstOrDefault(h => h.Name == "rust.logs.inspect") ?? eligible[0],
            _ => eligible[0]
        };
    }
}
