using System.Text.Json;
﻿using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Sentry;
using RustOpsAgent.Core;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Core.Interaction;
using RustOpsAgent.Domains.Rust;
using RustOpsAgent.Domains.Rust.Rcon;
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
if (!config.Memory.WriteEnabled)
{
    Console.WriteLine("[memory] WARNING: Memory writes are disabled (memory.writeEnabled=false). Agent will not learn from interactions.");
}

if (config.Memory.MaxWritesPerWorkflowStep <= 0)
{
    Console.WriteLine("[memory] WARNING: Memory writes are disabled (memory.maxWritesPerWorkflowStep<=0). Agent will not learn from interactions.");
}

var neoCortex = new NeoCortexStore(config.Memory.NeoCortexRoot, config.Memory.StatePath);
neoCortex.EnsureMigrated();
var legacyState = new LegacyAgentStateStore(config.Memory.StatePath);
var semanticStore = BuildMemoryStore(config.Memory);
var embeddingProvider = BuildEmbeddingProvider(config.Memory);
var semanticMemory = new SemanticMemoryService(
    config.Memory,
    semanticStore,
    embeddingProvider,
    config.Memory.StatePath,
    config.Memory.NeoCortexRoot);
var memoryImport = new MemoryImportService(config.Memory, semanticMemory, semanticStore);
if (await TryHandleMemoryMaintenanceAsync(startup, semanticMemory, memoryImport))
{
    return 0;
}

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

var classifier = new AdminIntentClassifier(kernel, config.Llm, neoCortex, semanticMemory);
using var apiClient = new RustOpsApiClient(config.Api);
var pluginReferenceStore = new SqlitePluginReferenceIndexStore(config.PluginUpdates.ReferenceIndexDatabasePath);
var pluginReferenceIndexer = new PluginReferenceIndexer(apiClient, pluginReferenceStore, semanticMemory);
var catalogIndexStore = new SqliteServerCatalogIndexStore(config.PluginUpdates.ReferenceIndexDatabasePath);

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

// Shared server knowledge catalog for convar/command lookups across chat and RCON handlers
var serverKnowledge = new ServerKnowledgeCatalog();
var catalogSnapshot = serverKnowledge.GetSnapshot();
Console.WriteLine($"[agent] Server knowledge loaded: {catalogSnapshot.Variables.Count} variables, {catalogSnapshot.Commands.Count} commands");
if (catalogSnapshot.Variables.Count > 0 || catalogSnapshot.Commands.Count > 0)
{
    await catalogIndexStore.SyncAsync(
        catalogSnapshot.Variables.Values.ToList(),
        catalogSnapshot.Commands.Values.ToList(),
        CancellationToken.None);
    Console.WriteLine($"[agent] Catalog index synced: {catalogSnapshot.Variables.Count} convars, {catalogSnapshot.Commands.Count} commands");
}

// Register remote server RCON credentials so the agent can connect directly to them.
// Local servers are also warmed up eagerly below using credentials from their config files.
var remoteServerNamesForWarmup = new List<string>();
try
{
    using var rconConfigResponse = await apiClient.GetAsync("/servers/remote/rcon-config", CancellationToken.None);
    var root = rconConfigResponse.RootElement;
    var count = 0;
    if (root.ValueKind == JsonValueKind.Array)
    {
        foreach (var entry in root.EnumerateArray())
        {
            var name = entry.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
            var ip = entry.TryGetProperty("rconIp", out var ipEl) ? ipEl.GetString()?.Trim() : null;
            var port = entry.TryGetProperty("rconPort", out var portEl) && portEl.ValueKind == JsonValueKind.Number ? portEl.GetInt32() : 0;
            var password = entry.TryGetProperty("rconPassword", out var pwdEl) ? pwdEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(password))
            {
                Console.WriteLine($"[agent] Skipping remote RCON entry: missing name/ip/password.");
                continue;
            }
            if (port <= 0 || port > 65535)
            {
                Console.WriteLine($"[agent] Skipping remote RCON for '{name}': port {port} is out of range (1–65535).");
                continue;
            }
            // Build the WebRCON URI. Host stays raw (Uri handles IPv4/IPv6/hostnames),
            // password is path-escaped because some Rust servers ship long random
            // passwords with characters like '+' or '/'.
            Uri uri;
            try
            {
                uri = new Uri($"ws://{ip}:{port}/{Uri.EscapeDataString(password.Trim())}");
            }
            catch (UriFormatException ex)
            {
                Console.WriteLine($"[agent] Skipping remote RCON for '{name}': invalid host '{ip}' ({ex.Message}).");
                continue;
            }
            RustDirectRconHelper.RegisterRemoteServer(name, uri, password.Trim());
            remoteServerNamesForWarmup.Add(name);
            count++;
        }
    }
    Console.WriteLine($"[agent] Registered direct RCON for {count} remote server(s).");
}
catch (Exception ex)
{
    Console.WriteLine($"[agent] WARNING: Could not load remote RCON config from API: {ex.Message}");
    RustOpsSentry.CaptureException(ex, "Failed to load remote RCON config", "startup");
}

var handlers = new List<IToolHandler>
{
    new RustServerControlToolHandler(apiClient, serverStatusNotifier, semanticMemory),
    new RustStatusToolHandler(apiClient, semanticMemory),
    new RustPlayerLookupToolHandler(apiClient),
    new RustRconToolHandler(apiClient, neoCortex, config.CommandExecution, serverKnowledge, semanticMemory),
    new RustLogsToolHandler(apiClient, neoCortex, semanticMemory),
    new RustPluginToolHandler(apiClient, config.PluginUpdates, semanticMemory, pluginReferenceIndexer),
    new RustNetworkToolHandler(apiClient, config.Network.TrackedInterfaces),
    new RustFileEditToolHandler(apiClient, gitOps, config.GitOps, semanticMemory),
    new RustChatToolHandler(neoCortex, semanticMemory, autoPull, serverKnowledge, memoryImport, pluginReferenceIndexer, catalogIndexStore),
    new RustServerManagementToolHandler(apiClient)
};

if (config.WebSearch.Enabled)
{
    handlers.Add(new WebSearchToolHandler());
}

var registry = new ToolRegistry(handlers);
var executor = new ActionExecutor(registry, semanticMemory);
var composer = new ResponseComposer(composeKernel, effectiveComposeSettings);

var runtime = new AgentRuntime(config, classifier, executor, composer, neoCortex, legacyState, semanticMemory, gitOps, autoPull, apiClient, deepKernel);

// Eagerly open RCON sessions to remote servers so the chat-monitor subscriber (registered
// inside AgentRuntime's ctor) starts receiving unsolicited messages immediately. Without
// this, sessions are only created lazily on the first command — meaning chat from before
// the first admin interaction would be lost.
//
// Failed warmups are NOT fatal — the server may be temporarily down, behind a firewall,
// or have WebRCON disabled. AgentRuntime periodically re-warms (see WarmupRemoteSessionsAsync)
// so chat starts flowing as soon as the server becomes reachable.
if (remoteServerNamesForWarmup.Count > 0)
{
    Console.WriteLine($"[agent] Warming up RCON for {remoteServerNamesForWarmup.Count} remote server(s)...");
    var warmupTasks = remoteServerNamesForWarmup
        .Select(async name =>
        {
            var outcome = await RustDirectRconHelper.WarmupAsync(name, CancellationToken.None);
            Console.WriteLine($"[agent] RCON remote warmup {name}: {outcome.Message}");
        })
        .ToList();
    // Wait for remote warmups to complete so chat monitoring is active from startup.
    // Timeout after 30s per server to avoid blocking startup forever if servers are unreachable.
    await Task.WhenAny(Task.WhenAll(warmupTasks), Task.Delay(TimeSpan.FromSeconds(Math.Min(300, remoteServerNamesForWarmup.Count * 30))));
}

// Eagerly open RCON sessions for LOCAL servers so chat-monitor receives unsolicited
// messages (chat, console events) from startup — not only after the first admin command.
// Local servers derive their RCON credentials from their rustmgr config files.
try
{
    using var serversResponse = await apiClient.GetAsync("/servers", CancellationToken.None);
    var serversRoot = serversResponse.RootElement;
    if (serversRoot.ValueKind == JsonValueKind.Array)
    {
        var localServerNames = serversRoot.EnumerateArray()
            .Where(e => !e.TryGetProperty("remote", out var r) || r.ValueKind == JsonValueKind.False)
            .Select(e => e.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();

        if (localServerNames.Count > 0)
        {
            Console.WriteLine($"[agent] Warming up RCON for {localServerNames.Count} local server(s)...");
            var localWarmupTasks = localServerNames
                .Select(async name =>
                {
                    var outcome = await RustDirectRconHelper.WarmupAsync(name, CancellationToken.None);
                    Console.WriteLine($"[agent] RCON local warmup {name}: {outcome.Message}");
                })
                .ToList();
            // Wait for local warmups with timeout so chat monitoring is active from startup.
            await Task.WhenAny(Task.WhenAll(localWarmupTasks), Task.Delay(TimeSpan.FromSeconds(Math.Min(300, localServerNames.Count * 30))));
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[agent] WARNING: Could not warm up local RCON sessions: {ex.Message}");
    RustOpsSentry.CaptureException(ex, "Failed to warm up local RCON sessions", "startup");
}

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

static IEmbeddingProvider? BuildEmbeddingProvider(MemorySettings settings)
{
    if (!settings.SearchEnabled && !settings.WriteEnabled)
    {
        return null;
    }

    if (!string.Equals(settings.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"[memory] Unsupported provider '{settings.Provider}'. Semantic memory disabled.");
        return null;
    }

    if (string.IsNullOrWhiteSpace(settings.Embedding.BaseUrl) ||
        RustOpsEnv.HasUnresolvedPlaceholder(settings.Embedding.BaseUrl) ||
        string.IsNullOrWhiteSpace(settings.Embedding.Model) ||
        RustOpsEnv.HasUnresolvedPlaceholder(settings.Embedding.Model))
    {
        Console.WriteLine("[memory] Embedding provider not configured. Semantic retrieval/writes will be skipped.");
        return null;
    }

    if (!string.Equals(settings.Embedding.Provider, "openai-compatible", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"[memory] Unsupported embedding provider '{settings.Embedding.Provider}'. Semantic memory disabled.");
        return null;
    }

    try
    {
        return new OpenAiCompatibleEmbeddingProvider(
            settings.Embedding.BaseUrl,
            settings.Embedding.Model,
            settings.Embedding.ApiKey,
            settings.Embedding.RequireApiKey,
            settings.Embedding.TimeoutSeconds,
            settings.Embedding.BatchSize);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[memory] Failed to initialize embedding provider: {ex.Message}");
        return null;
    }
}

static IInspectableMemoryStore BuildMemoryStore(MemorySettings settings)
{
    if (!string.Equals(settings.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"[memory] Unsupported provider '{settings.Provider}'. Using no-op memory store.");
        return new NullMemoryStore();
    }

    try
    {
        return new SqliteMemoryStore(settings.DatabasePath, settings, settings.DebugLoggingEnabled ? message => Console.WriteLine($"[memory] {message}") : null);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[memory] Failed to initialize semantic store: {ex.Message}. Using no-op memory store.");
        return new NullMemoryStore();
    }
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
        else if (string.Equals(args[i], "--memory-migrate", StringComparison.OrdinalIgnoreCase))
        {
            startup.MemoryCommand = "migrate";
        }
        else if (string.Equals(args[i], "--memory-stats", StringComparison.OrdinalIgnoreCase))
        {
            startup.MemoryCommand = "stats";
        }
        else if (string.Equals(args[i], "--memory-prune", StringComparison.OrdinalIgnoreCase))
        {
            startup.MemoryCommand = "prune";
        }
        else if (string.Equals(args[i], "--memory-rebuild-embeddings", StringComparison.OrdinalIgnoreCase))
        {
            startup.MemoryCommand = "rebuild";
        }
        else if (string.Equals(args[i], "--memory-search", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            startup.MemoryCommand = "search";
            startup.MemorySearchQuery = args[++i];
        }
        else if (string.Equals(args[i], "--memory-import", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            startup.MemoryCommand = "import";
            startup.MemoryImportFolder = args[++i];
        }
        else if (string.Equals(args[i], "--trusted", StringComparison.OrdinalIgnoreCase))
        {
            startup.Trusted = true;
        }
        else if (string.Equals(args[i], "--dry-run", StringComparison.OrdinalIgnoreCase))
        {
            startup.DryRun = true;
        }
    }

    return startup;
}

static async Task<bool> TryHandleMemoryMaintenanceAsync(AgentStartupOptions startup, ISemanticMemoryService semanticMemory, IMemoryImportService memoryImport)
{
    if (string.IsNullOrWhiteSpace(startup.MemoryCommand))
    {
        return false;
    }

    switch (startup.MemoryCommand)
    {
        case "migrate":
            var migration = await semanticMemory.MigrateLegacyMemoryAsync(startup.DryRun, CancellationToken.None);
            Console.WriteLine($"[memory] Migration complete: {migration.ToSummary()}");
            return true;
        case "stats":
            var stats = await semanticMemory.GetStatsAsync(CancellationToken.None);
            Console.WriteLine($"[memory] total={stats.TotalRecords} active={stats.ActiveRecords} expired={stats.ExpiredRecords}");
            foreach (var entry in stats.ByType.OrderByDescending(item => item.Value))
            {
                Console.WriteLine($"[memory] type {entry.Key}={entry.Value}");
            }
            return true;
        case "prune":
            var pruned = await semanticMemory.PruneAsync(CancellationToken.None);
            Console.WriteLine($"[memory] Pruned {pruned} record(s).");
            return true;
        case "rebuild":
            var rebuilt = await semanticMemory.RebuildEmbeddingsAsync(CancellationToken.None);
            Console.WriteLine($"[memory] Rebuilt embeddings for {rebuilt} record(s).");
            return true;
        case "search":
            var results = await semanticMemory.SearchAsync(startup.MemorySearchQuery ?? string.Empty, 8, CancellationToken.None);
            foreach (var result in results)
            {
                Console.WriteLine($"[memory] {result.MemoryRecord.Id} [{result.MemoryRecord.Type}/{result.MemoryRecord.Scope}] {result.MemoryRecord.Summary} score={result.FinalScore:F2}");
            }
            return true;
        case "import":
            var report = await memoryImport.ImportFolderAsync(new MemoryImportOptions
            {
                FolderPath = startup.MemoryImportFolder ?? string.Empty,
                Trusted = startup.Trusted,
                DryRun = startup.DryRun
            }, CancellationToken.None);
            Console.WriteLine($"[memory] Import complete: {report.ToSummary()}");
            foreach (var message in report.Messages.Take(10))
            {
                Console.WriteLine($"[memory] {message}");
            }
            return true;
        default:
            return false;
    }
}

internal sealed class AgentStartupOptions
{
    public string? ConfigPath { get; set; }
    public string? MemoryCommand { get; set; }
    public string? MemorySearchQuery { get; set; }
    public string? MemoryImportFolder { get; set; }
    public bool DryRun { get; set; }
    public bool Trusted { get; set; }
}
