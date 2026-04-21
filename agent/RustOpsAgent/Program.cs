using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using RustOpsAgent.Core;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Core.Interaction;
using RustOpsAgent.Domains.Rust;
using RustOpsAgent.Infrastructure;
using RustOpsAgent.Infrastructure.GitOps;
using RustOpsAgent.Infrastructure.Memory;

RustOpsEnv.LoadFromDefaultLocations();
using var sentry = RustOpsSentry.Initialize("rustopsagent");

var startup = ParseStartupOptions(args);
var configPath = startup.ConfigPath ?? Path.Combine(AppContext.BaseDirectory, "agentsettings.json");

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file not found: {configPath}");
    return 1;
}

var config = ConfigLoader.Load(configPath);
Directory.CreateDirectory(config.Memory.NeoCortexRoot);
Directory.CreateDirectory(config.Inbox.ChatInboxPath);
Directory.CreateDirectory(config.Inbox.DecisionInboxPath);
Directory.CreateDirectory(config.Inbox.FeedbackInboxPath);
Directory.CreateDirectory(config.Outbox.MessageOutboxPath);

var neoCortex = new NeoCortexStore(config.Memory.NeoCortexRoot, config.Memory.StatePath);
neoCortex.EnsureMigrated();
var legacyState = new LegacyAgentStateStore(config.Memory.StatePath);

var kernel = BuildKernel(config.Llm);
if (kernel is null)
{
    config.Llm.Enabled = false;
}
var classifier = new AdminIntentClassifier(kernel);
using var apiClient = new RustOpsApiClient(config.Api);

var handlers = new List<IToolHandler>
{
    new RustServerControlToolHandler(apiClient),
    new RustStatusToolHandler(apiClient),
    new RustPlayerLookupToolHandler(apiClient),
    new RustRconToolHandler(apiClient),
    new RustLogsToolHandler(apiClient, neoCortex),
    new RustPluginToolHandler(apiClient),
    new RustNetworkToolHandler(apiClient),
    new RustChatToolHandler()
};

var registry = new ToolRegistry(handlers);
var executor = new ActionExecutor(registry);
var composer = new ResponseComposer();

if (!string.Equals(config.GitOps.PushBranchPrefix, "agent/", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("gitOps.pushBranchPrefix must be agent/ to satisfy branch safety policy.");
}

_ = new GitOpsService(config.GitOps);

var runtime = new AgentRuntime(config, classifier, executor, composer, neoCortex, legacyState);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    runtime.RequestStop();
};

try
{
    await runtime.RunAsync(CancellationToken.None);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}

static Kernel? BuildKernel(LlmSettings settings)
{
    if (!settings.Enabled)
    {
        return null;
    }

    if (string.IsNullOrWhiteSpace(settings.BaseUrl) || RustOpsEnv.HasUnresolvedPlaceholder(settings.BaseUrl))
    {
        return null;
    }

    if (!Uri.TryCreate(settings.BaseUrl.TrimEnd('/'), UriKind.Absolute, out var endpoint))
    {
        return null;
    }

    var builder = Kernel.CreateBuilder();
    var model = string.IsNullOrWhiteSpace(settings.Model) ? "gpt-4o-mini" : settings.Model;
    var apiKey = string.IsNullOrWhiteSpace(settings.ApiKey) ? "not-required" : settings.ApiKey;

    builder.AddOpenAIChatCompletion(
        modelId: model,
        apiKey: apiKey,
        endpoint: endpoint,
        serviceId: "intent-router");

    return builder.Build();
}

static AgentStartupOptions ParseStartupOptions(string[] args)
{
    var startup = new AgentStartupOptions();
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            startup.ConfigPath = args[++i];
        }
    }

    return startup;
}

internal sealed class AgentStartupOptions
{
    public string? ConfigPath { get; set; }
}
