using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CoreRCON;
using Microsoft.AspNetCore.Http.Json;
using Sentry;

RustOpsEnv.LoadFromDefaultLocations();
using var sentry = RustOpsSentry.Initialize("rustmgrapi");

try
{
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.WriteIndented = true;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();
var rustMgr = new RustMgrExecutor();

// -- Configuration ------------------------------------------------------------
// API key: set RUSTMGR_API_KEY env var. Falls back to "changeme" for dev only.
var apiKey      = RustOpsEnv.FirstNonEmptyEnvironment("RUSTMGR_API_KEY", "RUSTOPS_API_KEY") ?? "changeme";
var bindUrl     = Environment.GetEnvironmentVariable("RUSTMGR_BIND")    ?? "http://0.0.0.0:2077";
var rustMgrPath = Environment.GetEnvironmentVariable("RUSTMGR_PATH")    ?? "/opt/rust-manager/rustmgr.sh";
var runtimeDir  = Environment.GetEnvironmentVariable("RUSTMGR_RUNTIME") ?? "/opt/rust-manager/runtime";
var configDir   = Environment.GetEnvironmentVariable("RUSTMGR_CONFIG")  ?? "/opt/rust-manager/config";
var tasksDir    = Environment.GetEnvironmentVariable("RUSTMGR_TASKS_DIR") ?? "/opt/rust-manager/tasks";
var agentRootDir = Environment.GetEnvironmentVariable("RUSTOPS_AGENT_ROOT") ?? "/opt/rust-manager/agent/RustOpsAgent";
var botRootDir = Environment.GetEnvironmentVariable("RUSTOPS_STEAMBOT_ROOT") ?? "/opt/rust-manager/SteamBot/OpsSteamBot";
var agentSettingsPath = Environment.GetEnvironmentVariable("RUSTOPS_AGENT_SETTINGS_PATH") ?? Path.Combine(agentRootDir, "agentsettings.json");
var botSettingsPath = Environment.GetEnvironmentVariable("RUSTOPS_STEAMBOT_SETTINGS_PATH") ?? Path.Combine(botRootDir, "botsettings.json");
var sharedEnvPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_ENV_FILE") ?? Path.Combine(configDir, "rustops.env");
RustOpsSentry.ConfigureScope(scope =>
{
    scope.SetExtra("bindUrl", bindUrl);
    scope.SetExtra("rustMgrPath", rustMgrPath);
    scope.SetExtra("runtimeDir", runtimeDir);
    scope.SetExtra("configDir", configDir);
    scope.SetExtra("tasksDir", tasksDir);
    scope.SetExtra("agentRootDir", agentRootDir);
    scope.SetExtra("botRootDir", botRootDir);
    scope.SetExtra("agentSettingsPath", agentSettingsPath);
    scope.SetExtra("botSettingsPath", botSettingsPath);
    scope.SetExtra("sharedEnvPath", sharedEnvPath);
});
RustOpsSentry.AddBreadcrumb($"API starting on {bindUrl}.", "startup");

const int defaultConsoleLines = 120;
const int defaultEventLines   = 100;

Directory.CreateDirectory(configDir);
Directory.CreateDirectory(tasksDir);

app.Urls.Clear();
app.Urls.Add(bindUrl);

app.Use(async (ctx, next) =>
{
    RustOpsSentry.AddBreadcrumb($"{ctx.Request.Method} {ctx.Request.Path}", "http.request");
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        RustOpsSentry.CaptureException(
            ex,
            $"Unhandled HTTP pipeline exception for {ctx.Request.Method} {ctx.Request.Path}.",
            "http.request",
            tags: new Dictionary<string, string?>
            {
                ["http.method"] = ctx.Request.Method,
                ["http.path"] = ctx.Request.Path.Value ?? "/"
            },
            extras: new Dictionary<string, object?>
            {
                ["queryString"] = ctx.Request.QueryString.Value ?? string.Empty,
                ["traceIdentifier"] = ctx.TraceIdentifier
            });
        throw;
    }
});

// -- Auth middleware -----------------------------------------------------------
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/health") ||
        ctx.Request.Path.StartsWithSegments("/ui") ||
        ctx.Request.Path == "/")
    {
        await next();
        return;
    }
    var supplied = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
    if (!string.Equals(supplied, apiKey, StringComparison.Ordinal))
    {
        RustOpsSentry.AddBreadcrumb($"Rejected unauthorized request for {ctx.Request.Path}.", "http.auth");
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new ApiError("unauthorized", "Invalid API key."));
        return;
    }
    await next();
});

// -- Health --------------------------------------------------------------------
app.MapGet("/health", () => Results.Ok(new
{
    ok        = true,
    utc       = DateTime.UtcNow,
    rustMgrPath,
    configDir,
    runtimeDir,
    tasksDir
}));

app.MapGet("/", () => Results.Redirect("/ui"));

app.MapGet("/ui", () => Results.Content(BuildDashboardHtml(), "text/html; charset=utf-8"));

app.MapGet("/dashboard/summary", async () =>
{
    var agentPaths = ResolveAgentRuntimePaths(agentSettingsPath, botSettingsPath, agentRootDir);
    var listResult = await ExecRustMgrAsync("list");
    var serverNames = (listResult.StdOut ?? string.Empty)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .ToList();

    var statuses = await Task.WhenAll(serverNames.Select(async name =>
    {
        var statusResult = await ExecRustMgrAsync("status", name);
        var status = ParseStatus(name, statusResult.StdOut);
        var logsResult = await ExecRustMgrAsync("logs", name);
        var recentWarningCount = TailLines(logsResult.StdOut ?? string.Empty, 80)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(line =>
                line.Contains("warn", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("exception", StringComparison.OrdinalIgnoreCase));

        var config = LoadServerConfig(name);
        var normalized = config is null ? null : NormalizeConfig(name, config);
        var processStats = status.Pid.HasValue ? await ReadProcessSnapshotAsync(status.Pid.Value) : null;
        var playerSnapshot = status.Online ? await TryReadPlayerSnapshotAsync(name) : null;
        var infoSnapshot = status.Online ? await TryReadServerInfoSnapshotAsync(name) : null;

        return new
        {
            name = status.Name,
            state = status.State,
            online = status.Online,
            autoRestart = status.AutoRestart,
            pid = status.Pid,
            recentWarningCount,
            currentPlayers = playerSnapshot?.CurrentPlayers ?? infoSnapshot?.CurrentPlayers,
            maxPlayers = playerSnapshot?.MaxPlayers ?? infoSnapshot?.MaxPlayers ?? normalized?.ServerMaxPlayers,
            playerPreview = playerSnapshot?.PlayerNames ?? new List<string>(),
            uptimeSeconds = processStats?.UptimeSeconds,
            memoryMb = processStats?.MemoryMb,
            queryOk = playerSnapshot?.QueryOk ?? false,
            hostname = infoSnapshot?.Hostname ?? normalized?.ServerHostname,
            map = infoSnapshot?.Map,
            framerate = infoSnapshot?.Framerate,
            queuedPlayers = infoSnapshot?.QueuedPlayers
        };
    }));

    var services = await GetManagedServicesSnapshotAsync();

    var memory = LoadAgentMemorySnapshot(agentPaths);

    return Results.Ok(new
    {
        generatedAtUtc = DateTime.UtcNow,
        host = new
        {
            bindUrl,
            rustMgrPath,
            runtimeDir,
            configDir,
            tasksDir,
            agentSettingsPath = agentPaths.AgentSettingsPath,
            botSettingsPath = agentPaths.BotSettingsPath,
            agentStatePath = agentPaths.StatePath,
            agentNeoCortexRoot = agentPaths.NeoCortexRoot,
            agentLogRulesPath = agentPaths.LogRulesPath
        },
        counts = new
        {
            servers = statuses.Length,
            onlineServers = statuses.Count(s => s.online),
            pendingActions = memory.PendingActions.Count,
            incidents = memory.RecentIncidents.Count,
            agentErrors = memory.AgentErrors.Count,
            capabilityGaps = memory.CapabilityGaps.Count,
            selfRepairRuns = memory.SelfRepairHistory.Count,
            feedbackInbox = CountJsonFiles(agentPaths.FeedbackInboxPath),
            decisionInbox = CountJsonFiles(agentPaths.DecisionInboxPath),
            chatInbox = CountJsonFiles(agentPaths.ChatInboxPath),
            messageOutbox = CountJsonFiles(agentPaths.MessageOutboxPath),
            sentOutbox = CountJsonFiles(agentPaths.SentOutboxPath)
        },
        servers = statuses.OrderBy(s => s.name, StringComparer.OrdinalIgnoreCase),
        mailboxes = new[]
        {
            DescribeMailbox("feedback-inbox", agentPaths.FeedbackInboxPath),
            DescribeMailbox("decision-inbox", agentPaths.DecisionInboxPath),
            DescribeMailbox("chat-inbox", agentPaths.ChatInboxPath),
            DescribeMailbox("message-outbox", agentPaths.MessageOutboxPath),
            DescribeMailbox("message-outbox-sent", agentPaths.SentOutboxPath)
        },
        recentIncidents = memory.RecentIncidents,
        recentActions = memory.RecentActions,
        pendingActions = memory.PendingActions,
        recentFeedback = memory.RecentFeedback,
        agentErrors = memory.AgentErrors,
        runtimeStatus = memory.RuntimeStatus,
        agentState = memory.StateFile,
        llmInteractions = memory.LlmInteractions,
        capabilityGaps = memory.CapabilityGaps,
        selfRepairHistory = memory.SelfRepairHistory,
        services
    });
});

app.MapGet("/agent/log-rules", () =>
{
    var agentPaths = ResolveAgentRuntimePaths(agentSettingsPath, botSettingsPath, agentRootDir);
    var content = File.Exists(agentPaths.LogRulesPath)
        ? File.ReadAllText(agentPaths.LogRulesPath)
        : "{\n  \"ignoreContains\": [],\n  \"startupIgnoreContains\": [],\n  \"incidentContains\": []\n}";

    return Results.Ok(new
    {
        path = agentPaths.LogRulesPath,
        exists = File.Exists(agentPaths.LogRulesPath),
        content
    });
});

app.MapPut("/agent/log-rules", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var content = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(content))
        return Results.BadRequest(new ApiError("invalid_request", "Rules content is required."));

    try
    {
        using var _ = JsonDocument.Parse(content);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new ApiError("invalid_json", ex.Message));
    }

    var agentPaths = ResolveAgentRuntimePaths(agentSettingsPath, botSettingsPath, agentRootDir);
    Directory.CreateDirectory(Path.GetDirectoryName(agentPaths.LogRulesPath)!);
    var normalized = JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(content), JsonDefaults.Options);
    await File.WriteAllTextAsync(agentPaths.LogRulesPath, normalized);

    return Results.Ok(new
    {
        path = agentPaths.LogRulesPath,
        savedAtUtc = DateTime.UtcNow
    });
});

IResult ReadLlmConfig()
{
    var agentPaths = ResolveAgentRuntimePaths(agentSettingsPath, botSettingsPath, agentRootDir);
    var memory = LoadAgentMemorySnapshot(agentPaths);
    var values = ReadAgentLlmConfig(sharedEnvPath, agentPaths.AgentSettingsPath, memory.RuntimeStatus);

    return Results.Ok(new
    {
        path = sharedEnvPath,
        agentSettingsPath = agentPaths.AgentSettingsPath,
        restartRequired = true,
        values,
        effective = memory.RuntimeStatus,
        note = "Changes are written to the shared env file and agentsettings.json, then apply after restarting rustopsagent.service."
    });
}

IResult WriteLlmConfig(AgentLlmConfigUpdate request)
{
    var agentPaths = ResolveAgentRuntimePaths(agentSettingsPath, botSettingsPath, agentRootDir);
    var memory = LoadAgentMemorySnapshot(agentPaths);
    var current = ReadAgentLlmConfig(sharedEnvPath, agentPaths.AgentSettingsPath, memory.RuntimeStatus);

    var normalized = new AgentLlmConfigView
    {
        Provider = string.IsNullOrWhiteSpace(request.Provider) ? current.Provider : request.Provider.Trim(),
        Enabled = request.Enabled,
        BaseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? current.BaseUrl : request.BaseUrl.Trim(),
        Model = string.IsNullOrWhiteSpace(request.Model) ? current.Model : request.Model.Trim(),
        ApiKey = request.ApiKey?.Trim() ?? string.Empty,
        HttpReferer = string.IsNullOrWhiteSpace(request.HttpReferer) ? current.HttpReferer : request.HttpReferer.Trim(),
        AppTitle = string.IsNullOrWhiteSpace(request.AppTitle) ? current.AppTitle : request.AppTitle.Trim(),
        UseForRecommendations = request.UseForRecommendations,
        RequestStrategy = string.IsNullOrWhiteSpace(request.RequestStrategy) ? current.RequestStrategy : request.RequestStrategy.Trim().ToLowerInvariant(),
        Secondary = request.Secondary is null
            ? current.Secondary
            : new AgentLlmEndpointConfigView
            {
                Enabled = request.Secondary.Enabled,
                BaseUrl = request.Secondary.BaseUrl?.Trim() ?? string.Empty,
                Model = request.Secondary.Model?.Trim() ?? string.Empty,
                ApiKey = request.Secondary.ApiKey?.Trim() ?? string.Empty,
                HttpReferer = request.Secondary.HttpReferer?.Trim() ?? string.Empty,
                AppTitle = request.Secondary.AppTitle?.Trim() ?? string.Empty
            },
        UseChatSystemPrompt = request.UseChatSystemPrompt,
        ChatSystemPrompt = request.ChatSystemPrompt ?? current.ChatSystemPrompt
    };

    var validationError = ValidateLlmConfig(normalized);
    if (validationError is not null)
        return Results.BadRequest(new ApiError("invalid_request", validationError));

    UpsertEnvFileValues(sharedEnvPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["RUSTOPS_LLM_PROVIDER"] = normalized.Provider,
        ["RUSTOPS_LLM_ENABLED"] = normalized.Enabled ? "true" : "false",
        ["RUSTOPS_LLM_BASE_URL"] = normalized.BaseUrl,
        ["RUSTOPS_LLM_MODEL"] = normalized.Model,
        ["RUSTOPS_LLM_API_KEY"] = normalized.ApiKey?.Trim() ?? string.Empty,
        ["RUSTOPS_LLM_HTTP_REFERER"] = normalized.HttpReferer?.Trim() ?? string.Empty,
        ["RUSTOPS_LLM_APP_TITLE"] = normalized.AppTitle?.Trim() ?? string.Empty,
        ["RUSTOPS_LLM_USE_FOR_RECOMMENDATIONS"] = normalized.UseForRecommendations ? "true" : "false",
        ["RUSTOPS_LLM_REQUEST_STRATEGY"] = normalized.RequestStrategy,
        ["RUSTOPS_LLM_SECONDARY_ENABLED"] = normalized.Secondary.Enabled ? "true" : "false",
        ["RUSTOPS_LLM_SECONDARY_BASE_URL"] = normalized.Secondary.BaseUrl,
        ["RUSTOPS_LLM_SECONDARY_MODEL"] = normalized.Secondary.Model,
        ["RUSTOPS_LLM_SECONDARY_API_KEY"] = normalized.Secondary.ApiKey?.Trim() ?? string.Empty,
        ["RUSTOPS_LLM_SECONDARY_HTTP_REFERER"] = normalized.Secondary.HttpReferer?.Trim() ?? string.Empty,
        ["RUSTOPS_LLM_SECONDARY_APP_TITLE"] = normalized.Secondary.AppTitle?.Trim() ?? string.Empty,
        ["RUSTOPS_LLM_USE_CHAT_SYSTEM_PROMPT"] = normalized.UseChatSystemPrompt ? "true" : "false",
        // Newlines are escaped as literal \n so the env file stays single-line.
        ["RUSTOPS_LLM_CHAT_SYSTEM_PROMPT"] = (normalized.ChatSystemPrompt ?? string.Empty).Replace("\r", "").Replace("\n", "\\n")
    });
    UpsertAgentSettingsLlmValues(agentPaths.AgentSettingsPath, normalized);

    return Results.Ok(new
    {
        path = sharedEnvPath,
        agentSettingsPath = agentPaths.AgentSettingsPath,
        savedAtUtc = DateTime.UtcNow,
        restartRequired = true
    });
}

app.MapGet("/agent/llm/config", ReadLlmConfig);
app.MapGet("/agent/ollama/config", ReadLlmConfig);
app.MapPut("/agent/llm/config", WriteLlmConfig);
app.MapPut("/agent/ollama/config", WriteLlmConfig);

IResult ReadCommandConfig()
{
    var agentPaths = ResolveAgentRuntimePaths(agentSettingsPath, botSettingsPath, agentRootDir);
    var values = ReadAgentCommandConfig(sharedEnvPath, agentPaths.AgentSettingsPath);
    return Results.Ok(new
    {
        path = sharedEnvPath,
        agentSettingsPath = agentPaths.AgentSettingsPath,
        restartRequired = true,
        values,
        note = "Changes are written to the shared env file and apply after restarting rustopsagent.service."
    });
}

IResult WriteCommandConfig(AgentCommandConfigUpdate request)
{
    var agentPaths = ResolveAgentRuntimePaths(agentSettingsPath, botSettingsPath, agentRootDir);
    var current = ReadAgentCommandConfig(sharedEnvPath, agentPaths.AgentSettingsPath);

    var normalized = new AgentCommandConfigView
    {
        Enabled = request.Enabled,
        FreeMode = request.FreeMode,
        DefaultWaitMs = request.DefaultWaitMs <= 0 ? current.DefaultWaitMs : request.DefaultWaitMs,
        MaxWaitMs = request.MaxWaitMs <= 0 ? current.MaxWaitMs : request.MaxWaitMs,
        MaxOutputChars = request.MaxOutputChars <= 0 ? current.MaxOutputChars : request.MaxOutputChars,
        AllowList = (request.AllowList ?? current.AllowList)
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
    };

    if (normalized.DefaultWaitMs < 200 || normalized.DefaultWaitMs > 20_000)
        return Results.BadRequest(new ApiError("invalid_request", "defaultWaitMs must be between 200 and 20000."));
    if (normalized.MaxWaitMs < 500 || normalized.MaxWaitMs > 30_000)
        return Results.BadRequest(new ApiError("invalid_request", "maxWaitMs must be between 500 and 30000."));
    if (normalized.MaxWaitMs < normalized.DefaultWaitMs)
        return Results.BadRequest(new ApiError("invalid_request", "maxWaitMs must be greater than or equal to defaultWaitMs."));
    if (normalized.MaxOutputChars < 500 || normalized.MaxOutputChars > 64_000)
        return Results.BadRequest(new ApiError("invalid_request", "maxOutputChars must be between 500 and 64000."));
    if (!normalized.FreeMode && normalized.AllowList.Count == 0)
        return Results.BadRequest(new ApiError("invalid_request", "allowList must include at least one command when freeMode is disabled."));

    UpsertEnvFileValues(sharedEnvPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["RUSTOPS_COMMANDS_ENABLED"] = normalized.Enabled ? "true" : "false",
        ["RUSTOPS_COMMANDS_FREE_MODE"] = normalized.FreeMode ? "true" : "false",
        ["RUSTOPS_COMMANDS_DEFAULT_WAIT_MS"] = normalized.DefaultWaitMs.ToString(),
        ["RUSTOPS_COMMANDS_MAX_WAIT_MS"] = normalized.MaxWaitMs.ToString(),
        ["RUSTOPS_COMMANDS_MAX_OUTPUT_CHARS"] = normalized.MaxOutputChars.ToString(),
        ["RUSTOPS_COMMANDS_ALLOWLIST"] = string.Join(",", normalized.AllowList)
    });

    return Results.Ok(new
    {
        path = sharedEnvPath,
        savedAtUtc = DateTime.UtcNow,
        restartRequired = true
    });
}

app.MapGet("/agent/commands/config", ReadCommandConfig);
app.MapPut("/agent/commands/config", WriteCommandConfig);

app.MapGet("/agent/incidents/list", () =>
{
    var agentPaths = ResolveAgentRuntimePaths(agentSettingsPath, botSettingsPath, agentRootDir);
    var incidentsPath = Path.Combine(agentPaths.NeoCortexRoot, "evolution", "incidents.jsonl");
    var feedbackPath = Path.Combine(agentPaths.NeoCortexRoot, "evolution", "incidents-feedback.json");

    var feedbacks = new Dictionary<string, IncidentFeedbackEntry>(StringComparer.OrdinalIgnoreCase);
    if (File.Exists(feedbackPath))
    {
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(feedbackPath));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                feedbacks[prop.Name] = new IncidentFeedbackEntry(
                    prop.Value.TryGetProperty("verdict", out var v) ? v.GetString() : null,
                    prop.Value.TryGetProperty("note", out var n) ? n.GetString() : null,
                    prop.Value.TryGetProperty("answeredAtUtc", out var a) && a.TryGetDateTime(out var dt) ? dt : null);
            }
        }
        catch { /* ignore parse errors */ }
    }

    if (!File.Exists(incidentsPath))
        return Results.Ok(Array.Empty<object>());

    var incidents = File.ReadLines(incidentsPath)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line =>
        {
            try
            {
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = root.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? string.Empty : string.Empty;
                feedbacks.TryGetValue(id, out var fb);
                return (object)new
                {
                    id,
                    classification = root.TryGetProperty("classification", out var c) ? c.GetString() : null,
                    request = root.TryGetProperty("request", out var r) ? r.GetString() : null,
                    failureReason = root.TryGetProperty("failureReason", out var f) ? f.GetString() : null,
                    missingCapability = root.TryGetProperty("missingCapability", out var m) ? m.GetString() : null,
                    recurrencePrevention = root.TryGetProperty("recurrencePrevention", out var rp) ? rp.GetString() : null,
                    timestamp = root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var tdt) ? tdt : (DateTime?)null,
                    resolved = root.TryGetProperty("resolved", out var res) && res.GetBoolean(),
                    adminVerdict = fb?.Verdict,
                    adminNote = fb?.Note,
                    answeredAtUtc = fb?.AnsweredAtUtc
                };
            }
            catch { return null; }
        })
        .Where(item => item is not null)
        .Reverse()
        .Take(50)
        .ToList();

    return Results.Ok(incidents);
});

app.MapPost("/agent/incidents/{id}/feedback", async (string id, HttpRequest request) =>
{
    var agentPaths = ResolveAgentRuntimePaths(agentSettingsPath, botSettingsPath, agentRootDir);
    var feedbackPath = Path.Combine(agentPaths.NeoCortexRoot, "evolution", "incidents-feedback.json");

    string body;
    using (var reader = new StreamReader(request.Body))
        body = await reader.ReadToEndAsync();

    string? verdict = null;
    string? note = null;
    try
    {
        var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("verdict", out var v)) verdict = v.GetString();
        if (doc.RootElement.TryGetProperty("note", out var n)) note = n.GetString();
    }
    catch { return Results.BadRequest(new ApiError("invalid_json", "Could not parse request body.")); }

    var feedbacks = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    if (File.Exists(feedbackPath))
    {
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(feedbackPath));
            foreach (var prop in doc.RootElement.EnumerateObject())
                feedbacks[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText())!;
        }
        catch { /* start fresh */ }
    }

    feedbacks[id] = new { verdict, note, answeredAtUtc = DateTime.UtcNow };
    Directory.CreateDirectory(Path.GetDirectoryName(feedbackPath)!);
    File.WriteAllText(feedbackPath, JsonSerializer.Serialize(feedbacks, new JsonSerializerOptions { WriteIndented = true }));

    return Results.Ok(new { id, verdict, note, savedAtUtc = DateTime.UtcNow });
});

// -- Console monitor ----------------------------------------------------------
app.MapGet("/agent/console-monitor", () =>
{
    var agentPaths = ResolveAgentRuntimePaths(agentSettingsPath, botSettingsPath, agentRootDir);
    var consolePath = Path.Combine(agentPaths.NeoCortexRoot, "console", "monitor.json");
    if (!File.Exists(consolePath))
        return Results.Ok(new { servers = new Dictionary<string, object>(), updatedAtUtc = (DateTime?)null });
    try
    {
        var content = File.ReadAllText(consolePath);
        return Results.Content(content, "application/json");
    }
    catch (Exception ex)
    {
        return Results.Ok(new { error = ex.Message });
    }
});

// -- Player chat / sentiment --------------------------------------------------
app.MapGet("/agent/player-chat/recent", () =>
{
    var agentPaths = ResolveAgentRuntimePaths(agentSettingsPath, botSettingsPath, agentRootDir);
    var chatPath = Path.Combine(agentPaths.NeoCortexRoot, "chat", "knowledge.json");
    if (!File.Exists(chatPath))
        return Results.Ok(new { recentMessages = Array.Empty<object>(), sentimentScore = (double?)null, sentimentLabel = (string?)null, sentimentSummary = (string?)null, keyThemes = Array.Empty<string>(), constructiveFeedback = Array.Empty<string>(), analysedAtUtc = (DateTime?)null });
    try
    {
        var content = File.ReadAllText(chatPath);
        return Results.Content(content, "application/json");
    }
    catch (Exception ex)
    {
        return Results.Ok(new { error = ex.Message });
    }
});

app.MapGet("/host/services", async () => Results.Ok(await GetManagedServicesSnapshotAsync()));

app.MapGet("/host/llm/summary", async () =>
{
    var agentPaths = ResolveAgentRuntimePaths(agentSettingsPath, botSettingsPath, agentRootDir);
    var memory = LoadAgentMemorySnapshot(agentPaths);
    var values = ReadAgentLlmConfig(sharedEnvPath, agentPaths.AgentSettingsPath, memory.RuntimeStatus);
    var summary = await ReadLmStudioSummaryAsync(values);
    return Results.Ok(summary);
});

app.MapGet("/host/ollama/summary", async () =>
{
    var agentPaths = ResolveAgentRuntimePaths(agentSettingsPath, botSettingsPath, agentRootDir);
    var memory = LoadAgentMemorySnapshot(agentPaths);
    var values = ReadAgentLlmConfig(sharedEnvPath, agentPaths.AgentSettingsPath, memory.RuntimeStatus);
    var summary = await ReadLmStudioSummaryAsync(values);
    return Results.Ok(summary);
});

// -- Host inspection -----------------------------------------------------------
app.MapGet("/host/network/interfaces", async () =>
{
    var result = await ExecProcessAsync("ip", "-json", "addr");
    if (result.Ok && !string.IsNullOrWhiteSpace(result.StdOut))
    {
        var payload = TryExtractJson(result.StdOut);
        return payload is not null
            ? Results.Content(payload, "application/json")
            : Results.Ok(new { raw = result.StdOut });
    }

    var fallback = await ExecProcessAsync("ip", "addr");
    return fallback.Ok
        ? Results.Ok(new { raw = fallback.StdOut ?? string.Empty })
        : Results.BadRequest(fallback);
});

app.MapGet("/host/network/summary", () => Results.Ok(BuildHostNetworkSummary()));

// -- Server list ---------------------------------------------------------------
// Calls rustmgr list as the authoritative source, then cross-checks config dir.
app.MapGet("/servers", async () =>
{
    var result = await ExecRustMgrAsync("list");

    // rustmgr list exits 1 with no output when no servers exist � that is fine.
    var names = (result.StdOut ?? string.Empty)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .ToList();

    return Results.Ok(names.Select(name => new
    {
        name,
        configExists = File.Exists(Path.Combine(configDir, $"{name}.json"))
    }));
});

// -- Agent convenience: all servers with status in one call --------------------
app.MapGet("/servers/summary", async () =>
{
    var listResult = await ExecRustMgrAsync("list");
    var names = (listResult.StdOut ?? string.Empty)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .ToList();

    var tasks = names.Select(async name =>
    {
        var r = await ExecRustMgrAsync("status", name);
        return ParseStatus(name, r.StdOut);
    });

    var statuses = await Task.WhenAll(tasks);
    return Results.Ok(statuses);
});

// -- Status --------------------------------------------------------------------
app.MapGet("/servers/{server}/status", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var result = await ExecRustMgrAsync("status", server);
    if (!result.Ok)
        return Results.BadRequest(result);

    return Results.Ok(ParseStatus(server, result.StdOut));
});

// -- Agent convenience: composite health check ---------------------------------
// Combines: process status + recent log error scan + meta availability.
// The agent calls this instead of assembling several calls itself.
app.MapGet("/servers/{server}/health", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var statusResult = await ExecRustMgrAsync("status", server);
    var status       = ParseStatus(server, statusResult.StdOut);

    // Scan last 200 log lines for ERROR / Exception / NullRef patterns
    var logsResult  = await ExecRustMgrAsync("logs", server);
    var recentLines = TailLines(logsResult.StdOut ?? string.Empty, 200)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    var errorKeywords = new[] { "ERROR", "Exception", "NullReferenceException", "fatal", "crash" };
    var recentErrors  = recentLines
        .Where(l => errorKeywords.Any(k => l.Contains(k, StringComparison.OrdinalIgnoreCase)))
        .TakeLast(10)
        .ToList();

    // Read last supervisor restart event from command trace
    var tracePath = Path.Combine(runtimeDir, $"{server}.commands.log");
    string? lastRestart = null;
    if (File.Exists(tracePath))
    {
        var traceLines = File.ReadLines(tracePath)
            .Where(l => l.Contains("process exit") || l.Contains("process start"))
            .TakeLast(5)
            .ToList();
        lastRestart = traceLines.LastOrDefault();
    }

    return Results.Ok(new
    {
        name        = server,
        status,
        recentErrors,
        lastRestartEvent = lastRestart,
        checkedAt   = DateTime.UtcNow
    });
});

// -- Start ---------------------------------------------------------------------
app.MapPost("/servers/{server}/start", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    if (LoadServerConfig(server) is null)
        return Results.BadRequest(new ApiError("missing_config", $"No config found for '{server}'."));

    var result = await rustMgr.ExecuteLifecycleAsync(server, "start");
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

// -- Stop ----------------------------------------------------------------------
app.MapPost("/servers/{server}/stop", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var result = await rustMgr.ExecuteLifecycleAsync(server, "stop");
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

// -- Restart -------------------------------------------------------------------
app.MapPost("/servers/{server}/restart", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    if (LoadServerConfig(server) is null)
        return Results.BadRequest(new ApiError("missing_config", $"No config found for '{server}'."));

    var result = await rustMgr.ExecuteLifecycleAsync(server, "restart");
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

// -- Kill ----------------------------------------------------------------------
app.MapPost("/servers/{server}/kill", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var result = await ExecRustMgrAsync("kill", server);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

// -- Update (SteamCMD) ---------------------------------------------------------
app.MapPost("/servers/{server}/update", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var result = await ExecRustMgrAsync("update", server);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

// -- uMod update ---------------------------------------------------------------
app.MapPost("/servers/{server}/umod", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var result = await ExecRustMgrAsync("umod", server);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

// -- Sync config ---------------------------------------------------------------
app.MapPost("/servers/{server}/sync-config", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var result = await ExecRustMgrAsync("sync-config", server);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

// -- Wipe (map + save data) ----------------------------------------------------
// rustmgr refuses to wipe while server is running � that safety check stays in
// the script; the API just proxies it.
app.MapPost("/servers/{server}/wipe", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var result = await ExecRustMgrAsync("wipe", server);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

// -- Config read ---------------------------------------------------------------
app.MapGet("/servers/{server}/config", (string server) =>
{
    var cfg = LoadServerConfig(server);
    if (cfg is null)
        return Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."));
    return Results.Ok(cfg);
});

// -- Config write --------------------------------------------------------------
app.MapPut("/servers/{server}/config", (string server, ServerConfig config) =>
{
    var normalized    = NormalizeConfig(server, config);
    var validationErr = ValidateConfig(normalized);
    if (validationErr is not null)
        return Results.BadRequest(new ApiError("invalid_config", validationErr));

    SaveServerConfig(normalized);
    return Results.Ok(new
    {
        ok              = true,
        message         = "Config saved.",
        restartRequired = true,
        config          = normalized
    });
});

// -- Config validation / provisioning -----------------------------------------
app.MapPost("/servers/{server}/config/validate", (string server, ServerConfig config) =>
{
    var normalized = NormalizeConfig(server, config);
    var errors = new List<string>();
    var validationErr = ValidateConfig(normalized);
    if (validationErr is not null)
        errors.Add(validationErr);

    errors.AddRange(FindConfigConflicts(normalized, ignoreServer: server));

    return Results.Ok(new
    {
        valid      = errors.Count == 0,
        errors,
        normalized
    });
});

app.MapPost("/servers/provision", (ProvisionServerRequest request) =>
{
    if (request.Config is null)
        return Results.BadRequest(new ApiError("invalid_request", "Config is required."));

    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new ApiError("invalid_request", "Name is required."));

    var normalized = NormalizeConfig(request.Name.Trim(), request.Config);
    var errors = new List<string>();
    var validationErr = ValidateConfig(normalized);
    if (validationErr is not null)
        errors.Add(validationErr);

    if (File.Exists(GetConfigPath(normalized.Name)))
        errors.Add($"Server '{normalized.Name}' already exists.");

    errors.AddRange(FindConfigConflicts(normalized));

    if (errors.Count > 0)
    {
        return Results.BadRequest(new
        {
            ok = false,
            errors,
            normalized
        });
    }

    SaveServerConfig(normalized);

    if (request.CreateDirectories)
    {
        Directory.CreateDirectory(normalized.ServerDir);
        Directory.CreateDirectory(Path.Combine(normalized.ServerDir, "oxide", "config"));
        Directory.CreateDirectory(Path.Combine(normalized.ServerDir, "oxide", "plugins"));
    }

    return Results.Ok(new
    {
        ok = true,
        message = $"Provisioned config for '{normalized.Name}'.",
        config = normalized
    });
});

// -- Runtime meta (ports, identity, seed � for agent context) ------------------
// Reads the .meta file rustmgr writes on sync-config. Gives the agent a fast
// way to know what ports/identity a server is running without parsing JSON config.
app.MapGet("/servers/{server}/meta", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var metaPath = Path.Combine(runtimeDir, $"{server}.meta");
    if (!File.Exists(metaPath))
        return Results.NotFound(new ApiError("not_found", "Meta file not found. Run sync-config first."));

    var lines = await File.ReadAllLinesAsync(metaPath);
    var meta  = lines
        .Select(l => l.Split('=', 2))
        .Where(p => p.Length == 2)
        .ToDictionary(
            p => p[0].Trim().ToLowerInvariant(),
            p => p[1].Trim('"')
        );

    return Results.Ok(meta);
});

// -- Oxide / plugin validation ------------------------------------------------
app.MapGet("/servers/{server}/oxide/validate", (string server) =>
{
    var cfg = LoadServerConfig(server);
    if (cfg is null)
        return Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."));    

    var normalized = NormalizeConfig(server, cfg);
    var oxideRoots = GetOxideRootCandidates(normalized);
    var configPaths = oxideRoots
        .Select(root => Path.Combine(root, "config"))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    var pluginsPaths = oxideRoots
        .Select(root => Path.Combine(root, "plugins"))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    var jsonFiles = configPaths
        .Where(Directory.Exists)
        .SelectMany(path => Directory.GetFiles(path, "*.json", SearchOption.AllDirectories))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    var pluginFiles = pluginsPaths
        .Where(Directory.Exists)
        .SelectMany(path => Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    var jsonResults = jsonFiles.Select(ValidateJsonFile).ToList();
    var pluginResults = pluginFiles.Select(ValidateOxidePluginFile).ToList();

    return Results.Ok(new
    {
        server,
        ok = jsonResults.All(r => r.Ok) && pluginResults.All(r => r.Ok),
        searchedPaths = new
        {
            oxideRoots,
            configPaths,
            pluginsPaths
        },
        jsonConfigCount = jsonFiles.Length,
        pluginCount = pluginFiles.Length,
        jsonConfigs = jsonResults,
        plugins = pluginResults
    });
});

// -- Managed tasks for agent-created cron jobs --------------------------------
app.MapGet("/tasks", () =>
{
    var files = Directory.GetFiles(tasksDir, "*.cron")
        .OrderBy(Path.GetFileName)
        .Select(ParseManagedTaskFile)
        .Where(t => t is not null)
        .ToList();

    return Results.Ok(files);
});

app.MapPost("/tasks", (ManagedTaskRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new ApiError("invalid_request", "Task name is required."));

    if (string.IsNullOrWhiteSpace(request.Command))
        return Results.BadRequest(new ApiError("invalid_request", "Task command is required."));

    var safeName = SanitizeTaskName(request.Name);
    if (string.IsNullOrWhiteSpace(safeName))
        return Results.BadRequest(new ApiError("invalid_request", "Task name contains no valid characters."));

    var schedule = BuildCronSchedule(request);
    if (schedule is null)
        return Results.BadRequest(new ApiError("invalid_request", "Either schedule or onceAtUtc must be provided."));

    var path = Path.Combine(tasksDir, $"{safeName}.cron");
    var command = request.Command.Trim();

    if (request.OnceAtUtc.HasValue)
    {
        command = $"({command}) ; rm -f {path}";
    }

    var lines = new[]
    {
        "# Managed by rustmgr api",
        $"# name: {request.Name.Trim()}",
        $"# createdUtc: {DateTime.UtcNow:O}",
        "SHELL=/bin/bash",
        "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
        $"{schedule} root {command}"
    };

    File.WriteAllLines(path, lines);

    return Results.Ok(new
    {
        ok = true,
        task = ParseManagedTaskFile(path),
        installHint = $"Copy or symlink {path} into /etc/cron.d/ on the Linux host."
    });
});

// -- Send RCON command ---------------------------------------------------------
app.MapPost("/servers/{server}/command", async (string server, ServerCommandRequest request) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    if (request is null || string.IsNullOrWhiteSpace(request.Command))
        return Results.BadRequest(new ApiError("invalid_request", "Command is required."));

    var cfg = LoadServerConfig(server);
    if (cfg is null)
        return Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."));

    var command = request.Command.Trim();
    if (command.Length > 256)
        return Results.BadRequest(new ApiError("invalid_request", "Command length exceeds 256 characters."));
    if (command.Contains('\n') || command.Contains('\r'))
        return Results.BadRequest(new ApiError("invalid_request", "Command must be a single line."));

    var endpoint = ResolveRconConnectionInfo(server, cfg);
    if (!endpoint.WebRconEnabled)
        return Results.BadRequest(new ApiError("invalid_config", $"WebRCON is disabled for '{server}'. Enable +rcon.web 1 in config/additionalArgs."));

    try
    {
        await using var rcon = new RustRcon(endpoint.Host, endpoint.Port, endpoint.Password);
        await rcon.ConnectAsync();
        var reply = await rcon.SendAndReceiveAsync(command);
        return Results.Ok(new
        {
            ok = true,
            server,
            command,
            transport = "rcon-web",
            endpoint = new { host = endpoint.Host, port = endpoint.Port },
            reply = string.IsNullOrWhiteSpace(reply) ? null : reply.Trim()
        });
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "RCON command endpoint failed.", server, request.Command?.Trim());
        var fallback = await ExecRustMgrAsync("send", server, command);
        if (!fallback.Ok)
        {
            return Results.BadRequest(new ApiError("rcon_error", $"WebRCON failed: {ex.Message}. rustmgr fallback failed: {BuildRustMgrError(fallback)}"));
        }

        return Results.Ok(new
        {
            ok = true,
            server,
            command,
            transport = "rustmgr-send",
            endpoint = new { host = endpoint.Host, port = endpoint.Port },
            reply = (string?)null,
            warning = $"WebRCON failed: {ex.Message}",
            fallback = string.IsNullOrWhiteSpace(fallback.StdOut) ? "command accepted by rustmgr send" : fallback.StdOut.Trim()
        });
    }
});

app.MapPost("/servers/{server}/command/exec", async (string server, ServerCommandExecRequest request) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    if (request is null || string.IsNullOrWhiteSpace(request.Command))
        return Results.BadRequest(new ApiError("invalid_request", "Command is required."));

    var cfg = LoadServerConfig(server);
    if (cfg is null)
        return Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."));

    var normalized = NormalizeConfig(server, cfg);
    var command = request.Command.Trim();
    if (command.Length > 256)
        return Results.BadRequest(new ApiError("invalid_request", "Command length exceeds 256 characters."));
    if (command.Contains('\n') || command.Contains('\r'))
        return Results.BadRequest(new ApiError("invalid_request", "Command must be a single line."));

    var logPath = GetServerLogPath(normalized);
    var startOffset = File.Exists(logPath)
        ? new FileInfo(logPath).Length
        : 0L;
    var waitMs = Math.Clamp(request.WaitMs <= 0 ? 2500 : request.WaitMs, 200, 20_000);
    var maxBytes = Math.Clamp(request.MaxBytes <= 0 ? 128 * 1024 : request.MaxBytes, 4 * 1024, 512 * 1024);
    var maxLines = Math.Clamp(request.MaxLines <= 0 ? 120 : request.MaxLines, 1, 600);

    var endpoint = ResolveRconConnectionInfo(server, cfg);
    if (!endpoint.WebRconEnabled)
        return Results.BadRequest(new ApiError("invalid_config", $"WebRCON is disabled for '{server}'. Enable +rcon.web 1 in config/additionalArgs."));

    string? directReply;
    try
    {
        await using var rcon = new RustRcon(endpoint.Host, endpoint.Port, endpoint.Password);
        await rcon.ConnectAsync();
        directReply = await rcon.SendAndReceiveAsync(command);
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "Command exec RCON endpoint failed.", server, command);
        var fallback = await ExecRustMgrAsync("send", server, command);
        if (!fallback.Ok)
        {
            return Results.BadRequest(new ApiError("rcon_error", $"WebRCON failed: {ex.Message}. rustmgr fallback failed: {BuildRustMgrError(fallback)}"));
        }

        var fallbackOutput = await ReadCommandOutputDeltaAsync(logPath, startOffset, waitMs, maxBytes, maxLines);
        return Results.Ok(new
        {
            ok = true,
            server,
            command,
            waitMs,
            logPath,
            transport = "rustmgr-send",
            endpoint = new { host = endpoint.Host, port = endpoint.Port },
            directReply = (string?)null,
            warning = $"WebRCON failed: {ex.Message}",
            output = fallbackOutput
        });
    }

    var output = await ReadCommandOutputDeltaAsync(logPath, startOffset, waitMs, maxBytes, maxLines);
    if (!string.IsNullOrWhiteSpace(directReply))
    {
        output.Messages.Insert(0, directReply.Trim());
        output.Messages = output.Messages.Take(maxLines).ToList();
    }

    return Results.Ok(new
    {
        ok = true,
        server,
        command,
        waitMs,
        logPath,
        transport = "rcon-web",
        endpoint = new { host = endpoint.Host, port = endpoint.Port },
        directReply = string.IsNullOrWhiteSpace(directReply) ? null : directReply.Trim(),
        output
    });
});

// -- Console log snapshot ------------------------------------------------------
app.MapGet("/servers/{server}/console", async (string server, int? lines) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var result = await ExecRustMgrAsync("logs", server);
    if (!result.Ok) return Results.BadRequest(result);

    var n = Math.Max(1, lines.GetValueOrDefault(defaultConsoleLines));
    var content = TailLines(result.StdOut ?? string.Empty, n);
    return Results.Ok(new { server, lines = n, content });
});

// -- Agent convenience: structured recent log lines ----------------------------
// Returns parsed log entries as JSON. Optional `since` ISO8601 param lets the
// agent ask "what happened in the last N minutes" without sending the full log.
// Lines that don't start with a recognised timestamp are attached to the
// previous entry as continuation text (stack traces etc).
app.MapGet("/servers/{server}/logs/tail", async (string server, int? lines, string? since, int? offset) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var result = await ExecRustMgrAsync("logs", server);
    if (!result.Ok) return Results.BadRequest(result);

    var n          = Math.Max(1, lines.GetValueOrDefault(200));
    var rawLines   = TailLines(result.StdOut ?? string.Empty, n)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var entries    = ParseLogLines(rawLines);

    if (!string.IsNullOrWhiteSpace(since) && DateTime.TryParse(since, out var sinceUtc))
        entries = entries.Where(e => e.Timestamp == null || e.Timestamp >= sinceUtc).ToList();

    var skip  = Math.Max(0, offset.GetValueOrDefault(0));
    var total = entries.Count;
    if (skip > 0)
        entries = entries.Skip(skip).ToList();

    return Results.Ok(new
    {
        server,
        total,
        offset = skip,
        count  = entries.Count,
        entries
    });
});

app.MapGet("/servers/{server}/logs/read", async (string server, long? offset, int? maxBytes) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var cfg = LoadServerConfig(server);
    if (cfg is null)
        return Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."));

    var normalized = NormalizeConfig(server, cfg);
    var logPath = GetServerLogPath(normalized);
    var slice = ReadLogSlice(logPath, offset, maxBytes.GetValueOrDefault(64 * 1024));

    return Results.Ok(new
    {
        server,
        path = logPath,
        exists = slice.Exists,
        startOffset = slice.StartOffset,
        endOffset = slice.EndOffset,
        truncated = slice.Truncated,
        reset = slice.Reset,
        count = slice.Entries.Count,
        entries = slice.Entries
    });
});

// -- Command trace (what was sent to the server) -------------------------------
app.MapGet("/servers/{server}/commands", async (string server, int? lines) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var n      = Math.Max(1, lines.GetValueOrDefault(80));
    var result = await ExecRustMgrAsync("commands", server, n.ToString());
    if (!result.Ok) return Results.BadRequest(result);

    var content = result.StdOut ?? string.Empty;
    return Results.Ok(new { server, lines = n, content });
});

// -- Agent convenience: command trace as structured JSON -----------------------
app.MapGet("/servers/{server}/events", async (string server, int? lines) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var n         = Math.Max(1, lines.GetValueOrDefault(defaultEventLines));
    var tracePath = Path.Combine(runtimeDir, $"{server}.commands.log");

    if (!File.Exists(tracePath))
        return Results.Ok(new { server, count = 0, events = Array.Empty<object>() });

    var rawLines = File.ReadLines(tracePath).TakeLast(n).ToList();
    var events   = rawLines
        .Select(l => ParseTraceEvent(l))
        .Where(e => e is not null)
        .ToList();

    return Results.Ok(new { server, count = events.Count, events });
});

// -- Serverinfo (live RCON query) ----------------------------------------------
app.MapGet("/servers/{server}/serverinfo", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var cfg = LoadServerConfig(server);
    if (cfg is null)
        return Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."));

    var endpoint = ResolveRconConnectionInfo(server, cfg);
    if (!endpoint.WebRconEnabled)
        return Results.BadRequest(new ApiError("invalid_config", $"WebRCON is disabled for '{server}'. Enable +rcon.web 1 in config/additionalArgs."));

    try
    {
        await using var rcon = new RustRcon(endpoint.Host, endpoint.Port, endpoint.Password);
        await rcon.ConnectAsync();
        var directReply = await rcon.SendAndReceiveAsync("serverinfo");
        var payload = TryExtractJson(directReply);
        return payload is null
            ? Results.BadRequest(new ApiError("parse_error", "Could not parse serverinfo response."))
            : Results.Content(payload, "application/json");
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "Server info RCON endpoint failed.", server, "serverinfo");
        var fallback = await ExecRustMgrAsync("query", server, "serverinfo");
        var payload = TryExtractJson(fallback.StdOut);
        return fallback.Ok && payload is not null
            ? Results.Content(payload, "application/json")
            : Results.BadRequest(new ApiError("rcon_error", $"WebRCON failed: {ex.Message}. rustmgr fallback failed: {BuildRustMgrError(fallback)}"));
    }
});

// -- Player list (live RCON query) ---------------------------------------------
app.MapGet("/servers/{server}/players", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var cfg = LoadServerConfig(server);
    if (cfg is null)
        return Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."));

    var endpoint = ResolveRconConnectionInfo(server, cfg);
    if (!endpoint.WebRconEnabled)
        return Results.BadRequest(new ApiError("invalid_config", $"WebRCON is disabled for '{server}'. Enable +rcon.web 1 in config/additionalArgs."));

    try
    {
        await using var rcon = new RustRcon(endpoint.Host, endpoint.Port, endpoint.Password);
        await rcon.ConnectAsync();
        var directReply = await rcon.SendAndReceiveAsync("playerlist");
        var payload = TryExtractJson(directReply);
        return payload is null
            ? Results.BadRequest(new ApiError("parse_error", "Could not parse playerlist response."))
            : Results.Content(payload, "application/json");
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "Player list RCON endpoint failed.", server, "playerlist");
        var fallback = await ExecRustMgrAsync("query", server, "playerlist");
        var payload = TryExtractJson(fallback.StdOut);
        return fallback.Ok && payload is not null
            ? Results.Content(payload, "application/json")
            : Results.BadRequest(new ApiError("rcon_error", $"WebRCON failed: {ex.Message}. rustmgr fallback failed: {BuildRustMgrError(fallback)}"));
    }
});

// -- Bans ----------------------------------------------------------------------
app.MapGet("/servers/{server}/bans", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    var cfg = LoadServerConfig(server);
    if (cfg is null)
        return Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."));

    var endpoint = ResolveRconConnectionInfo(server, cfg);
    if (!endpoint.WebRconEnabled)
        return Results.BadRequest(new ApiError("invalid_config", $"WebRCON is disabled for '{server}'. Enable +rcon.web 1 in config/additionalArgs."));

    try
    {
        await using var rcon = new RustRcon(endpoint.Host, endpoint.Port, endpoint.Password);
        await rcon.ConnectAsync();
        var directReply = await rcon.SendAndReceiveAsync("bans");
        var payload = TryExtractJson(directReply);
        return payload is null
            ? Results.BadRequest(new ApiError("parse_error", "Could not parse bans response."))
            : Results.Content(payload, "application/json");
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "Bans RCON endpoint failed.", server, "bans");
        var fallback = await ExecRustMgrAsync("query", server, "bans");
        var payload = TryExtractJson(fallback.StdOut);
        return fallback.Ok && payload is not null
            ? Results.Content(payload, "application/json")
            : Results.BadRequest(new ApiError("rcon_error", $"WebRCON failed: {ex.Message}. rustmgr fallback failed: {BuildRustMgrError(fallback)}"));
    }
});

// -- Kick ----------------------------------------------------------------------
app.MapPost("/servers/{server}/kick", async (string server, ModerationRequest request) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    if (request is null || string.IsNullOrWhiteSpace(request.SteamId))
        return Results.BadRequest(new ApiError("invalid_request", "SteamId is required."));

    var cfg = LoadServerConfig(server);
    if (cfg is null)
        return Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."));

    var endpoint = ResolveRconConnectionInfo(server, cfg);
    if (!endpoint.WebRconEnabled)
        return Results.BadRequest(new ApiError("invalid_config", $"WebRCON is disabled for '{server}'. Enable +rcon.web 1 in config/additionalArgs."));

    var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Kicked by admin" : request.Reason.Trim();
    var command = $"kick {request.SteamId} \"{Escape(reason)}\"";
    
    try
    {
        await using var rcon = new RustRcon(endpoint.Host, endpoint.Port, endpoint.Password);
        await rcon.ConnectAsync();
        var reply = await rcon.SendAndReceiveAsync(command);
        return Results.Ok(new
        {
            ok = true,
            server,
            command,
            reply = string.IsNullOrWhiteSpace(reply) ? null : reply.Trim()
        });
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "Kick RCON endpoint failed.", server, command);
        return Results.BadRequest(new ApiError("rcon_error", ex.Message));
    }
});

// -- Ban -----------------------------------------------------------------------
app.MapPost("/servers/{server}/ban", async (string server, ModerationRequest request) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    if (request is null || string.IsNullOrWhiteSpace(request.SteamId))
        return Results.BadRequest(new ApiError("invalid_request", "SteamId is required."));

    var cfg = LoadServerConfig(server);
    if (cfg is null)
        return Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."));

    var endpoint = ResolveRconConnectionInfo(server, cfg);
    if (!endpoint.WebRconEnabled)
        return Results.BadRequest(new ApiError("invalid_config", $"WebRCON is disabled for '{server}'. Enable +rcon.web 1 in config/additionalArgs."));

    var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Banned by admin" : request.Reason.Trim();
    var command = $"ban {request.SteamId} \"{Escape(reason)}\"";

    try
    {
        await using var rcon = new RustRcon(endpoint.Host, endpoint.Port, endpoint.Password);
        await rcon.ConnectAsync();
        var reply = await rcon.SendAndReceiveAsync(command);
        return Results.Ok(new
        {
            ok = true,
            server,
            command,
            reply = string.IsNullOrWhiteSpace(reply) ? null : reply.Trim()
        });
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "Ban RCON endpoint failed.", server, command);
        return Results.BadRequest(new ApiError("rcon_error", ex.Message));
    }
});

// -- Unban ---------------------------------------------------------------------
app.MapPost("/servers/{server}/unban", async (string server, ModerationRequest request) =>
{
    if (!await IsValidServerAsync(server))
        return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

    if (request is null || string.IsNullOrWhiteSpace(request.SteamId))
        return Results.BadRequest(new ApiError("invalid_request", "SteamId is required."));

    var cfg = LoadServerConfig(server);
    if (cfg is null)
        return Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."));

    var endpoint = ResolveRconConnectionInfo(server, cfg);
    if (!endpoint.WebRconEnabled)
        return Results.BadRequest(new ApiError("invalid_config", $"WebRCON is disabled for '{server}'. Enable +rcon.web 1 in config/additionalArgs."));

    var command = $"unban {request.SteamId}";

    try
    {
        await using var rcon = new RustRcon(endpoint.Host, endpoint.Port, endpoint.Password);
        await rcon.ConnectAsync();
        var reply = await rcon.SendAndReceiveAsync(command);
        return Results.Ok(new
        {
            ok = true,
            server,
            command,
            reply = string.IsNullOrWhiteSpace(reply) ? null : reply.Trim()
        });
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "Unban RCON endpoint failed.", server, command);
        return Results.BadRequest(new ApiError("rcon_error", ex.Message));
    }
});

app.Run();
}
catch (Exception ex)
{
    RustOpsSentry.CaptureException(ex, "rustmgrapi terminated unexpectedly.", "runtime");
    throw;
}
finally
{
    await RustOpsSentry.FlushAsync();
}

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

static void CaptureHandledApiException(Exception ex, string context, string? server = null, string? command = null, string? path = null)
{
    var tags = new Dictionary<string, string?> { ["handled"] = "true" };
    if (!string.IsNullOrWhiteSpace(server))
        tags["server"] = server;

    var extras = new Dictionary<string, object?>();
    if (!string.IsNullOrWhiteSpace(command))
        extras["command"] = command;
    if (!string.IsNullOrWhiteSpace(path))
        extras["path"] = path;

    RustOpsSentry.CaptureException(ex, context, "api.handled", tags, extras);
}

static string BuildRustMgrError(CommandExecutionResult result)
{
    if (!string.IsNullOrWhiteSpace(result.Message))
        return result.Message!;
    if (!string.IsNullOrWhiteSpace(result.StdErr))
        return result.StdErr!;
    if (!string.IsNullOrWhiteSpace(result.StdOut))
        return result.StdOut!;
    return $"exit code {result.ExitCode}";
}

static async Task<bool> IsValidServerAsync(string server)
{
    var result = await ExecRustMgrAsync("list");
    var names  = (result.StdOut ?? string.Empty)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return names.Contains(server, StringComparer.OrdinalIgnoreCase);
}

// Parses the structured output rustmgr status actually emits:
//   name: <server>
//   state: running|offline|starting|session-only
//   session: yes|no
//   autorestart: yes|no
//   pid: <number>           ? only present when running
static ServerStatusResponse ParseStatus(string server, string? stdout)
{
    var output = stdout ?? string.Empty;
    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var idx = line.IndexOf(':');
        if (idx <= 0) continue;
        var key = line[..idx].Trim();
        var val = line[(idx + 1)..].Trim();
        fields[key] = val;
    }

    int? pid = null;
    if (fields.TryGetValue("pid", out var pidStr) && int.TryParse(pidStr, out var parsedPid))
        pid = parsedPid;

    fields.TryGetValue("state",       out var state);
    fields.TryGetValue("autorestart", out var autorestart);

    return new ServerStatusResponse
    {
        Name        = server,
        State       = state ?? "unknown",
        Online      = string.Equals(state, "running", StringComparison.OrdinalIgnoreCase),
        AutoRestart = string.Equals(autorestart, "yes", StringComparison.OrdinalIgnoreCase),
        Pid         = pid,
        Raw         = output
    };
}

// Parses log lines into structured entries. Rust server logs lines typically
// begin with a timestamp token like "12/31/2024 03:14:22" or "[03:14:22]".
// Lines without a recognisable timestamp are appended to the previous entry.
static List<LogEntry> ParseLogLines(IEnumerable<string> lines)
{
    var result  = new List<LogEntry>();
    LogEntry? current = null;

    foreach (var line in lines)
    {
        var ts    = TryParseLogTimestamp(line);
        var level = DetectLogLevel(line);

        if (ts.HasValue || current is null)
        {
            current = new LogEntry
            {
                Timestamp = ts,
                Level     = level,
                Message   = line.Trim()
            };
            result.Add(current);
        }
        else
        {
            // continuation (stack trace, multiline output)
            current.Message += "\n" + line.TrimEnd();
        }
    }

    return result;
}

static LogSliceResult ReadLogSlice(string path, long? offset, int maxBytes)
{
    var safeMaxBytes = Math.Clamp(maxBytes, 1024, 512 * 1024);
    if (!File.Exists(path))
    {
        return new LogSliceResult
        {
            Exists = false
        };
    }

    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    var endOffset = stream.Length;
    var reset = false;
    long startOffset;

    if (!offset.HasValue)
    {
        startOffset = Math.Max(0, endOffset - safeMaxBytes);
    }
    else if (offset.Value > endOffset)
    {
        reset = true;
        startOffset = Math.Max(0, endOffset - safeMaxBytes);
    }
    else
    {
        startOffset = offset.Value;
        if (endOffset - startOffset > safeMaxBytes)
            startOffset = Math.Max(startOffset, endOffset - safeMaxBytes);
    }

    var truncated = startOffset > 0 && (!offset.HasValue || startOffset != offset.Value);

    stream.Seek(startOffset, SeekOrigin.Begin);
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
    var text = reader.ReadToEnd();
    var entries = ParseLogLines(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

    return new LogSliceResult
    {
        Exists = true,
        StartOffset = startOffset,
        EndOffset = endOffset,
        Truncated = truncated,
        Reset = reset,
        Entries = entries
    };
}

static async Task<CommandOutputCapture> ReadCommandOutputDeltaAsync(
    string path,
    long startOffset,
    int waitMs,
    int maxBytes,
    int maxLines)
{
    var capture = new CommandOutputCapture
    {
        Exists = File.Exists(path),
        StartOffset = startOffset,
        EndOffset = startOffset
    };

    if (!capture.Exists)
        return capture;

    var entries = new List<LogEntry>();
    var endAt = DateTime.UtcNow.AddMilliseconds(Math.Max(200, waitMs));
    var settleUntilUtc = DateTime.MinValue;
    var nextOffset = startOffset;

    while (DateTime.UtcNow < endAt)
    {
        var slice = ReadLogSlice(path, nextOffset, maxBytes);
        capture.Exists = slice.Exists;
        capture.EndOffset = slice.EndOffset;
        capture.Truncated = capture.Truncated || slice.Truncated;
        capture.Reset = capture.Reset || slice.Reset;
        nextOffset = slice.EndOffset;

        if (slice.Entries.Count > 0)
        {
            entries.AddRange(slice.Entries);
            if (entries.Count > maxLines * 4)
                entries = entries.TakeLast(maxLines * 4).ToList();

            settleUntilUtc = DateTime.UtcNow.AddMilliseconds(350);
        }

        if (settleUntilUtc != DateTime.MinValue && DateTime.UtcNow >= settleUntilUtc)
            break;

        await Task.Delay(120);
    }

    capture.Entries = entries.TakeLast(maxLines).ToList();
    capture.Count = capture.Entries.Count;
    capture.Messages = capture.Entries
        .Select(entry => entry.Message)
        .Where(message => !string.IsNullOrWhiteSpace(message))
        .TakeLast(maxLines)
        .ToList();

    return capture;
}

static string GetServerLogPath(ServerConfig cfg) =>
    Path.IsPathRooted(cfg.LogFile)
        ? cfg.LogFile
        : Path.Combine(cfg.ServerDir, cfg.LogFile);

static DateTime? TryParseLogTimestamp(string line)
{
    // Rust log format: "12/31/2024 03:14:22: ..."
    if (line.Length > 20)
    {
        var candidate = line[..20].TrimEnd(':', ' ');
        if (DateTime.TryParseExact(candidate, "MM/dd/yyyy HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            return dt.ToUniversalTime();
    }

    // Supervisor / rustmgr trace format: "[2024-12-31T03:14:22Z] ..."
    if (line.StartsWith('['))
    {
        var close = line.IndexOf(']');
        if (close > 1 && DateTime.TryParse(line[1..close], out var dt2))
            return dt2.ToUniversalTime();
    }

    return null;
}

static string DetectLogLevel(string line)
{
    var u = line.ToUpperInvariant();
    if (u.Contains("ERROR") || u.Contains("EXCEPTION") || u.Contains("FATAL") || u.Contains("CRASH"))
        return "error";
    if (u.Contains("WARNING") || u.Contains("WARN"))
        return "warning";
    return "info";
}

// Parses a trace event line:  [2024-12-31T03:14:22Z] send: global.say hello
static TraceEvent? ParseTraceEvent(string line)
{
    if (string.IsNullOrWhiteSpace(line)) return null;

    DateTime? ts   = null;
    string    body = line.Trim();

    if (line.StartsWith('['))
    {
        var close = line.IndexOf(']');
        if (close > 1)
        {
            if (DateTime.TryParse(line[1..close], out var dt))
                ts = dt.ToUniversalTime();
            body = line[(close + 1)..].Trim();
        }
    }

    var colonIdx = body.IndexOf(':');
    var kind     = colonIdx > 0 ? body[..colonIdx].Trim() : "event";
    var detail   = colonIdx > 0 ? body[(colonIdx + 1)..].Trim() : body;

    return new TraceEvent { Timestamp = ts, Kind = kind, Detail = detail };
}

static string TailLines(string text, int lines)
{
    if (string.IsNullOrWhiteSpace(text)) return string.Empty;
    var all = text.Replace("\r\n", "\n").Split('\n');
    return string.Join(Environment.NewLine, all.TakeLast(lines));
}

static string? TryExtractJson(string? text)
{
    if (string.IsNullOrWhiteSpace(text)) return null;
    var trimmed  = text.Trim();
    var firstObj = trimmed.IndexOf('{');
    var firstArr = trimmed.IndexOf('[');
    if (firstObj == -1 && firstArr == -1) return null;

    var start = (firstObj == -1) ? firstArr
              : (firstArr == -1) ? firstObj
              : Math.Min(firstObj, firstArr);

    var candidate = trimmed[start..].Trim();
    try
    {
        using var _ = JsonDocument.Parse(candidate);
        return candidate;
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "Failed to extract JSON payload from RCON response.");
        return null;
    }
}

static string Escape(string value) => value.Replace("\"", "\\\"");

static string GetConfigPath(string server) =>
    Path.Combine(
        Environment.GetEnvironmentVariable("RUSTMGR_CONFIG") ?? "/opt/rust-manager/config",
        $"{server}.json");

static RconConnectionInfo ResolveRconConnectionInfo(string server, ServerConfig cfg)
{
    var host =
        ReadRawServerConfigValue(server, "rcon.ip") ??
        ReadArgValue(cfg.AdditionalArgs, "rcon.ip") ??
        ReadRawServerConfigValue(server, "server.ip") ??
        ReadArgValue(cfg.AdditionalArgs, "server.ip") ??
        "127.0.0.1";

    var webValue =
        ReadRawServerConfigValue(server, "rcon.web") ??
        ReadArgValue(cfg.AdditionalArgs, "rcon.web");
    var webEnabled = string.IsNullOrWhiteSpace(webValue) ||
        webValue == "1" ||
        webValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        webValue.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        webValue.Equals("on", StringComparison.OrdinalIgnoreCase);

    var trimmedHost = host.Trim().Trim('"');
    var port = (ushort)Math.Clamp(cfg.RconPort, 1, 65535);
    var password = cfg.RconPassword?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(password))
        throw new InvalidOperationException($"rcon.password is empty for '{server}'.");

    return new RconConnectionInfo(trimmedHost, port, password, webEnabled);
}

static string? ReadRawServerConfigValue(string server, string key)
{
    var path = GetConfigPath(server);
    if (!File.Exists(path))
        return null;

    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty(key, out var node))
            return null;

        return node.ValueKind switch
        {
            JsonValueKind.String => node.GetString(),
            JsonValueKind.Number => node.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => node.ToString()
        };
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "Failed to read raw server config value.", path: path);
        return null;
    }
}

static string? ReadArgValue(string additionalArgs, string key)
{
    if (string.IsNullOrWhiteSpace(additionalArgs))
        return null;

    var pattern = $@"(?:^|\s)\+{Regex.Escape(key)}\s+(?:""(?<v>[^""]+)""|(?<v>\S+))";
    var match = Regex.Match(additionalArgs, pattern, RegexOptions.IgnoreCase);
    return match.Success ? match.Groups["v"].Value : null;
}

static ServerConfig? LoadServerConfig(string server)
{
    var path = GetConfigPath(server);
    if (!File.Exists(path))
        return null;

    try
    {
        return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), JsonDefaults.Options);
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "Failed to deserialize server config.", server, path: path);
        return null;
    }
}

static void SaveServerConfig(ServerConfig config)
{
    File.WriteAllText(GetConfigPath(config.Name),
        JsonSerializer.Serialize(config, JsonDefaults.Options));
}

static int CountJsonFiles(string path) =>
    Directory.Exists(path) ? Directory.GetFiles(path, "*.json").Length : 0;

static object DescribeMailbox(string name, string path)
{
    var files = Directory.Exists(path)
        ? Directory.GetFiles(path, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(8)
            .Select(file => new MailboxFileSummary
            {
                Name = Path.GetFileName(file),
                ModifiedAtUtc = File.GetLastWriteTimeUtc(file),
                SizeBytes = new FileInfo(file).Length
            })
            .ToList()
        : new List<MailboxFileSummary>();

    return new
    {
        name,
        path,
        count = Directory.Exists(path) ? Directory.GetFiles(path, "*.json").Length : 0,
        files
    };
}

static AgentDashboardSnapshot LoadAgentMemorySnapshot(AgentRuntimePaths paths)
{
    var snapshot = LoadLegacyAgentMemorySnapshot(paths.StatePath);
    MergeNeoCortexSnapshot(snapshot, paths.NeoCortexRoot);
    return snapshot;
}

static AgentDashboardSnapshot LoadLegacyAgentMemorySnapshot(string path)
{
    var stateFile = BuildStateFileStatus(path);
    if (!File.Exists(path))
        return new AgentDashboardSnapshot
        {
            StateFile = stateFile
        };

    try
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;

        return new AgentDashboardSnapshot
        {
            AgentErrors = ReadStringArray(root, "agentErrors", 8),
            RecentIncidents = ReadRecentIncidents(root),
            RecentActions = ReadRecentActions(root),
            PendingActions = ReadPendingActions(root),
            RecentFeedback = ReadRecentFeedback(root),
            RuntimeStatus = ReadRuntimeStatus(root),
            LlmInteractions = ReadLlmInteractions(root),
            CapabilityGaps = ReadCapabilityGaps(root),
            SelfRepairHistory = ReadSelfRepairHistory(root),
            StateFile = new DashboardStateFileStatus
            {
                Path = stateFile.Path,
                Exists = stateFile.Exists,
                SizeBytes = stateFile.SizeBytes,
                LastWriteAtUtc = stateFile.LastWriteAtUtc,
                LastSavedAtUtc = ReadDateTime(root, "lastSavedAtUtc"),
                ParseOk = true
            }
        };
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "Failed to parse agent dashboard state file.", path: path);
        return new AgentDashboardSnapshot
        {
            AgentErrors = new List<string> { "Failed to parse agent-state.json." },
            StateFile = new DashboardStateFileStatus
            {
                Path = stateFile.Path,
                Exists = stateFile.Exists,
                SizeBytes = stateFile.SizeBytes,
                LastWriteAtUtc = stateFile.LastWriteAtUtc,
                ParseOk = false,
                ParseError = ex.Message
            }
        };
    }
}

static DashboardStateFileStatus BuildStateFileStatus(string path)
{
    var info = new FileInfo(path);
    return new DashboardStateFileStatus
    {
        Path = path,
        Exists = info.Exists,
        SizeBytes = info.Exists ? info.Length : 0,
        LastWriteAtUtc = info.Exists ? info.LastWriteTimeUtc : null
    };
}

static AgentRuntimePaths ResolveAgentRuntimePaths(string agentSettingsPath, string botSettingsPath, string defaultAgentRootDir)
{
    var agentSettings = TryLoadAgentSettingsFile(agentSettingsPath);
    var botSettings = TryLoadBotSettingsFile(botSettingsPath);
    var agentBaseDir = Path.GetDirectoryName(agentSettingsPath) ?? defaultAgentRootDir;
    var botBaseDir = Path.GetDirectoryName(botSettingsPath) ?? Path.Combine(Path.GetDirectoryName(defaultAgentRootDir) ?? "/opt/rust-manager", "SteamBot", "OpsSteamBot");

    return new AgentRuntimePaths
    {
        AgentSettingsPath = agentSettingsPath,
        BotSettingsPath = botSettingsPath,
        StatePath = RustOpsEnv.ResolveConfiguredPath(
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_AGENT_STATE_PATH") ?? agentSettings?.Memory?.StatePath,
            agentBaseDir,
            Path.Combine(defaultAgentRootDir, "data", "agent-state.json")),
        NeoCortexRoot = RustOpsEnv.ResolveConfiguredPath(
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_AGENT_NEOCORTEX_ROOT") ?? agentSettings?.Memory?.NeoCortexRoot,
            agentBaseDir,
            Path.Combine(defaultAgentRootDir, "data", "NeoCortex")),
        FeedbackInboxPath = RustOpsEnv.ResolveConfiguredPath(
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_FEEDBACK_INBOX_PATH") ?? agentSettings?.Inbox?.FeedbackInboxPath,
            agentBaseDir,
            Path.Combine(defaultAgentRootDir, "data", "feedback-inbox")),
        DecisionInboxPath = RustOpsEnv.ResolveConfiguredPath(
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_DECISION_INBOX_PATH") ?? agentSettings?.Inbox?.DecisionInboxPath,
            agentBaseDir,
            Path.Combine(defaultAgentRootDir, "data", "decision-inbox")),
        ChatInboxPath = RustOpsEnv.ResolveConfiguredPath(
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_CHAT_INBOX_PATH") ?? agentSettings?.Inbox?.ChatInboxPath,
            agentBaseDir,
            Path.Combine(defaultAgentRootDir, "data", "chat-inbox")),
        MessageOutboxPath = RustOpsEnv.ResolveConfiguredPath(
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_MESSAGE_OUTBOX_PATH") ?? agentSettings?.Outbox?.MessageOutboxPath,
            agentBaseDir,
            Path.Combine(defaultAgentRootDir, "data", "message-outbox")),
        SentOutboxPath = RustOpsEnv.ResolveConfiguredPath(
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_MESSAGE_OUTBOX_SENT_PATH") ?? botSettings?.Agent?.SentOutboxPath,
            botBaseDir,
            Path.Combine(defaultAgentRootDir, "data", "message-outbox-sent")),
        LogRulesPath = RustOpsEnv.ResolveConfiguredPath(
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_AGENT_LOG_RULES_PATH") ?? agentSettings?.Monitor?.LogRulesPath,
            agentBaseDir,
            Path.Combine(defaultAgentRootDir, "agent-log-rules.json"))
    };
}

static AgentLlmConfigView ReadAgentLlmConfig(string envPath, string agentSettingsPath, DashboardRuntimeStatus runtimeStatus)
{
    var env = ReadEnvFileKeyValues(envPath);
    var settings = TryLoadAgentSettingsFile(agentSettingsPath);
    var llm = settings?.Llm ?? settings?.LegacyOllama;

    return new AgentLlmConfigView
    {
        Provider = GetEnvValue(env, "RUSTOPS_LLM_PROVIDER") ?? llm?.Provider ?? runtimeStatus.LlmProvider ?? "lmstudio",
        Enabled = TryParseBooleanValue(
            GetEnvValue(env, "RUSTOPS_LLM_ENABLED") ?? GetEnvValue(env, "RUSTOPS_OLLAMA_ENABLED"),
            llm?.Enabled ?? runtimeStatus.LlmEnabled),
        BaseUrl = GetEnvValue(env, "RUSTOPS_LLM_BASE_URL")
            ?? GetEnvValue(env, "RUSTOPS_OLLAMA_BASE_URL")
            ?? llm?.BaseUrl
            ?? runtimeStatus.LlmBaseUrl
            ?? "http://127.0.0.1:1234",
        Model = GetEnvValue(env, "RUSTOPS_LLM_MODEL")
            ?? GetEnvValue(env, "RUSTOPS_OLLAMA_MODEL")
            ?? llm?.Model
            ?? runtimeStatus.LlmModel
            ?? string.Empty,
        ApiKey = GetEnvValue(env, "RUSTOPS_LLM_API_KEY")
            ?? GetEnvValue(env, "LM_API_TOKEN")
            ?? llm?.ApiKey
            ?? string.Empty,
        HttpReferer = GetEnvValue(env, "RUSTOPS_LLM_HTTP_REFERER")
            ?? llm?.HttpReferer
            ?? string.Empty,
        AppTitle = GetEnvValue(env, "RUSTOPS_LLM_APP_TITLE")
            ?? llm?.AppTitle
            ?? string.Empty,
        UseForRecommendations = TryParseBooleanValue(
            GetEnvValue(env, "RUSTOPS_LLM_USE_FOR_RECOMMENDATIONS")
            ?? GetEnvValue(env, "RUSTOPS_OLLAMA_USE_FOR_RECOMMENDATIONS"),
            llm?.UseForRecommendations ?? true),
        RequestStrategy = (GetEnvValue(env, "RUSTOPS_LLM_REQUEST_STRATEGY")
                ?? llm?.RequestStrategy
                ?? "fallback")
            .Trim()
            .ToLowerInvariant() is "race" ? "race" : "fallback",
        Secondary = new AgentLlmEndpointConfigView
        {
            Enabled = TryParseBooleanValue(
                GetEnvValue(env, "RUSTOPS_LLM_SECONDARY_ENABLED"),
                llm?.Secondary?.Enabled ?? false),
            BaseUrl = GetEnvValue(env, "RUSTOPS_LLM_SECONDARY_BASE_URL")
                ?? llm?.Secondary?.BaseUrl
                ?? string.Empty,
            Model = GetEnvValue(env, "RUSTOPS_LLM_SECONDARY_MODEL")
                ?? llm?.Secondary?.Model
                ?? string.Empty,
            ApiKey = GetEnvValue(env, "RUSTOPS_LLM_SECONDARY_API_KEY")
                ?? llm?.Secondary?.ApiKey
                ?? string.Empty,
            HttpReferer = GetEnvValue(env, "RUSTOPS_LLM_SECONDARY_HTTP_REFERER")
                ?? llm?.Secondary?.HttpReferer
                ?? string.Empty,
            AppTitle = GetEnvValue(env, "RUSTOPS_LLM_SECONDARY_APP_TITLE")
                ?? llm?.Secondary?.AppTitle
                ?? string.Empty
        },
        UseChatSystemPrompt = TryParseBooleanValue(
            GetEnvValue(env, "RUSTOPS_LLM_USE_CHAT_SYSTEM_PROMPT"),
            llm?.UseChatSystemPrompt ?? false),
        // Unescape \n sequences stored by WriteLlmConfig to keep env file single-line.
        ChatSystemPrompt = (GetEnvValue(env, "RUSTOPS_LLM_CHAT_SYSTEM_PROMPT") is { } envPrompt
            ? envPrompt.Replace("\\n", "\n")
            : null)
            ?? llm?.ChatSystemPrompt
            ?? "You are a local Rust server operations agent talking to an admin.\nUse the provided tools to inspect state and perform bounded operations.\nPrefer using tools over guessing.\nFor start, stop, restart, and validate-oxide you must target a known server.\nIf the server is unclear, ask a concise clarification question instead of guessing.\nUse recent memory, incidents, and action history to explain what is happening.\nReply naturally, with concrete operational language.\nStart with the direct answer, then key evidence or next action.\nDo not invent facts.\nYou may use self-diagnostics and workspace tools to improve your own behavior.\nAny file writes must stay inside the configured self-repair scope root.\nIf an admin asks to execute a server console command, use execute_server_command.\nIf an admin asks what a command does, use get_server_command_memory.\nIf an admin teaches command behavior, use teach_server_command.\nIf an admin asks about plugins or updates, use list_server_plugins and check_plugin_updates.\nIf an admin asks to push source changes to git, use git_push_branch.\nIf an admin asks to pull latest source updates, use git_pull_rebuild."
    };
}

static AgentCommandConfigView ReadAgentCommandConfig(string envPath, string agentSettingsPath)
{
    var env = ReadEnvFileKeyValues(envPath);
    var settings = TryLoadAgentSettingsFile(agentSettingsPath);
    var commandSettings = settings?.CommandExecution;

    var allowList = ParseCommandAllowList(GetEnvValue(env, "RUSTOPS_COMMANDS_ALLOWLIST"));
    if (allowList.Count == 0 && commandSettings?.AllowList is not null)
        allowList = commandSettings.AllowList
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    if (allowList.Count == 0)
        allowList = AgentCommandConfigView.DefaultAllowList.ToList();

    return new AgentCommandConfigView
    {
        Enabled = TryParseBooleanValue(
            GetEnvValue(env, "RUSTOPS_COMMANDS_ENABLED"),
            commandSettings?.Enabled ?? true),
        FreeMode = TryParseBooleanValue(
            GetEnvValue(env, "RUSTOPS_COMMANDS_FREE_MODE"),
            commandSettings?.FreeMode ?? false),
        DefaultWaitMs = TryParseInt32Value(
            GetEnvValue(env, "RUSTOPS_COMMANDS_DEFAULT_WAIT_MS"),
            commandSettings?.DefaultWaitMs ?? 2500,
            200,
            20_000),
        MaxWaitMs = TryParseInt32Value(
            GetEnvValue(env, "RUSTOPS_COMMANDS_MAX_WAIT_MS"),
            commandSettings?.MaxWaitMs ?? 12_000,
            500,
            30_000),
        MaxOutputChars = TryParseInt32Value(
            GetEnvValue(env, "RUSTOPS_COMMANDS_MAX_OUTPUT_CHARS"),
            commandSettings?.MaxOutputChars ?? 8000,
            500,
            64_000),
        AllowList = allowList
    };
}

static string? ValidateLlmConfig(AgentLlmConfigView config)
{
    if (config.Enabled)
    {
        if (string.IsNullOrWhiteSpace(config.Model))
            return "Primary model is required.";
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            return "Primary baseUrl is required.";
        if (!IsValidHttpUrl(config.BaseUrl))
            return "Primary baseUrl must be a valid absolute http/https URL.";
        if (!string.IsNullOrWhiteSpace(config.HttpReferer) && !IsValidHttpUrl(config.HttpReferer))
            return "Primary httpReferer must be a valid absolute http/https URL when provided.";
    }

    if (!string.Equals(config.RequestStrategy, "fallback", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(config.RequestStrategy, "race", StringComparison.OrdinalIgnoreCase))
    {
        return "requestStrategy must be either 'fallback' or 'race'.";
    }

    if (config.Secondary.Enabled)
    {
        if (string.IsNullOrWhiteSpace(config.Secondary.Model))
            return "Secondary model is required when secondary endpoint is enabled.";
        if (string.IsNullOrWhiteSpace(config.Secondary.BaseUrl))
            return "Secondary baseUrl is required when secondary endpoint is enabled.";
        if (!IsValidHttpUrl(config.Secondary.BaseUrl))
            return "Secondary baseUrl must be a valid absolute http/https URL.";
        if (!string.IsNullOrWhiteSpace(config.Secondary.HttpReferer) && !IsValidHttpUrl(config.Secondary.HttpReferer))
            return "Secondary httpReferer must be a valid absolute http/https URL when provided.";
    }

    return null;
}

static bool IsValidHttpUrl(string? value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        return false;

    return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}

static AgentSettingsFileView? TryLoadAgentSettingsFile(string path)
{
    if (!File.Exists(path))
        return null;

    try
    {
        var raw = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(raw);
        var settings = JsonSerializer.Deserialize<AgentSettingsFileView>(raw, JsonDefaults.Options);
        if (settings is null)
            return null;

        if (!doc.RootElement.TryGetProperty("llm", out _) &&
            doc.RootElement.TryGetProperty("ollama", out _) &&
            settings.LegacyOllama is not null)
        {
            settings.Llm = settings.LegacyOllama;
        }

        return settings;
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "Failed to load agent settings file.", path: path);
        return null;
    }
}

static void MergeNeoCortexSnapshot(AgentDashboardSnapshot snapshot, string neoCortexRoot)
{
    if (string.IsNullOrWhiteSpace(neoCortexRoot) || !Directory.Exists(neoCortexRoot))
    {
        return;
    }

    var operationsPath = Path.Combine(neoCortexRoot, "operations", "active-state.json");
    if (File.Exists(operationsPath))
    {
        try
        {
            using var operations = JsonDocument.Parse(File.ReadAllText(operationsPath));
            var root = operations.RootElement;

            if (root.TryGetProperty("runtimeStatus", out var runtimeStatus) && runtimeStatus.ValueKind == JsonValueKind.Object)
            {
                if (runtimeStatus.TryGetProperty("llmEnabled", out var llmEnabledNode) &&
                    (llmEnabledNode.ValueKind == JsonValueKind.True || llmEnabledNode.ValueKind == JsonValueKind.False))
                {
                    snapshot.RuntimeStatus.LlmEnabled = llmEnabledNode.GetBoolean();
                }
                snapshot.RuntimeStatus.LlmProvider = ReadString(runtimeStatus, "llmProvider") ?? snapshot.RuntimeStatus.LlmProvider;
                snapshot.RuntimeStatus.UpdatedAtUtc = ReadDateTime(runtimeStatus, "updatedAtUtc") ?? snapshot.RuntimeStatus.UpdatedAtUtc;
                snapshot.RuntimeStatus.LastLlmInteractionAtUtc = ReadDateTime(runtimeStatus, "lastLlmInteractionAtUtc") ?? snapshot.RuntimeStatus.LastLlmInteractionAtUtc;
            }

            if (root.TryGetProperty("recentActions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            {
                snapshot.RecentActions = snapshot.RecentActions
                    .Concat(actions.EnumerateArray().Select(item => new DashboardAction
                    {
                        ActionId = null,
                        ServerName = ReadString(item, "serverName"),
                        ActionType = ReadString(item, "intent"),
                        ExecutedAtUtc = ReadDateTime(item, "timestampUtc"),
                        Success = string.Equals(ReadString(item, "result"), "success", StringComparison.OrdinalIgnoreCase),
                        Trigger = "neocortex",
                        Summary = ReadString(item, "result")
                    }))
                    .OrderByDescending(item => item.ExecutedAtUtc)
                    .Take(20)
                    .ToList();
            }

            if (root.TryGetProperty("llmInteractions", out var interactions) && interactions.ValueKind == JsonValueKind.Array)
            {
                snapshot.LlmInteractions = interactions.EnumerateArray()
                    .Select(item => new DashboardLlmInteraction
                    {
                        AtUtc = ReadDateTime(item, "atUtc"),
                        Type = ReadString(item, "type"),
                        Model = ReadString(item, "model"),
                        Success = ReadBool(item, "success"),
                        Context = ReadString(item, "context"),
                        ResponsePreview = ReadString(item, "responsePreview")
                    })
                    .OrderByDescending(item => item.AtUtc)
                    .Take(20)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            CaptureHandledApiException(ex, "Failed to parse NeoCortex operations state.", path: operationsPath);
            snapshot.AgentErrors = snapshot.AgentErrors
                .Concat(new[] { "Failed to parse NeoCortex operations state." })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }
    }

    var evolutionPath = Path.Combine(neoCortexRoot, "evolution", "incidents.jsonl");
    if (!File.Exists(evolutionPath))
    {
        return;
    }

    try
    {
        var records = File.ReadLines(evolutionPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line =>
            {
                try
                {
                    return JsonDocument.Parse(line).RootElement.Clone();
                }
                catch
                {
                    return default;
                }
            })
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .ToList();

        snapshot.RecentIncidents = snapshot.RecentIncidents
            .Concat(records.Select(item => new DashboardIncident
            {
                ServerName = "general",
                CreatedAtUtc = ReadDateTime(item, "timestamp"),
                Title = ReadString(item, "classification"),
                Category = ReadString(item, "missingCapability"),
                Summary = ReadString(item, "failureReason")
            }))
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(20)
            .ToList();

        var groupedGaps = records
            .GroupBy(item => $"{ReadString(item, "classification")}|{ReadString(item, "missingCapability")}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group
                    .Select(item => ReadDateTime(item, "timestamp"))
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .OrderBy(value => value)
                    .ToList();
                return new DashboardCapabilityGap
                {
                    Category = group.Select(item => ReadString(item, "classification")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    Description = group.Select(item => ReadString(item, "recurrencePrevention")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                        ?? group.Select(item => ReadString(item, "failureReason")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    Count = group.Count(),
                    FirstObservedAtUtc = first.FirstOrDefault(),
                    LastObservedAtUtc = first.LastOrDefault()
                };
            })
            .OrderByDescending(item => item.LastObservedAtUtc)
            .Take(20)
            .ToList();

        if (groupedGaps.Count > 0)
        {
            snapshot.CapabilityGaps = groupedGaps;
        }
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "Failed to parse NeoCortex evolution state.", path: evolutionPath);
        snapshot.AgentErrors = snapshot.AgentErrors
            .Concat(new[] { "Failed to parse NeoCortex evolution state." })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }
}

static BotSettingsFileView? TryLoadBotSettingsFile(string path)
{
    if (!File.Exists(path))
        return null;

    try
    {
        return JsonSerializer.Deserialize<BotSettingsFileView>(File.ReadAllText(path), JsonDefaults.Options);
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(ex, "Failed to load Steam bot settings file.", path: path);
        return null;
    }
}

static Dictionary<string, string> ReadEnvFileKeyValues(string path)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path))
        return values;

    foreach (var rawLine in File.ReadAllLines(path))
    {
        var line = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            continue;

        var separator = line.IndexOf('=');
        if (separator <= 0)
            continue;

        values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
    }

    return values;
}

static string? GetEnvValue(Dictionary<string, string> env, string key) =>
    env.TryGetValue(key, out var value) ? value : null;

static bool TryParseBooleanValue(string? value, bool fallback)
{
    if (string.IsNullOrWhiteSpace(value))
        return fallback;

    return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("on", StringComparison.OrdinalIgnoreCase);
}

static int TryParseInt32Value(string? value, int fallback, int min, int max)
{
    if (string.IsNullOrWhiteSpace(value))
        return Math.Clamp(fallback, min, max);

    return int.TryParse(value, out var parsed)
        ? Math.Clamp(parsed, min, max)
        : Math.Clamp(fallback, min, max);
}

static List<string> ParseCommandAllowList(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return new List<string>();

    return value
        .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Select(item => item.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static void UpsertEnvFileValues(string path, Dictionary<string, string> updates)
{
    var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
    var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < lines.Count; i++)
    {
        var rawLine = lines[i];
        var trimmed = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            continue;

        var separator = rawLine.IndexOf('=');
        if (separator <= 0)
            continue;

        var key = rawLine[..separator].Trim();
        if (!updates.TryGetValue(key, out var newValue))
            continue;

        lines[i] = $"{key}={newValue}";
        touched.Add(key);
    }

    foreach (var update in updates.Where(update => !touched.Contains(update.Key)))
        lines.Add($"{update.Key}={update.Value}");

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllLines(path, lines);
}

static void UpsertAgentSettingsLlmValues(string path, AgentLlmConfigView update)
{
    JsonObject root;
    if (File.Exists(path))
    {
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }
    }
    else
    {
        root = new JsonObject();
    }

    var llm = root["llm"] as JsonObject ?? new JsonObject();
    llm["provider"] = string.IsNullOrWhiteSpace(update.Provider) ? "lmstudio" : update.Provider.Trim();
    llm["enabled"] = update.Enabled;
    llm["baseUrl"] = update.BaseUrl.Trim();
    llm["model"] = update.Model.Trim();
    llm["apiKey"] = update.ApiKey?.Trim() ?? string.Empty;
    llm["httpReferer"] = update.HttpReferer?.Trim() ?? string.Empty;
    llm["appTitle"] = update.AppTitle?.Trim() ?? string.Empty;
    llm["useForRecommendations"] = update.UseForRecommendations;
    llm["requestStrategy"] = string.IsNullOrWhiteSpace(update.RequestStrategy) ? "fallback" : update.RequestStrategy.Trim().ToLowerInvariant();
    llm["secondary"] = new JsonObject
    {
        ["enabled"] = update.Secondary.Enabled,
        ["baseUrl"] = update.Secondary.BaseUrl,
        ["model"] = update.Secondary.Model,
        ["apiKey"] = update.Secondary.ApiKey?.Trim() ?? string.Empty,
        ["httpReferer"] = update.Secondary.HttpReferer?.Trim() ?? string.Empty,
        ["appTitle"] = update.Secondary.AppTitle?.Trim() ?? string.Empty
    };
    llm["useChatSystemPrompt"] = update.UseChatSystemPrompt;
    llm["chatSystemPrompt"] = update.ChatSystemPrompt ?? string.Empty;
    root["llm"] = llm;

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, root.ToJsonString(JsonDefaults.Options));
}

static List<string> ReadStringArray(JsonElement root, string propertyName, int takeLast)
{
    if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        return new List<string>();

    return array.EnumerateArray()
        .Where(item => item.ValueKind == JsonValueKind.String)
        .Select(item => item.GetString() ?? string.Empty)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .TakeLast(takeLast)
        .ToList();
}

static DashboardRuntimeStatus ReadRuntimeStatus(JsonElement root)
{
    if (!root.TryGetProperty("runtimeStatus", out var status) || status.ValueKind != JsonValueKind.Object)
        return new DashboardRuntimeStatus();

    return new DashboardRuntimeStatus
    {
        LlmEnabled = ReadBoolAny(status, "llmEnabled", "ollamaEnabled"),
        LlmProvider = ReadStringAny(status, "llmProvider", "provider"),
        LlmModel = ReadStringAny(status, "llmModel", "ollamaModel"),
        LlmBaseUrl = ReadStringAny(status, "llmBaseUrl", "ollamaBaseUrl"),
        LogRulesPath = ReadString(status, "logRulesPath"),
        UpdatedAtUtc = ReadDateTime(status, "updatedAtUtc"),
        LastLlmInteractionAtUtc = ReadDateTime(status, "lastLlmInteractionAtUtc")
    };
}

static async Task<List<DashboardServiceStatus>> GetManagedServicesSnapshotAsync()
{
    var units = new[]
    {
        ("rustmgrapi.service", "API"),
        ("rustopsagent.service", "Agent"),
        ("opssteambot.service", "Steam Adapter")
    };

    var statuses = await Task.WhenAll(units.Select(unit => ReadManagedServiceStatusAsync(unit.Item1, unit.Item2)));
    return statuses.ToList();
}

static async Task<DashboardServiceStatus> ReadManagedServiceStatusAsync(string unitName, string label)
{
    var result = await ExecProcessAsync(
        "systemctl",
        "show",
        unitName,
        "--property=Description,LoadState,ActiveState,SubState,MainPID,ActiveEnterTimestamp");

    if (!result.Ok || string.IsNullOrWhiteSpace(result.StdOut))
    {
        return new DashboardServiceStatus
        {
            Name = label,
            Unit = unitName,
            ActiveState = "unknown",
            SubState = result.StdErr ?? "unavailable"
        };
    }

    var values = result.StdOut
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split('=', 2))
        .Where(parts => parts.Length == 2)
        .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

    int? mainPid = null;
    if (values.TryGetValue("MainPID", out var pidText) && int.TryParse(pidText, out var parsedPid) && parsedPid > 0)
        mainPid = parsedPid;

    return new DashboardServiceStatus
    {
        Name = label,
        Unit = unitName,
        Description = values.GetValueOrDefault("Description"),
        ActiveState = values.GetValueOrDefault("ActiveState") ?? "unknown",
        SubState = values.GetValueOrDefault("SubState"),
        MainPid = mainPid,
        Since = values.GetValueOrDefault("ActiveEnterTimestamp")
    };
}

static async Task<ProcessSnapshot?> ReadProcessSnapshotAsync(int pid)
{
    var result = await ExecProcessAsync("ps", "-p", pid.ToString(), "--no-headers", "-o", "etimes=,rss=");
    if (!result.Ok || string.IsNullOrWhiteSpace(result.StdOut))
        return null;

    var parts = result.StdOut
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (parts.Length < 2)
        return null;

    if (!long.TryParse(parts[0], out var uptimeSeconds))
        return null;

    if (!long.TryParse(parts[1], out var rssKb))
        return null;

    return new ProcessSnapshot
    {
        UptimeSeconds = uptimeSeconds,
        MemoryMb = Math.Round(rssKb / 1024d, 1)
    };
}

static async Task<PlayerSnapshot?> TryReadPlayerSnapshotAsync(string server)
{
    var result = await ExecRustMgrAsync("query", server, "playerlist");
    if (!result.Ok)
        return new PlayerSnapshot { QueryOk = false };

    var payload = TryExtractJson(result.StdOut);
    if (payload is null)
        return new PlayerSnapshot { QueryOk = false };

    using var json = JsonDocument.Parse(payload);
    var root = json.RootElement;
    var playersNode = root.ValueKind == JsonValueKind.Array
        ? root
        : FindArrayPropertyIgnoreCase(root, "players", "playerList", "playerlist");

    var names = playersNode.ValueKind == JsonValueKind.Array
        ? playersNode.EnumerateArray()
            .Select(player => ReadStringAny(player, "name", "displayName", "display_name", "username"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Take(5)
            .Select(name => name!)
            .ToList()
        : new List<string>();

    return new PlayerSnapshot
    {
        QueryOk = true,
        CurrentPlayers = playersNode.ValueKind == JsonValueKind.Array
            ? playersNode.GetArrayLength()
            : ReadIntAny(root, "players", "currentPlayers", "playersConnected", "onlinePlayers"),
        MaxPlayers = ReadIntAny(root, "maxPlayers", "max_players", "serverMaxPlayers"),
        PlayerNames = names
    };
}
	
static async Task<ServerInfoSnapshot?> TryReadServerInfoSnapshotAsync(string server)
{
    var result = await ExecRustMgrAsync("query", server, "serverinfo");
    if (!result.Ok)
        return null;

    var payload = TryExtractJson(result.StdOut);
    if (payload is null)
        return null;

    using var json = JsonDocument.Parse(payload);
    var root = json.RootElement;
    if (root.ValueKind != JsonValueKind.Object)
        return null;

    return new ServerInfoSnapshot
    {
        Hostname = ReadStringAny(root, "hostname", "name"),
        Map = ReadStringAny(root, "map", "level", "world", "mapName"),
        Framerate = ReadDoubleAny(root, "framerate", "fps", "frameRate"),
        QueuedPlayers = ReadIntAny(root, "queued", "queue", "queuedPlayers"),
        CurrentPlayers = ReadIntAny(root, "players", "currentPlayers", "playersConnected"),
        MaxPlayers = ReadIntAny(root, "maxPlayers", "max_players", "serverMaxPlayers")
    };
}

static JsonElement FindArrayPropertyIgnoreCase(JsonElement element, params string[] names)
{
    if (element.ValueKind != JsonValueKind.Object)
        return default;

    foreach (var property in element.EnumerateObject())
    {
        if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            continue;

        if (property.Value.ValueKind == JsonValueKind.Array)
            return property.Value;
    }

    return default;
}

static string? ReadStringAny(JsonElement element, params string[] names)
{
    if (element.ValueKind != JsonValueKind.Object)
        return null;

    foreach (var property in element.EnumerateObject())
    {
        if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            continue;

        if (property.Value.ValueKind == JsonValueKind.String)
            return property.Value.GetString();
    }

    return null;
}

static int? ReadIntAny(JsonElement element, params string[] names)
{
    if (element.ValueKind != JsonValueKind.Object)
        return null;

    foreach (var property in element.EnumerateObject())
    {
        if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            continue;

        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value))
            return value;

        if (property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), out value))
            return value;
    }

    return null;
}

static double? ReadDoubleAny(JsonElement element, params string[] names)
{
    if (element.ValueKind != JsonValueKind.Object)
        return null;

    foreach (var property in element.EnumerateObject())
    {
        if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            continue;

        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var value))
            return value;

        if (property.Value.ValueKind == JsonValueKind.String && double.TryParse(property.Value.GetString(), out value))
            return value;
    }

    return null;
}

static List<DashboardLlmInteraction> ReadLlmInteractions(JsonElement root)
{
    if (!root.TryGetProperty("llmInteractions", out var interactions) || interactions.ValueKind != JsonValueKind.Array)
        return new List<DashboardLlmInteraction>();

    return interactions.EnumerateArray()
        .Select(item => new DashboardLlmInteraction
        {
            AtUtc = ReadDateTime(item, "atUtc"),
            Type = ReadString(item, "type"),
            Model = ReadString(item, "model"),
            Success = ReadBool(item, "success"),
            Context = ReadString(item, "context"),
            ResponsePreview = ReadString(item, "responsePreview")
        })
        .OrderByDescending(item => item.AtUtc)
        .Take(12)
        .ToList();
}

static List<DashboardCapabilityGap> ReadCapabilityGaps(JsonElement root)
{
    if (!root.TryGetProperty("capabilityGaps", out var gaps) || gaps.ValueKind != JsonValueKind.Array)
        return new List<DashboardCapabilityGap>();

    return gaps.EnumerateArray()
        .Select(item => new DashboardCapabilityGap
        {
            Category = ReadString(item, "category"),
            Description = ReadString(item, "description"),
            Count = item.TryGetProperty("count", out var countNode) && countNode.ValueKind == JsonValueKind.Number ? countNode.GetInt32() : 1,
            FirstObservedAtUtc = ReadDateTime(item, "firstObservedAtUtc"),
            LastObservedAtUtc = ReadDateTime(item, "lastObservedAtUtc")
        })
        .OrderByDescending(item => item.LastObservedAtUtc)
        .Take(20)
        .ToList();
}

static List<DashboardSelfRepairRun> ReadSelfRepairHistory(JsonElement root)
{
    if (!root.TryGetProperty("selfRepairHistory", out var history) || history.ValueKind != JsonValueKind.Array)
        return new List<DashboardSelfRepairRun>();

    return history.EnumerateArray()
        .Select(item => new DashboardSelfRepairRun
        {
            AtUtc = ReadDateTime(item, "atUtc"),
            Summary = ReadString(item, "summary"),
            AppliedActions = item.TryGetProperty("appliedActions", out var appliedNode) && appliedNode.ValueKind == JsonValueKind.Number ? appliedNode.GetInt32() : 0,
            RejectedActions = item.TryGetProperty("rejectedActions", out var rejectedNode) && rejectedNode.ValueKind == JsonValueKind.Number ? rejectedNode.GetInt32() : 0,
            RawModelReasoning = ReadString(item, "rawModelReasoning")
        })
        .OrderByDescending(item => item.AtUtc)
        .Take(12)
        .ToList();
}

static async Task<LlmSummaryView> ReadLmStudioSummaryAsync(AgentLlmConfigView config)
{
    var summary = new LlmSummaryView
    {
        Provider = string.IsNullOrWhiteSpace(config.Provider) ? "lmstudio" : config.Provider,
        BaseUrl = config.BaseUrl,
        CurrentModel = config.Model
    };

    try
    {
        using var http = new HttpClient { BaseAddress = new Uri(config.BaseUrl.TrimEnd('/')) };
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);

        var nativeModelsText = await TryGetRemoteJsonAsync(http, "/api/v1/models");
        var openAiModelsText = await TryGetRemoteJsonAsync(http, "/v1/models");

        summary.Reachable = nativeModelsText is not null || openAiModelsText is not null;
        summary.Models = ExtractLmStudioModels(nativeModelsText, openAiModelsText);
        summary.LoadedModels = ExtractLmStudioLoadedModels(nativeModelsText);

        if (!string.IsNullOrWhiteSpace(config.Model))
        {
            var currentModel = summary.Models.FirstOrDefault(model =>
                string.Equals(model.Id, config.Model, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model.Name, config.Model, StringComparison.OrdinalIgnoreCase));
            if (currentModel is not null)
                summary.CurrentModelDetails = PrettyJson(JsonSerializer.Serialize(currentModel, JsonDefaults.Options));
        }
    }
    catch (Exception ex)
    {
        summary.Error = ex.Message;
        CaptureHandledApiException(ex, "Failed to read LM Studio summary.");
    }

    return summary;
}

static async Task<string?> TryGetRemoteJsonAsync(HttpClient http, string path)
{
    using var response = await http.GetAsync(path);
    if (!response.IsSuccessStatusCode)
        return null;

    return await response.Content.ReadAsStringAsync();
}

static List<LlmModelView> ExtractLmStudioModels(string? nativeJsonText, string? openAiJsonText)
{
    var results = new List<LlmModelView>();

    if (!string.IsNullOrWhiteSpace(nativeJsonText))
    {
        using var json = JsonDocument.Parse(nativeJsonText);
        var models = json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement
            : FindArrayPropertyIgnoreCase(json.RootElement, "data", "models");

        if (models.ValueKind == JsonValueKind.Array)
        {
            results.AddRange(models.EnumerateArray()
                .Select(model => new LlmModelView
                {
                    Id = ReadStringAny(model, "id", "identifier", "modelKey", "model"),
                    Name = ReadStringAny(model, "displayName", "name", "id", "identifier", "modelKey", "model"),
                    Publisher = ReadStringAny(model, "publisher", "owned_by", "owner"),
                    Architecture = ReadStringAny(model, "architecture", "family", "format"),
                    Quantization = ReadStringAny(model, "quantization", "quantization_level"),
                    ParameterSize = ReadStringAny(model, "parameter_size", "parameters"),
                    SizeBytes = ReadLongAny(model, "sizeBytes", "size"),
                    MaxContextLength = ReadIntAny(model, "maxContextLength", "contextLength", "max_context_length"),
                    Loaded = ReadBoolAny(model, "loaded", "isLoaded")
                })
                .Where(model => !string.IsNullOrWhiteSpace(model.Name)));
        }
    }

    if (results.Count == 0 && !string.IsNullOrWhiteSpace(openAiJsonText))
    {
        using var json = JsonDocument.Parse(openAiJsonText);
        var models = FindArrayPropertyIgnoreCase(json.RootElement, "data", "models");
        if (models.ValueKind == JsonValueKind.Array)
        {
            results.AddRange(models.EnumerateArray()
                .Select(model => new LlmModelView
                {
                    Id = ReadStringAny(model, "id", "model"),
                    Name = ReadStringAny(model, "id", "model"),
                    Publisher = ReadStringAny(model, "owned_by")
                })
                .Where(model => !string.IsNullOrWhiteSpace(model.Name)));
        }
    }

    return results
        .GroupBy(model => model.Id ?? model.Name ?? Guid.NewGuid().ToString("N"), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static List<string> GetOxideRootCandidates(ServerConfig normalized)
{
    return new[]
        {
            Path.Combine(normalized.ServerDir, "oxide"),
            Path.Combine(normalized.ServerDir, normalized.ServerIdentity, "oxide"),
            Path.Combine(normalized.ServerDir, "server", normalized.ServerIdentity, "oxide"),
            Path.Combine("/srv/rust", normalized.Name, "oxide"),
            Path.Combine("/srv/rust", normalized.ServerIdentity, "oxide")
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static List<LlmLoadedModelView> ExtractLmStudioLoadedModels(string? nativeJsonText)
{
    if (string.IsNullOrWhiteSpace(nativeJsonText))
        return new();

    using var json = JsonDocument.Parse(nativeJsonText);
    var models = json.RootElement.ValueKind == JsonValueKind.Array
        ? json.RootElement
        : FindArrayPropertyIgnoreCase(json.RootElement, "data", "models");

    if (models.ValueKind != JsonValueKind.Array)
        return new();

    var loaded = new List<LlmLoadedModelView>();
    foreach (var model in models.EnumerateArray())
    {
        var name = ReadStringAny(model, "displayName", "name", "id", "identifier", "modelKey", "model") ?? "model";
        var instances = FindArrayPropertyIgnoreCase(model, "loadedInstances", "loaded_instances", "instances");
        if (instances.ValueKind == JsonValueKind.Array && instances.GetArrayLength() > 0)
        {
            loaded.AddRange(instances.EnumerateArray().Select(instance => new LlmLoadedModelView
            {
                Name = name,
                State = ReadStringAny(instance, "state", "status") ?? "loaded",
                ContextLength = ReadIntAny(instance, "contextLength", "maxContextLength", "context_length"),
                Preset = ReadStringAny(instance, "preset", "presetName", "name")
            }));
            continue;
        }

        if (ReadBoolAny(model, "loaded", "isLoaded"))
        {
            loaded.Add(new LlmLoadedModelView
            {
                Name = name,
                State = ReadStringAny(model, "state", "status") ?? "loaded",
                ContextLength = ReadIntAny(model, "contextLength", "maxContextLength", "max_context_length"),
                Preset = ReadStringAny(model, "preset", "presetName")
            });
        }
    }

    return loaded
        .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static string? PrettyJson(string? jsonText)
{
    if (string.IsNullOrWhiteSpace(jsonText))
        return null;

    try
    {
        using var json = JsonDocument.Parse(jsonText);
        return JsonSerializer.Serialize(json.RootElement, JsonDefaults.Options);
    }
    catch
    {
        return jsonText;
    }
}

static List<DashboardIncident> ReadRecentIncidents(JsonElement root)
{
    if (!root.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
        return new List<DashboardIncident>();

    return servers.EnumerateArray()
        .SelectMany(server =>
        {
            var serverName = server.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "unknown" : "unknown";
            if (!server.TryGetProperty("incidents", out var incidents) || incidents.ValueKind != JsonValueKind.Array)
                return Enumerable.Empty<DashboardIncident>();

            return incidents.EnumerateArray().Select(incident => new DashboardIncident
            {
                ServerName = serverName,
                CreatedAtUtc = ReadDateTime(incident, "createdAtUtc"),
                Title = ReadString(incident, "title"),
                Category = ReadString(incident, "category"),
                Summary = ReadString(incident, "summary")
            });
        })
        .OrderByDescending(item => item.CreatedAtUtc)
        .Take(12)
        .ToList();
}

static List<DashboardAction> ReadRecentActions(JsonElement root)
{
    if (!root.TryGetProperty("actionHistory", out var actions) || actions.ValueKind != JsonValueKind.Array)
        return new List<DashboardAction>();

    return actions.EnumerateArray()
        .Select(action => new DashboardAction
        {
            ActionId = ReadString(action, "actionId"),
            ServerName = ReadString(action, "serverName"),
            ActionType = ReadString(action, "actionType"),
            ExecutedAtUtc = ReadDateTime(action, "executedAtUtc"),
            Success = ReadBool(action, "success"),
            Trigger = ReadString(action, "trigger"),
            Summary = ReadString(action, "summary")
        })
        .OrderByDescending(item => item.ExecutedAtUtc)
        .Take(12)
        .ToList();
}

static List<DashboardPendingAction> ReadPendingActions(JsonElement root)
{
    if (!root.TryGetProperty("pendingActions", out var actions) || actions.ValueKind != JsonValueKind.Array)
        return new List<DashboardPendingAction>();

    return actions.EnumerateArray()
        .Where(action => string.Equals(ReadString(action, "status"), "Pending", StringComparison.OrdinalIgnoreCase))
        .Select(action => new DashboardPendingAction
        {
            Id = ReadString(action, "id"),
            ServerName = ReadString(action, "serverName"),
            ActionType = ReadString(action, "actionType"),
            CreatedAtUtc = ReadDateTime(action, "createdAtUtc"),
            Summary = ReadString(action, "summary")
        })
        .OrderByDescending(item => item.CreatedAtUtc)
        .Take(12)
        .ToList();
}

static List<DashboardFeedback> ReadRecentFeedback(JsonElement root)
{
    if (!root.TryGetProperty("feedbackHistory", out var feedback) || feedback.ValueKind != JsonValueKind.Array)
        return new List<DashboardFeedback>();

    return feedback.EnumerateArray()
        .Select(item => new DashboardFeedback
        {
            ReceivedAtUtc = ReadDateTime(item, "receivedAtUtc"),
            AdminId = ReadString(item, "adminId"),
            ServerName = ReadString(item, "serverName"),
            ActionId = ReadString(item, "actionId"),
            Verdict = ReadString(item, "verdict"),
            Note = ReadString(item, "note")
        })
        .OrderByDescending(item => item.ReceivedAtUtc)
        .Take(12)
        .ToList();
}

static string? ReadString(JsonElement element, string propertyName) =>
    element.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.String
        ? node.GetString()
        : null;

static DateTime? ReadDateTime(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
        return null;

    return DateTime.TryParse(node.GetString(), out var value) ? value.ToUniversalTime() : null;
}

static bool ReadBool(JsonElement element, string propertyName) =>
    element.TryGetProperty(propertyName, out var node) &&
    node.ValueKind is JsonValueKind.True or JsonValueKind.False &&
    node.GetBoolean();

static bool ReadBoolAny(JsonElement element, params string[] propertyNames)
{
    foreach (var propertyName in propertyNames)
    {
        if (ReadBool(element, propertyName))
            return true;

        if (element.TryGetProperty(propertyName, out var node) && node.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return node.GetBoolean();
    }

    return false;
}

static long? ReadLong(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var node))
        return null;

    if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var value))
        return value;

    if (node.ValueKind == JsonValueKind.String && long.TryParse(node.GetString(), out value))
        return value;

    return null;
}

static long? ReadLongAny(JsonElement element, params string[] propertyNames)
{
    foreach (var propertyName in propertyNames)
    {
        var value = ReadLong(element, propertyName);
        if (value.HasValue)
            return value;
    }

    return null;
}

static string BuildDashboardHtml() => """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>RustOps Dashboard</title>
  <style>
    :root { --bg:#09111a; --panel:#0f1b28; --panel2:#152434; --line:#274057; --text:#e7f1f8; --muted:#8fa8bc; --good:#55d38a; --warn:#f4b95c; --bad:#ff6f61; --accent:#66c8ff; }
    * { box-sizing:border-box; }
    body { margin:0; font-family:"Segoe UI",system-ui,sans-serif; background:radial-gradient(circle at top left, rgba(102,200,255,.12), transparent 32%),linear-gradient(180deg,#081019,#0a1420 55%,#09111a); color:var(--text); }
    .wrap { max-width:1440px; margin:0 auto; padding:24px; }
    .topbar { display:grid; grid-template-columns:1.2fr .8fr auto; gap:14px; align-items:end; margin-bottom:18px; }
    .hero,.auth,.card { background:rgba(15,27,40,.92); border:1px solid var(--line); border-radius:18px; padding:18px; backdrop-filter:blur(10px); }
    .hero h1 { margin:0 0 8px; font-size:28px; }
    .muted { color:var(--muted); }
    .auth label { display:block; font-size:12px; color:var(--muted); margin-bottom:8px; }
    input,textarea { width:100%; border-radius:12px; border:1px solid var(--line); background:#09131d; color:var(--text); padding:11px 12px; }
    textarea { min-height:280px; font-family:Consolas, monospace; resize:vertical; }
    button { border:0; background:linear-gradient(135deg,#66c8ff,#2f8cff); color:#03121f; font-weight:700; border-radius:12px; padding:12px 16px; cursor:pointer; }
    button.ghost { background:#0b1723; color:var(--text); border:1px solid var(--line); }
    .tabs { display:flex; gap:10px; margin-bottom:16px; flex-wrap:wrap; }
    .tab { border:1px solid var(--line); background:#0b1723; color:var(--text); }
    .tab.active { background:linear-gradient(135deg,#66c8ff,#2f8cff); color:#03121f; border-color:transparent; }
    .section-group { display:none; }
    .section-group.active { display:block; }
    .stats,.grid,.micro-grid { display:grid; gap:14px; }
    .stats { grid-template-columns:repeat(6,minmax(0,1fr)); margin-bottom:18px; }
    .grid { grid-template-columns:1.1fr .9fr; }
    .micro-grid { grid-template-columns:repeat(3,minmax(0,1fr)); }
    .card h2,.card h3 { margin:0 0 12px; display:flex; align-items:center; gap:8px; }
    .pill { display:inline-flex; padding:4px 10px; border-radius:999px; font-size:12px; font-weight:700; text-transform:uppercase; letter-spacing:.04em; }
    .running,.active,.ok { background:rgba(85,211,138,.16); color:var(--good); }
    .offline,.failed,.inactive { background:rgba(255,111,97,.14); color:var(--bad); }
    .unknown,.pending,.degraded,.starting { background:rgba(244,185,92,.14); color:var(--warn); }
    table { width:100%; border-collapse:collapse; }
    th,td { text-align:left; padding:10px 8px; border-bottom:1px solid rgba(39,64,87,.7); vertical-align:top; font-size:14px; }
    .item { padding:12px; border:1px solid rgba(39,64,87,.7); border-radius:14px; background:rgba(21,36,52,.55); }
    .mailbox-grid,.kv { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:12px; }
    pre { white-space:pre-wrap; font-size:12px; color:var(--muted); margin:0; }
    .legend { display:inline-flex; align-items:center; justify-content:center; width:18px; height:18px; border-radius:999px; background:rgba(102,200,255,.16); color:var(--accent); font-size:12px; font-weight:700; position:relative; cursor:help; }
    .legend[data-tip]:hover::after { content:attr(data-tip); position:absolute; top:22px; left:0; width:300px; white-space:pre-wrap; padding:10px 12px; border-radius:12px; background:#08131d; border:1px solid var(--line); color:var(--text); z-index:10; box-shadow:0 10px 30px rgba(0,0,0,.35); }
    .row { display:flex; gap:12px; align-items:center; }
    .row > * { flex:1; }
    .check { display:flex; gap:10px; align-items:center; }
    .check input { width:auto; }
    .wide { grid-column:1 / -1; }
    .tiny { font-size:12px; }
    .chip-list { display:flex; gap:8px; flex-wrap:wrap; margin-top:8px; }
    .chip { display:inline-flex; align-items:center; padding:6px 10px; border-radius:999px; background:rgba(102,200,255,.10); border:1px solid rgba(102,200,255,.22); color:var(--text); font-size:12px; }
    .codebox { padding:14px; border:1px solid rgba(39,64,87,.7); border-radius:14px; background:#08131d; min-height:200px; max-height:420px; overflow-y:auto; }
    .list { display:grid; gap:10px; max-height:420px; overflow-y:auto; padding-right:4px; }
    .toolbar { display:flex; justify-content:space-between; gap:12px; align-items:center; margin-top:12px; flex-wrap:wrap; }
    @media (max-width:1080px) { .stats { grid-template-columns:repeat(2,minmax(0,1fr)); } .grid,.topbar,.mailbox-grid,.kv,.row,.micro-grid { grid-template-columns:1fr; display:grid; } }
  </style>
</head>
<body>
  <div class="wrap">
    <div class="topbar">
      <div class="hero"><h1>RustOps Dashboard</h1><div class="muted">Control-room view for server state, agent behavior, and LM Studio runtime.</div><div id="stamp" class="muted" style="margin-top:10px;">Not loaded yet.</div></div>
      <div class="auth"><label for="apiKey">API key</label><input id="apiKey" type="password" placeholder="X-Api-Key"></div>
      <div><button id="refresh">Refresh</button></div>
    </div>
    <div class="stats" id="stats"></div>
    <div class="tabs">
      <button class="tab active" data-target="serversGroup">Servers</button>
      <button class="tab" data-target="agentGroup">Agent</button>
      <button class="tab" data-target="llmGroup">LLM</button>
    </div>

    <section id="serversGroup" class="section-group active">
      <div class="grid">
        <div class="card wide"><h2>Server Status <span class="legend" data-tip="This pane merges rustmgr status with live server query data. Players, uptime, memory, queue, and FPS only populate while the server answers queries.">?</span></h2><table><thead><tr><th>Name</th><th>State</th><th>Players</th><th>Uptime</th><th>Memory</th><th>Autorestart</th><th>Warnings</th></tr></thead><tbody id="servers"></tbody></table></div>
        <div class="card wide"><h2>Live Query Snapshot <span class="legend" data-tip="Shows parsed details from recent server queries. Player preview comes from playerlist. Hostname, map, queue, and FPS come from serverinfo when available.">?</span></h2><div id="serverCards" class="micro-grid"></div></div>
      </div>
    </section>

    <section id="agentGroup" class="section-group">
      <div class="grid">
        <div class="card wide"><h2>Service Status <span class="legend" data-tip="All three core services should normally stay active: API, Agent, and Steam Adapter. If one is inactive or failed, the stack is only partially working.">?</span></h2><div id="services" class="micro-grid"></div></div>
        <div class="card"><h2>Agent Runtime</h2><div id="agentRuntime" class="kv"></div></div>
        <div class="card"><h2>Pending Actions</h2><div id="pending" class="list"></div></div>
        <div class="card"><h2>Recent Incidents</h2><div id="incidents" class="list"></div></div>
        <div class="card"><h2>Recent Actions</h2><div id="actions" class="list"></div></div>
        <div class="card"><h2>Mailboxes</h2><div id="mailboxes" class="mailbox-grid"></div></div>
        <div class="card"><h2>Agent Errors / Feedback</h2><div id="errors" class="list"></div><h3 style="margin-top:18px;">Recent Feedback</h3><div id="feedback" class="list"></div></div>
        <div class="card"><h2>Capability Gaps <span class="legend" data-tip="Patterns the agent recorded when it could not fulfill a request. Each entry feeds the self-repair evolution cycle to propose a concrete improvement.">?</span></h2><div id="capabilityGaps" class="list"></div></div>
        <div class="card"><h2>Evolution History <span class="legend" data-tip="Each entry is one self-repair cycle: how many bounded actions were applied, what they changed, and the LLM reasoning behind the decision.">?</span></h2><div id="selfRepairHistory" class="list"></div></div>
        <div class="card"><h2>Server Console <span class="legend" data-tip="Live error counts per server pulled from the agent's console monitor. Errors are deduplicated and counted since the last agent restart.">?</span></h2><div id="consoleStats" class="list"></div></div>
        <div class="card"><h2>Player Chat <span class="legend" data-tip="Stats computed from the player chat stream the agent reads in real time. Word counts cover today's messages only. Sentiment is updated every 30 minutes by the LLM.">?</span></h2><div id="chatStats" class="list"></div></div>
        <div class="card wide"><h2>Incident Feedback <span class="legend" data-tip="Each row is an incident the agent recorded when it failed to fulfill a request. Use the verdict buttons and note field to give the agent real feedback — it reads this to improve future handling.">?</span></h2><div id="incidentFeedbackStatus" class="muted" style="margin-bottom:10px;">Loading incidents…</div><div id="incidentFeedbackList" class="list"></div></div>
        <div class="card"><h2>Agent Log Rules <span class="legend" data-tip="Edit JSON arrays here.\nignoreContains: always ignore these substrings.\nstartupIgnoreContains: ignore only during startup window.\nincidentContains: always elevate these substrings.\nExample:\n{\n  &quot;ignoreContains&quot;: [&quot;known noise&quot;],\n  &quot;startupIgnoreContains&quot;: [&quot;shader unsupported&quot;],\n  &quot;incidentContains&quot;: [&quot;Error while compiling&quot;]\n}">?</span></h2><div id="rulesMeta" class="muted">Not loaded.</div><textarea id="rulesEditor" spellcheck="false" style="margin-top:12px;"></textarea><div class="toolbar"><div id="rulesSaveStatus" class="muted">No changes saved yet.</div><button id="saveRules">Save Rules</button></div></div>
        <div class="card"><h2>Command Policy <span class="legend" data-tip="Controls whether the agent can execute raw server commands.\nIf Free mode is disabled, only allowlisted commands can be executed.\nChanges apply after restarting rustopsagent.service.">?</span></h2><div id="commandConfigMeta" class="muted">Not loaded.</div><div class="row" style="margin-top:12px;"><label class="check"><input id="commandEnabled" type="checkbox"> Command execution enabled</label><label class="check"><input id="commandFreeMode" type="checkbox"> Free mode (no allowlist restriction)</label></div><div class="row" style="margin-top:12px;"><div><label for="commandDefaultWaitMs" class="tiny muted">Default wait (ms)</label><input id="commandDefaultWaitMs" type="number" min="200" max="20000" step="100"></div><div><label for="commandMaxWaitMs" class="tiny muted">Max wait (ms)</label><input id="commandMaxWaitMs" type="number" min="500" max="30000" step="100"></div></div><div class="row" style="margin-top:12px;"><div><label for="commandMaxOutputChars" class="tiny muted">Max output chars</label><input id="commandMaxOutputChars" type="number" min="500" max="64000" step="100"></div></div><div style="margin-top:12px;"><label for="commandAllowList" class="tiny muted">Allowlist (comma/line separated)</label><textarea id="commandAllowList" spellcheck="false" style="min-height:140px;"></textarea></div><div class="toolbar"><div id="commandSaveStatus" class="muted">No changes saved yet.</div><button id="saveCommandConfig">Save Command Policy</button></div></div>
      </div>
    </section>

    <section id="llmGroup" class="section-group">
      <div class="grid">
        <div class="card"><h2>LLM Runtime</h2><div id="ollamaRuntime" class="kv"></div><div id="llmRuntimeDetails" class="list" style="margin-top:12px;"></div></div>
        <div class="card"><h2>LM Studio Control <span class="legend" data-tip="This writes LLM settings to the shared env plus agentsettings.json. Restart rustopsagent.service after saving. You can disable system prompt injection and also configure secondary endpoint fallback/race behavior.">?</span></h2><div id="ollamaConfigMeta" class="muted">Not loaded.</div><div class="row" style="margin-top:12px;"><label class="check"><input id="ollamaEnabled" type="checkbox"> LLM enabled</label><label class="check"><input id="ollamaUseRecommendations" type="checkbox"> Use for recommendations</label></div><div class="row" style="margin-top:12px;"><label class="check"><input id="llmUseChatSystemPrompt" type="checkbox"> Send system prompt</label></div><div style="margin-top:12px;"><label for="llmChatSystemPrompt" class="tiny muted">System prompt text (used only when enabled)</label><textarea id="llmChatSystemPrompt" spellcheck="false" style="margin-top:8px; min-height:180px;"></textarea></div><div class="row" style="margin-top:12px;"><div><label for="ollamaBaseUrl" class="tiny muted">Primary Base URL</label><input id="ollamaBaseUrl" type="text" placeholder="http://127.0.0.1:1234"></div><div><label for="ollamaModel" class="tiny muted">Primary Model</label><input id="ollamaModel" type="text" placeholder="llmster"></div></div><div class="row" style="margin-top:12px;"><div><label for="llmProvider" class="tiny muted">Provider</label><input id="llmProvider" type="text" placeholder="lmstudio"></div><div><label for="llmApiKey" class="tiny muted">Primary API token (optional)</label><input id="llmApiKey" type="password" placeholder="LM Studio API token"></div></div><div class="row" style="margin-top:12px;"><div><label for="llmHttpReferer" class="tiny muted">Primary HTTP-Referer (optional)</label><input id="llmHttpReferer" type="text" placeholder="http://localhost"></div><div><label for="llmAppTitle" class="tiny muted">Primary App Title (optional)</label><input id="llmAppTitle" type="text" placeholder="RustOpsAgent"></div></div><div class="row" style="margin-top:12px;"><label class="check"><input id="llmSecondaryEnabled" type="checkbox"> Secondary endpoint enabled</label><div><label for="llmRequestStrategy" class="tiny muted">Request strategy</label><select id="llmRequestStrategy"><option value="fallback">fallback</option><option value="race">race</option></select></div></div><div class="row" style="margin-top:12px;"><div><label for="llmSecondaryBaseUrl" class="tiny muted">Secondary Base URL</label><input id="llmSecondaryBaseUrl" type="text" placeholder="http://127.0.0.1:8000"></div><div><label for="llmSecondaryModel" class="tiny muted">Secondary Model</label><input id="llmSecondaryModel" type="text" placeholder="model-fallback"></div></div><div class="row" style="margin-top:12px;"><div><label for="llmSecondaryApiKey" class="tiny muted">Secondary API token (optional)</label><input id="llmSecondaryApiKey" type="password" placeholder="Bearer token"></div></div><div class="row" style="margin-top:12px;"><div><label for="llmSecondaryHttpReferer" class="tiny muted">Secondary HTTP-Referer (optional)</label><input id="llmSecondaryHttpReferer" type="text" placeholder="http://localhost"></div><div><label for="llmSecondaryAppTitle" class="tiny muted">Secondary App Title (optional)</label><input id="llmSecondaryAppTitle" type="text" placeholder="RustOpsAgent"></div></div><div class="toolbar"><div id="ollamaSaveStatus" class="muted">No changes saved yet.</div><button id="saveOllamaConfig">Save LLM Settings</button></div></div>
        <div class="card"><h2>Installed Models</h2><div id="ollamaModels" class="list"></div></div>
        <div class="card"><h2>Loaded Models</h2><div id="ollamaRunning" class="list"></div></div>
        <div class="card wide"><h2>Current Model Details</h2><div class="codebox"><pre id="currentModelDetails">No model details loaded.</pre></div></div>
        <div class="card wide"><h2>Recent LLM Interactions <span class="legend" data-tip="Each record shows when the agent called the model, what kind of request it made, whether it succeeded, and a short response preview if one was recorded.">?</span></h2><div id="llmInteractions" class="list"></div></div>
      </div>
    </section>
  </div>
  <script>
    const $ = id => document.getElementById(id);
    const keyInput = $('apiKey');
    const rulesEditor = $('rulesEditor');
    const tabs = Array.from(document.querySelectorAll('.tab'));
    const panes = Array.from(document.querySelectorAll('.section-group'));
    const fromStorage = localStorage.getItem('rustops.apiKey');
    if (fromStorage) keyInput.value = fromStorage;

    const esc = value => String(value ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');
    const fmt = value => { if (!value) return 'n/a'; const date = new Date(value); return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleString(); };
    const dur = value => { const seconds = Number(value); if (!Number.isFinite(seconds)) return 'n/a'; const d = Math.floor(seconds / 86400); const h = Math.floor((seconds % 86400) / 3600); const m = Math.floor((seconds % 3600) / 60); return d > 0 ? `${d}d ${h}h ${m}m` : (h > 0 ? `${h}h ${m}m` : `${m}m`); };
    const bytes = value => { const n = Number(value); if (!Number.isFinite(n) || n <= 0) return 'n/a'; if (n >= 1073741824) return `${(n / 1073741824).toFixed(2)} GB`; if (n >= 1048576) return `${(n / 1048576).toFixed(1)} MB`; return `${Math.round(n / 1024)} KB`; };
    const metric = (label, value, hint = '') => `<div class="card"><div class="muted">${esc(label)}</div><div style="font-size:30px;font-weight:800;margin-top:6px;">${esc(value)}</div>${hint ? `<div class="tiny muted" style="margin-top:8px;">${esc(hint)}</div>` : ''}</div>`;
    const pill = state => { const s = (state || 'unknown').toLowerCase(); const cls = ['running','offline','failed','pending','active','inactive','degraded','ok','starting'].includes(s) ? s : 'unknown'; return `<span class="pill ${cls}">${esc(state || 'unknown')}</span>`; };
    const item = (title, body, meta = '') => `<div class="item"><div style="font-weight:700;">${esc(title)}</div><div class="muted" style="margin-top:6px;">${esc(body || '')}</div>${meta ? `<div style="margin-top:8px;font-size:12px;color:var(--muted);">${meta}</div>` : ''}</div>`;
    const kv = (label, value) => `<div class="item"><div class="muted tiny">${esc(label)}</div><div style="margin-top:6px;font-weight:700;">${esc(value ?? 'n/a')}</div></div>`;

    async function fetchJson(path, options = {}) {
      const response = await fetch(path, { ...options, headers: { 'X-Api-Key': keyInput.value.trim(), ...(options.headers || {}) } });
      if (!response.ok) throw new Error(`${response.status} ${await response.text()}`);
      return response.json();
    }


    function activateTab(target) {
      tabs.forEach(tab => tab.classList.toggle('active', tab.dataset.target === target));
      panes.forEach(pane => pane.classList.toggle('active', pane.id === target));
    }

    async function loadRules() {
      const data = await fetchJson('/agent/log-rules');
      rulesEditor.value = data.content || '';
      $('rulesMeta').textContent = `${data.path} ${data.exists ? '' : '(new file on first save)'}`;
      return data;
    }

    function renderServers(servers) {
      $('servers').innerHTML = (servers || []).map(server => `<tr><td><strong>${esc(server.name)}</strong><div class="tiny muted">${esc(server.hostname || 'hostname unavailable')}</div></td><td>${pill(server.state)}</td><td>${esc(server.currentPlayers ?? (server.online ? '?' : '-'))}/${esc(server.maxPlayers ?? '?')}</td><td>${esc(dur(server.uptimeSeconds))}</td><td>${server.memoryMb !== null && server.memoryMb !== undefined ? `${esc(server.memoryMb)} MB` : '-'}</td><td>${server.autoRestart ? 'on' : 'off'}</td><td>${esc(server.recentWarningCount ?? 0)}</td></tr>`).join('') || '<tr><td colspan="7" class="muted">No servers returned.</td></tr>';
      $('serverCards').innerHTML = (servers || []).map(server => `<div class="item"><div style="display:flex;justify-content:space-between;gap:8px;align-items:center;"><strong>${esc(server.name)}</strong>${pill(server.state)}</div><div class="muted" style="margin-top:6px;">${esc(server.hostname || 'hostname unavailable')}</div><div class="tiny muted" style="margin-top:8px;">Map ${esc(server.map || 'n/a')} | Queue ${esc(server.queuedPlayers ?? 0)} | FPS ${esc(server.framerate ?? 'n/a')}</div><div class="tiny muted" style="margin-top:8px;">PID ${esc(server.pid ?? '-')} | Uptime ${esc(dur(server.uptimeSeconds))} | Mem ${server.memoryMb !== null && server.memoryMb !== undefined ? `${esc(server.memoryMb)} MB` : 'n/a'} | ${server.queryOk ? 'query-ok' : (server.online ? 'query-missed' : 'offline')}</div><div class="chip-list">${(server.playerPreview || []).slice(0, 5).map(name => `<span class="chip">${esc(name)}</span>`).join('') || '<span class="muted tiny">No player names sampled.</span>'}</div></div>`).join('') || '<div class="muted">No live query data available.</div>';
    }

    function renderAgent(summary) {
      $('services').innerHTML = (summary.services || []).map(service => item(service.name, `${service.unit}${service.subState ? ` | ${service.subState}` : ''}`, `${pill(service.activeState || 'unknown')} ${service.mainPid ? `| PID ${esc(service.mainPid)}` : ''}${service.since ? ` | ${esc(service.since)}` : ''}`)).join('') || '<div class="muted">No service status available.</div>';
      const runtime = summary.runtimeStatus || {};
      const state = summary.agentState || {};
      const stateStatus = !state.exists ? 'missing' : (state.parseOk === false ? 'parse-failed' : 'ok');
      $('agentRuntime').innerHTML = [
        kv('Pending actions', summary.counts?.pendingActions ?? 0),
        kv('Incidents tracked', summary.counts?.incidents ?? 0),
        kv('Feedback inbox', summary.counts?.feedbackInbox ?? 0),
        kv('Chat inbox', summary.counts?.chatInbox ?? 0),
        kv('Last LLM use', fmt(runtime.lastLlmInteractionAtUtc)),
        kv('State file', `${stateStatus}${state.sizeBytes ? ` | ${bytes(state.sizeBytes)}` : ''}`),
        kv('State save', fmt(state.lastSavedAtUtc || state.lastWriteAtUtc)),
        kv('Rules path', runtime.logRulesPath || summary.host?.agentLogRulesPath || 'n/a')
      ].join('');
      $('pending').innerHTML = (summary.pendingActions || []).map(entry => item(`${entry.serverName} | ${entry.actionType}`, entry.summary || '', `${esc(fmt(entry.createdAtUtc))} | ${esc(entry.id)}`)).join('') || '<div class="muted">No pending actions.</div>';
      $('incidents').innerHTML = (summary.recentIncidents || []).map(entry => item(`${entry.serverName} | ${entry.title || entry.category || 'incident'}`, entry.summary || '', esc(fmt(entry.createdAtUtc)))).join('') || '<div class="muted">No incidents recorded.</div>';
      $('actions').innerHTML = (summary.recentActions || []).map(entry => item(`${entry.serverName} | ${entry.actionType}`, entry.summary || '', `${esc(fmt(entry.executedAtUtc))} | ${entry.success ? 'success' : 'failed'} | ${esc(entry.trigger)}`)).join('') || '<div class="muted">No recent actions.</div>';
      $('mailboxes').innerHTML = (summary.mailboxes || []).map(box => `<div class="item"><div style="display:flex;justify-content:space-between;gap:8px;"><strong>${esc(box.name)}</strong><span class="muted">${esc(box.count)} files</span></div><pre style="margin-top:8px;">${esc(box.path)}</pre><div class="muted" style="margin-top:8px;">${(box.files || []).slice(0, 4).map(file => `${esc(file.name)} (${fmt(file.modifiedAtUtc)})`).join('\n') || 'empty'}</div></div>`).join('') || '<div class="muted">No mailbox data available.</div>';
      const diagnostics = [];
      if (state.path) diagnostics.push(item('Agent state file', state.path, `${state.exists ? 'exists' : 'missing'}${state.parseError ? ` | ${esc(state.parseError)}` : ''}`));
      $('errors').innerHTML = diagnostics.concat((summary.agentErrors || []).map(text => item('Agent error', text || ''))).join('') || '<div class="muted">No recent agent errors.</div>';
      $('feedback').innerHTML = (summary.recentFeedback || []).map(entry => item(`${entry.verdict || 'note'} | ${entry.serverName || 'general'}`, entry.note || '', `${esc(fmt(entry.receivedAtUtc))} | ${esc(entry.adminId || 'unknown admin')}`)).join('') || '<div class="muted">No recent feedback.</div>';
      $('capabilityGaps').innerHTML = (summary.capabilityGaps || []).map(entry => item(`${esc(entry.category || 'unknown')} | count=${entry.count ?? 1}`, entry.description || '', `first=${esc(fmt(entry.firstObservedAtUtc))} | last=${esc(fmt(entry.lastObservedAtUtc))}`)).join('') || '<div class="muted">No capability gaps recorded yet. The agent will populate this once it encounters unfulfillable requests.</div>';
      $('selfRepairHistory').innerHTML = (summary.selfRepairHistory || []).map(entry => item(`${entry.appliedActions ?? 0} action(s) applied | ${entry.rejectedActions ?? 0} rejected`, entry.summary || '', `${esc(fmt(entry.atUtc))}${entry.rawModelReasoning ? ` | reasoning: ${esc(String(entry.rawModelReasoning).slice(0, 120))}` : ''}`)).join('') || '<div class="muted">No evolution cycles run yet. The agent runs self-repair when capability gaps or learning incidents exist.</div>';
    }

    function renderCommandConfig(commandConfig) {
      const values = commandConfig?.values || {};
      const allowList = Array.isArray(values.allowList) ? values.allowList : [];
      $('commandConfigMeta').textContent = `${commandConfig?.path || 'n/a'} | restart rustopsagent.service after save`;
      $('commandEnabled').checked = values.enabled !== false;
      $('commandFreeMode').checked = !!values.freeMode;
      $('commandDefaultWaitMs').value = Number(values.defaultWaitMs || 2500);
      $('commandMaxWaitMs').value = Number(values.maxWaitMs || 12000);
      $('commandMaxOutputChars').value = Number(values.maxOutputChars || 8000);
      $('commandAllowList').value = allowList.join('\n');
      $('commandAllowList').disabled = !!values.freeMode;
    }

    function renderLlm(summary, llmSummary, llmConfig) {
      const runtime = summary.runtimeStatus || {};
      const values = llmConfig?.values || {};
      const secondary = values.secondary || {};
      const configuredModel = runtime.llmModel || values.model || llmSummary?.currentModel || 'n/a';
      const configuredBaseUrl = runtime.llmBaseUrl || values.baseUrl || llmSummary?.baseUrl || 'n/a';
      const provider = runtime.llmProvider || values.provider || llmSummary?.provider || 'lmstudio';
      const reachable = !!llmSummary?.reachable;
      const loadedModels = llmSummary?.loadedModels || [];
      const installedModels = llmSummary?.models || [];

      $('ollamaRuntime').innerHTML = [
        kv('Provider', provider),
        kv('Enabled', runtime.llmEnabled ? 'yes' : 'no'),
        kv('Model', configuredModel),
        kv('Base URL', configuredBaseUrl),
        kv('System prompt', values.useChatSystemPrompt ? 'enabled' : 'disabled'),
        kv('Last interaction', fmt(runtime.lastLlmInteractionAtUtc))
      ].join('');

      $('llmRuntimeDetails').innerHTML = [
        item('LM Studio reachability', reachable ? 'Reachable from API host.' : 'Not reachable from API host.', `${pill(reachable ? 'active' : 'offline')}${llmSummary?.error ? ` | ${esc(llmSummary.error)}` : ''}`),
        item('Configured model', configuredModel, `Loaded ${esc(loadedModels.length)} | Installed ${esc(installedModels.length)}`),
        item('Routing', values.requestStrategy === 'race' ? 'race' : 'fallback', secondary.enabled ? 'secondary enabled' : 'secondary disabled')
      ].join('');

      $('ollamaConfigMeta').textContent = `${llmConfig?.path || 'n/a'} | restart rustopsagent.service after save`;
      $('ollamaEnabled').checked = !!values.enabled;
      $('ollamaUseRecommendations').checked = !!values.useForRecommendations;
      $('llmUseChatSystemPrompt').checked = !!values.useChatSystemPrompt;
      $('llmChatSystemPrompt').value = values.chatSystemPrompt || 'You are a local Rust server operations agent talking to an admin.\\nUse the provided tools to inspect state and perform bounded operations.\\nPrefer using tools over guessing.\\nFor start, stop, restart, and validate-oxide you must target a known server.\\nIf the server is unclear, ask a concise clarification question instead of guessing.\\nUse recent memory, incidents, and action history to explain what is happening.\\nReply naturally, with concrete operational language.\\nStart with the direct answer, then key evidence or next action.\\nDo not invent facts.\\nYou may use self-diagnostics and workspace tools to improve your own behavior.\\nAny file writes must stay inside the configured self-repair scope root.\\nIf an admin asks to execute a server console command, use execute_server_command.\\nIf an admin asks what a command does, use get_server_command_memory.\\nIf an admin teaches command behavior, use teach_server_command.\\nIf an admin asks about plugins or updates, use list_server_plugins and check_plugin_updates.\\nIf an admin asks to push source changes to git, use git_push_branch.\\nIf an admin asks to pull latest source updates, use git_pull_rebuild.';
      $('ollamaBaseUrl').value = values.baseUrl || 'http://127.0.0.1:1234';
      $('ollamaModel').value = values.model || '';
      $('llmProvider').value = values.provider || 'lmstudio';
      $('llmApiKey').value = values.apiKey || '';
      $('llmHttpReferer').value = values.httpReferer || '';
      $('llmAppTitle').value = values.appTitle || '';
      $('llmRequestStrategy').value = values.requestStrategy === 'race' ? 'race' : 'fallback';
      $('llmSecondaryEnabled').checked = !!secondary.enabled;
      $('llmSecondaryBaseUrl').value = secondary.baseUrl || '';
      $('llmSecondaryModel').value = secondary.model || '';
      $('llmSecondaryApiKey').value = secondary.apiKey || '';
      $('llmSecondaryHttpReferer').value = secondary.httpReferer || '';
      $('llmSecondaryAppTitle').value = secondary.appTitle || '';

      $('ollamaModels').innerHTML = installedModels.map(model => `<div class="item"><div style="display:flex;justify-content:space-between;gap:8px;align-items:center;"><strong>${esc(model.name || model.id || 'unnamed model')}</strong><button type="button" class="ghost use-model" data-model="${esc(model.id || model.name || '')}">Use</button></div><div class="muted" style="margin-top:6px;">${esc(model.publisher || 'publisher n/a')} | ${esc(model.architecture || 'architecture n/a')} | ${esc(model.parameterSize || 'params n/a')} | ${esc(model.quantization || 'quant n/a')}</div><div class="tiny muted" style="margin-top:8px;">${esc(bytes(model.sizeBytes))}${model.maxContextLength ? ` | context ${esc(model.maxContextLength)}` : ''}${model.loaded ? ' | loaded' : ''}</div></div>`).join('') || '<div class="muted">No installed models reported.</div>';
      $('ollamaRunning').innerHTML = loadedModels.map(model => item(model.name || 'unnamed model', `${model.state || 'state n/a'}${model.contextLength ? ` | ctx ${esc(model.contextLength)}` : ''}`, model.preset || 'no preset reported')).join('') || '<div class="muted">No models currently loaded.</div>';
      $('currentModelDetails').textContent = llmSummary?.currentModelDetails || 'No model details loaded.';
      $('llmInteractions').innerHTML = (summary.llmInteractions || []).map(entry => item(`${entry.type || 'llm'} | ${entry.success ? 'success' : 'failed'}`, entry.responsePreview || entry.context || '', `${esc(fmt(entry.atUtc))} | ${esc(entry.model || 'unknown model')}${entry.context ? ` | ${esc(entry.context)}` : ''}`)).join('') || '<div class="muted">No recorded LLM interactions yet.</div>';
    }

    async function load() {
      localStorage.setItem('rustops.apiKey', keyInput.value.trim());
      try {
        const [summary, _, llmSummary, llmConfig, commandConfig, consoleData, chatData] = await Promise.all([fetchJson('/dashboard/summary'), loadRules(), fetchJson('/host/llm/summary'), fetchJson('/agent/llm/config'), fetchJson('/agent/commands/config'), fetchJson('/agent/console-monitor').catch(() => ({})), fetchJson('/agent/player-chat/recent').catch(() => ({}))]);
        $('stamp').textContent = `Updated ${fmt(summary.generatedAtUtc)} | API ${summary.host.bindUrl}`;
        $('stats').innerHTML = [metric('Servers', summary.counts?.servers ?? 0, 'configured via rustmgr'), metric('Online', summary.counts?.onlineServers ?? 0, 'currently running'), metric('Pending Actions', summary.counts?.pendingActions ?? 0, 'agent approval queue'), metric('Incidents', summary.counts?.incidents ?? 0, 'recent tracked issues'), metric('Outbox', summary.counts?.messageOutbox ?? 0, 'messages waiting for Steam'), metric('LLM Calls', (summary.llmInteractions || []).length, 'recent recorded interactions'), metric('Capability Gaps', summary.counts?.capabilityGaps ?? 0, 'unfulfilled request patterns'), metric('Evolution Runs', summary.counts?.selfRepairRuns ?? 0, 'agent improvement cycles')].join('');
        renderServers(summary.servers || []);
        renderAgent(summary);
        renderLlm(summary, llmSummary, llmConfig);
        renderCommandConfig(commandConfig);
        renderConsoleStats(consoleData);
        renderChatStats(chatData);
      } catch (error) { alert(`Dashboard request failed: ${error.message}`); }
    }

    async function saveRules() {
      try { await fetchJson('/agent/log-rules', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: rulesEditor.value }); $('rulesSaveStatus').textContent = `Saved ${new Date().toLocaleString()}`; }
      catch (error) { alert(`Failed to save rules: ${error.message}`); }
    }

    async function saveOllamaConfig() {
      try {
        await fetchJson('/agent/llm/config', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ provider: $('llmProvider').value.trim() || 'lmstudio', enabled: $('ollamaEnabled').checked, baseUrl: $('ollamaBaseUrl').value.trim(), model: $('ollamaModel').value.trim(), apiKey: $('llmApiKey').value.trim(), httpReferer: $('llmHttpReferer').value.trim(), appTitle: $('llmAppTitle').value.trim(), useForRecommendations: $('ollamaUseRecommendations').checked, requestStrategy: $('llmRequestStrategy').value, secondary: { enabled: $('llmSecondaryEnabled').checked, baseUrl: $('llmSecondaryBaseUrl').value.trim(), model: $('llmSecondaryModel').value.trim(), apiKey: $('llmSecondaryApiKey').value.trim(), httpReferer: $('llmSecondaryHttpReferer').value.trim(), appTitle: $('llmSecondaryAppTitle').value.trim() }, useChatSystemPrompt: $('llmUseChatSystemPrompt').checked, chatSystemPrompt: $('llmChatSystemPrompt').value }) });
        $('ollamaSaveStatus').textContent = `Saved ${new Date().toLocaleString()} | restart rustopsagent.service`;
        await load();
      } catch (error) { alert(`Failed to save LLM settings: ${error.message}`); }
    }

    async function saveCommandConfig() {
      try {
        await fetchJson('/agent/commands/config', {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            enabled: $('commandEnabled').checked,
            freeMode: $('commandFreeMode').checked,
            defaultWaitMs: Number($('commandDefaultWaitMs').value || 2500),
            maxWaitMs: Number($('commandMaxWaitMs').value || 12000),
            maxOutputChars: Number($('commandMaxOutputChars').value || 8000),
            allowList: $('commandAllowList').value
              .split(/\r?\n|,/)
              .map(v => v.trim())
              .filter(v => v.length > 0)
          })
        });
        $('commandSaveStatus').textContent = `Saved ${new Date().toLocaleString()} | restart rustopsagent.service`;
        await load();
      } catch (error) { alert(`Failed to save command policy: ${error.message}`); }
    }

    function renderConsoleStats(data) {
      const servers = data?.servers || {};
      const entries = Object.entries(servers);
      if (entries.length === 0) { $('consoleStats').innerHTML = '<div class="muted">No console data yet.</div>'; return; }
      $('consoleStats').innerHTML = entries.map(([name, s]) => {
        const topErrors = (s.recentErrors || []).sort((a, b) => b.count - a.count).slice(0, 3);
        const topHtml = topErrors.length ? topErrors.map(e => `<div class="muted" style="margin-top:4px;font-size:12px;">• ${esc(e.message.length > 70 ? e.message.slice(0,70)+'…' : e.message)} <span style="color:var(--accent)">(${e.count}x)</span></div>`).join('') : '<div class="muted" style="font-size:12px;">No errors recorded.</div>';
        const total = s.totalErrorsIngested ?? 0;
        return `<div class="item"><div style="font-weight:700;">${esc(name)}</div><div class="muted" style="margin-top:4px;font-size:12px;">${total} error${total !== 1 ? 's' : ''} ingested total</div>${topHtml}</div>`;
      }).join('');
    }

    function renderChatStats(data) {
      const messages = data?.recentMessages || [];
      const score = data?.sentimentScore;
      const label = data?.sentimentLabel || 'unknown';
      const summary = data?.sentimentSummary || '';
      const themes = (data?.keyThemes || []).slice(0, 5);
      const feedback = (data?.constructiveFeedback || []).slice(0, 3);

      const today = new Date().toDateString();
      const todayMsgs = messages.filter(m => m.capturedAtUtc && new Date(m.capturedAtUtc).toDateString() === today);

      // Word frequency (excluding common stopwords)
      const stopwords = new Set(['the','and','to','a','in','i','you','is','it','of','for','on','that','this','was','are','with','he','she','they','we','be','at','by','not','my','have','had','has','do','me','can','get','just','so','go','up','got','did','yeah','ok','im','its','but','no','yes','hey','lol','gg','wtf','idk']);
      const freq = {};
      for (const m of messages) { for (const [w] of ((m.message||'').toLowerCase().matchAll(/\b[a-z]{3,}\b/g))) { if (!stopwords.has(w)) freq[w] = (freq[w]||0) + 1; } }
      const topWords = Object.entries(freq).sort((a,b)=>b[1]-a[1]).slice(0,8);

      // Fun word counts for today
      const funWords = ['fuck','shit','damn','noob','toxic','cheater','hacker','cope','seethe'];
      const funStats = funWords.map(w => [w, todayMsgs.filter(m=>(m.message||'').toLowerCase().includes(w)).length]).filter(([,c])=>c>0);

      const scoreColor = score == null ? 'var(--muted)' : score >= 6 ? '#4caf50' : score >= 4 ? '#ff9800' : '#f44336';
      const scoreHtml = score != null ? `<span style="color:${scoreColor};font-weight:700;">${score.toFixed(1)}/10</span> <span class="muted">${esc(label)}</span>` : '<span class="muted">Not yet analysed</span>';

      const parts = [
        `<div class="item"><div style="font-weight:700;">Sentiment</div><div style="margin-top:6px;">${scoreHtml}</div>${summary ? `<div class="muted" style="margin-top:4px;font-size:12px;">${esc(summary)}</div>` : ''}</div>`,
        `<div class="item"><div style="font-weight:700;">Volume</div><div class="muted" style="margin-top:6px;font-size:12px;">${messages.length} messages total &nbsp;·&nbsp; ${todayMsgs.length} today</div></div>`,
      ];
      if (themes.length) parts.push(`<div class="item"><div style="font-weight:700;">Key Themes</div><div class="muted" style="margin-top:6px;font-size:12px;">${themes.map(t=>esc(t)).join(' · ')}</div></div>`);
      if (feedback.length) parts.push(`<div class="item"><div style="font-weight:700;">Player Feedback</div>${feedback.map(f=>`<div class="muted" style="margin-top:4px;font-size:12px;">• ${esc(f)}</div>`).join('')}</div>`);
      if (topWords.length) parts.push(`<div class="item"><div style="font-weight:700;">Top Words</div><div style="margin-top:6px;font-size:12px;">${topWords.map(([w,c])=>`<span style="margin-right:10px;">${esc(w)} <span class="muted">${c}</span></span>`).join('')}</div></div>`);
      if (funStats.length) parts.push(`<div class="item"><div style="font-weight:700;">Today's Fun Facts</div>${funStats.map(([w,c])=>`<div class="muted" style="margin-top:4px;font-size:12px;">"${esc(w)}" said ${c}x today</div>`).join('')}</div>`);
      if (!parts.length) { $('chatStats').innerHTML = '<div class="muted">No player chat data yet.</div>'; return; }
      $('chatStats').innerHTML = parts.join('');
    }

    async function loadIncidentFeedback() {
      try {
        const incidents = await fetchJson('/agent/incidents/list');
        $('incidentFeedbackStatus').textContent = `${incidents.length} incident(s) recorded.`;
        if (!incidents.length) { $('incidentFeedbackList').innerHTML = '<div class="muted">No incidents recorded yet.</div>'; return; }
        $('incidentFeedbackList').innerHTML = incidents.map(inc => {
          const verdictBadge = inc.adminVerdict ? `<span class="pill ${inc.adminVerdict === 'resolved' ? 'ok' : inc.adminVerdict === 'wontfix' ? 'offline' : 'unknown'}" style="margin-left:8px;">${esc(inc.adminVerdict)}</span>` : '';
          return `<div class="item" data-incident-id="${esc(inc.id)}">
            <div style="display:flex;justify-content:space-between;align-items:center;gap:8px;flex-wrap:wrap;">
              <strong>${esc(inc.classification || 'unknown')}</strong>${verdictBadge}
              <span class="muted tiny">${esc(fmt(inc.timestamp))}</span>
            </div>
            <div class="muted" style="margin-top:6px;">${esc(inc.request || '')}</div>
            <div class="tiny muted" style="margin-top:6px;">Failure: ${esc(inc.failureReason || 'n/a')}</div>
            <div class="tiny muted">Missing: ${esc(inc.missingCapability || 'n/a')}</div>
            <div class="tiny muted">Prevention: ${esc(inc.recurrencePrevention || 'n/a')}</div>
            ${inc.adminNote ? `<div class="tiny" style="margin-top:6px;color:var(--accent);">Admin note: ${esc(inc.adminNote)}</div>` : ''}
            <div style="margin-top:10px;display:flex;gap:8px;flex-wrap:wrap;align-items:center;">
              <button type="button" class="ghost incident-verdict" data-id="${esc(inc.id)}" data-verdict="resolved" style="padding:6px 10px;font-size:12px;">Resolved</button>
              <button type="button" class="ghost incident-verdict" data-id="${esc(inc.id)}" data-verdict="wontfix" style="padding:6px 10px;font-size:12px;">Won&apos;t fix</button>
              <button type="button" class="ghost incident-verdict" data-id="${esc(inc.id)}" data-verdict="needs-work" style="padding:6px 10px;font-size:12px;">Needs work</button>
              <input type="text" class="incident-note" data-id="${esc(inc.id)}" placeholder="Admin note (optional)" value="${esc(inc.adminNote || '')}" style="flex:1;min-width:140px;padding:6px 10px;font-size:12px;">
            </div>
          </div>`;
        }).join('');
      } catch (error) { $('incidentFeedbackStatus').textContent = `Failed to load: ${error.message}`; }
    }

    async function submitIncidentFeedback(id, verdict, note) {
      try {
        await fetchJson(`/agent/incidents/${encodeURIComponent(id)}/feedback`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ verdict, note }) });
        await loadIncidentFeedback();
      } catch (error) { alert(`Failed to save feedback: ${error.message}`); }
    }

    tabs.forEach(tab => tab.addEventListener('click', () => {
      activateTab(tab.dataset.target);
      if (tab.dataset.target === 'agentGroup') loadIncidentFeedback();
    }));
    $('refresh').addEventListener('click', () => { load(); loadIncidentFeedback(); });
    $('saveRules').addEventListener('click', saveRules);
    $('saveOllamaConfig').addEventListener('click', saveOllamaConfig);
    $('saveCommandConfig').addEventListener('click', saveCommandConfig);
    $('commandFreeMode').addEventListener('change', () => { $('commandAllowList').disabled = $('commandFreeMode').checked; });
    document.addEventListener('click', event => {
      const target = event.target;
      if (!(target instanceof HTMLElement)) return;
      if (target.classList.contains('use-model')) { $('ollamaModel').value = target.dataset.model || ''; return; }
      if (target.classList.contains('incident-verdict')) {
        const id = target.dataset.id || '';
        const verdict = target.dataset.verdict || '';
        const noteEl = document.querySelector(`.incident-note[data-id="${CSS.escape(id)}"]`);
        const note = noteEl instanceof HTMLInputElement ? noteEl.value.trim() : '';
        submitIncidentFeedback(id, verdict, note);
      }
    });
    if (keyInput.value) { load(); loadIncidentFeedback(); }
  </script>
</body>
</html>
""";

static object BuildHostNetworkSummary()
{
    var capturedAtUtc = DateTime.UtcNow;
    var interfaces = ReadInterfaceCounters();
    var previous = NetworkSummaryCacheState.Previous;
    var elapsedSeconds = previous is not null
        ? Math.Max(0.001, (capturedAtUtc - previous.CapturedAtUtc).TotalSeconds)
        : 0d;

    foreach (var iface in interfaces)
    {
        if (previous?.Interfaces.TryGetValue(iface.Name, out var prior) == true && elapsedSeconds > 0)
        {
            var rxDelta = Math.Max(0L, iface.RxBytes - prior.RxBytes);
            var txDelta = Math.Max(0L, iface.TxBytes - prior.TxBytes);
            var combinedRateBytesPerSecond = (rxDelta + txDelta) / elapsedSeconds;

            iface.RxRateMiBps = Math.Round((rxDelta / elapsedSeconds) / (1024d * 1024d), 3);
            iface.TxRateMiBps = Math.Round((txDelta / elapsedSeconds) / (1024d * 1024d), 3);
            iface.CombinedRateMbps = Math.Round(combinedRateBytesPerSecond * 8d / 1_000_000d, 2);

            var nextAverage = prior.AverageCombinedRateMbps.HasValue
                ? (prior.AverageCombinedRateMbps.Value * 0.7) + (iface.CombinedRateMbps.Value * 0.3)
                : iface.CombinedRateMbps.Value;
            iface.AverageCombinedRateMbps = Math.Round(nextAverage, 2);

            var nextPeak = Math.Max(prior.PeakCombinedRateMbps ?? 0d, iface.CombinedRateMbps.Value);
            iface.PeakCombinedRateMbps = Math.Round(nextPeak, 2);

            if (iface.SpeedMbps.HasValue && iface.SpeedMbps.Value > 0)
            {
                iface.UtilizationPercent = Math.Round((iface.CombinedRateMbps.Value / iface.SpeedMbps.Value) * 100d, 1);
            }

            iface.SpikeDetected =
                iface.CombinedRateMbps.Value >= 10d &&
                iface.AverageCombinedRateMbps.Value > 0 &&
                iface.CombinedRateMbps.Value >= iface.AverageCombinedRateMbps.Value * 2d;
        }
    }

    NetworkSummaryCacheState.Previous = new NetworkSummarySample
    {
        CapturedAtUtc = capturedAtUtc,
        Interfaces = interfaces.ToDictionary(
            iface => iface.Name,
            iface => new NetworkInterfaceSample
            {
                RxBytes = iface.RxBytes,
                TxBytes = iface.TxBytes,
                PeakCombinedRateMbps = iface.PeakCombinedRateMbps,
                AverageCombinedRateMbps = iface.AverageCombinedRateMbps
            },
            StringComparer.OrdinalIgnoreCase)
    };

    var interesting = interfaces
        .Where(i =>
            i.SpikeDetected ||
            i.RxErrors > 0 || i.TxErrors > 0 ||
            i.RxDropped > 0 || i.TxDropped > 0 ||
            (i.CombinedRateMbps ?? 0d) >= 10d)
        .OrderByDescending(i => (i.CombinedRateMbps ?? 0d) + i.RxErrors + i.TxErrors + i.RxDropped + i.TxDropped)
        .ToList();

    return new
    {
        capturedAtUtc,
        sampleSeconds = previous is null ? (double?)null : Math.Round(elapsedSeconds, 2),
        interfaces,
        interestingInterfaces = interesting,
        topThroughputInterfaces = interfaces
            .Where(i => i.CombinedRateMbps.HasValue)
            .OrderByDescending(i => i.CombinedRateMbps)
            .Take(5)
            .ToList()
    };
}

static List<HostInterfaceCounter> ReadInterfaceCounters()
{
    const string sysClassNet = "/sys/class/net";
    if (!Directory.Exists(sysClassNet))
        return new List<HostInterfaceCounter>();

    return Directory.GetDirectories(sysClassNet)
        .Select(path =>
        {
            var name = Path.GetFileName(path);
            var statsDir = Path.Combine(path, "statistics");

            return new HostInterfaceCounter
            {
                Name = name,
                OperState = SafeReadText(Path.Combine(path, "operstate")),
                Mtu = SafeReadInt(Path.Combine(path, "mtu")),
                SpeedMbps = SafeReadInt(Path.Combine(path, "speed")),
                RxBytes = SafeReadLong(Path.Combine(statsDir, "rx_bytes")),
                TxBytes = SafeReadLong(Path.Combine(statsDir, "tx_bytes")),
                RxPackets = SafeReadLong(Path.Combine(statsDir, "rx_packets")),
                TxPackets = SafeReadLong(Path.Combine(statsDir, "tx_packets")),
                RxErrors = SafeReadLong(Path.Combine(statsDir, "rx_errors")),
                TxErrors = SafeReadLong(Path.Combine(statsDir, "tx_errors")),
                RxDropped = SafeReadLong(Path.Combine(statsDir, "rx_dropped")),
                TxDropped = SafeReadLong(Path.Combine(statsDir, "tx_dropped"))
            };
        })
        .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static string? SafeReadText(string path)
{
    try
    {
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }
    catch
    {
        return null;
    }
}

static int? SafeReadInt(string path)
{
    var text = SafeReadText(path);
    return int.TryParse(text, out var value) ? value : null;
}

static long SafeReadLong(string path)
{
    var text = SafeReadText(path);
    return long.TryParse(text, out var value) ? value : 0L;
}

static ServerConfig NormalizeConfig(string server, ServerConfig cfg) => new()
{
    Name                       = server,
    ServerHostname             = cfg.ServerHostname?.Trim()             ?? string.Empty,
    ServerDescription          = cfg.ServerDescription?.Trim()          ?? string.Empty,
    ServerUrl                  = cfg.ServerUrl?.Trim()                  ?? string.Empty,
    ServerLogoImage            = cfg.ServerLogoImage?.Trim()            ?? string.Empty,
    ServerHeaderImage          = cfg.ServerHeaderImage?.Trim()          ?? string.Empty,
    ServerTags                 = cfg.ServerTags?.Trim()                 ?? string.Empty,
    ServerIdentity             = (cfg.ServerIdentity?.Trim() is { Length: > 0 } id) ? id : server,
    ServerPort                 = cfg.ServerPort,
    RconPort                   = cfg.RconPort,
    AppPort                    = cfg.AppPort,
    ServerWorldSize            = cfg.ServerWorldSize,
    ServerSeed                 = cfg.ServerSeed,
    ServerMaxPlayers           = cfg.ServerMaxPlayers <= 0 ? 100 : cfg.ServerMaxPlayers,
    ServerLevel                = string.IsNullOrWhiteSpace(cfg.ServerLevel) ? "Procedural Map" : cfg.ServerLevel.Trim(),
    ServerLevelUrl             = cfg.ServerLevelUrl?.Trim()             ?? string.Empty,
    RconPassword               = cfg.RconPassword?.Trim()               ?? string.Empty,
    ServerReportsServerEndpoint= cfg.ServerReportsServerEndpoint?.Trim() ?? string.Empty,
    LogFile                    = string.IsNullOrWhiteSpace(cfg.LogFile) ? "Log.txt" : RustOpsEnv.NormalizePath(cfg.LogFile.Trim()),
    ServerEncryption           = string.IsNullOrWhiteSpace(cfg.ServerEncryption) ? "1" : cfg.ServerEncryption.Trim(),
    BoomboxServerUrlList       = cfg.BoomboxServerUrlList?.Trim()       ?? string.Empty,
    AdditionalArgs             = cfg.AdditionalArgs?.Trim()             ?? string.Empty,
    ServerDir                  = string.IsNullOrWhiteSpace(cfg.ServerDir) ? $"/srv/rust/{server}" : RustOpsEnv.NormalizePath(cfg.ServerDir.Trim())
};

static string? ValidateConfig(ServerConfig cfg)
{
    if (string.IsNullOrWhiteSpace(cfg.Name))            return "Name is required.";
    if (string.IsNullOrWhiteSpace(cfg.ServerHostname))  return "server.hostname is required.";
    if (string.IsNullOrWhiteSpace(cfg.ServerIdentity))  return "server.identity is required.";
    if (string.IsNullOrWhiteSpace(cfg.ServerDir))       return "serverDir is required.";
    if (string.IsNullOrWhiteSpace(cfg.RconPassword))    return "rcon.password is required.";
    if (cfg.ServerPort     is <= 0 or > 65535)          return "server.port is invalid.";
    if (cfg.RconPort       is <= 0 or > 65535)          return "rcon.port is invalid.";
    if (cfg.AppPort        is <= 0 or > 65535)          return "app.port is invalid.";
    if (cfg.ServerWorldSize <= 0)                       return "server.worldsize must be > 0.";
    if (cfg.ServerMaxPlayers <= 0)                      return "server.maxplayers must be > 0.";
    return null;
}

static List<string> FindConfigConflicts(ServerConfig cfg, string? ignoreServer = null)
{
    var conflicts = new List<string>();

    foreach (var path in Directory.GetFiles(
                 Environment.GetEnvironmentVariable("RUSTMGR_CONFIG") ?? "/opt/rust-manager/config",
                 "*.json",
                 SearchOption.TopDirectoryOnly))
    {
        try
        {
            var other = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), JsonDefaults.Options);
            if (other is null) continue;
            if (!string.IsNullOrWhiteSpace(ignoreServer) &&
                string.Equals(other.Name, ignoreServer, StringComparison.OrdinalIgnoreCase))
                continue;

            if (other.ServerPort == cfg.ServerPort) conflicts.Add($"server.port {cfg.ServerPort} already used by '{other.Name}'.");
            if (other.RconPort   == cfg.RconPort)   conflicts.Add($"rcon.port {cfg.RconPort} already used by '{other.Name}'.");
            if (other.AppPort    == cfg.AppPort)    conflicts.Add($"app.port {cfg.AppPort} already used by '{other.Name}'.");
            if (string.Equals(other.ServerIdentity, cfg.ServerIdentity, StringComparison.OrdinalIgnoreCase))
                conflicts.Add($"server.identity '{cfg.ServerIdentity}' already used by '{other.Name}'.");
            if (string.Equals(other.ServerDir, cfg.ServerDir, StringComparison.OrdinalIgnoreCase))
                conflicts.Add($"serverDir '{cfg.ServerDir}' already used by '{other.Name}'.");
        }
        catch
        {
            conflicts.Add($"Failed to inspect existing config '{Path.GetFileName(path)}'.");
        }
    }

    return conflicts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

static ValidationResult ValidateJsonFile(string path)
{
    try
    {
        using var _ = JsonDocument.Parse(File.ReadAllText(path));
        return new ValidationResult { Path = path, Ok = true };
    }
    catch (Exception ex)
    {
        return new ValidationResult { Path = path, Ok = false, Message = ex.Message };
    }
}

static ValidationResult ValidateOxidePluginFile(string path)
{
    var text = File.ReadAllText(path);
    var infoMatch = Regex.Match(
        text,
        "\\[\\s*Info\\s*\\(\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"(?<author>[^\"]+)\"\\s*,\\s*\"(?<version>[^\"]+)\"\\s*\\)\\s*\\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    var pluginName = infoMatch.Success ? infoMatch.Groups["name"].Value.Trim() : null;
    var pluginAuthor = infoMatch.Success ? infoMatch.Groups["author"].Value.Trim() : null;
    var pluginVersion = infoMatch.Success ? infoMatch.Groups["version"].Value.Trim() : null;
    var pluginSlug = !string.IsNullOrWhiteSpace(pluginName) ? ToPluginSlug(pluginName) : null;

    var hasPluginBase =
        text.Contains(": RustPlugin", StringComparison.Ordinal) ||
        text.Contains(": CovalencePlugin", StringComparison.Ordinal) ||
        text.Contains(": CSPlugin", StringComparison.Ordinal);

    if (!hasPluginBase)
    {
        return new ValidationResult
        {
            Path = path,
            Ok = false,
            Message = "Missing expected Oxide plugin base class.",
            PluginName = pluginName,
            PluginAuthor = pluginAuthor,
            PluginVersion = pluginVersion,
            PluginSlug = pluginSlug
        };
    }

    var open = text.Count(c => c == '{');
    var close = text.Count(c => c == '}');
    if (open != close)
    {
        return new ValidationResult
        {
            Path = path,
            Ok = false,
            Message = $"Brace mismatch: {open} '{{' vs {close} '}}'.",
            PluginName = pluginName,
            PluginAuthor = pluginAuthor,
            PluginVersion = pluginVersion,
            PluginSlug = pluginSlug
        };
    }

    return new ValidationResult
    {
        Path = path,
        Ok = true,
        PluginName = pluginName,
        PluginAuthor = pluginAuthor,
        PluginVersion = pluginVersion,
        PluginSlug = pluginSlug
    };
}

static string ToPluginSlug(string input)
{
    var slug = Regex.Replace(input.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-");
    return slug.Trim('-');
}


static string? BuildCronSchedule(ManagedTaskRequest request)
{
    if (!string.IsNullOrWhiteSpace(request.Schedule))
        return request.Schedule.Trim();

    if (!request.OnceAtUtc.HasValue)
        return null;

    var dt = request.OnceAtUtc.Value.ToUniversalTime();
    return $"{dt.Minute} {dt.Hour} {dt.Day} {dt.Month} *";
}

static string SanitizeTaskName(string input)
{
    var chars = input
        .Trim()
        .ToLowerInvariant()
        .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
        .ToArray();

    var cleaned = new string(chars);
    while (cleaned.Contains("--", StringComparison.Ordinal))
        cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);
    return cleaned.Trim('-');
}

static ManagedTaskInfo? ParseManagedTaskFile(string path)
{
    var lines = File.ReadAllLines(path);
    var cronLine = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#') && l.Contains(" root "));
    if (cronLine is null) return null;

    var split = cronLine.Split(' ', 7, StringSplitOptions.RemoveEmptyEntries);
    if (split.Length < 7) return null;

    var schedule = string.Join(' ', split.Take(5));
    var command = split[6];
    var nameLine = lines.FirstOrDefault(l => l.StartsWith("# name:", StringComparison.OrdinalIgnoreCase));

    return new ManagedTaskInfo
    {
        Name = nameLine?.Split(':', 2)[1].Trim() ?? Path.GetFileNameWithoutExtension(path),
        Schedule = schedule,
        Command = command,
        Path = path
    };
}

static Task<CommandExecutionResult> ExecRustMgrAsync(params string[] args) => new RustMgrExecutor().ExecuteAsync(args);

static async Task<CommandExecutionResult> ExecProcessAsync(string fileName, params string[] args)
{
    var psi = new ProcessStartInfo
    {
        FileName               = fileName,
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
        CreateNoWindow         = true
    };

    foreach (var arg in args) psi.ArgumentList.Add(arg);

    try
    {
        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            RustOpsSentry.CaptureMessage(
                $"Failed to start external process '{fileName}'.",
                "api.process",
                SentryLevel.Error,
                extras: new Dictionary<string, object?> { ["arguments"] = args });
            return new CommandExecutionResult { Ok = false, ExitCode = -1, Arguments = args, StdErr = $"Failed to start '{fileName}'." };
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CommandExecutionResult
        {
            Ok        = process.ExitCode == 0,
            ExitCode  = process.ExitCode,
            Arguments = new[] { fileName }.Concat(args),
            StdOut    = (await stdOutTask).Trim(),
            StdErr    = (await stdErrTask).Trim()
        };
    }
    catch (Exception ex)
    {
        CaptureHandledApiException(
            ex,
            $"External process '{fileName}' execution failed.",
            command: string.Join(' ', args),
            path: fileName);
        return new CommandExecutionResult
            { Ok = false, ExitCode = -1, Arguments = new[] { fileName }.Concat(args), StdErr = ex.Message };
    }
}

// -----------------------------------------------------------------------------
// Models
// -----------------------------------------------------------------------------

sealed class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented           = true,
        DefaultIgnoreCondition  = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed class ServerCommandRequest
{
    public string Command { get; set; } = string.Empty;
}

public sealed class ServerCommandExecRequest
{
    public string Command { get; set; } = string.Empty;
    public int WaitMs { get; set; } = 2500;
    public int MaxLines { get; set; } = 120;
    public int MaxBytes { get; set; } = 128 * 1024;
}

public sealed class ModerationRequest
{
    public string  SteamId { get; set; } = string.Empty;
    public string? Reason  { get; set; }
}

public sealed class ProvisionServerRequest
{
    public string Name { get; set; } = string.Empty;
    public bool CreateDirectories { get; set; }
    public ServerConfig? Config { get; set; }
}

public sealed class ManagedTaskRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Schedule { get; set; }
    public DateTime? OnceAtUtc { get; set; }
    public string Command { get; set; } = string.Empty;
}

public sealed class ServerStatusResponse
{
    public string  Name        { get; set; } = string.Empty;
    /// <summary>running | offline | starting | session-only | unknown</summary>
    public string  State       { get; set; } = "unknown";
    /// <summary>true only when State == "running"</summary>
    public bool    Online      { get; set; }
    public bool    AutoRestart { get; set; }
    public int?    Pid         { get; set; }
    public string  Raw         { get; set; } = string.Empty;
}

public sealed class LogEntry
{
    public DateTime? Timestamp { get; set; }
    public string    Level     { get; set; } = "info";
    public string    Message   { get; set; } = string.Empty;
}

public sealed class LogSliceResult
{
    public bool Exists { get; set; }
    public long StartOffset { get; set; }
    public long EndOffset { get; set; }
    public bool Truncated { get; set; }
    public bool Reset { get; set; }
    public List<LogEntry> Entries { get; set; } = new();
}

public sealed class CommandOutputCapture
{
    public bool Exists { get; set; }
    public long StartOffset { get; set; }
    public long EndOffset { get; set; }
    public bool Truncated { get; set; }
    public bool Reset { get; set; }
    public int Count { get; set; }
    public List<LogEntry> Entries { get; set; } = new();
    public List<string> Messages { get; set; } = new();
}

public sealed class TraceEvent
{
    public DateTime? Timestamp { get; set; }
    /// <summary>send | process start | process exit | kill | update | umod</summary>
    public string    Kind      { get; set; } = string.Empty;
    public string    Detail    { get; set; } = string.Empty;
}

public sealed class ValidationResult
{
    public string Path { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? PluginName { get; set; }
    public string? PluginAuthor { get; set; }
    public string? PluginVersion { get; set; }
    public string? PluginSlug { get; set; }
}

public sealed class ManagedTaskInfo
{
    public string Name { get; set; } = string.Empty;
    public string Schedule { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public sealed class AgentDashboardSnapshot
{
    public List<string> AgentErrors { get; set; } = new();
    public List<DashboardIncident> RecentIncidents { get; set; } = new();
    public List<DashboardAction> RecentActions { get; set; } = new();
    public List<DashboardPendingAction> PendingActions { get; set; } = new();
    public List<DashboardFeedback> RecentFeedback { get; set; } = new();
    public DashboardRuntimeStatus RuntimeStatus { get; set; } = new();
    public DashboardStateFileStatus StateFile { get; set; } = new();
    public List<DashboardLlmInteraction> LlmInteractions { get; set; } = new();
    public List<DashboardCapabilityGap> CapabilityGaps { get; set; } = new();
    public List<DashboardSelfRepairRun> SelfRepairHistory { get; set; } = new();
    public List<DashboardServiceStatus> Services { get; set; } = new();
}

public sealed class MailboxFileSummary
{
    public string Name { get; set; } = string.Empty;
    public DateTime ModifiedAtUtc { get; set; }
    public long SizeBytes { get; set; }
}

public sealed class DashboardIncident
{
    public string ServerName { get; set; } = string.Empty;
    public DateTime? CreatedAtUtc { get; set; }
    public string? Title { get; set; }
    public string? Category { get; set; }
    public string? Summary { get; set; }
}

public sealed class DashboardAction
{
    public string? ActionId { get; set; }
    public string? ServerName { get; set; }
    public string? ActionType { get; set; }
    public DateTime? ExecutedAtUtc { get; set; }
    public bool Success { get; set; }
    public string? Trigger { get; set; }
    public string? Summary { get; set; }
}

public sealed class DashboardPendingAction
{
    public string? Id { get; set; }
    public string? ServerName { get; set; }
    public string? ActionType { get; set; }
    public DateTime? CreatedAtUtc { get; set; }
    public string? Summary { get; set; }
}

public sealed class DashboardFeedback
{
    public DateTime? ReceivedAtUtc { get; set; }
    public string? AdminId { get; set; }
    public string? ServerName { get; set; }
    public string? ActionId { get; set; }
    public string? Verdict { get; set; }
    public string? Note { get; set; }
}

public sealed class DashboardRuntimeStatus
{
    public bool LlmEnabled { get; set; }
    public string? LlmProvider { get; set; }
    public string? LlmModel { get; set; }
    public string? LlmBaseUrl { get; set; }
    public string? LogRulesPath { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? LastLlmInteractionAtUtc { get; set; }
}

public sealed class DashboardStateFileStatus
{
    public string Path { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public long SizeBytes { get; set; }
    public DateTime? LastWriteAtUtc { get; set; }
    public DateTime? LastSavedAtUtc { get; set; }
    public bool? ParseOk { get; set; }
    public string? ParseError { get; set; }
}

public sealed class DashboardLlmInteraction
{
    public DateTime? AtUtc { get; set; }
    public string? Type { get; set; }
    public string? Model { get; set; }
    public bool Success { get; set; }
    public string? Context { get; set; }
    public string? ResponsePreview { get; set; }
}

public sealed class DashboardCapabilityGap
{
    public string? Category { get; set; }
    public string? Description { get; set; }
    public int Count { get; set; } = 1;
    public DateTime? FirstObservedAtUtc { get; set; }
    public DateTime? LastObservedAtUtc { get; set; }
}

public sealed class DashboardSelfRepairRun
{
    public DateTime? AtUtc { get; set; }
    public string? Summary { get; set; }
    public int AppliedActions { get; set; }
    public int RejectedActions { get; set; }
    public string? RawModelReasoning { get; set; }
}

public sealed class DashboardServiceStatus
{
    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ActiveState { get; set; } = "unknown";
    public string? SubState { get; set; }
    public int? MainPid { get; set; }
    public string? Since { get; set; }
}

public sealed class AgentRuntimePaths
{
    public string AgentSettingsPath { get; set; } = string.Empty;
    public string BotSettingsPath { get; set; } = string.Empty;
    public string StatePath { get; set; } = string.Empty;
    public string NeoCortexRoot { get; set; } = string.Empty;
    public string FeedbackInboxPath { get; set; } = string.Empty;
    public string DecisionInboxPath { get; set; } = string.Empty;
    public string ChatInboxPath { get; set; } = string.Empty;
    public string MessageOutboxPath { get; set; } = string.Empty;
    public string SentOutboxPath { get; set; } = string.Empty;
    public string LogRulesPath { get; set; } = string.Empty;
}

public class AgentLlmConfigUpdate
{
    public string Provider { get; set; } = "lmstudio";
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:1234";
    public string Model { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? HttpReferer { get; set; }
    public string? AppTitle { get; set; }
    public bool UseForRecommendations { get; set; } = true;
    public string RequestStrategy { get; set; } = "fallback";
    public AgentLlmEndpointConfigView? Secondary { get; set; }
    public bool UseChatSystemPrompt { get; set; }
    public string? ChatSystemPrompt { get; set; }
}

public sealed class AgentLlmConfigView
{
    public string Provider { get; set; } = "lmstudio";
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:1234";
    public string Model { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? HttpReferer { get; set; }
    public string? AppTitle { get; set; }
    public bool UseForRecommendations { get; set; } = true;
    public string RequestStrategy { get; set; } = "fallback";
    public AgentLlmEndpointConfigView Secondary { get; set; } = new();
    public bool UseChatSystemPrompt { get; set; }
    public string? ChatSystemPrompt { get; set; }
}

public sealed class AgentLlmEndpointConfigView
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? HttpReferer { get; set; }
    public string? AppTitle { get; set; }
}

public class AgentCommandConfigUpdate
{
    public bool Enabled { get; set; } = true;
    public bool FreeMode { get; set; }
    public int DefaultWaitMs { get; set; } = 2500;
    public int MaxWaitMs { get; set; } = 12_000;
    public int MaxOutputChars { get; set; } = 8000;
    public List<string>? AllowList { get; set; }
}

public sealed class AgentCommandConfigView
{
    public static readonly IReadOnlyList<string> DefaultAllowList = new[]
    {
        "playerlist",
        "serverinfo",
        "bans",
        "oxide.plugins",
        "status",
        "version"
    };

    public bool Enabled { get; set; } = true;
    public bool FreeMode { get; set; }
    public int DefaultWaitMs { get; set; } = 2500;
    public int MaxWaitMs { get; set; } = 12_000;
    public int MaxOutputChars { get; set; } = 8000;
    public List<string> AllowList { get; set; } = DefaultAllowList.ToList();
}

public sealed class LlmSummaryView
{
    public string Provider { get; set; } = "lmstudio";
    public string BaseUrl { get; set; } = string.Empty;
    public bool Reachable { get; set; }
    public string? Error { get; set; }
    public string? CurrentModel { get; set; }
    public string? CurrentModelDetails { get; set; }
    public List<LlmModelView> Models { get; set; } = new();
    public List<LlmLoadedModelView> LoadedModels { get; set; } = new();
}

public sealed class LlmModelView
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Publisher { get; set; }
    public string? Architecture { get; set; }
    public int? MaxContextLength { get; set; }
    public long? SizeBytes { get; set; }
    public string? ParameterSize { get; set; }
    public string? Quantization { get; set; }
    public bool Loaded { get; set; }
}

public sealed class LlmLoadedModelView
{
    public string? Name { get; set; }
    public string? State { get; set; }
    public int? ContextLength { get; set; }
    public string? Preset { get; set; }
}

public sealed class AgentSettingsFileView
{
    [JsonPropertyName("memory")] public AgentSettingsMemoryView? Memory { get; set; }
    [JsonPropertyName("inbox")] public AgentSettingsInboxView? Inbox { get; set; }
    [JsonPropertyName("outbox")] public AgentSettingsOutboxView? Outbox { get; set; }
    [JsonPropertyName("monitor")] public AgentSettingsMonitorView? Monitor { get; set; }
    [JsonPropertyName("commandExecution")] public AgentSettingsCommandExecutionView? CommandExecution { get; set; }
    [JsonPropertyName("llm")] public AgentSettingsLlmView? Llm { get; set; }
    [JsonPropertyName("ollama")] public AgentSettingsLlmView? LegacyOllama { get; set; }
}

public sealed class AgentSettingsMemoryView
{
    [JsonPropertyName("statePath")] public string? StatePath { get; set; }
    [JsonPropertyName("neoCortexRoot")] public string? NeoCortexRoot { get; set; }
}

public sealed class AgentSettingsInboxView
{
    [JsonPropertyName("feedbackInboxPath")] public string? FeedbackInboxPath { get; set; }
    [JsonPropertyName("decisionInboxPath")] public string? DecisionInboxPath { get; set; }
    [JsonPropertyName("chatInboxPath")] public string? ChatInboxPath { get; set; }
}

public sealed class AgentSettingsOutboxView
{
    [JsonPropertyName("messageOutboxPath")] public string? MessageOutboxPath { get; set; }
}

public sealed class AgentSettingsMonitorView
{
    [JsonPropertyName("logRulesPath")] public string? LogRulesPath { get; set; }
}

public sealed class AgentSettingsCommandExecutionView
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("freeMode")] public bool FreeMode { get; set; }
    [JsonPropertyName("defaultWaitMs")] public int DefaultWaitMs { get; set; } = 2500;
    [JsonPropertyName("maxWaitMs")] public int MaxWaitMs { get; set; } = 12_000;
    [JsonPropertyName("maxOutputChars")] public int MaxOutputChars { get; set; } = 8000;
    [JsonPropertyName("allowList")] public List<string> AllowList { get; set; } = AgentCommandConfigView.DefaultAllowList.ToList();
}

public sealed class AgentSettingsLlmView
{
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }
    [JsonPropertyName("httpReferer")] public string? HttpReferer { get; set; }
    [JsonPropertyName("appTitle")] public string? AppTitle { get; set; }
    [JsonPropertyName("useForRecommendations")] public bool UseForRecommendations { get; set; } = true;
    [JsonPropertyName("requestStrategy")] public string? RequestStrategy { get; set; }
    [JsonPropertyName("secondary")] public AgentSettingsLlmEndpointView? Secondary { get; set; }
    [JsonPropertyName("useChatSystemPrompt")] public bool UseChatSystemPrompt { get; set; }
    [JsonPropertyName("chatSystemPrompt")] public string? ChatSystemPrompt { get; set; }
}

public sealed class AgentSettingsLlmEndpointView
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }
    [JsonPropertyName("httpReferer")] public string? HttpReferer { get; set; }
    [JsonPropertyName("appTitle")] public string? AppTitle { get; set; }
}

public sealed class BotSettingsFileView
{
    [JsonPropertyName("agent")] public BotAgentPathsView? Agent { get; set; }
}

public sealed class BotAgentPathsView
{
    [JsonPropertyName("sentOutboxPath")] public string? SentOutboxPath { get; set; }
}

public sealed class ProcessSnapshot
{
    public long UptimeSeconds { get; set; }
    public double MemoryMb { get; set; }
}

public sealed class PlayerSnapshot
{
    public bool QueryOk { get; set; }
    public int? CurrentPlayers { get; set; }
    public int? MaxPlayers { get; set; }
    public List<string> PlayerNames { get; set; } = new();
}

public sealed class ServerInfoSnapshot
{
    public string? Hostname { get; set; }
    public string? Map { get; set; }
    public double? Framerate { get; set; }
    public int? QueuedPlayers { get; set; }
    public int? CurrentPlayers { get; set; }
    public int? MaxPlayers { get; set; }
}

public sealed class HostInterfaceCounter
{
    public string Name { get; set; } = string.Empty;
    public string? OperState { get; set; }
    public int? Mtu { get; set; }
    public int? SpeedMbps { get; set; }
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }
    public long RxPackets { get; set; }
    public long TxPackets { get; set; }
    public long RxErrors { get; set; }
    public long TxErrors { get; set; }
    public long RxDropped { get; set; }
    public long TxDropped { get; set; }
    public double? RxRateMiBps { get; set; }
    public double? TxRateMiBps { get; set; }
    public double? CombinedRateMbps { get; set; }
    public double? AverageCombinedRateMbps { get; set; }
    public double? PeakCombinedRateMbps { get; set; }
    public double? UtilizationPercent { get; set; }
    public bool SpikeDetected { get; set; }
}

public static class NetworkSummaryCacheState
{
    public static NetworkSummarySample? Previous { get; set; }
}

public sealed class NetworkSummarySample
{
    public DateTime CapturedAtUtc { get; set; }
    public Dictionary<string, NetworkInterfaceSample> Interfaces { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class NetworkInterfaceSample
{
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }
    public double? AverageCombinedRateMbps { get; set; }
    public double? PeakCombinedRateMbps { get; set; }
}

public sealed class ApiError
{
    public ApiError(string code, string message) { Code = code; Message = message; }
    public string Code    { get; }
    public string Message { get; }
}

public sealed class ServerConfig
{
    [JsonPropertyName("name")]                        public string Name                        { get; set; } = string.Empty;
    [JsonPropertyName("server.hostname")]             public string ServerHostname              { get; set; } = string.Empty;
    [JsonPropertyName("server.description")]          public string ServerDescription           { get; set; } = string.Empty;
    [JsonPropertyName("server.url")]                  public string ServerUrl                   { get; set; } = string.Empty;
    [JsonPropertyName("server.logoimage")]            public string ServerLogoImage             { get; set; } = string.Empty;
    [JsonPropertyName("server.headerimage")]          public string ServerHeaderImage           { get; set; } = string.Empty;
    [JsonPropertyName("server.tags")]                 public string ServerTags                  { get; set; } = string.Empty;
    [JsonPropertyName("server.identity")]             public string ServerIdentity              { get; set; } = string.Empty;
    [JsonPropertyName("server.port")]                 public int    ServerPort                  { get; set; }
    [JsonPropertyName("rcon.port")]                   public int    RconPort                    { get; set; }
    [JsonPropertyName("app.port")]                    public int    AppPort                     { get; set; }
    [JsonPropertyName("server.worldsize")]            public int    ServerWorldSize             { get; set; }
    [JsonPropertyName("server.seed")]                 public int    ServerSeed                  { get; set; }
    [JsonPropertyName("server.maxplayers")]           public int    ServerMaxPlayers            { get; set; }
    [JsonPropertyName("server.level")]                public string ServerLevel                 { get; set; } = "Procedural Map";
    [JsonPropertyName("server.levelurl")]             public string ServerLevelUrl              { get; set; } = string.Empty;
    [JsonPropertyName("rcon.password")]               public string RconPassword                { get; set; } = string.Empty;
    [JsonPropertyName("server.reportsserverendpoint")]public string ServerReportsServerEndpoint { get; set; } = string.Empty;
    [JsonPropertyName("logFile")]                     public string LogFile                     { get; set; } = "Log.txt";
    [JsonPropertyName("server.encryption")]           public string ServerEncryption            { get; set; } = string.Empty;
    [JsonPropertyName("boombox.serverurllist")]       public string BoomboxServerUrlList        { get; set; } = string.Empty;
    [JsonPropertyName("additionalArgs")]              public string AdditionalArgs              { get; set; } = string.Empty;
    [JsonPropertyName("serverDir")]                   public string ServerDir                   { get; set; } = string.Empty;
}

public sealed record RconConnectionInfo(string Host, ushort Port, string Password, bool WebRconEnabled);

internal sealed record IncidentFeedbackEntry(string? Verdict, string? Note, DateTime? AnsweredAtUtc);

/// <summary>
/// Lightweight RCON wrapper for Rust server console interaction.
/// Supports fire-and-forget commands and awaited reply matching.
/// </summary>
public sealed class RustRcon : IAsyncDisposable
{
    private RCON? _rcon;
    private readonly string _host;
    private readonly ushort _port;
    private readonly string _password;

    public bool IsConnected { get; private set; }

    public RustRcon(string host, ushort port, string password)
    {
        _host = host;
        _port = port;
        _password = password;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var address = await ResolveAddressAsync(_host, ct);
        _rcon = new RCON(address, _port, _password);
        await _rcon.ConnectAsync();
        IsConnected = true;
        _rcon.OnDisconnected += () => IsConnected = false;
    }

    /// <summary>Fire-and-forget. Does not wait for a response.</summary>
    public async Task SendAsync(string command)
    {
        EnsureConnected();
        await _rcon!.SendCommandAsync(command);
    }

    /// <summary>
    /// Sends a command and returns the direct response string.
    /// Note: RCON responses are best-effort � not all commands echo a reply.
    /// </summary>
    public async Task<string> SendAndReceiveAsync(string command, CancellationToken ct = default)
    {
        EnsureConnected();
        return await _rcon!.SendCommandAsync(command);
    }

    /// <summary>
    /// Sends a command and waits until a console log line matching
    /// <paramref name="filter"/> appears, or the timeout elapses.
    /// </summary>
    public async Task<string?> SendAndWaitForLogAsync(
        string command,
        Func<string, bool> filter,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        EnsureConnected();
        var reply = await _rcon!.SendCommandAsync(command);
        if (string.IsNullOrWhiteSpace(reply))
            return null;
        return filter(reply) ? reply : null;
    }

    private void EnsureConnected()
    {
        if (_rcon is null || !IsConnected)
            throw new InvalidOperationException("RCON is not connected. Call ConnectAsync() first.");
    }

    private static async Task<IPAddress> ResolveAddressAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var address))
            return address;

        var resolved = await Dns.GetHostAddressesAsync(host, ct);
        return resolved.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? resolved.FirstOrDefault()
            ?? throw new InvalidOperationException($"Could not resolve host '{host}'.");
    }

    public ValueTask DisposeAsync()
    {
        if (_rcon is not null)
        {
            IsConnected = false;
            _rcon.Dispose();
            _rcon = null;
        }

        return ValueTask.CompletedTask;
    }
}
