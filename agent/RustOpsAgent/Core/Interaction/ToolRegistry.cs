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

        var message = context.Message.ToLowerInvariant();
        if (route.Intent is AdminIntentType.StatusCheck or AdminIntentType.Troubleshooting)
        {
            if (message.Contains("network") || message.Contains("latency") || message.Contains("throughput") || message.Contains("eth0") || message.Contains("wg1") || message.Contains("wt1"))
            {
                return eligible.FirstOrDefault(h => h.Name == "rust.network.inspect") ?? eligible[0];
            }

            if (message.Contains("plugin") || message.Contains("umod") || message.Contains("oxide"))
            {
                return eligible.FirstOrDefault(h => h.Name == "rust.plugins.verify") ?? eligible[0];
            }

            if (message.Contains("log") || message.Contains("error") || message.Contains("exception"))
            {
                return eligible.FirstOrDefault(h => h.Name == "rust.logs.inspect") ?? eligible[0];
            }
        }

        return route.Intent switch
        {
            AdminIntentType.ServerControl => eligible.FirstOrDefault(h => h.Name == "rust.server.control") ?? eligible[0],
            AdminIntentType.RconCommand => eligible.FirstOrDefault(h => h.Name == "rust.rcon.command") ?? eligible[0],
            AdminIntentType.PlayerLookup => eligible.FirstOrDefault(h => h.Name == "rust.player.lookup") ?? eligible[0],
            AdminIntentType.StatusCheck => eligible.FirstOrDefault(h => h.Name == "rust.status.check") ?? eligible[0],
            AdminIntentType.Troubleshooting => eligible.FirstOrDefault(h => h.Name == "rust.logs.inspect") ?? eligible[0],
            _ => eligible[0]
        };
    }
}
