internal sealed class ActionExecutor
{
    private readonly Func<string, ChatInterpretation, List<ServerSnapshot>, DateTime, Task<string>> _executePlanAsync;
    private readonly Func<string, string, List<ServerSnapshot>, DateTime, Task<string?>> _tryDirectAsync;

    public ActionExecutor(
        Func<string, ChatInterpretation, List<ServerSnapshot>, DateTime, Task<string>> executePlanAsync,
        Func<string, string, List<ServerSnapshot>, DateTime, Task<string?>> tryDirectAsync)
    {
        _executePlanAsync = executePlanAsync;
        _tryDirectAsync = tryDirectAsync;
    }

    public bool CanExecute(AdminIntentRoute route)
    {
        return route.Intent switch
        {
            AdminIntentType.server_control => true,
            AdminIntentType.status_check => true,
            AdminIntentType.troubleshooting => true,
            AdminIntentType.rcon_command => !string.IsNullOrWhiteSpace(route.CommandText) &&
                route.CommandText.StartsWith("run command ", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public async Task<(string reply, string? serverName)> ExecuteAsync(string adminId, AdminIntentRoute route, List<ServerSnapshot> servers, DateTime utcNow)
    {
        var plan = new ChatInterpretation
        {
            Intent = route.Intent switch
            {
                AdminIntentType.server_control => InferLifecycleIntent(route.CommandText ?? string.Empty),
                AdminIntentType.status_check => "server-status",
                AdminIntentType.troubleshooting => "server-health",
                _ => "unknown"
            },
            ServerName = route.ServerName
        };

        if (route.Intent == AdminIntentType.rcon_command && !string.IsNullOrWhiteSpace(route.CommandText))
        {
            var handled = await _tryDirectAsync(adminId, route.CommandText, servers, utcNow);
            if (!string.IsNullOrWhiteSpace(handled))
                return (handled, route.ServerName);

            return ("Use: run command <command text> on <server>", route.ServerName);
        }

        var reply = await _executePlanAsync(adminId, plan, servers, utcNow);
        return (reply, plan.ServerName);
    }

    private static string InferLifecycleIntent(string text)
    {
        var lowered = text.ToLowerInvariant();
        if (lowered.Contains("restart")) return "restart-server";
        if (lowered.Contains("stop")) return "stop-server";
        if (lowered.Contains("start")) return "start-server";
        return "server-status";
    }
}
