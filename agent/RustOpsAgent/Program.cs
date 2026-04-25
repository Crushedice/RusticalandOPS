using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Sentry;
using RustOpsAgent.Core;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Core.Interaction;
using RustOpsAgent.Domains.Rust;
using RustOpsAgent.Infrastructure;
using RustOpsAgent.Infrastructure.GitOps;
using RustOpsAgent.Infrastructure.Memory;
using AutoPullService = RustOpsAgent.Infrastructure.AutoPullService;

RustOpsEnv.LoadFromDefaultLocations();
using var sentry = RustOpsSentry.Initialize("rustopsagent");

var startup = ParseStartupOptions(args);
var configPath = startup.ConfigPath ?? Path.Combine(AppContext.BaseDirectory, "agentsettings.json");
RustOpsSentry.ConfigureScope(scope =>
{
    scope.SetExtra("configPath", Path.GetFullPath(configPath));
    scope.SetExtra("appBaseDirectory", AppContext.BaseDirectory);
});
RustOpsSentry.AddBreadcrumb($"Agent starting with config path {Path.GetFullPath(configPath)}.", "startup");

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file not found: {configPath}");
    RustOpsSentry.CaptureMessage(
        $"RustOps agent config file not found: {configPath}",
        "startup",
        SentryLevel.Error,
        extras: new Dictionary<string, object?> { ["configPath"] = Path.GetFullPath(configPath) });
    return 1;
}

var config = ConfigLoader.Load(configPath);
RustOpsSentry.ConfigureScope(scope =>
{
    scope.SetTag("llm.enabled", config.Llm.Enabled ? "true" : "false");
    scope.SetTag("gitops.enabled", config.GitOps.Enabled ? "true" : "false");
    scope.SetExtra("neoCortexRoot", config.Memory.NeoCortexRoot);
    scope.SetExtra("chatInboxPath", config.Inbox.ChatInboxPath);
    scope.SetExtra("decisionInboxPath", config.Inbox.DecisionInboxPath);
    scope.SetExtra("feedbackInboxPath", config.Inbox.FeedbackInboxPath);
    scope.SetExtra("messageOutboxPath", config.Outbox.MessageOutboxPath);
});
RustOpsSentry.AddBreadcrumb("Agent configuration loaded.", "startup");
Directory.CreateDirectory(config.Memory.NeoCortexRoot);
Directory.CreateDirectory(config.Inbox.ChatInboxPath);
Directory.CreateDirectory(config.Inbox.DecisionInboxPath);
Directory.CreateDirectory(config.Inbox.FeedbackInboxPath);
Directory.CreateDirectory(config.Outbox.MessageOutboxPath);

Console.WriteLine($"[agent] Config loaded. API={config.Api.BaseUrl} LLM={config.Llm.Enabled}({config.Llm.Provider})");
Console.WriteLine($"[agent] Paths: state={config.Memory.StatePath}");
Console.WriteLine($"[agent] Paths: chat-inbox={config.Inbox.ChatInboxPath}");
Console.WriteLine($"[agent] Paths: outbox={config.Outbox.MessageOutboxPath}");

var neoCortex = new NeoCortexStore(config.Memory.NeoCortexRoot, config.Memory.StatePath);
neoCortex.EnsureMigrated();
var legacyState = new LegacyAgentStateStore(config.Memory.StatePath);

var kernel = BuildKernel(config.Llm);
if (kernel is null)
{
    if (config.Llm.Enabled)
    {
        Console.WriteLine("[agent] WARNING: LLM kernel failed to initialize. Check RUSTOPS_LLM_BASE_URL and RUSTOPS_LLM_MODEL env vars. Running without LLM — responses will use heuristics only.");
    }
    else
    {
        Console.WriteLine("[agent] LLM disabled by config (llm.enabled=false).");
    }
    config.Llm.Enabled = false;
}
else
{
    Console.WriteLine($"[agent] LLM kernel ready. provider={config.Llm.Provider} model={config.Llm.Model} recommendations={config.Llm.UseForRecommendations}");
}

// Deep kernel: used for background analysis (incident review, classifier evolution, sentiment).
// Falls back to the fast kernel if llmDeep is not configured.
var deepKernelConfigured = !string.IsNullOrWhiteSpace(config.LlmDeep.BaseUrl)
    && !RustOpsEnv.HasUnresolvedPlaceholder(config.LlmDeep.BaseUrl)
    && config.LlmDeep.Enabled;
var deepKernel = deepKernelConfigured ? BuildKernel(config.LlmDeep) : null;
if (deepKernel is not null)
    Console.WriteLine($"[agent] Deep LLM kernel ready. provider={config.LlmDeep.Provider} model={config.LlmDeep.Model}");
else
    Console.WriteLine("[agent] Deep LLM not configured — background tasks will share the fast kernel.");
deepKernel ??= kernel;

// Compose kernel: used for response generation (natural language, personality).
// Falls back to the fast kernel if llmCompose is not configured.
var composeKernelConfigured = !string.IsNullOrWhiteSpace(config.LlmCompose.BaseUrl)
    && !RustOpsEnv.HasUnresolvedPlaceholder(config.LlmCompose.BaseUrl)
    && config.LlmCompose.Enabled;
var composeKernel = composeKernelConfigured ? BuildKernel(config.LlmCompose) : null;
if (composeKernel is not null)
    Console.WriteLine($"[agent] Compose LLM kernel ready. provider={config.LlmCompose.Provider} model={config.LlmCompose.Model}");
else
    Console.WriteLine("[agent] Compose LLM not configured — response composition will use the fast kernel.");
composeKernel ??= kernel;
var effectiveComposeSettings = composeKernelConfigured ? config.LlmCompose : config.Llm;

var classifier = new AdminIntentClassifier(kernel, config.Llm, neoCortex);
using var apiClient = new RustOpsApiClient(config.Api);

if (!string.Equals(config.GitOps.PushBranchPrefix, "agent/", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("gitOps.pushBranchPrefix must be agent/ to satisfy branch safety policy.");
}

var gitOps = new GitOpsService(config.GitOps);
var autoPull = new AutoPullService(config.AutoPull);

if (config.PluginUpdates.DownloadEnabled)
{
    Directory.CreateDirectory(config.PluginUpdates.StagingPath);
}

var serverControlOutboxPath = config.Outbox.MessageOutboxPath;
Action<string, string, string?> serverStatusNotifier = (adminId, message, serverName) =>
{
    var id = Guid.NewGuid().ToString("N");
    var payload = new AdapterMessage
    {
        Id = id,
        AdminId = adminId,
        Kind = "chat-reply",
        Audience = "admins",
        TargetAdminId = adminId,
        ServerName = serverName ?? string.Empty,
        Message = message,
        CreatedAtUtc = DateTime.UtcNow
    };
    Directory.CreateDirectory(serverControlOutboxPath);
    File.WriteAllText(
        Path.Combine(serverControlOutboxPath, $"{payload.CreatedAtUtc:yyyyMMddHHmmssfff}-chat-reply-{id}.json"),
        System.Text.Json.JsonSerializer.Serialize(payload, JsonDefaults.Default));
};

var handlers = new List<IToolHandler>
{
    new RustServerControlToolHandler(apiClient, serverStatusNotifier),
    new RustStatusToolHandler(apiClient),
    new RustPlayerLookupToolHandler(apiClient),
    new RustRconToolHandler(apiClient, neoCortex, config.CommandExecution),
    new RustLogsToolHandler(apiClient, neoCortex),
    new RustPluginToolHandler(apiClient, config.PluginUpdates),
    new RustNetworkToolHandler(apiClient, config.Network.TrackedInterfaces),
    new RustFileEditToolHandler(apiClient, gitOps, config.GitOps),
    new RustChatToolHandler(neoCortex, autoPull)
};

var registry = new ToolRegistry(handlers);
var executor = new ActionExecutor(registry);
var composer = new ResponseComposer(composeKernel, effectiveComposeSettings);

var runtime = new AgentRuntime(config, classifier, executor, composer, neoCortex, legacyState, gitOps, autoPull, apiClient, deepKernel);

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
    RustOpsSentry.AddBreadcrumb("Agent shutdown requested via cancellation.", "runtime");
    return 0;
}
catch (Exception ex)
{
    RustOpsSentry.CaptureException(
        ex,
        "RustOps agent terminated unexpectedly.",
        "runtime",
        extras: new Dictionary<string, object?> { ["configPath"] = Path.GetFullPath(configPath) });
    return 1;
}
finally
{
    await RustOpsSentry.FlushAsync();
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
