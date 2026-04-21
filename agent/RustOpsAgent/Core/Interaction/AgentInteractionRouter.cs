internal sealed class AgentInteractionRouter
{
    private readonly AdminIntentClassifier _classifier;
    private readonly ToolRegistry _toolRegistry;
    private readonly ActionExecutor _executor;
    private readonly ResponseComposer _responseComposer;

    public AgentInteractionRouter(AdminIntentClassifier classifier, ToolRegistry toolRegistry, ActionExecutor executor, ResponseComposer responseComposer)
    {
        _classifier = classifier;
        _toolRegistry = toolRegistry;
        _executor = executor;
        _responseComposer = responseComposer;
    }

    public async Task<RoutedReply?> TryHandleAsync(AgentInteractionContext context)
    {
        var route = _classifier.Classify(context);
        if (route.Intent == AdminIntentType.chat)
            return null;

        if (!_executor.CanExecute(route))
            return null;

        if (route.NeedsClarification)
        {
            var clarification = "Which server should I target?";
            return _responseComposer.Compose(route, clarification, Array.Empty<string>());
        }

        var eligibleTools = _toolRegistry.GetEligibleTools(route);
        var (reply, serverName) = await _executor.ExecuteAsync(context.AdminId, route, context.Servers, context.UtcNow);
        route.ServerName = serverName ?? route.ServerName;
        return _responseComposer.Compose(route, reply, eligibleTools);
    }
}
