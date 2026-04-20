using System.Net.Http.Headers;
using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Sentry;

var configPath = args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "agentsettings.json");
RustOpsEnv.LoadFromDefaultLocations(configPath);
using var sentry = RustOpsSentry.Initialize("rustopsagent");

try
{
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file not found: {configPath}");
    Console.Error.WriteLine("Copy agentsettings.example.json to agentsettings.json and fill in your values.");
    return 1;
}

var config = LoadConfig(configPath);
Directory.CreateDirectory(Path.GetDirectoryName(config.Memory.StatePath)!);
Directory.CreateDirectory(config.Inbox.FeedbackInboxPath);
Directory.CreateDirectory(config.Inbox.DecisionInboxPath);
Directory.CreateDirectory(config.Inbox.ChatInboxPath);
Directory.CreateDirectory(config.Outbox.MessageOutboxPath);
Directory.CreateDirectory(config.SelfRepair.WorkspacePath);
Directory.CreateDirectory(config.SelfRepair.BuildOutputPath);

Console.WriteLine($"Config loaded from {Path.GetFullPath(configPath)}");
Console.WriteLine($"API base URL: {config.Api.BaseUrl}");
Console.WriteLine($"Agent state path: {config.Memory.StatePath}");
Console.WriteLine($"Feedback inbox: {config.Inbox.FeedbackInboxPath}");
Console.WriteLine($"Decision inbox: {config.Inbox.DecisionInboxPath}");
Console.WriteLine($"Chat inbox: {config.Inbox.ChatInboxPath}");
Console.WriteLine($"Message outbox: {config.Outbox.MessageOutboxPath}");
Console.WriteLine($"LLM provider: {config.Llm.Provider}");
Console.WriteLine($"LLM enabled: {config.Llm.Enabled}");
Console.WriteLine($"LLM strategy: {config.Llm.RequestStrategy}");
Console.WriteLine($"LLM secondary enabled: {config.Llm.Secondary.Enabled}");
if (config.Llm.Secondary.Enabled)
{
    Console.WriteLine($"LLM secondary base URL: {config.Llm.Secondary.BaseUrl}");
    Console.WriteLine($"LLM secondary model: {config.Llm.Secondary.Model}");
}
Console.WriteLine($"Self-repair workspace: {config.SelfRepair.WorkspacePath}");
Console.WriteLine($"Self-repair scope root: {config.SelfRepair.ScopeRootPath}");
Console.WriteLine($"Self-repair enabled: {config.SelfRepair.Enabled}");
Console.WriteLine($"Self-repair source root: {config.SelfRepair.SourceRootPath}");
Console.WriteLine($"Self-repair build output: {config.SelfRepair.BuildOutputPath}");
Console.WriteLine($"Self-repair source builds enabled: {config.SelfRepair.AllowSourceBuilds}");
Console.WriteLine($"Self-repair service restarts enabled: {config.SelfRepair.AllowServiceRestarts}");
Console.WriteLine($"Command execution enabled: {config.CommandExecution.Enabled}");
Console.WriteLine($"Command free mode: {config.CommandExecution.FreeMode}");
Console.WriteLine($"Command allowlist entries: {config.CommandExecution.AllowList.Count}");
Console.WriteLine($"Plugin update monitor enabled: {config.PluginUpdates.Enabled}");
Console.WriteLine($"Plugin update interval minutes: {config.PluginUpdates.CheckIntervalMinutes}");
Console.WriteLine($"Git ops enabled: {config.GitOps.Enabled}");
Console.WriteLine($"Git repo path: {config.GitOps.RepoPath}");
Console.WriteLine($"Git auto-pull enabled: {config.GitOps.AutoPullEnabled}");

using var api = new RustMgrApiClient(config.Api);
var executor = new RustMgrExecutor();
var logRules = AgentLogRulesFile.Load(config.Monitor.LogRulesPath);
var memory = AgentMemoryStore.Load(config.Memory.StatePath);
memory.UpdateRuntimeStatus(config.Llm.Enabled, config.Llm.Provider, config.Llm.Model, config.Llm.BaseUrl, config.Monitor.LogRulesPath);
using var llm = new LlmClient(config.Llm, memory.RecordLlmInteraction);
var agent = new RustOpsAgent(config, api, executor, llm, logRules, memory);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    agent.RequestStop();
};

await agent.RunAsync();
return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    SentrySdk.CaptureException(ex);
    return 1;
}
finally
{
    await RustOpsSentry.FlushAsync();
}

static AgentConfig LoadConfig(string path)
{
    var rawJson = File.ReadAllText(path);
    using var jsonDocument = JsonDocument.Parse(rawJson);
    var json = JsonSerializer.Deserialize<AgentConfig>(
        rawJson,
        JsonOptions.Default)
        ?? throw new InvalidOperationException("Failed to parse config.");

    if (!jsonDocument.RootElement.TryGetProperty("llm", out _) &&
        jsonDocument.RootElement.TryGetProperty("ollama", out _) &&
        json.LegacyOllama is not null)
    {
        json.Llm = json.LegacyOllama;
    }

    ApplyEnvironmentOverrides(json);

    var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
    json.BaseDirectory = dir;
    json.Memory.StatePath = RustOpsEnv.NormalizePath(RustOpsEnv.ResolvePlaceholders(json.Memory.StatePath));
    json.Inbox.FeedbackInboxPath = RustOpsEnv.NormalizePath(RustOpsEnv.ResolvePlaceholders(json.Inbox.FeedbackInboxPath));
    json.Inbox.DecisionInboxPath = RustOpsEnv.NormalizePath(RustOpsEnv.ResolvePlaceholders(json.Inbox.DecisionInboxPath));
    json.Inbox.ChatInboxPath = RustOpsEnv.NormalizePath(RustOpsEnv.ResolvePlaceholders(json.Inbox.ChatInboxPath));
    json.Outbox.MessageOutboxPath = RustOpsEnv.NormalizePath(RustOpsEnv.ResolvePlaceholders(json.Outbox.MessageOutboxPath));
    json.Monitor.LogRulesPath = RustOpsEnv.NormalizePath(RustOpsEnv.ResolvePlaceholders(json.Monitor.LogRulesPath));
    json.SelfRepair.WorkspacePath = RustOpsEnv.NormalizePath(RustOpsEnv.ResolvePlaceholders(json.SelfRepair.WorkspacePath));
    json.SelfRepair.ScopeRootPath = RustOpsEnv.NormalizePath(RustOpsEnv.ResolvePlaceholders(json.SelfRepair.ScopeRootPath));
    json.SelfRepair.SourceRootPath = RustOpsEnv.NormalizePath(RustOpsEnv.ResolvePlaceholders(json.SelfRepair.SourceRootPath));
    json.SelfRepair.BuildOutputPath = RustOpsEnv.NormalizePath(RustOpsEnv.ResolvePlaceholders(json.SelfRepair.BuildOutputPath));
    json.GitOps.RepoPath = RustOpsEnv.NormalizePath(RustOpsEnv.ResolvePlaceholders(json.GitOps.RepoPath));
    json.GitOps.RemoteName = RustOpsEnv.ResolvePlaceholders(json.GitOps.RemoteName);
    json.GitOps.BaseBranch = RustOpsEnv.ResolvePlaceholders(json.GitOps.BaseBranch);
    json.GitOps.PushBranchPrefix = RustOpsEnv.ResolvePlaceholders(json.GitOps.PushBranchPrefix);
    json.CommandExecution.AllowList = json.CommandExecution.AllowList
        .Select(item => RustOpsEnv.ResolvePlaceholders(item))
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .ToList();
    json.PluginUpdates.SearchUrlTemplate = RustOpsEnv.ResolvePlaceholders(json.PluginUpdates.SearchUrlTemplate);
    json.PluginUpdates.SearchFilter = RustOpsEnv.ResolvePlaceholders(json.PluginUpdates.SearchFilter);
    json.Llm.RequestStrategy = RustOpsEnv.ResolvePlaceholders(json.Llm.RequestStrategy);
    json.Llm.Secondary.BaseUrl = RustOpsEnv.ResolvePlaceholders(json.Llm.Secondary.BaseUrl);
    json.Llm.Secondary.Model = RustOpsEnv.ResolvePlaceholders(json.Llm.Secondary.Model);
    json.Llm.Secondary.ApiKey = RustOpsEnv.ResolvePlaceholders(json.Llm.Secondary.ApiKey);
    json.Llm.Secondary.HttpReferer = RustOpsEnv.ResolvePlaceholders(json.Llm.Secondary.HttpReferer);
    json.Llm.Secondary.AppTitle = RustOpsEnv.ResolvePlaceholders(json.Llm.Secondary.AppTitle);

    if (!Path.IsPathRooted(json.Memory.StatePath))
        json.Memory.StatePath = Path.GetFullPath(Path.Combine(dir, json.Memory.StatePath));
    if (!Path.IsPathRooted(json.Inbox.FeedbackInboxPath))
        json.Inbox.FeedbackInboxPath = Path.GetFullPath(Path.Combine(dir, json.Inbox.FeedbackInboxPath));
    if (!Path.IsPathRooted(json.Inbox.DecisionInboxPath))
        json.Inbox.DecisionInboxPath = Path.GetFullPath(Path.Combine(dir, json.Inbox.DecisionInboxPath));
    if (!Path.IsPathRooted(json.Inbox.ChatInboxPath))
        json.Inbox.ChatInboxPath = Path.GetFullPath(Path.Combine(dir, json.Inbox.ChatInboxPath));
    if (!Path.IsPathRooted(json.Outbox.MessageOutboxPath))
        json.Outbox.MessageOutboxPath = Path.GetFullPath(Path.Combine(dir, json.Outbox.MessageOutboxPath));
    if (!string.IsNullOrWhiteSpace(json.Monitor.LogRulesPath) && !Path.IsPathRooted(json.Monitor.LogRulesPath))
        json.Monitor.LogRulesPath = Path.GetFullPath(Path.Combine(dir, json.Monitor.LogRulesPath));
    if (!string.IsNullOrWhiteSpace(json.SelfRepair.WorkspacePath) && !Path.IsPathRooted(json.SelfRepair.WorkspacePath))
        json.SelfRepair.WorkspacePath = Path.GetFullPath(Path.Combine(dir, json.SelfRepair.WorkspacePath));
    if (!string.IsNullOrWhiteSpace(json.SelfRepair.ScopeRootPath) && !Path.IsPathRooted(json.SelfRepair.ScopeRootPath))
        json.SelfRepair.ScopeRootPath = Path.GetFullPath(Path.Combine(dir, json.SelfRepair.ScopeRootPath));
    if (!string.IsNullOrWhiteSpace(json.SelfRepair.SourceRootPath) && !Path.IsPathRooted(json.SelfRepair.SourceRootPath))
        json.SelfRepair.SourceRootPath = Path.GetFullPath(Path.Combine(dir, json.SelfRepair.SourceRootPath));
    if (!string.IsNullOrWhiteSpace(json.SelfRepair.BuildOutputPath) && !Path.IsPathRooted(json.SelfRepair.BuildOutputPath))
        json.SelfRepair.BuildOutputPath = Path.GetFullPath(Path.Combine(dir, json.SelfRepair.BuildOutputPath));
    if (!string.IsNullOrWhiteSpace(json.GitOps.RepoPath) && !Path.IsPathRooted(json.GitOps.RepoPath))
        json.GitOps.RepoPath = Path.GetFullPath(Path.Combine(dir, json.GitOps.RepoPath));

    ValidateResolvedConfig(json);
    return json;
}

static void ApplyEnvironmentOverrides(AgentConfig config)
{
    config.Api.BaseUrl = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_API_BASE_URL")
        ?? RustOpsEnv.ResolvePlaceholders(config.Api.BaseUrl);
    config.Api.ApiKey = RustOpsEnv.FirstNonEmptyEnvironment("RUSTMGR_API_KEY", "RUSTOPS_API_KEY")
        ?? RustOpsEnv.ResolvePlaceholders(config.Api.ApiKey);

    config.Memory.StatePath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_AGENT_STATE_PATH")
        ?? config.Memory.StatePath;
    config.Inbox.FeedbackInboxPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_FEEDBACK_INBOX_PATH")
        ?? config.Inbox.FeedbackInboxPath;
    config.Inbox.DecisionInboxPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_DECISION_INBOX_PATH")
        ?? config.Inbox.DecisionInboxPath;
    config.Inbox.ChatInboxPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_CHAT_INBOX_PATH")
        ?? config.Inbox.ChatInboxPath;
    config.Outbox.MessageOutboxPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_MESSAGE_OUTBOX_PATH")
        ?? config.Outbox.MessageOutboxPath;
    config.Policy.AllowAnyServerCommand = RustOpsEnv.GetBoolean("RUSTOPS_ALLOW_ANY_SERVER_COMMAND", config.Policy.AllowAnyServerCommand);
    var allowedCommandPrefixes = RustOpsEnv.GetCsvValues("RUSTOPS_ALLOWED_SERVER_COMMAND_PREFIXES");
    if (allowedCommandPrefixes.Count > 0)
        config.Policy.AllowedServerCommandPrefixes = allowedCommandPrefixes;

    config.Monitor.PollSeconds = RustOpsEnv.GetInt32("RUSTOPS_AGENT_POLL_SECONDS", config.Monitor.PollSeconds);
    config.Monitor.ControlPollSeconds = RustOpsEnv.GetInt32("RUSTOPS_AGENT_CONTROL_POLL_SECONDS", config.Monitor.ControlPollSeconds);
    config.Monitor.LogLinesPerScan = RustOpsEnv.GetInt32("RUSTOPS_AGENT_LOG_LINES", config.Monitor.LogLinesPerScan);
    config.Monitor.HealthCooldownMinutes = RustOpsEnv.GetInt32("RUSTOPS_AGENT_HEALTH_COOLDOWN_MINUTES", config.Monitor.HealthCooldownMinutes);
    config.Monitor.StartupIgnoreSeconds = RustOpsEnv.GetInt32("RUSTOPS_AGENT_STARTUP_IGNORE_SECONDS", config.Monitor.StartupIgnoreSeconds);
    config.Monitor.PluginUpdateCheckMinutes = RustOpsEnv.GetInt32("RUSTOPS_AGENT_PLUGIN_UPDATE_CHECK_MINUTES", config.Monitor.PluginUpdateCheckMinutes);
    config.Monitor.LogRulesPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_AGENT_LOG_RULES_PATH")
        ?? config.Monitor.LogRulesPath;
    config.SelfRepair.Enabled = RustOpsEnv.GetBoolean("RUSTOPS_SELF_REPAIR_ENABLED", config.SelfRepair.Enabled);
    config.SelfRepair.IntervalSeconds = RustOpsEnv.GetInt32("RUSTOPS_SELF_REPAIR_INTERVAL_SECONDS", config.SelfRepair.IntervalSeconds);
    config.SelfRepair.MaxActionsPerCycle = RustOpsEnv.GetInt32("RUSTOPS_SELF_REPAIR_MAX_ACTIONS", config.SelfRepair.MaxActionsPerCycle);
    config.SelfRepair.MaxFileBytes = RustOpsEnv.GetInt32("RUSTOPS_SELF_REPAIR_MAX_FILE_BYTES", config.SelfRepair.MaxFileBytes);
    config.SelfRepair.WorkspacePath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_SELF_REPAIR_WORKSPACE_PATH")
        ?? config.SelfRepair.WorkspacePath;
    config.SelfRepair.ScopeRootPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_SELF_REPAIR_SCOPE_ROOT")
        ?? config.SelfRepair.ScopeRootPath;
    config.SelfRepair.SourceRootPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_SELF_REPAIR_SOURCE_ROOT")
        ?? config.SelfRepair.SourceRootPath;
    config.SelfRepair.BuildOutputPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_SELF_REPAIR_BUILD_OUTPUT")
        ?? config.SelfRepair.BuildOutputPath;
    config.SelfRepair.AllowScopeFileWrites = RustOpsEnv.GetBoolean("RUSTOPS_SELF_REPAIR_ALLOW_SCOPE_WRITES", config.SelfRepair.AllowScopeFileWrites);
    config.SelfRepair.ApplyLogRuleUpdates = RustOpsEnv.GetBoolean("RUSTOPS_SELF_REPAIR_APPLY_LOG_RULES", config.SelfRepair.ApplyLogRuleUpdates);
    config.SelfRepair.ApplyReplyStyleUpdates = RustOpsEnv.GetBoolean("RUSTOPS_SELF_REPAIR_APPLY_REPLY_STYLE", config.SelfRepair.ApplyReplyStyleUpdates);
    config.SelfRepair.AllowSourceBuilds = RustOpsEnv.GetBoolean("RUSTOPS_SELF_REPAIR_ALLOW_SOURCE_BUILDS", config.SelfRepair.AllowSourceBuilds);
    config.SelfRepair.AllowServiceRestarts = RustOpsEnv.GetBoolean("RUSTOPS_SELF_REPAIR_ALLOW_SERVICE_RESTARTS", config.SelfRepair.AllowServiceRestarts);
    config.SelfRepair.NotifyAdmins = RustOpsEnv.GetBoolean("RUSTOPS_SELF_REPAIR_NOTIFY_ADMINS", config.SelfRepair.NotifyAdmins);

    config.GitOps.Enabled = RustOpsEnv.GetBoolean("RUSTOPS_GITOPS_ENABLED", config.GitOps.Enabled);
    config.GitOps.RepoPath = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_GITOPS_REPO_PATH")
        ?? config.GitOps.RepoPath;
    config.GitOps.RemoteName = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_GITOPS_REMOTE")
        ?? config.GitOps.RemoteName;
    config.GitOps.BaseBranch = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_GITOPS_BASE_BRANCH")
        ?? config.GitOps.BaseBranch;
    config.GitOps.PushBranchPrefix = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_GITOPS_PUSH_BRANCH_PREFIX")
        ?? config.GitOps.PushBranchPrefix;
    config.GitOps.AllowPush = RustOpsEnv.GetBoolean("RUSTOPS_GITOPS_ALLOW_PUSH", config.GitOps.AllowPush);
    config.GitOps.AllowManualPullRebuild = RustOpsEnv.GetBoolean("RUSTOPS_GITOPS_ALLOW_MANUAL_PULL_REBUILD", config.GitOps.AllowManualPullRebuild);
    config.GitOps.AutoPullEnabled = RustOpsEnv.GetBoolean("RUSTOPS_GITOPS_AUTO_PULL_ENABLED", config.GitOps.AutoPullEnabled);
    config.GitOps.AutoPullIntervalMinutes = RustOpsEnv.GetInt32("RUSTOPS_GITOPS_AUTO_PULL_INTERVAL_MINUTES", config.GitOps.AutoPullIntervalMinutes);
    config.GitOps.AutoPullRebuild = RustOpsEnv.GetBoolean("RUSTOPS_GITOPS_AUTO_PULL_REBUILD", config.GitOps.AutoPullRebuild);
    config.GitOps.AutoRestartAfterPullRebuild = RustOpsEnv.GetBoolean("RUSTOPS_GITOPS_AUTO_RESTART_AFTER_PULL_REBUILD", config.GitOps.AutoRestartAfterPullRebuild);
    config.GitOps.RequireCleanWorktreeForPull = RustOpsEnv.GetBoolean("RUSTOPS_GITOPS_REQUIRE_CLEAN_WORKTREE_FOR_PULL", config.GitOps.RequireCleanWorktreeForPull);

    config.CommandExecution.Enabled = RustOpsEnv.GetBoolean("RUSTOPS_COMMANDS_ENABLED", config.CommandExecution.Enabled);
    config.CommandExecution.FreeMode = RustOpsEnv.GetBoolean("RUSTOPS_COMMANDS_FREE_MODE", config.CommandExecution.FreeMode);
    config.CommandExecution.DefaultWaitMs = RustOpsEnv.GetInt32("RUSTOPS_COMMANDS_DEFAULT_WAIT_MS", config.CommandExecution.DefaultWaitMs);
    config.CommandExecution.MaxWaitMs = RustOpsEnv.GetInt32("RUSTOPS_COMMANDS_MAX_WAIT_MS", config.CommandExecution.MaxWaitMs);
    config.CommandExecution.MaxOutputChars = RustOpsEnv.GetInt32("RUSTOPS_COMMANDS_MAX_OUTPUT_CHARS", config.CommandExecution.MaxOutputChars);
    var allowListOverride = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_COMMANDS_ALLOWLIST");
    if (!string.IsNullOrWhiteSpace(allowListOverride))
        config.CommandExecution.AllowList = ParseCommandAllowList(allowListOverride);

    config.PluginUpdates.Enabled = RustOpsEnv.GetBoolean("RUSTOPS_PLUGIN_UPDATES_ENABLED", config.PluginUpdates.Enabled);
    config.PluginUpdates.CheckIntervalMinutes = RustOpsEnv.GetInt32("RUSTOPS_PLUGIN_UPDATES_INTERVAL_MINUTES", config.PluginUpdates.CheckIntervalMinutes);
    config.PluginUpdates.NotifyAdmins = RustOpsEnv.GetBoolean("RUSTOPS_PLUGIN_UPDATES_NOTIFY_ADMINS", config.PluginUpdates.NotifyAdmins);
    config.PluginUpdates.SearchUrlTemplate = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_PLUGIN_UPDATES_SEARCH_URL")
        ?? config.PluginUpdates.SearchUrlTemplate;
    config.PluginUpdates.SearchFilter = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_PLUGIN_UPDATES_FILTER")
        ?? config.PluginUpdates.SearchFilter;

    config.Llm.Provider = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_PROVIDER")
        ?? RustOpsEnv.ResolvePlaceholders(config.Llm.Provider);
    config.Llm.Enabled = RustOpsEnv.GetBoolean("RUSTOPS_LLM_ENABLED",
        RustOpsEnv.GetBoolean("RUSTOPS_OLLAMA_ENABLED", config.Llm.Enabled));
    config.Llm.BaseUrl = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_BASE_URL", "RUSTOPS_OLLAMA_BASE_URL")
        ?? RustOpsEnv.ResolvePlaceholders(config.Llm.BaseUrl);
    config.Llm.Model = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_MODEL", "RUSTOPS_OLLAMA_MODEL")
        ?? RustOpsEnv.ResolvePlaceholders(config.Llm.Model);
    config.Llm.ApiKey = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_API_KEY", "LM_API_TOKEN")
        ?? RustOpsEnv.ResolvePlaceholders(config.Llm.ApiKey);
    config.Llm.HttpReferer = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_HTTP_REFERER", "OPENROUTER_HTTP_REFERER")
        ?? RustOpsEnv.ResolvePlaceholders(config.Llm.HttpReferer);
    config.Llm.AppTitle = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_APP_TITLE", "OPENROUTER_APP_TITLE")
        ?? RustOpsEnv.ResolvePlaceholders(config.Llm.AppTitle);
    config.Llm.UseForRecommendations = RustOpsEnv.GetBoolean("RUSTOPS_LLM_USE_FOR_RECOMMENDATIONS",
        RustOpsEnv.GetBoolean("RUSTOPS_OLLAMA_USE_FOR_RECOMMENDATIONS", config.Llm.UseForRecommendations));
    config.Llm.RequestStrategy = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_REQUEST_STRATEGY")
        ?? RustOpsEnv.ResolvePlaceholders(config.Llm.RequestStrategy);
    config.Llm.UseChatSystemPrompt = RustOpsEnv.GetBoolean("RUSTOPS_LLM_USE_CHAT_SYSTEM_PROMPT", config.Llm.UseChatSystemPrompt);
    config.Llm.ChatSystemPrompt = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_CHAT_SYSTEM_PROMPT")
        ?? RustOpsEnv.ResolvePlaceholders(config.Llm.ChatSystemPrompt);
    config.Llm.Secondary.Enabled = RustOpsEnv.GetBoolean("RUSTOPS_LLM_SECONDARY_ENABLED", config.Llm.Secondary.Enabled);
    config.Llm.Secondary.BaseUrl = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_SECONDARY_BASE_URL")
        ?? RustOpsEnv.ResolvePlaceholders(config.Llm.Secondary.BaseUrl);
    config.Llm.Secondary.Model = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_SECONDARY_MODEL")
        ?? RustOpsEnv.ResolvePlaceholders(config.Llm.Secondary.Model);
    config.Llm.Secondary.ApiKey = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_SECONDARY_API_KEY")
        ?? RustOpsEnv.ResolvePlaceholders(config.Llm.Secondary.ApiKey);
    config.Llm.Secondary.HttpReferer = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_SECONDARY_HTTP_REFERER")
        ?? RustOpsEnv.ResolvePlaceholders(config.Llm.Secondary.HttpReferer);
    config.Llm.Secondary.AppTitle = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_SECONDARY_APP_TITLE")
        ?? RustOpsEnv.ResolvePlaceholders(config.Llm.Secondary.AppTitle);
}

static void ValidateResolvedConfig(AgentConfig config)
{
    var unresolved = new List<string>();

    Check("api.baseUrl", config.Api.BaseUrl);
    Check("api.apiKey", config.Api.ApiKey);
    Check("memory.statePath", config.Memory.StatePath);
    Check("inbox.feedbackInboxPath", config.Inbox.FeedbackInboxPath);
    Check("inbox.decisionInboxPath", config.Inbox.DecisionInboxPath);
    Check("inbox.chatInboxPath", config.Inbox.ChatInboxPath);
    Check("outbox.messageOutboxPath", config.Outbox.MessageOutboxPath);
    Check("selfRepair.workspacePath", config.SelfRepair.WorkspacePath);
    Check("selfRepair.scopeRootPath", config.SelfRepair.ScopeRootPath);
    Check("selfRepair.sourceRootPath", config.SelfRepair.SourceRootPath);
    Check("selfRepair.buildOutputPath", config.SelfRepair.BuildOutputPath);
    Check("gitOps.repoPath", config.GitOps.RepoPath);

    var scopeRoot = Path.GetFullPath(config.SelfRepair.ScopeRootPath);
    var workspacePath = Path.GetFullPath(config.SelfRepair.WorkspacePath);
    var sourceRootPath = Path.GetFullPath(config.SelfRepair.SourceRootPath);
    var buildOutputPath = Path.GetFullPath(config.SelfRepair.BuildOutputPath);
    if (!workspacePath.StartsWith(scopeRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"selfRepair.workspacePath must stay inside selfRepair.scopeRootPath. Scope='{scopeRoot}', workspace='{workspacePath}'.");
    }
    if (!sourceRootPath.StartsWith(scopeRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"selfRepair.sourceRootPath must stay inside selfRepair.scopeRootPath. Scope='{scopeRoot}', source='{sourceRootPath}'.");
    }
    if (!buildOutputPath.StartsWith(scopeRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"selfRepair.buildOutputPath must stay inside selfRepair.scopeRootPath. Scope='{scopeRoot}', build='{buildOutputPath}'.");
    }
    var gitRepoPath = Path.GetFullPath(config.GitOps.RepoPath);
    if (!gitRepoPath.StartsWith(scopeRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"gitOps.repoPath must stay inside selfRepair.scopeRootPath. Scope='{scopeRoot}', repo='{gitRepoPath}'.");
    }

    if (config.Llm.Enabled)
    {
        Check("llm.baseUrl", config.Llm.BaseUrl);
        Check("llm.model", config.Llm.Model);
        if (!IsValidHttpUrl(config.Llm.BaseUrl))
            throw new InvalidOperationException("llm.baseUrl must be an absolute http/https URL.");
    }
    if (config.Llm.Secondary.Enabled)
    {
        Check("llm.secondary.baseUrl", config.Llm.Secondary.BaseUrl);
        Check("llm.secondary.model", config.Llm.Secondary.Model);
        if (!IsValidHttpUrl(config.Llm.Secondary.BaseUrl))
            throw new InvalidOperationException("llm.secondary.baseUrl must be an absolute http/https URL.");
    }

    config.CommandExecution.DefaultWaitMs = Math.Clamp(config.CommandExecution.DefaultWaitMs, 200, 20_000);
    config.CommandExecution.MaxWaitMs = Math.Clamp(config.CommandExecution.MaxWaitMs, 500, 30_000);
    if (config.CommandExecution.MaxWaitMs < config.CommandExecution.DefaultWaitMs)
        config.CommandExecution.MaxWaitMs = config.CommandExecution.DefaultWaitMs;
    config.CommandExecution.MaxOutputChars = Math.Clamp(config.CommandExecution.MaxOutputChars, 500, 64_000);
    config.CommandExecution.AllowList = config.CommandExecution.AllowList
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (!config.CommandExecution.FreeMode && config.CommandExecution.AllowList.Count == 0)
        throw new InvalidOperationException("commandExecution.allowList must include at least one command when freeMode is disabled.");

    config.PluginUpdates.CheckIntervalMinutes = Math.Clamp(config.PluginUpdates.CheckIntervalMinutes, 5, 24 * 60);
    if (string.IsNullOrWhiteSpace(config.PluginUpdates.SearchUrlTemplate))
        throw new InvalidOperationException("pluginUpdates.searchUrlTemplate is required.");

    config.GitOps.AutoPullIntervalMinutes = Math.Clamp(config.GitOps.AutoPullIntervalMinutes, 1, 24 * 60);
    config.GitOps.RemoteName = string.IsNullOrWhiteSpace(config.GitOps.RemoteName) ? "origin" : config.GitOps.RemoteName.Trim();
    config.GitOps.BaseBranch = string.IsNullOrWhiteSpace(config.GitOps.BaseBranch) ? "main" : config.GitOps.BaseBranch.Trim();
    config.GitOps.PushBranchPrefix = string.IsNullOrWhiteSpace(config.GitOps.PushBranchPrefix) ? "agent/" : config.GitOps.PushBranchPrefix.Trim();
    if (!config.GitOps.PushBranchPrefix.EndsWith("/", StringComparison.Ordinal))
        config.GitOps.PushBranchPrefix += "/";

    config.Llm.RequestStrategy = (config.Llm.RequestStrategy ?? "fallback").Trim().ToLowerInvariant();
    if (config.Llm.RequestStrategy is not ("fallback" or "race"))
        throw new InvalidOperationException("llm.requestStrategy must be 'fallback' or 'race'.");

    if (unresolved.Count > 0)
    {
        throw new InvalidOperationException(
            "Config contains unresolved placeholders. Missing env-backed values: " +
            string.Join(", ", unresolved));
    }

    void Check(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || RustOpsEnv.HasUnresolvedPlaceholder(value))
            unresolved.Add(name);
    }
}

static List<string> ParseCommandAllowList(string value)
{
    return value
        .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Select(item => item.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static bool IsValidHttpUrl(string? value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        return false;

    return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}

static class AgentLogRulesFile
{
    public static AgentLogRules Load(string? path)
    {
        var rules = AgentLogRules.CreateDefault();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return rules;

        try
        {
            var loaded = JsonSerializer.Deserialize<AgentLogRules>(File.ReadAllText(path), JsonOptions.Default);
            if (loaded is null)
                return rules;

            loaded.ApplyDefaults(rules);
            Console.WriteLine($"Log rules loaded from {path}");
            return loaded;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load log rules from '{path}': {ex.Message}");
            SentrySdk.CaptureException(ex);
            return rules;
        }
    }
}

internal sealed class RustOpsAgent
{
    private static readonly Regex CompileErrorPattern = new(@"\bCS\d{4}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OxideInfoAttributePattern = new(@"\[\s*Info\s*\(\s*""(?<title>[^""]+)""\s*,\s*""(?<author>[^""]*)""\s*,\s*""(?<version>[^""]+)""\s*\)\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] KnownChatToolNames =
    {
        "list_servers",
        "get_server_status",
        "get_server_health",
        "get_recent_incidents",
        "get_recent_actions",
        "get_pending_actions",
        "validate_oxide",
        "inspect_host_network",
        "start_server",
        "stop_server",
        "restart_server",
        "get_server_players",
        "get_server_events",
        "execute_server_command",
        "get_server_command_memory",
        "teach_server_command",
        "list_server_plugins",
        "check_plugin_updates",
        "diagnose_agent_runtime",
        "list_scope_files",
        "read_scope_file",
        "write_scope_file",
        "list_agent_workspace_files",
        "read_agent_workspace_file",
        "write_agent_workspace_file",
        "git_push_branch",
        "git_pull_rebuild",
        "update_log_rules",
        "update_reply_style"
    };
    private const string DefaultChatToolSystemPrompt = """
You are a local Rust server operations agent talking to an admin via Steam chat.
Use the provided tools to inspect state and perform bounded operations.
Prefer using tools over guessing.
For start, stop, restart, and validate-oxide you must target a known server.
If the server is unclear, ask a concise clarification question instead of guessing.
Use recent memory, incidents, and action history to explain what is happening.

REPLY RULES — follow these without exception:
- Reply in plain, direct admin language. No markdown. No bullet points unless listing 3+ items.
- Start with the direct answer, then key evidence or next action.
- Never describe what tools you used or are about to use. Do not say "I'll call", "I'm using", "According to the tool", "The tool returned", "Let me check", or any similar phrase.
- Never include reasoning, planning, or meta-commentary about your process.
- Never say what you cannot do. If a request is outside your scope, say so in one sentence.
- Keep replies concise enough to read in a Steam chat window. Avoid wall-of-text.
- Do not invent facts, server states, or action outcomes.

If an admin asks to execute a server console command, use execute_server_command.
If an admin asks what a command does, use get_server_command_memory.
If an admin teaches command behavior, use teach_server_command.
If an admin asks about plugins or updates, use list_server_plugins and check_plugin_updates.
If an admin asks to push source changes to git, use git_push_branch.
If an admin asks to pull latest source updates, use git_pull_rebuild.
""";
    private readonly AgentConfig _config;
    private readonly RustMgrApiClient _api;
    private readonly RustMgrExecutor _executor;
    private readonly LlmClient _llm;
    private AgentLogRules _logRules;
    private readonly string _logRulesPath;
    private DateTime? _lastLogRulesWriteTimeUtc;
    private readonly string _selfRepairWorkspacePath;
    private readonly string _selfRepairScopeRootPath;
    private readonly string _replyStylePath;
    private readonly string _learningInboxPath;
    private readonly string _learningArchivePath;
    private DateTime? _lastReplyStyleWriteTimeUtc;
    private string _replyStyleGuidance = string.Empty;
    private DateTime? _lastSelfRepairCycleUtc;
    private DateTime? _lastGitAutoPullCheckUtc;
    private readonly AgentMemoryStore _memory;
    private volatile bool _stopRequested;

    public RustOpsAgent(AgentConfig config, RustMgrApiClient api, RustMgrExecutor executor, LlmClient llm, AgentLogRules logRules, AgentMemoryStore memory)
    {
        _config = config;
        _api = api;
        _executor = executor;
        _llm = llm;
        _logRules = logRules;
        _logRulesPath = config.Monitor.LogRulesPath;
        _lastLogRulesWriteTimeUtc = File.Exists(_logRulesPath) ? File.GetLastWriteTimeUtc(_logRulesPath) : null;
        _selfRepairScopeRootPath = Path.GetFullPath(config.SelfRepair.ScopeRootPath);
        _selfRepairWorkspacePath = Path.GetFullPath(config.SelfRepair.WorkspacePath);
        _replyStylePath = Path.Combine(_selfRepairWorkspacePath, "reply-style.txt");
        _learningInboxPath = Path.Combine(_selfRepairWorkspacePath, "learning", "inbox");
        _learningArchivePath = Path.Combine(_selfRepairWorkspacePath, "learning", "archive");
        _memory = memory;
        EnsureSelfRepairWorkspace();
        RefreshReplyStyleIfChanged(force: true);
    }

    public void RequestStop() => _stopRequested = true;

    public async Task RunAsync()
    {
        Console.WriteLine("RustOpsAgent started.");
        var lastMonitorCycleUtc = DateTime.MinValue;

        while (!_stopRequested)
        {
            var utcNow = DateTime.UtcNow;
            try
            {
                await RunControlCycleAsync(utcNow);

                if (lastMonitorCycleUtc == DateTime.MinValue ||
                    utcNow - lastMonitorCycleUtc >= TimeSpan.FromSeconds(_config.Monitor.PollSeconds))
                {
                    await RunMonitorCycleAsync(utcNow);
                    lastMonitorCycleUtc = utcNow;
                }

                _memory.Save(_config.Memory.StatePath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Cycle failed: {ex}");
                _memory.RecordAgentError(ex.Message);
                SentrySdk.CaptureException(ex);
                _memory.Save(_config.Memory.StatePath);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _config.Monitor.ControlPollSeconds)));
        }

        _memory.Save(_config.Memory.StatePath);
        Console.WriteLine("RustOpsAgent stopped.");
    }

    private async Task RunControlCycleAsync(DateTime utcNow)
    {
        RefreshLogRulesIfChanged();
        RefreshReplyStyleIfChanged();
        ProcessFeedbackInbox(utcNow);
        ProcessDecisionInbox(utcNow);
        await ProcessChatInboxAsync(utcNow);
        await ExecuteApprovedActionsAsync(utcNow);
        await RunSelfRepairCycleAsync(utcNow);
    }

    private void RefreshLogRulesIfChanged()
    {
        if (string.IsNullOrWhiteSpace(_logRulesPath) || !File.Exists(_logRulesPath))
            return;

        var writeTimeUtc = File.GetLastWriteTimeUtc(_logRulesPath);
        if (_lastLogRulesWriteTimeUtc.HasValue && writeTimeUtc <= _lastLogRulesWriteTimeUtc.Value)
            return;

        _logRules = AgentLogRulesFile.Load(_logRulesPath);
        _lastLogRulesWriteTimeUtc = writeTimeUtc;
    }

    private void EnsureSelfRepairWorkspace()
    {
        Directory.CreateDirectory(_selfRepairScopeRootPath);
        Directory.CreateDirectory(_selfRepairWorkspacePath);
        Directory.CreateDirectory(_learningInboxPath);
        Directory.CreateDirectory(_learningArchivePath);
        if (!File.Exists(_replyStylePath))
        {
            File.WriteAllText(_replyStylePath,
                "Use concise, natural admin language.\n" +
                "Start with the direct answer, then include key evidence in plain terms.\n" +
                "When an operation fails, explain why and give the next concrete check.\n" +
                "Do not use robotic wording or placeholders.");
        }
    }

    private void RefreshReplyStyleIfChanged(bool force = false)
    {
        if (!File.Exists(_replyStylePath))
            return;

        var writeTimeUtc = File.GetLastWriteTimeUtc(_replyStylePath);
        if (!force && _lastReplyStyleWriteTimeUtc.HasValue && writeTimeUtc <= _lastReplyStyleWriteTimeUtc.Value)
            return;

        _replyStyleGuidance = File.ReadAllText(_replyStylePath).Trim();
        _lastReplyStyleWriteTimeUtc = writeTimeUtc;
    }

    private async Task RunSelfRepairCycleAsync(DateTime utcNow)
    {
        if (!_config.SelfRepair.Enabled || !_config.Llm.Enabled)
            return;

        var intervalSeconds = Math.Max(30, _config.SelfRepair.IntervalSeconds);
        if (_lastSelfRepairCycleUtc.HasValue &&
            utcNow - _lastSelfRepairCycleUtc.Value < TimeSpan.FromSeconds(intervalSeconds))
        {
            return;
        }

        _lastSelfRepairCycleUtc = utcNow;

        var context = BuildSelfRepairContext(utcNow);
        if (!context.ShouldAttemptRepair)
            return;

        var plan = await _llm.TryCreateSelfRepairPlanAsync(context, KnownChatToolNames);
        if (plan is null || plan.Actions.Count == 0)
            return;

        var run = await ApplySelfRepairPlanAsync(plan, utcNow);
        _memory.RecordSelfRepairRun(run);
        if (run.AppliedActions > 0)
            ArchiveLearningIncidents(utcNow, maxCount: 8, reason: "capability-evolution");

        if (run.AppliedActions > 0 && _config.SelfRepair.NotifyAdmins)
        {
           // WriteOutboxMessage(new AdapterMessage
           // {
           //     CreatedAtUtc = utcNow,
           //     Kind = "self-repair",
           //     Audience = "admins",
           //     Message = $"Capability evolution applied {run.AppliedActions} action(s): {run.Summary}"
           // });
        }
    }

    private SelfRepairContext BuildSelfRepairContext(DateTime utcNow)
    {
        var recentErrors = _memory.AgentErrors.TakeLast(12).ToList();
        var recentFailures = _memory.ActionHistory
            .Where(entry => !entry.Success)
            .OrderByDescending(entry => entry.ExecutedAtUtc)
            .Take(12)
            .Select(entry => $"{entry.ExecutedAtUtc:O} {entry.ActionType} on {entry.ServerName}: {TrimSingleLine(entry.Summary, 180)}")
            .ToList();
        var recentGaps = _memory.CapabilityGaps
            .OrderByDescending(gap => gap.LastObservedAtUtc)
            .Take(12)
            .Select(gap => $"{gap.Category}: {TrimSingleLine(gap.Description, 180)} (count={gap.Count})")
            .ToList();
        var recentIncidents = _memory.Servers
            .SelectMany(server => server.Incidents.Select(incident => $"{server.Name}: {TrimSingleLine(incident.Title, 160)}"))
            .TakeLast(12)
            .ToList();
        var workspaceFiles = EnumerateWorkspaceFilePreviews();
        var scopeFiles = EnumerateScopeFilePreviews();
        var learningIncidents = EnumerateLearningIncidentPreviews();

        return new SelfRepairContext
        {
            AtUtc = utcNow,
            ShouldAttemptRepair = recentErrors.Count > 0 || recentFailures.Count > 0 || recentGaps.Count > 0 || learningIncidents.Count > 0,
            ScopeRootPath = _selfRepairScopeRootPath,
            WorkspacePath = _selfRepairWorkspacePath,
            CurrentReplyStyle = _replyStyleGuidance,
            KnownChatTools = KnownChatToolNames.ToList(),
            RecentErrors = recentErrors,
            RecentFailures = recentFailures,
            CapabilityGaps = recentGaps,
            RecentIncidents = recentIncidents,
            LearningIncidents = learningIncidents,
            WorkspaceFiles = workspaceFiles,
            ScopeFiles = scopeFiles
        };
    }

    private List<string> EnumerateLearningIncidentPreviews()
    {
        if (!Directory.Exists(_learningInboxPath))
            return new List<string>();

        return Directory.GetFiles(_learningInboxPath, "*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(20)
            .Select(path => ReadFilePreview(path, 360))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => TrimSingleLine(text, 320))
            .ToList();
    }

    private void ArchiveLearningIncidents(DateTime utcNow, int maxCount, string reason)
    {
        if (!Directory.Exists(_learningInboxPath))
            return;

        foreach (var path in Directory.GetFiles(_learningInboxPath, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName)
                     .Take(Math.Max(1, maxCount)))
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                var archivedName = $"{fileName}-worked-{utcNow:yyyyMMddHHmmssfff}.json";
                var target = Path.Combine(_learningArchivePath, archivedName);
                File.Move(path, target, true);
                File.WriteAllText($"{target}.note.txt", $"Archived after {reason} at {utcNow:O}");
            }
            catch (Exception ex)
            {
                _memory.RecordAgentError($"learning-archive failed: {ex.Message}");
            }
        }
    }

    private List<SelfRepairWorkspaceFilePreview> EnumerateWorkspaceFilePreviews()
    {
        if (!Directory.Exists(_selfRepairWorkspacePath))
            return new List<SelfRepairWorkspaceFilePreview>();

        return Directory.GetFiles(_selfRepairWorkspacePath, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(16)
            .Select(file => new SelfRepairWorkspaceFilePreview
            {
                RelativePath = Path.GetRelativePath(_selfRepairWorkspacePath, file.FullName).Replace('\\', '/'),
                SizeBytes = file.Length,
                LastWriteAtUtc = file.LastWriteTimeUtc,
                Preview = ReadFilePreview(file.FullName, 320)
            })
            .ToList();
    }

    private List<SelfRepairWorkspaceFilePreview> EnumerateScopeFilePreviews()
    {
        if (!Directory.Exists(_selfRepairScopeRootPath))
            return new List<SelfRepairWorkspaceFilePreview>();

        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".csproj", ".sln", ".json", ".sh", ".service", ".md", ".txt", ".env", ".example", ".yml", ".yaml", ".toml", ".bat"
        };

        return Directory.EnumerateFiles(_selfRepairScopeRootPath, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => allowedExtensions.Contains(file.Extension))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(24)
            .Select(file => new SelfRepairWorkspaceFilePreview
            {
                RelativePath = Path.GetRelativePath(_selfRepairScopeRootPath, file.FullName).Replace('\\', '/'),
                SizeBytes = file.Length,
                LastWriteAtUtc = file.LastWriteTimeUtc,
                Preview = ReadFilePreview(file.FullName, 320)
            })
            .ToList();
    }

    private static string ReadFilePreview(string path, int maxChars)
    {
        try
        {
            var text = File.ReadAllText(path);
            if (text.Length <= maxChars)
                return text;
            return text[..maxChars];
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<SelfRepairRunRecord> ApplySelfRepairPlanAsync(SelfRepairPlan plan, DateTime utcNow)
    {
        var applied = 0;
        var rejected = 0;
        var notes = new List<string>();
        var maxActions = Math.Max(1, _config.SelfRepair.MaxActionsPerCycle);

        foreach (var action in plan.Actions.Take(maxActions))
        {
            try
            {
                if (string.Equals(action.Type, "write_file", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyWriteWorkspaceFile(action.RelativePath, action.Content);
                    applied++;
                    notes.Add($"write_file:{action.RelativePath}");
                    continue;
                }

                if (string.Equals(action.Type, "write_scope_file", StringComparison.OrdinalIgnoreCase))
                {
                    if (_config.SelfRepair.AllowScopeFileWrites)
                    {
                        ApplyWriteScopeFile(action.RelativePath, action.Content);
                        applied++;
                        notes.Add($"write_scope_file:{action.RelativePath}");
                    }
                    else
                    {
                        rejected++;
                    }
                    continue;
                }

                if (string.Equals(action.Type, "merge_log_rules", StringComparison.OrdinalIgnoreCase))
                {
                    if (_config.SelfRepair.ApplyLogRuleUpdates)
                    {
                        MergeLogRules(action);
                        applied++;
                        notes.Add("merge_log_rules");
                    }
                    else
                    {
                        rejected++;
                    }
                    continue;
                }

                if (string.Equals(action.Type, "update_reply_style", StringComparison.OrdinalIgnoreCase))
                {
                    if (_config.SelfRepair.ApplyReplyStyleUpdates)
                    {
                        ApplyReplyStyleUpdate(action.Content);
                        applied++;
                        notes.Add("update_reply_style");
                    }
                    else
                    {
                        rejected++;
                    }
                    continue;
                }

                if (string.Equals(action.Type, "record_capability_gap", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(action.Description))
                    {
                        _memory.RecordCapabilityGap("self-repair", action.Description!);
                        applied++;
                        notes.Add("record_capability_gap");
                    }
                    else
                    {
                        rejected++;
                    }
                    continue;
                }

                if (string.Equals(action.Type, "build_from_source", StringComparison.OrdinalIgnoreCase))
                {
                    if (_config.SelfRepair.AllowSourceBuilds)
                    {
                        var build = await BuildFromSourceAsync("Release", "linux-x64");
                        if (build.Success)
                        {
                            applied++;
                            notes.Add("build_from_source");
                        }
                        else
                        {
                            rejected++;
                            notes.Add($"build_from_source_failed:{build.Summary}");
                        }
                    }
                    else
                    {
                        rejected++;
                    }
                    continue;
                }

                if (string.Equals(action.Type, "restart_managed_services", StringComparison.OrdinalIgnoreCase))
                {
                    if (_config.SelfRepair.AllowServiceRestarts)
                    {
                        var restart = await RestartManagedServicesAsync();
                        if (restart.Success)
                        {
                            applied++;
                            notes.Add("restart_managed_services");
                        }
                        else
                        {
                            rejected++;
                            notes.Add($"restart_managed_services_failed:{restart.Summary}");
                        }
                    }
                    else
                    {
                        rejected++;
                    }
                    continue;
                }

                if (string.Equals(action.Type, "git_push_branch", StringComparison.OrdinalIgnoreCase))
                {
                    var push = await ExecuteGitPushBranchAsync(action.Description);
                    if (push.Success)
                    {
                        applied++;
                        notes.Add($"git_push_branch:{push.BranchName}");
                    }
                    else
                    {
                        rejected++;
                        notes.Add($"git_push_branch_failed:{push.Summary}");
                    }
                    continue;
                }

                if (string.Equals(action.Type, "git_pull_rebuild", StringComparison.OrdinalIgnoreCase))
                {
                    var restartServices = !string.IsNullOrWhiteSpace(action.Description) &&
                        (action.Description.Contains("restart", StringComparison.OrdinalIgnoreCase) ||
                         action.Description.Contains("service", StringComparison.OrdinalIgnoreCase));
                    var sync = await ExecuteGitPullRebuildAsync(restartServices, automatic: false);
                    if (sync.Success)
                    {
                        applied++;
                        notes.Add(sync.Updated ? "git_pull_rebuild:updated" : "git_pull_rebuild:no_updates");
                    }
                    else
                    {
                        rejected++;
                        notes.Add($"git_pull_rebuild_failed:{sync.Summary}");
                    }
                    continue;
                }

                rejected++;
            }
            catch (Exception ex)
            {
                rejected++;
                _memory.RecordAgentError($"self-repair action '{action.Type}' failed: {ex.Message}");
                SentrySdk.CaptureException(ex);
            }
        }

        return new SelfRepairRunRecord
        {
            AtUtc = utcNow,
            Summary = string.IsNullOrWhiteSpace(plan.Summary) ? string.Join(", ", notes) : plan.Summary!,
            AppliedActions = applied,
            RejectedActions = rejected,
            Notes = notes,
            RawModelReasoning = plan.Reasoning
        };
    }

    private void ApplyWriteWorkspaceFile(string? relativePath, string? content)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("write_file requires relativePath.");

        var normalizedContent = content ?? string.Empty;
        var maxBytes = Math.Max(1024, _config.SelfRepair.MaxFileBytes);
        var sizeBytes = Encoding.UTF8.GetByteCount(normalizedContent);
        if (sizeBytes > maxBytes)
            throw new InvalidOperationException($"write_file exceeds max bytes ({sizeBytes} > {maxBytes}).");

        var fullPath = ResolveWorkspacePath(relativePath!);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, normalizedContent);
    }

    private void ApplyWriteScopeFile(string? relativePath, string? content)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("write_scope_file requires relativePath.");

        var normalizedContent = content ?? string.Empty;
        var maxBytes = Math.Max(1024, _config.SelfRepair.MaxFileBytes);
        var sizeBytes = Encoding.UTF8.GetByteCount(normalizedContent);
        if (sizeBytes > maxBytes)
            throw new InvalidOperationException($"write_scope_file exceeds max bytes ({sizeBytes} > {maxBytes}).");

        var fullPath = ResolveScopePath(relativePath!);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, normalizedContent);
    }

    private string ResolveWorkspacePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Relative path is empty.");

        var fullPath = Path.GetFullPath(Path.Combine(_selfRepairWorkspacePath, normalized));
        if (!fullPath.StartsWith(_selfRepairWorkspacePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path escapes self-repair workspace.");

        return fullPath;
    }

    private string ResolveScopePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Relative path is empty.");

        var fullPath = Path.GetFullPath(Path.Combine(_selfRepairScopeRootPath, normalized));
        if (!fullPath.StartsWith(_selfRepairScopeRootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path escapes self-repair scope root.");

        return fullPath;
    }

    private void MergeLogRules(SelfRepairAction action)
    {
        if (!IsPathWithinSelfRepairScope(_logRulesPath))
            throw new InvalidOperationException("Log-rules path is outside the agent scope.");

        var rules = AgentLogRulesFile.Load(_logRulesPath);
        rules.IgnoreContains = rules.IgnoreContains
            .Concat(action.IgnoreContains ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        rules.StartupIgnoreContains = rules.StartupIgnoreContains
            .Concat(action.StartupIgnoreContains ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        rules.IncidentContains = rules.IncidentContains
            .Concat(action.IncidentContains ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(_logRulesPath)!);
        File.WriteAllText(_logRulesPath, JsonSerializer.Serialize(rules, JsonOptions.Default));
        _logRules = rules;
        _lastLogRulesWriteTimeUtc = File.GetLastWriteTimeUtc(_logRulesPath);
    }

    private void ApplyReplyStyleUpdate(string? content)
    {
        var normalized = string.IsNullOrWhiteSpace(content)
            ? "Use concise, natural admin language."
            : content!.Trim();

        var maxBytes = Math.Max(1024, _config.SelfRepair.MaxFileBytes);
        var sizeBytes = Encoding.UTF8.GetByteCount(normalized);
        if (sizeBytes > maxBytes)
            throw new InvalidOperationException($"update_reply_style exceeds max bytes ({sizeBytes} > {maxBytes}).");

        File.WriteAllText(_replyStylePath, normalized);
        _replyStyleGuidance = normalized;
        _lastReplyStyleWriteTimeUtc = File.GetLastWriteTimeUtc(_replyStylePath);
    }

    private bool IsPathWithinSelfRepairScope(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(_selfRepairScopeRootPath, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunMonitorCycleAsync(DateTime utcNow)
    {
        using var summary = await _api.GetJsonAsync("/servers/summary");
        var servers = summary.RootElement.EnumerateArray()
            .Select(ServerSnapshot.FromSummary)
            .ToList();

        foreach (var server in servers)
        {
            var serverMemory = _memory.GetOrCreateServer(server.Name);
            ObserveStatusTransition(serverMemory, server, utcNow);
            ObserveServerProcessChange(serverMemory, server, utcNow);

            using var health = await _api.GetJsonAsync($"/servers/{Uri.EscapeDataString(server.Name)}/health");
            using var logs = await _api.GetJsonAsync(BuildLiveLogPath(server.Name, serverMemory.LastLogOffset, _config.Monitor.LogLinesPerScan));

            var healthView = HealthSnapshot.FromJson(server.Name, health.RootElement);
            var logEntries = ParseLogMessages(logs.RootElement);
            if (logs.RootElement.TryGetProperty("endOffset", out var endOffsetNode) && endOffsetNode.ValueKind == JsonValueKind.Number)
                serverMemory.LastLogOffset = endOffsetNode.GetInt64();

            var incident = await DetectIncidentAsync(serverMemory, server, healthView, logEntries, utcNow);
            if (incident is not null)
            {
                serverMemory.Incidents.Add(incident);
                serverMemory.LastObservedIssueAtUtc = utcNow;
                Console.WriteLine($"[{utcNow:O}] incident on {server.Name}: {incident.Title}");
                await QueueOrExecuteActionAsync(serverMemory, server, incident, utcNow);
            }

            await TryCheckPluginUpdatesAsync(server.Name, serverMemory, utcNow);

            serverMemory.LastStatus = server.State;
            serverMemory.LastObservedAtUtc = utcNow;
            serverMemory.LastKnownPid = server.Pid;
        }

        await RunGitAutoPullCycleAsync(utcNow);
    }

    private void ObserveStatusTransition(ServerMemory serverMemory, ServerSnapshot server, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(serverMemory.LastStatus))
            return;

        if (string.Equals(serverMemory.LastStatus, server.State, StringComparison.OrdinalIgnoreCase))
            return;

        serverMemory.Incidents.Add(new IncidentMemory
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = utcNow,
            Title = $"State changed: {serverMemory.LastStatus} -> {server.State}",
            Summary = $"Server '{server.Name}' changed state from '{serverMemory.LastStatus}' to '{server.State}'.",
            Category = "state-change",
            Source = "agent"
        });

        if (string.Equals(server.State, "offline", StringComparison.OrdinalIgnoreCase))
        {
            var incident = new IncidentMemory
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = utcNow,
                Title = $"{server.Name}: server went offline",
                Summary = $"Server '{server.Name}' transitioned to offline state.",
                Category = "state-change",
                Source = "agent",
                Evidence = new() { $"state transition {serverMemory.LastStatus} -> {server.State}" }
            };
            serverMemory.Incidents.Add(incident);
        }
    }

    private void ObserveServerProcessChange(ServerMemory serverMemory, ServerSnapshot server, DateTime utcNow)
    {
        if (!string.Equals(server.State, "running", StringComparison.OrdinalIgnoreCase) || !server.Pid.HasValue)
            return;

        if (!serverMemory.LastKnownPid.HasValue || serverMemory.LastKnownPid.Value != server.Pid.Value)
            serverMemory.LastStartedAtUtc = utcNow;
    }

    private async Task<IncidentMemory?> DetectIncidentAsync(
        ServerMemory serverMemory,
        ServerSnapshot server,
        HealthSnapshot health,
        List<string> logEntries,
        DateTime utcNow)
    {
        var healthEvidence = health.RecentErrors
            .Where(line => ClassifyLogLine(serverMemory, line, utcNow) >= LogSignalLevel.Interesting);
        var consoleEvidence = logEntries
            .Where(line => ClassifyLogLine(serverMemory, line, utcNow) >= LogSignalLevel.Interesting);
        var evidence = healthEvidence
            .Concat(consoleEvidence)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (evidence.Count == 0)
            return null;

        if (serverMemory.LastObservedIssueAtUtc.HasValue &&
            utcNow - serverMemory.LastObservedIssueAtUtc.Value < TimeSpan.FromMinutes(_config.Monitor.HealthCooldownMinutes))
            return null;

        var title = BuildIncidentTitle(server, evidence);
        var summary = BuildFallbackSummary(server, health, evidence);

        if (_config.Llm.Enabled)
        {
            var llmSummary = await _llm.TrySummarizeIncidentAsync(server.Name, serverMemory, health, evidence);
            if (!string.IsNullOrWhiteSpace(llmSummary))
                summary = llmSummary!;
        }

        RecommendationResult? recommendation = null;
        if (_config.Llm.Enabled && _config.Llm.UseForRecommendations)
        {
            recommendation = await _llm.TryRecommendActionAsync(server.Name, serverMemory, health, evidence);
        }

        serverMemory.KnownPatterns.AddRange(evidence
            .Where(e => !serverMemory.KnownPatterns.Contains(e, StringComparer.OrdinalIgnoreCase))
            .Take(3));

        return new IncidentMemory
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = utcNow,
            Title = title,
            Summary = summary,
            Category = InferCategory(evidence),
            Source = "monitor",
            Evidence = evidence,
            Recommendation = recommendation
        };
    }

    private void ProcessFeedbackInbox(DateTime utcNow)
    {
        foreach (var path in Directory.GetFiles(_config.Inbox.FeedbackInboxPath, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<FeedbackInboxItem>(File.ReadAllText(path), JsonOptions.Default);
                if (item is null)
                    continue;

                var entry = new FeedbackEntry
                {
                    ReceivedAtUtc = utcNow,
                    AdminId = item.AdminId?.Trim() ?? string.Empty,
                    ServerName = item.ServerName?.Trim(),
                    ActionId = item.ActionId?.Trim(),
                    Verdict = item.Verdict?.Trim() ?? "note",
                    Note = item.Note?.Trim(),
                    Preference = item.Preference?.Trim()
                };

                _memory.FeedbackHistory.Add(entry);
                _memory.FeedbackHistory = _memory.FeedbackHistory.TakeLast(200).ToList();

                if (!string.IsNullOrWhiteSpace(entry.AdminId) &&
                    (!string.IsNullOrWhiteSpace(entry.Note) || !string.IsNullOrWhiteSpace(entry.Preference)))
                {
                    var adminPref = _memory.GetOrCreateAdmin(entry.AdminId);
                    if (!string.IsNullOrWhiteSpace(entry.Note))
                        adminPref.Preferences.Add(entry.Note!);
                    if (!string.IsNullOrWhiteSpace(entry.Preference))
                        adminPref.Preferences.Add(entry.Preference!);
                    adminPref.Preferences = adminPref.Preferences.Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(50).ToList();
                    adminPref.LastUpdatedAtUtc = utcNow;
                }

                ApplyFeedbackLearning(entry, utcNow);
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to process feedback file '{path}': {ex.Message}");
                _memory.RecordAgentError($"feedback-inbox: {Path.GetFileName(path)} failed: {ex.Message}");
                SentrySdk.CaptureException(ex);
            }
        }
    }

    private void ProcessDecisionInbox(DateTime utcNow)
    {
        foreach (var path in Directory.GetFiles(_config.Inbox.DecisionInboxPath, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<DecisionInboxItem>(File.ReadAllText(path), JsonOptions.Default);
                if (item is null || string.IsNullOrWhiteSpace(item.ActionId))
                    continue;

                var proposal = _memory.PendingActions.FirstOrDefault(a => string.Equals(a.Id, item.ActionId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (proposal is null)
                {
                    File.Delete(path);
                    continue;
                }

                var decision = (item.Decision ?? string.Empty).Trim().ToLowerInvariant();
                proposal.Status = decision == "approve" ? ActionStatus.Approved : ActionStatus.Rejected;
                proposal.DecisionBy = item.AdminId?.Trim();
                proposal.DecisionNote = item.Note?.Trim();
                proposal.LastUpdatedAtUtc = utcNow;

                if (proposal.Status == ActionStatus.Rejected && !string.IsNullOrWhiteSpace(item.Note))
                {
                    var serverMemory = _memory.GetOrCreateServer(proposal.ServerName);
                    serverMemory.LearnedActionRules.Add(new LearnedActionRule
                    {
                        ActionType = proposal.ActionType,
                        Guidance = "avoid-auto",
                        Note = item.Note.Trim(),
                        AdminId = item.AdminId?.Trim(),
                        LearnedAtUtc = utcNow
                    });
                }

                File.Delete(path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to process decision file '{path}': {ex.Message}");
                _memory.RecordAgentError($"decision-inbox: {Path.GetFileName(path)} failed: {ex.Message}");
                SentrySdk.CaptureException(ex);
            }
        }
    }

    private async Task ProcessChatInboxAsync(DateTime utcNow)
    {
        foreach (var path in Directory.GetFiles(_config.Inbox.ChatInboxPath, "*.json").OrderBy(Path.GetFileName))
        {
            ChatInboxItem? item = null;
            try
            {
                item = JsonSerializer.Deserialize<ChatInboxItem>(File.ReadAllText(path), JsonOptions.Default);
                if (item is null || string.IsNullOrWhiteSpace(item.AdminId) || string.IsNullOrWhiteSpace(item.Message))
                {
                    File.Delete(path);
                    continue;
                }

                var reply = await HandleChatRequestAsync(item, utcNow);
                if (!string.IsNullOrWhiteSpace(reply))
                {
                    WriteOutboxMessage(new AdapterMessage
                    {
                        CreatedAtUtc = utcNow,
                        Kind = "chat-reply",
                        Audience = "admin",
                        TargetAdminId = item.AdminId.Trim(),
                        Message = reply.Trim()
                    });
                }

                File.Delete(path);
                _memory.Save(_config.Memory.StatePath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to process chat file '{path}': {ex.Message}");
                _memory.RecordAgentError($"chat-inbox: {Path.GetFileName(path)} failed: {ex.Message}");
                if (!string.IsNullOrWhiteSpace(item?.AdminId) && !string.IsNullOrWhiteSpace(item?.Message))
                    RecordLearningIncident(item.AdminId!, item.Message!, "processing-error", ex.Message, null, "processing-error");
                SentrySdk.CaptureException(ex);
                _memory.Save(_config.Memory.StatePath);
            }
        }
    }

    private async Task<string> HandleChatRequestAsync(ChatInboxItem item, DateTime utcNow)
    {
        var adminId = item.AdminId?.Trim() ?? string.Empty;
        var message = item.Message?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message))
            return "I didn't receive any text to work with.";

        RustOpsSentry.AddBreadcrumb($"Chat request from admin {adminId}.", "agent.chat");

        var adminPreference = _memory.GetOrCreateAdmin(adminId);
        var conversation = adminPreference.Conversation ??= new AdminConversationState();
        RememberChatTurn(conversation, "user", message, utcNow);

        var preferenceAck = TryCaptureAdminPreferenceFromChat(adminPreference, message, utcNow);
        if (!string.IsNullOrWhiteSpace(preferenceAck))
        {
            SetConversationTrace(conversation, utcNow, "preference", "preference-update", usedTools: null, note: "Stored admin preference from chat.");
            RememberChatTurn(conversation, "assistant", preferenceAck, utcNow);
            adminPreference.LastUpdatedAtUtc = utcNow;
            return preferenceAck;
        }

        var metaReply = TryExplainLastReplyWorkflow(conversation, message);
        if (!string.IsNullOrWhiteSpace(metaReply))
        {
            SetConversationTrace(conversation, utcNow, "meta", "explain-last-reply", usedTools: null, note: "Explained previous reply workflow.");
            RememberChatTurn(conversation, "assistant", metaReply, utcNow);
            adminPreference.LastUpdatedAtUtc = utcNow;
            return metaReply;
        }

        var servers = await GetServerSnapshotsAsync();
        var directCommandReply = await TryHandleDirectCommandMessageAsync(adminId, message, servers, utcNow);
        if (!string.IsNullOrWhiteSpace(directCommandReply))
        {
            SetConversationTrace(conversation, utcNow, "direct", "direct-message-handler", usedTools: null, note: "Handled using deterministic direct parser.");
            RememberChatTurn(conversation, "assistant", directCommandReply, utcNow);
            adminPreference.LastUpdatedAtUtc = utcNow;
            return directCommandReply;
        }

        if (_config.Llm.Enabled)
        {
            var toolReply = await TryHandleChatWithLlmToolsAsync(adminId, message, servers, conversation, adminPreference, utcNow);
            if (toolReply is not null)
            {
                if (!string.IsNullOrWhiteSpace(toolReply.PendingClarificationIntent))
                {
                    conversation.PendingClarification = new PendingChatClarification
                    {
                        Intent = toolReply.PendingClarificationIntent!,
                        OriginalMessage = message,
                        Question = toolReply.Reply,
                        RequestedAtUtc = utcNow
                    };
                }
                else
                {
                    conversation.PendingClarification = null;
                }

                if (!string.IsNullOrWhiteSpace(toolReply.LastServerName))
                    conversation.LastServerName = toolReply.LastServerName;

                SetConversationTrace(
                    conversation,
                    utcNow,
                    "llm-tools",
                    string.IsNullOrWhiteSpace(toolReply.PendingClarificationIntent) ? "tool-reply" : toolReply.PendingClarificationIntent!,
                    toolReply.UsedTools,
                    note: "Reply generated through LLM tool-calling.");
                RememberChatTurn(conversation, "assistant", toolReply.Reply, utcNow);
                adminPreference.LastUpdatedAtUtc = utcNow;
                return toolReply.Reply;
            }
        }

        var plan = await PlanChatTurnAsync(message, servers, conversation, adminPreference);
        RustOpsSentry.AddBreadcrumb($"Chat plan selected intent '{plan.Intent}'.", "agent.chat");

        ApplyConversationContext(plan, message, servers, conversation);

        if (plan.NeedsClarification)
        {
            conversation.PendingClarification = new PendingChatClarification
            {
                Intent = plan.Intent,
                OriginalMessage = message,
                Question = plan.ClarificationQuestion ?? BuildClarificationQuestion(plan.Intent, servers),
                RequestedAtUtc = utcNow
            };

            var clarification = conversation.PendingClarification.Question;
            SetConversationTrace(conversation, utcNow, "clarification", plan.Intent, usedTools: null, note: "Server clarification required before action.");
            RememberChatTurn(conversation, "assistant", clarification, utcNow);
            adminPreference.LastUpdatedAtUtc = utcNow;
            return clarification;
        }

        conversation.PendingClarification = null;
        var reply = await ExecuteChatPlanAsync(adminId, plan, servers, utcNow);
        reply = await EnhanceAdminReplyAsync(message, reply, plan, servers, conversation, adminPreference);
        UpdateConversationState(conversation, plan);
        SetConversationTrace(conversation, utcNow, "deterministic", plan.Intent, usedTools: null, note: "Fallback deterministic planning/execution path.");
        if (string.Equals(plan.Intent, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            RecordLearningIncident(adminId, message, plan.Intent, reply, conversation.LastTrace, "unknown-intent");
        }
        RememberChatTurn(conversation, "assistant", reply, utcNow);
        adminPreference.LastUpdatedAtUtc = utcNow;
        return reply;
    }

    private async Task<string?> TryHandleDirectCommandMessageAsync(string adminId, string message, List<ServerSnapshot> servers, DateTime utcNow)
    {
        var text = message.Trim();
        var lowered = text.ToLowerInvariant();

        if ((lowered.Contains("plugin", StringComparison.Ordinal) || lowered.Contains("plugins", StringComparison.Ordinal)) &&
            (lowered.Contains("update", StringComparison.Ordinal) || lowered.Contains("upgrade", StringComparison.Ordinal) || lowered.Contains("new version", StringComparison.Ordinal)))
        {
            var requestedServer = ResolveServerName(text, servers);
            if (string.IsNullOrWhiteSpace(requestedServer))
                return BuildClarificationQuestion("check-plugin-updates", servers);
            return await BuildPluginUpdatesReplyAsync(requestedServer, servers);
        }

        if ((lowered.Contains("plugin", StringComparison.Ordinal) || lowered.Contains("plugins", StringComparison.Ordinal)) &&
            (lowered.Contains("compile", StringComparison.Ordinal) || lowered.Contains("build", StringComparison.Ordinal)))
        {
            var requestedServer = ResolveServerName(text, servers);
            if (string.IsNullOrWhiteSpace(requestedServer))
                return BuildClarificationQuestion("validate-oxide", servers);
            return await BuildOxideCompileIssuesReplyAsync(requestedServer, servers);
        }

        var runMatch = Regex.Match(text, @"^(run|execute)\s+command\s+(?<command>.+?)\s+on\s+(?<server>[A-Za-z0-9_\-]+)$", RegexOptions.IgnoreCase);
        if (runMatch.Success)
        {
            if (!_config.CommandExecution.Enabled)
                return "Server command execution is disabled by config.";

            var requestedServer = runMatch.Groups["server"].Value.Trim();
            var serverName = ResolveServerName(requestedServer, servers);
            if (string.IsNullOrWhiteSpace(serverName))
                return BuildServerNotFoundReply(servers);

            var command = runMatch.Groups["command"].Value.Trim();
            var normalized = NormalizeCommand(command);
            if (normalized is null)
                return "Command must be a single line and 256 characters or less.";
            if (!IsCommandAllowed(normalized, out var policyError))
                return policyError ?? "Command not allowed by current policy.";

            JsonDocument json;
            try
            {
                json = await _api.PostJsonAsync($"/servers/{Uri.EscapeDataString(serverName)}/command/exec", new
                {
                    command = normalized,
                    waitMs = _config.CommandExecution.DefaultWaitMs,
                    maxLines = 240,
                    maxBytes = 256 * 1024
                });
            }
            catch (Exception ex)
            {
                return $"{serverName}: command execution failed: {ex.Message}";
            }
            using (json)
            {
                var output = ExtractCommandOutputMessages(json.RootElement);
                var summary = TrimSingleLine(string.Join(" | ", output.Take(8)), 220);
                RecordCommandKnowledge(serverName, normalized, summary, success: true, adminId, utcNow);

                return $"{serverName}: command '{normalized}' executed | {TrimSingleLine(summary, 260)}";
            }
        }

        var askMatch = Regex.Match(text, @"^what\s+does\s+command\s+(?<command>.+?)(\s+on\s+(?<server>[A-Za-z0-9_\-]+))?$", RegexOptions.IgnoreCase);
        if (!askMatch.Success)
            return null;

        var commandFilter = NormalizeCommandKey(askMatch.Groups["command"].Value.Trim());
        var requestedServerName = askMatch.Groups["server"].Success
            ? ResolveServerName(askMatch.Groups["server"].Value.Trim(), servers)
            : servers.FirstOrDefault()?.Name;
        if (string.IsNullOrWhiteSpace(requestedServerName))
            return "Specify a server to check command memory.";

        var serverMemory = _memory.GetOrCreateServer(requestedServerName);
        serverMemory.KnownConsoleCommands ??= new List<KnownConsoleCommand>();
        var known = serverMemory.KnownConsoleCommands
            .FirstOrDefault(entry => string.Equals(entry.Command, commandFilter, StringComparison.OrdinalIgnoreCase));
        if (known is null)
            return $"{requestedServerName}: no learned command memory for '{commandFilter}' yet.";

        return $"{requestedServerName}: {known.Command} | purpose={known.Purpose ?? "unknown"} | usefulWhen={known.UsefulWhen ?? "unknown"} | lastOutput={known.LastOutputSummary ?? "n/a"} | observations={known.Observations}";
    }

    private static string? TryCaptureAdminPreferenceFromChat(AdminPreference adminPreference, string message, DateTime utcNow)
    {
        var lowered = message.Trim().ToLowerInvariant();
        var captured = string.Empty;

        if (lowered.StartsWith("preference:", StringComparison.Ordinal) ||
            lowered.StartsWith("remember preference", StringComparison.Ordinal))
        {
            captured = message.Split(':', 2).Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(captured))
                captured = message["remember preference".Length..].Trim();
        }
        else if (lowered.Contains("don't mention selfrepair", StringComparison.Ordinal) ||
                 lowered.Contains("dont mention selfrepair", StringComparison.Ordinal) ||
                 lowered.Contains("do not mention selfrepair", StringComparison.Ordinal) ||
                 lowered.Contains("don't mention self-repair", StringComparison.Ordinal) ||
                 lowered.Contains("dont mention self-repair", StringComparison.Ordinal) ||
                 lowered.Contains("do not mention self-repair", StringComparison.Ordinal))
        {
            captured = "Do not mention routine self-repair actions unless explicitly asked.";
        }

        if (string.IsNullOrWhiteSpace(captured))
            return null;

        adminPreference.Preferences.Add(TrimSingleLine(captured, 240));
        adminPreference.Preferences = adminPreference.Preferences.TakeLast(80).ToList();
        adminPreference.LastUpdatedAtUtc = utcNow;
        return $"Preference saved: {TrimSingleLine(captured, 160)}";
    }

    private async Task<List<ServerSnapshot>> GetServerSnapshotsAsync()
    {
        using var summary = await _api.GetJsonAsync("/servers/summary");
        return summary.RootElement.EnumerateArray()
            .Select(ServerSnapshot.FromSummary)
            .ToList();
    }

    private async Task<ChatInterpretation> PlanChatTurnAsync(string message, List<ServerSnapshot> servers, AdminConversationState conversation, AdminPreference adminPreference)
    {
        var clarified = TryResolvePendingClarification(message, servers, conversation);
        if (clarified is not null)
            return clarified;

        if (_config.Llm.Enabled)
        {
            var planningContext = BuildChatPlanningContext(message, servers, conversation, adminPreference);
            var modelInterpretation = await _llm.TryPlanChatTurnAsync(message, conversation, planningContext);
            if (modelInterpretation is not null && IsKnownChatIntent(modelInterpretation.Intent))
            {
                if (!string.IsNullOrWhiteSpace(modelInterpretation.ServerName))
                    modelInterpretation.ServerName = ResolveServerName(modelInterpretation.ServerName, servers);

                var shouldFallbackToHeuristics =
                    string.Equals(modelInterpretation.Intent, "unknown", StringComparison.OrdinalIgnoreCase) ||
                    (modelInterpretation.Confidence.HasValue && modelInterpretation.Confidence.Value < 0.45);

                if (!shouldFallbackToHeuristics)
                    return modelInterpretation;
            }
        }

        return InterpretChatRequestHeuristically(message, servers, conversation);
    }

    private async Task<string> EnhanceAdminReplyAsync(
        string request,
        string deterministicReply,
        ChatInterpretation plan,
        List<ServerSnapshot> servers,
        AdminConversationState conversation,
        AdminPreference adminPreference)
    {
        if (!_config.Llm.Enabled || string.IsNullOrWhiteSpace(deterministicReply))
            return deterministicReply;

        var planningContext = BuildChatPlanningContext(request, servers, conversation, adminPreference);
        var drafted = await _llm.TryDraftAdminReplyAsync(request, deterministicReply, plan, planningContext);
        return string.IsNullOrWhiteSpace(drafted) ? deterministicReply : drafted.Trim();
    }

    private ChatPlanningContext BuildChatPlanningContext(
        string message,
        List<ServerSnapshot> servers,
        AdminConversationState conversation,
        AdminPreference adminPreference)
    {
        var relevantServerName = ResolveRelevantServerForContext(message, servers, conversation);
        var relevantServerMemory = !string.IsNullOrWhiteSpace(relevantServerName)
            ? _memory.GetOrCreateServer(relevantServerName)
            : null;
        if (relevantServerMemory is not null)
        {
            relevantServerMemory.KnownConsoleCommands ??= new List<KnownConsoleCommand>();
            relevantServerMemory.CommandInteractions ??= new List<ConsoleCommandInteraction>();
            relevantServerMemory.KnownPlugins ??= new List<KnownPluginRecord>();
        }

        return new ChatPlanningContext
        {
            KnownServers = servers.Select(s => s.Name).ToList(),
            ServerStates = BuildServerStateSummary(servers),
            RelevantServerName = relevantServerName,
            RelevantServerMemory = BuildRelevantServerMemorySummary(relevantServerMemory),
            AdminPreferences = adminPreference.Preferences.TakeLast(6).ToList(),
            LearnedRules = relevantServerMemory?.LearnedActionRules
                .OrderByDescending(rule => rule.LearnedAtUtc)
                .Take(6)
                .Select(rule => $"{rule.ActionType}: {TrimSingleLine(rule.Guidance, 120)}")
                .ToList()
                ?? new List<string>(),
            PendingActions = _memory.PendingActions
                .Where(action => action.Status == ActionStatus.Pending)
                .TakeLast(6)
                .Select(action => $"{action.ActionType} on {action.ServerName}: {TrimSingleLine(action.Summary, 120)}")
                .ToList(),
            RecentActions = _memory.ActionHistory
                .TakeLast(8)
                .Select(action => $"{action.ServerName}: {action.ActionType} ({(action.Success ? "success" : "failed")}) - {TrimSingleLine(action.Summary, 140)}")
                .ToList(),
            RecentIncidents = (relevantServerMemory?.Incidents ?? _memory.Servers.SelectMany(server => server.Incidents))
                .OrderByDescending(incident => incident.CreatedAtUtc)
                .Take(6)
                .Select(incident => $"{incident.CreatedAtUtc:O} {TrimSingleLine(incident.Title, 120)}")
                .ToList(),
            CommandKnowledge = relevantServerMemory?.KnownConsoleCommands
                .OrderByDescending(command => command.LastObservedAtUtc)
                .Take(8)
                .Select(command => $"{command.Command}: {TrimSingleLine(command.Purpose ?? command.LastOutputSummary ?? "observed", 140)}")
                .ToList()
                ?? new List<string>(),
            ReplyStyleGuidance = _replyStyleGuidance
        };
    }

    private string? ResolveRelevantServerForContext(string message, List<ServerSnapshot> servers, AdminConversationState conversation)
    {
        var resolved = ResolveServerName(message, servers);
        if (!string.IsNullOrWhiteSpace(resolved))
            return resolved;

        return string.IsNullOrWhiteSpace(conversation.LastServerName)
            ? null
            : ResolveServerName(conversation.LastServerName, servers);
    }

    private static List<string> BuildServerStateSummary(List<ServerSnapshot> servers)
    {
        return servers.Select(server =>
        {
            var details = new List<string> { server.State };
            if (server.Pid.HasValue)
                details.Add($"pid={server.Pid.Value}");
            if (server.CurrentPlayers.HasValue || server.MaxPlayers.HasValue)
                details.Add($"players={server.CurrentPlayers?.ToString() ?? "?"}/{server.MaxPlayers?.ToString() ?? "?"}");
            if (!string.IsNullOrWhiteSpace(server.Map))
                details.Add($"map={server.Map}");
            if (server.Framerate.HasValue)
                details.Add($"fps={server.Framerate.Value:0.#}");
            if (server.RecentWarningCount is > 0)
                details.Add($"warnings={server.RecentWarningCount}");
            return $"{server.Name}: {string.Join(", ", details)}";
        }).ToList();
    }

    private static List<string> BuildRelevantServerMemorySummary(ServerMemory? memory)
    {
        if (memory is null)
            return new List<string>();

        var summary = new List<string>();
        summary.AddRange(memory.KnownPatterns.TakeLast(6).Select(pattern => $"pattern: {TrimSingleLine(pattern, 140)}"));
        summary.AddRange(memory.ActionOutcomes.TakeLast(6).Select(outcome => $"outcome: {TrimSingleLine(outcome, 140)}"));
        summary.AddRange(memory.Incidents
            .OrderByDescending(incident => incident.CreatedAtUtc)
            .Take(4)
            .Select(incident => $"incident: {TrimSingleLine(incident.Title, 120)}"));
        return summary;
    }

    private async Task<ToolDrivenChatReply?> TryHandleChatWithLlmToolsAsync(
        string adminId,
        string message,
        List<ServerSnapshot> servers,
        AdminConversationState conversation,
        AdminPreference adminPreference,
        DateTime utcNow)
    {
        var planningContext = BuildChatPlanningContext(message, servers, conversation, adminPreference);
        var messages = BuildToolConversationMessages(conversation, message);
        var toolDefinitions = BuildChatToolDefinitions();
        var toolPrompt = BuildChatToolSystemPrompt(planningContext, conversation, _replyStyleGuidance, _config.Llm);
        var lastServerName = string.Empty;
        var pendingClarificationIntent = string.Empty;
        var usedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lifecycleMutations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (var round = 0; round < 3; round++)
            {
                var response = await _llm.RequestToolChatTurnAsync(toolPrompt, messages, toolDefinitions);
                if (response is null)
                    return null;

                if (response.ToolCalls.Count == 0)
                {
                    var content = response.Content?.Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        if (LooksLikeCapabilityDenial(content))
                        {
                            _memory.RecordCapabilityGap("llm-chat-tools", $"LLM produced capability denial instead of tool usage: {TrimSingleLine(content, 220)}");
                            RecordLearningIncident(adminId, message, "capability-denial", content, conversation.LastTrace, "capability-denial");
                            return null;
                        }

                        return new ToolDrivenChatReply
                        {
                            Reply = StripProcessNarration(content).Trim(),
                            LastServerName = lastServerName,
                            PendingClarificationIntent = pendingClarificationIntent,
                            UsedTools = usedTools.OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase).ToList()
                        };
                    }

                    return null;
                }

                messages.Add(LlmChatMessage.Assistant(response.Content, response.ToolCalls));

                foreach (var toolCall in response.ToolCalls.Take(6))
                {
                    usedTools.Add(NormalizeChatToolName(toolCall.Name));
                    var toolResult = await ExecuteLlmChatToolAsync(adminId, toolCall, servers, utcNow, lifecycleMutations);
                    if (!string.IsNullOrWhiteSpace(toolResult.ResolvedServerName))
                        lastServerName = toolResult.ResolvedServerName!;

                    if (!string.IsNullOrWhiteSpace(toolResult.PendingClarificationIntent))
                        pendingClarificationIntent = toolResult.PendingClarificationIntent!;

                    messages.Add(LlmChatMessage.Tool(toolCall.Id, toolCall.Name, toolResult.Content));
                }
            }

            var finalPrompt = string.IsNullOrWhiteSpace(toolPrompt)
                ? "Reply directly to the admin based on the tool results above. Do not call more tools. Do not describe what tools you used or your reasoning. Output only the final answer in plain admin language."
                : toolPrompt + "\n\nAll tools have been called. Now write only the final reply to the admin. Do not call more tools. Do not mention tool names, tool calls, or your reasoning process. Give a direct, concise answer.";
            var finalReply = await _llm.RequestChatCompletionAsync(finalPrompt, messages);

            if (string.IsNullOrWhiteSpace(finalReply))
                return null;

            return new ToolDrivenChatReply
            {
                Reply = StripProcessNarration(finalReply).Trim(),
                LastServerName = lastServerName,
                PendingClarificationIntent = pendingClarificationIntent,
                UsedTools = usedTools.OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }
        catch (Exception ex)
        {
            _memory.RecordAgentError($"llm-chat-tools failed: {ex.Message}");
            RecordLearningIncident(adminId, message, "tool-failure", ex.Message, conversation.LastTrace, "tool-failure");
            SentrySdk.CaptureException(ex);
            return null;
        }
    }

    private static List<LlmChatMessage> BuildToolConversationMessages(AdminConversationState conversation, string message)
    {
        var messages = conversation.History
            .TakeLast(6)
            .Select(entry => LlmChatMessage.Simple(entry.Role, entry.Text))
            .ToList();

        messages.Add(LlmChatMessage.Simple("user", message));
        return messages;
    }

    private static string BuildChatToolSystemPrompt(ChatPlanningContext planningContext, AdminConversationState conversation, string replyStyleGuidance, LlmSettings llmSettings)
    {
        var preamble = llmSettings.UseChatSystemPrompt && !string.IsNullOrWhiteSpace(llmSettings.ChatSystemPrompt)
            ? llmSettings.ChatSystemPrompt.Trim()
            : DefaultChatToolSystemPrompt;
        var lastServer = string.IsNullOrWhiteSpace(conversation.LastServerName) ? "none" : conversation.LastServerName;
        return $$"""
        {{preamble}}

        Reply style guidance:
        {{(string.IsNullOrWhiteSpace(replyStyleGuidance) ? "Use concise, natural admin language." : replyStyleGuidance)}}

        Known servers:
        {{string.Join(", ", planningContext.KnownServers)}}

        Last server in conversation:
        {{lastServer}}

        Current server states:
        {{FormatPromptList(planningContext.ServerStates)}}

        Relevant server memory:
        {{FormatPromptList(planningContext.RelevantServerMemory)}}

        Admin preferences:
        {{FormatPromptList(planningContext.AdminPreferences)}}

        Learned rules:
        {{FormatPromptList(planningContext.LearnedRules)}}

        Pending actions:
        {{FormatPromptList(planningContext.PendingActions)}}

        Recent actions:
        {{FormatPromptList(planningContext.RecentActions)}}

        Recent incidents:
        {{FormatPromptList(planningContext.RecentIncidents)}}

        Known server command behavior:
        {{FormatPromptList(planningContext.CommandKnowledge)}}
        """;
    }

    private static object[] BuildChatToolDefinitions() =>
    [
        BuildToolDefinition(
            "list_servers",
            "List all known servers with their current state, pid, players, map, fps, and recent warning count.",
            new { type = "object", additionalProperties = false, properties = new { } }),
        BuildToolDefinition(
            "get_server_status",
            "Get the current runtime status for one named server.",
            BuildServerToolParameters()),
        BuildToolDefinition(
            "get_server_health",
            "Get health details, recent errors, and last restart evidence for one named server.",
            BuildServerToolParameters()),
        BuildToolDefinition(
            "get_recent_incidents",
            "Get recent incidents from agent memory. Use an empty serverName for all servers.",
            BuildScopedHistoryToolParameters()),
        BuildToolDefinition(
            "get_recent_actions",
            "Get recent executed actions from agent memory. Use an empty serverName for all servers.",
            BuildScopedHistoryToolParameters()),
        BuildToolDefinition(
            "get_pending_actions",
            "Get pending approval actions from agent memory. Use an empty serverName for all servers.",
            BuildScopedHistoryToolParameters()),
        BuildToolDefinition(
            "validate_oxide",
            "Validate Oxide/uMod plugins and config files for one named server.",
            BuildServerToolParameters()),
        BuildToolDefinition(
            "inspect_host_network",
            "Inspect current host throughput, interface spikes, errors, and drops.",
            new { type = "object", additionalProperties = false, properties = new { } }),
        BuildToolDefinition(
            "start_server",
            "Start one named server.",
            BuildServerToolParameters()),
        BuildToolDefinition(
            "stop_server",
            "Stop one named server.",
            BuildServerToolParameters()),
        BuildToolDefinition(
            "restart_server",
            "Restart one named server.",
            BuildServerToolParameters()),
        BuildToolDefinition(
            "get_server_players",
            "Get the current player list snapshot for one named server.",
            BuildServerToolParameters()),
        BuildToolDefinition(
            "get_server_events",
            "Get recent command/event traces for one named server.",
            BuildServerEventToolParameters()),
        BuildToolDefinition(
            "execute_server_command",
            "Execute one server console command and capture stdout/stderr plus command-trace evidence.",
            BuildServerCommandToolParameters()),
        BuildToolDefinition(
            "get_server_command_memory",
            "Get learned server command behavior and recent command interaction summaries.",
            BuildServerCommandMemoryToolParameters()),
        BuildToolDefinition(
            "teach_server_command",
            "Store admin-provided guidance for what a server command does and when to use it.",
            BuildTeachCommandToolParameters()),
        BuildToolDefinition(
            "list_server_plugins",
            "List Oxide plugins for one server, including [Info] title/author/version when available.",
            BuildServerToolParameters()),
        BuildToolDefinition(
            "check_plugin_updates",
            "Check uMod for newer plugin versions for one server and return update candidates.",
            BuildPluginUpdateToolParameters()),
        BuildToolDefinition(
            "git_push_branch",
            "Commit local source changes in gitOps.repoPath to a new branch and push to gitOps.remoteName.",
            BuildGitPushToolParameters()),
        BuildToolDefinition(
            "git_pull_rebuild",
            "Fetch and pull updates from gitOps.remoteName/gitOps.baseBranch, then rebuild and optionally restart services.",
            BuildGitPullRebuildToolParameters()),
        BuildToolDefinition(
            "diagnose_agent_runtime",
            "Inspect agent runtime errors, failed actions, and capability gaps.",
            new { type = "object", additionalProperties = false, properties = new { } }),
        BuildToolDefinition(
            "list_scope_files",
            "List notable files under the self-repair scope root.",
            BuildScopeListToolParameters()),
        BuildToolDefinition(
            "read_scope_file",
            "Read one text file under the self-repair scope root using a relative path.",
            BuildScopeReadToolParameters()),
        BuildToolDefinition(
            "write_scope_file",
            "Write one text file under the self-repair scope root using a relative path.",
            BuildScopeWriteToolParameters()),
        BuildToolDefinition(
            "list_agent_workspace_files",
            "List writable files inside the self-repair workspace.",
            BuildWorkspaceListToolParameters()),
        BuildToolDefinition(
            "read_agent_workspace_file",
            "Read one file from the self-repair workspace by relative path.",
            BuildWorkspaceReadToolParameters()),
        BuildToolDefinition(
            "write_agent_workspace_file",
            "Write one file to the self-repair workspace by relative path.",
            BuildWorkspaceWriteToolParameters()),
        BuildToolDefinition(
            "update_log_rules",
            "Merge additional log-rule patterns into the active log-rules file.",
            BuildLogRuleUpdateToolParameters()),
        BuildToolDefinition(
            "update_reply_style",
            "Update reply-style guidance for natural admin responses.",
            BuildReplyStyleToolParameters())
    ];

    private static object BuildToolDefinition(string name, string description, object parameters) => new
    {
        type = "function",
        function = new
        {
            name,
            description,
            parameters
        }
    };

    private static object BuildServerToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            serverName = new
            {
                type = "string",
                description = "Exact server name when known. Use an empty string if you need clarification."
            }
        },
        required = new[] { "serverName" }
    };

    private static object BuildScopedHistoryToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            serverName = new
            {
                type = "string",
                description = "Exact server name to filter by, or empty string for all servers."
            },
            limit = new
            {
                type = "integer",
                description = "Maximum number of results to return."
            }
        },
        required = new[] { "serverName", "limit" }
    };

    private static object BuildServerEventToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            serverName = new
            {
                type = "string",
                description = "Exact server name when known."
            },
            limit = new
            {
                type = "integer",
                description = "Maximum number of events to return."
            }
        },
        required = new[] { "serverName", "limit" }
    };

    private static object BuildServerCommandToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            serverName = new
            {
                type = "string",
                description = "Exact server name."
            },
            command = new
            {
                type = "string",
                description = "Console command text, for example oxide.plugins."
            },
            waitMs = new
            {
                type = "integer",
                description = "How long to wait for fresh output after sending the command."
            }
        },
        required = new[] { "serverName", "command" }
    };

    private static object BuildServerCommandMemoryToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            serverName = new
            {
                type = "string",
                description = "Exact server name."
            },
            command = new
            {
                type = "string",
                description = "Optional command name to filter by."
            },
            limit = new
            {
                type = "integer",
                description = "Maximum records to return."
            }
        },
        required = new[] { "serverName", "command", "limit" }
    };

    private static object BuildTeachCommandToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            serverName = new
            {
                type = "string",
                description = "Exact server name."
            },
            command = new
            {
                type = "string",
                description = "Command name or full command text."
            },
            purpose = new
            {
                type = "string",
                description = "What this command is used for."
            },
            usefulWhen = new
            {
                type = "string",
                description = "Operational situation where this command helps."
            }
        },
        required = new[] { "serverName", "command", "purpose", "usefulWhen" }
    };

    private static object BuildPluginUpdateToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            serverName = new
            {
                type = "string",
                description = "Exact server name."
            },
            notifyAdmins = new
            {
                type = "boolean",
                description = "If true, send an adapter outbox message when updates are found."
            }
        },
        required = new[] { "serverName", "notifyAdmins" }
    };

    private static object BuildGitPushToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            commitMessage = new
            {
                type = "string",
                description = "Optional commit message for the branch push."
            }
        },
        required = Array.Empty<string>()
    };

    private static object BuildGitPullRebuildToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            restartServices = new
            {
                type = "boolean",
                description = "When true, restart managed services after a successful pull and build."
            }
        },
        required = Array.Empty<string>()
    };

    private static object BuildWorkspaceListToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            limit = new
            {
                type = "integer",
                description = "Maximum number of files to return."
            }
        },
        required = new[] { "limit" }
    };

    private static object BuildScopeListToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            limit = new
            {
                type = "integer",
                description = "Maximum number of scope files to return."
            }
        },
        required = new[] { "limit" }
    };

    private static object BuildScopeReadToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            relativePath = new
            {
                type = "string",
                description = "File path relative to selfRepair.scopeRootPath."
            },
            maxChars = new
            {
                type = "integer",
                description = "Maximum characters to return."
            }
        },
        required = new[] { "relativePath", "maxChars" }
    };

    private static object BuildScopeWriteToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            relativePath = new
            {
                type = "string",
                description = "File path relative to selfRepair.scopeRootPath."
            },
            content = new
            {
                type = "string",
                description = "Full file content."
            }
        },
        required = new[] { "relativePath", "content" }
    };

    private static object BuildWorkspaceReadToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            relativePath = new
            {
                type = "string",
                description = "File path relative to the self-repair workspace."
            },
            maxChars = new
            {
                type = "integer",
                description = "Maximum characters to return."
            }
        },
        required = new[] { "relativePath", "maxChars" }
    };

    private static object BuildWorkspaceWriteToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            relativePath = new
            {
                type = "string",
                description = "File path relative to the self-repair workspace."
            },
            content = new
            {
                type = "string",
                description = "Full content to write."
            }
        },
        required = new[] { "relativePath", "content" }
    };

    private static object BuildLogRuleUpdateToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            ignoreContains = new { type = "array", items = new { type = "string" } },
            startupIgnoreContains = new { type = "array", items = new { type = "string" } },
            incidentContains = new { type = "array", items = new { type = "string" } }
        },
        required = new[] { "ignoreContains", "startupIgnoreContains", "incidentContains" }
    };

    private static object BuildReplyStyleToolParameters() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            content = new
            {
                type = "string",
                description = "Reply style guidance text."
            }
        },
        required = new[] { "content" }
    };

    private async Task<ChatToolExecutionResult> ExecuteLlmChatToolAsync(
        string adminId,
        LlmToolCall toolCall,
        List<ServerSnapshot> servers,
        DateTime utcNow,
        HashSet<string> lifecycleMutations)
    {
        var arguments = ParseToolArguments(toolCall.ArgumentsJson);
        var toolName = NormalizeChatToolName(toolCall.Name);
        RustOpsSentry.AddBreadcrumb($"LLM requested tool '{toolName}'.", "agent.chat");

        return toolName switch
        {
            "list_servers" => ChatToolExecutionResult.Success(SerializeToolPayload(new
            {
                ok = true,
                servers = servers.Select(server => new
                {
                    name = server.Name,
                    state = server.State,
                    pid = server.Pid,
                    currentPlayers = server.CurrentPlayers,
                    maxPlayers = server.MaxPlayers,
                    map = server.Map,
                    framerate = server.Framerate,
                    recentWarningCount = server.RecentWarningCount
                }).ToList()
            })),
            "get_server_status" => await ExecuteServerStatusToolAsync(arguments, servers),
            "get_server_health" => await ExecuteServerHealthToolAsync(arguments, servers),
            "get_recent_incidents" => ExecuteRecentIncidentsTool(arguments),
            "get_recent_actions" => ExecuteRecentActionsTool(arguments),
            "get_pending_actions" => ExecutePendingActionsTool(arguments),
            "validate_oxide" => await ExecuteActionBackedToolAsync(adminId, arguments, servers, utcNow, "validate-oxide"),
            "inspect_host_network" => await ExecuteInspectNetworkToolAsync(adminId, utcNow),
            "start_server" => await ExecuteLifecycleToolAsync(adminId, arguments, servers, utcNow, lifecycleMutations, "start-server"),
            "stop_server" => await ExecuteLifecycleToolAsync(adminId, arguments, servers, utcNow, lifecycleMutations, "stop-server"),
            "restart_server" => await ExecuteLifecycleToolAsync(adminId, arguments, servers, utcNow, lifecycleMutations, "restart-server"),
            "get_server_players" => await ExecuteServerPlayersToolAsync(arguments, servers),
            "get_server_events" => await ExecuteServerEventsToolAsync(arguments, servers),
            "execute_server_command" => await ExecuteServerCommandToolAsync(arguments, servers, adminId, utcNow),
            "get_server_command_memory" => ExecuteServerCommandMemoryTool(arguments, servers),
            "teach_server_command" => ExecuteTeachServerCommandTool(arguments, servers, adminId, utcNow),
            "list_server_plugins" => await ExecuteListServerPluginsToolAsync(arguments, servers),
            "check_plugin_updates" => await ExecuteCheckPluginUpdatesToolAsync(arguments, servers, utcNow),
            "git_push_branch" => await ExecuteGitPushToolAsync(arguments),
            "git_pull_rebuild" => await ExecuteGitPullRebuildToolAsync(arguments),
            "diagnose_agent_runtime" => ExecuteDiagnoseAgentRuntimeTool(),
            "list_scope_files" => ExecuteListScopeFilesTool(arguments),
            "read_scope_file" => ExecuteReadScopeFileTool(arguments),
            "write_scope_file" => ExecuteWriteScopeFileTool(arguments),
            "list_agent_workspace_files" => ExecuteListAgentWorkspaceFilesTool(arguments),
            "read_agent_workspace_file" => ExecuteReadAgentWorkspaceFileTool(arguments),
            "write_agent_workspace_file" => ExecuteWriteAgentWorkspaceFileTool(arguments),
            "update_log_rules" => ExecuteUpdateLogRulesTool(arguments),
            "update_reply_style" => ExecuteUpdateReplyStyleTool(arguments),
            _ => HandleUnknownTool(toolCall.Name)
        };
    }

    private ChatToolExecutionResult HandleUnknownTool(string? toolName)
    {
        _memory.RecordCapabilityGap("chat-tool", $"Unknown tool call from model: {toolName}");
        return ChatToolExecutionResult.Error($"Unknown tool '{toolName}'.");
    }

    private async Task<ChatToolExecutionResult> ExecuteServerStatusToolAsync(JsonElement arguments, List<ServerSnapshot> servers)
    {
        var resolution = ResolveToolServer(arguments, servers, "server-status");
        if (!resolution.Matched)
            return resolution.Result;

        using var json = await _api.GetJsonAsync($"/servers/{Uri.EscapeDataString(resolution.Server!.Name)}/status");
        return ChatToolExecutionResult.Success(json.RootElement.GetRawText(), resolution.Server.Name);
    }

    private async Task<ChatToolExecutionResult> ExecuteServerHealthToolAsync(JsonElement arguments, List<ServerSnapshot> servers)
    {
        var resolution = ResolveToolServer(arguments, servers, "server-health");
        if (!resolution.Matched)
            return resolution.Result;

        using var json = await _api.GetJsonAsync($"/servers/{Uri.EscapeDataString(resolution.Server!.Name)}/health");
        return ChatToolExecutionResult.Success(json.RootElement.GetRawText(), resolution.Server.Name);
    }

    private async Task<ChatToolExecutionResult> ExecuteServerPlayersToolAsync(JsonElement arguments, List<ServerSnapshot> servers)
    {
        var resolution = ResolveToolServer(arguments, servers, "server-players");
        if (!resolution.Matched)
            return resolution.Result;

        using var json = await _api.GetJsonAsync($"/servers/{Uri.EscapeDataString(resolution.Server!.Name)}/players");
        return ChatToolExecutionResult.Success(json.RootElement.GetRawText(), resolution.Server.Name);
    }

    private async Task<ChatToolExecutionResult> ExecuteServerEventsToolAsync(JsonElement arguments, List<ServerSnapshot> servers)
    {
        var resolution = ResolveToolServer(arguments, servers, "server-events");
        if (!resolution.Matched)
            return resolution.Result;

        var limit = ReadToolIntArgument(arguments, "limit", 40, 5, 200);
        using var json = await _api.GetJsonAsync($"/servers/{Uri.EscapeDataString(resolution.Server!.Name)}/events?lines={limit}");
        return ChatToolExecutionResult.Success(json.RootElement.GetRawText(), resolution.Server.Name);
    }

    private async Task<ChatToolExecutionResult> ExecuteServerCommandToolAsync(JsonElement arguments, List<ServerSnapshot> servers, string adminId, DateTime utcNow)
    {
        if (!_config.CommandExecution.Enabled)
            return ChatToolExecutionResult.Error("Server command execution is disabled by config.");

        var resolution = ResolveToolServer(arguments, servers, "server-command");
        if (!resolution.Matched)
            return resolution.Result;
        var server = resolution.Server!;

        var command = ReadToolArgument(arguments, "command");
        if (string.IsNullOrWhiteSpace(command))
            return ChatToolExecutionResult.Error("command is required.", server.Name, "execute_server_command");
        var normalized = NormalizeCommand(command);
        if (normalized is null)
            return ChatToolExecutionResult.Error("Command must be a single line and 256 characters or less.", server.Name, "execute_server_command");

        if (!IsCommandAllowed(normalized, out var policyError))
            return ChatToolExecutionResult.Error(policyError ?? "Command not allowed by current policy.", server.Name, "execute_server_command");

        var waitMs = ReadToolIntArgument(
            arguments,
            "waitMs",
            _config.CommandExecution.DefaultWaitMs,
            200,
            _config.CommandExecution.MaxWaitMs);

        JsonDocument json;
        try
        {
            json = await _api.PostJsonAsync($"/servers/{Uri.EscapeDataString(server.Name)}/command/exec", new
            {
                command = normalized,
                waitMs,
                maxLines = 240,
                maxBytes = 256 * 1024
            });
        }
        catch (Exception ex)
        {
            return ChatToolExecutionResult.Error(ex.Message, server.Name, "execute_server_command");
        }

        using (json)
        {
            var output = ExtractCommandOutputMessages(json.RootElement);
            var outputPreview = TrimSingleLine(string.Join(" | ", output.Take(8)), 220);
            RecordCommandKnowledge(server.Name, normalized, outputPreview, success: true, adminId, utcNow);

            return ChatToolExecutionResult.Success(SerializeToolPayload(new
            {
                ok = true,
                serverName = server.Name,
                command = normalized,
                waitMs,
                outputCount = output.Count,
                outputPreview = TrimSingleLine(outputPreview, _config.CommandExecution.MaxOutputChars),
                output,
                raw = json.RootElement
            }), server.Name);
        }
    }

    private ChatToolExecutionResult ExecuteServerCommandMemoryTool(JsonElement arguments, List<ServerSnapshot> servers)
    {
        var resolution = ResolveToolServer(arguments, servers, "server-command-memory");
        if (!resolution.Matched)
            return resolution.Result;

        var commandFilter = NormalizeCommandKey(ReadToolArgument(arguments, "command"));
        var limit = ReadToolIntArgument(arguments, "limit", 8, 1, 30);
        var serverMemory = _memory.GetOrCreateServer(resolution.Server!.Name);
        serverMemory.KnownConsoleCommands ??= new List<KnownConsoleCommand>();
        serverMemory.CommandInteractions ??= new List<ConsoleCommandInteraction>();

        var known = serverMemory.KnownConsoleCommands
            .Where(entry => string.IsNullOrWhiteSpace(commandFilter) ||
                            string.Equals(entry.Command, commandFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.LastObservedAtUtc)
            .Take(limit)
            .ToList();

        var recent = serverMemory.CommandInteractions
            .Where(entry => string.IsNullOrWhiteSpace(commandFilter) ||
                            string.Equals(entry.CommandKey, commandFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.AtUtc)
            .Take(limit)
            .ToList();

        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            serverName = resolution.Server.Name,
            knownCommands = known,
            recentInteractions = recent
        }), resolution.Server.Name);
    }

    private ChatToolExecutionResult ExecuteTeachServerCommandTool(JsonElement arguments, List<ServerSnapshot> servers, string adminId, DateTime utcNow)
    {
        var resolution = ResolveToolServer(arguments, servers, "teach-server-command");
        if (!resolution.Matched)
            return resolution.Result;

        var command = ReadToolArgument(arguments, "command");
        var purpose = ReadToolArgument(arguments, "purpose");
        var usefulWhen = ReadToolArgument(arguments, "usefulWhen");
        if (string.IsNullOrWhiteSpace(command))
            return ChatToolExecutionResult.Error("command is required.", resolution.Server!.Name, "teach_server_command");

        RecordCommandKnowledge(
            resolution.Server!.Name,
            command,
            string.IsNullOrWhiteSpace(purpose) ? "admin guidance recorded" : purpose,
            success: true,
            adminId,
            utcNow,
            purpose,
            usefulWhen);

        var adminPreference = _memory.GetOrCreateAdmin(adminId);
        var note = $"command {NormalizeCommandKey(command)}: {purpose} | useful when {usefulWhen}".Trim();
        if (!string.IsNullOrWhiteSpace(note))
        {
            adminPreference.Preferences.Add(note);
            adminPreference.Preferences = adminPreference.Preferences.TakeLast(80).ToList();
            adminPreference.LastUpdatedAtUtc = utcNow;
        }

        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            serverName = resolution.Server.Name,
            command = NormalizeCommandKey(command),
            purpose = purpose,
            usefulWhen = usefulWhen
        }), resolution.Server.Name);
    }

    private async Task<ChatToolExecutionResult> ExecuteListServerPluginsToolAsync(JsonElement arguments, List<ServerSnapshot> servers)
    {
        var resolution = ResolveToolServer(arguments, servers, "list-server-plugins");
        if (!resolution.Matched)
            return resolution.Result;

        List<ServerPluginInfo> plugins;
        try
        {
            plugins = await ReadServerPluginsAsync(resolution.Server!.Name);
        }
        catch (Exception ex)
        {
            return ChatToolExecutionResult.Error(ex.Message, resolution.Server!.Name, "list_server_plugins");
        }
        var serverMemory = _memory.GetOrCreateServer(resolution.Server.Name);
        serverMemory.KnownPlugins ??= new List<KnownPluginRecord>();
        serverMemory.KnownPlugins = plugins
            .Select(plugin => new KnownPluginRecord
            {
                Name = plugin.Name,
                Author = plugin.Author,
                Version = plugin.Version,
                FilePath = plugin.Path,
                LastSeenAtUtc = DateTime.UtcNow
            })
            .TakeLast(200)
            .ToList();

        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            serverName = resolution.Server.Name,
            pluginCount = plugins.Count,
            plugins
        }), resolution.Server.Name);
    }

    private async Task<ChatToolExecutionResult> ExecuteCheckPluginUpdatesToolAsync(JsonElement arguments, List<ServerSnapshot> servers, DateTime utcNow)
    {
        if (!_config.PluginUpdates.Enabled)
            return ChatToolExecutionResult.Error("Plugin update monitor is disabled by config.");

        var resolution = ResolveToolServer(arguments, servers, "check-plugin-updates");
        if (!resolution.Matched)
            return resolution.Result;

        var notifyAdmins = ReadToolBooleanArgument(arguments, "notifyAdmins", true);
        List<PluginUpdateCandidate> updates;
        try
        {
            updates = await GetPluginUpdatesAsync(resolution.Server!.Name);
        }
        catch (Exception ex)
        {
            return ChatToolExecutionResult.Error(ex.Message, resolution.Server!.Name, "check_plugin_updates");
        }
        var serverMemory = _memory.GetOrCreateServer(resolution.Server.Name);
        serverMemory.KnownPlugins ??= new List<KnownPluginRecord>();
        serverMemory.LastPluginUpdateCheckAtUtc = utcNow;
        serverMemory.LastPluginUpdateSignature = BuildPluginUpdateSignature(updates);

        if (updates.Count > 0 && notifyAdmins && _config.PluginUpdates.NotifyAdmins)
        {
            WriteOutboxMessage(new AdapterMessage
            {
                CreatedAtUtc = utcNow,
                Kind = "plugin-update",
                ServerName = resolution.Server.Name,
                Audience = "admins",
                Message = BuildPluginUpdateAdminMessage(resolution.Server.Name, updates)
            });
        }

        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            serverName = resolution.Server.Name,
            updateCount = updates.Count,
            updates
        }), resolution.Server.Name);
    }

    private async Task<ChatToolExecutionResult> ExecuteGitPushToolAsync(JsonElement arguments)
    {
        var commitMessage = ReadToolArgument(arguments, "commitMessage");
        var result = await ExecuteGitPushBranchAsync(commitMessage);
        if (!result.Success)
            return ChatToolExecutionResult.Error(result.Summary);

        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            branchName = result.BranchName,
            previousBranch = result.PreviousBranch,
            commitMessage = result.CommitMessage,
            stagedFiles = result.StagedFiles,
            summary = result.Summary
        }));
    }

    private async Task<ChatToolExecutionResult> ExecuteGitPullRebuildToolAsync(JsonElement arguments)
    {
        var restart = ReadToolBooleanArgument(arguments, "restartServices", false);
        var result = await ExecuteGitPullRebuildAsync(restart, automatic: false);
        if (!result.Success)
            return ChatToolExecutionResult.Error(result.Summary);

        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            updated = result.Updated,
            summary = result.Summary,
            build = result.Build,
            restart = result.Restart
        }));
    }

    private async Task RunGitAutoPullCycleAsync(DateTime utcNow)
    {
        if (!_config.GitOps.Enabled || !_config.GitOps.AutoPullEnabled)
            return;

        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.GitOps.AutoPullIntervalMinutes));
        if (_lastGitAutoPullCheckUtc.HasValue &&
            utcNow - _lastGitAutoPullCheckUtc.Value < interval)
        {
            return;
        }

        _lastGitAutoPullCheckUtc = utcNow;
        try
        {
            var result = await ExecuteGitPullRebuildAsync(
                restartServices: _config.GitOps.AutoRestartAfterPullRebuild,
                automatic: true);

            if (!result.Updated)
                return;

            WriteOutboxMessage(new AdapterMessage
            {
                CreatedAtUtc = utcNow,
                Kind = "git-auto-sync",
                Audience = "admins",
                Message = result.Success
                    ? $"Auto pull+rebuild applied from {_config.GitOps.RemoteName}/{_config.GitOps.BaseBranch}. {result.Summary}"
                    : $"Auto pull+rebuild failed: {result.Summary}"
            });
        }
        catch (Exception ex)
        {
            _memory.RecordAgentError($"git-auto-pull failed: {ex.Message}");
            SentrySdk.CaptureException(ex);
        }
    }

    private async Task<GitPushResult> ExecuteGitPushBranchAsync(string? commitMessage)
    {
        if (!_config.GitOps.Enabled)
            return GitPushResult.Fail("Git operations are disabled by config.");
        if (!_config.GitOps.AllowPush)
            return GitPushResult.Fail("Git branch push is disabled by config.");
        if (!Directory.Exists(_config.GitOps.RepoPath))
            return GitPushResult.Fail($"Git repo path does not exist: {_config.GitOps.RepoPath}");
        if (!IsPathWithinSelfRepairScope(_config.GitOps.RepoPath))
            return GitPushResult.Fail("Git repo path is outside self-repair scope.");

        var repoPath = Path.GetFullPath(_config.GitOps.RepoPath);
        var inRepo = await ExecuteProcessAsync("git", new[] { "rev-parse", "--is-inside-work-tree" }, repoPath, TimeSpan.FromSeconds(12));
        if (!inRepo.Ok || !string.Equals(inRepo.StdOut?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return GitPushResult.Fail($"Not a git repo: {BuildLifecycleMessage(inRepo, "git rev-parse failed")}");

        var status = await ExecuteProcessAsync("git", new[] { "status", "--porcelain" }, repoPath, TimeSpan.FromSeconds(20));
        if (!status.Ok)
            return GitPushResult.Fail($"git status failed: {BuildLifecycleMessage(status, "status failed")}");
        if (string.IsNullOrWhiteSpace(status.StdOut))
            return GitPushResult.Fail("No local changes to commit.");

        var currentBranchResult = await ExecuteProcessAsync("git", new[] { "rev-parse", "--abbrev-ref", "HEAD" }, repoPath, TimeSpan.FromSeconds(10));
        if (!currentBranchResult.Ok || string.IsNullOrWhiteSpace(currentBranchResult.StdOut))
            return GitPushResult.Fail($"Could not resolve current branch: {BuildLifecycleMessage(currentBranchResult, "branch lookup failed")}");
        var previousBranch = currentBranchResult.StdOut.Trim();

        var branchName = BuildAgentBranchName();
        var checkout = await ExecuteProcessAsync("git", new[] { "checkout", "-b", branchName }, repoPath, TimeSpan.FromSeconds(20));
        if (!checkout.Ok)
            return GitPushResult.Fail($"Failed to create branch '{branchName}': {BuildLifecycleMessage(checkout, "checkout failed")}");

        var add = await ExecuteProcessAsync("git", new[] { "add", "-A" }, repoPath, TimeSpan.FromSeconds(60));
        if (!add.Ok)
        {
            await ExecuteProcessAsync("git", new[] { "checkout", previousBranch }, repoPath, TimeSpan.FromSeconds(20));
            return GitPushResult.Fail($"git add failed: {BuildLifecycleMessage(add, "add failed")}", branchName);
        }

        var staged = await ExecuteProcessAsync("git", new[] { "diff", "--cached", "--name-only" }, repoPath, TimeSpan.FromSeconds(15));
        if (!staged.Ok)
        {
            await ExecuteProcessAsync("git", new[] { "checkout", previousBranch }, repoPath, TimeSpan.FromSeconds(20));
            return GitPushResult.Fail($"git diff --cached failed: {BuildLifecycleMessage(staged, "diff failed")}", branchName);
        }

        var stagedFiles = (staged.StdOut ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (stagedFiles.Count == 0)
        {
            await ExecuteProcessAsync("git", new[] { "checkout", previousBranch }, repoPath, TimeSpan.FromSeconds(20));
            return GitPushResult.Fail("No staged changes after git add.", branchName);
        }

        var resolvedCommitMessage = ResolveGitCommitMessage(commitMessage);
        var commit = await ExecuteProcessAsync("git", new[] { "commit", "-m", resolvedCommitMessage }, repoPath, TimeSpan.FromSeconds(45));
        if (!commit.Ok)
        {
            await ExecuteProcessAsync("git", new[] { "checkout", previousBranch }, repoPath, TimeSpan.FromSeconds(20));
            return GitPushResult.Fail($"git commit failed: {BuildLifecycleMessage(commit, "commit failed")}", branchName);
        }

        var push = await ExecuteProcessAsync(
            "git",
            new[] { "push", "-u", _config.GitOps.RemoteName, branchName },
            repoPath,
            TimeSpan.FromSeconds(90));
        if (!push.Ok)
        {
            await ExecuteProcessAsync("git", new[] { "checkout", previousBranch }, repoPath, TimeSpan.FromSeconds(20));
            return GitPushResult.Fail($"git push failed: {BuildLifecycleMessage(push, "push failed")}", branchName);
        }

        return new GitPushResult
        {
            Success = true,
            BranchName = branchName,
            PreviousBranch = previousBranch,
            CommitMessage = resolvedCommitMessage,
            StagedFiles = stagedFiles,
            Summary = $"Pushed {stagedFiles.Count} file(s) to branch '{branchName}'."
        };
    }

    private async Task<GitSyncResult> ExecuteGitPullRebuildAsync(bool restartServices, bool automatic)
    {
        if (!_config.GitOps.Enabled)
            return GitSyncResult.Fail("Git operations are disabled by config.");
        if (!automatic && !_config.GitOps.AllowManualPullRebuild)
            return GitSyncResult.Fail("Manual pull+rebuild is disabled by config.");
        if (!Directory.Exists(_config.GitOps.RepoPath))
            return GitSyncResult.Fail($"Git repo path does not exist: {_config.GitOps.RepoPath}");
        if (!IsPathWithinSelfRepairScope(_config.GitOps.RepoPath))
            return GitSyncResult.Fail("Git repo path is outside self-repair scope.");

        var repoPath = Path.GetFullPath(_config.GitOps.RepoPath);
        var inRepo = await ExecuteProcessAsync("git", new[] { "rev-parse", "--is-inside-work-tree" }, repoPath, TimeSpan.FromSeconds(12));
        if (!inRepo.Ok || !string.Equals(inRepo.StdOut?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return GitSyncResult.Fail($"Not a git repo: {BuildLifecycleMessage(inRepo, "git rev-parse failed")}");

        if (_config.GitOps.RequireCleanWorktreeForPull)
        {
            var status = await ExecuteProcessAsync("git", new[] { "status", "--porcelain" }, repoPath, TimeSpan.FromSeconds(20));
            if (!status.Ok)
                return GitSyncResult.Fail($"git status failed: {BuildLifecycleMessage(status, "status failed")}");
            if (!string.IsNullOrWhiteSpace(status.StdOut))
                return GitSyncResult.Fail("Worktree is not clean; refusing pull+rebuild.");
        }

        var fetch = await ExecuteProcessAsync("git", new[] { "fetch", _config.GitOps.RemoteName }, repoPath, TimeSpan.FromSeconds(60));
        if (!fetch.Ok)
            return GitSyncResult.Fail($"git fetch failed: {BuildLifecycleMessage(fetch, "fetch failed")}");

        var remoteRef = $"{_config.GitOps.RemoteName}/{_config.GitOps.BaseBranch}";
        var behind = await ExecuteProcessAsync("git", new[] { "rev-list", "--count", $"HEAD..{remoteRef}" }, repoPath, TimeSpan.FromSeconds(20));
        if (!behind.Ok)
            return GitSyncResult.Fail($"git rev-list failed: {BuildLifecycleMessage(behind, "rev-list failed")}");
        var hasUpdates = int.TryParse((behind.StdOut ?? string.Empty).Trim(), out var behindCount) && behindCount > 0;
        if (!hasUpdates)
        {
            return new GitSyncResult
            {
                Success = true,
                Updated = false,
                Summary = $"No updates on {remoteRef}."
            };
        }

        var pull = await ExecuteProcessAsync(
            "git",
            new[] { "pull", "--rebase", _config.GitOps.RemoteName, _config.GitOps.BaseBranch },
            repoPath,
            TimeSpan.FromSeconds(90));
        if (!pull.Ok)
            return GitSyncResult.Fail($"git pull --rebase failed: {BuildLifecycleMessage(pull, "pull failed")}", true);

        BuildFromSourceResult? build = null;
        if (_config.GitOps.AutoPullRebuild || !automatic)
        {
            if (!_config.SelfRepair.AllowSourceBuilds)
                return GitSyncResult.Fail("Source builds are disabled by config.", true);

            build = await BuildFromSourceAsync("Release", "linux-x64");
            if (!build.Success)
                return GitSyncResult.Fail($"Pull succeeded but build failed: {build.Summary}", true, build);
        }

        RestartServicesResult? restart = null;
        if (restartServices)
        {
            if (!_config.SelfRepair.AllowServiceRestarts)
                return GitSyncResult.Fail("Restart requested but service restarts are disabled by config.", true, build);

            restart = await RestartManagedServicesAsync();
            if (!restart.Success)
                return GitSyncResult.Fail($"Pull/build succeeded but restart failed: {restart.Summary}", true, build, restart);
        }

        return new GitSyncResult
        {
            Success = true,
            Updated = true,
            Build = build,
            Restart = restart,
            Summary = restartServices
                ? $"Pulled {remoteRef}, rebuilt, and restarted managed services."
                : $"Pulled {remoteRef} and rebuilt successfully."
        };
    }

    private string BuildAgentBranchName()
    {
        var prefix = _config.GitOps.PushBranchPrefix;
        if (!prefix.EndsWith("/", StringComparison.Ordinal))
            prefix += "/";
        return $"{prefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}";
    }

    private static string SanitizeCommitMessage(string message)
    {
        var normalized = string.IsNullOrWhiteSpace(message)
            ? "agent: self-update"
            : message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }

    private static string ResolveGitCommitMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return $"agent: self-update {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
        return SanitizeCommitMessage(message);
    }

    private async Task<BuildFromSourceResult> BuildFromSourceAsync(string configuration, string runtime)
    {
        var normalizedConfiguration = string.IsNullOrWhiteSpace(configuration) ? "Release" : configuration.Trim();
        var normalizedRuntime = string.IsNullOrWhiteSpace(runtime) ? "linux-x64" : runtime.Trim();

        var sourceRoot = Path.GetFullPath(_config.SelfRepair.SourceRootPath);
        var outputRoot = Path.GetFullPath(_config.SelfRepair.BuildOutputPath);
        if (!IsPathWithinSelfRepairScope(sourceRoot) || !IsPathWithinSelfRepairScope(outputRoot))
        {
            return new BuildFromSourceResult
            {
                Success = false,
                Configuration = normalizedConfiguration,
                Runtime = normalizedRuntime,
                Summary = "Source/build paths are outside self-repair scope root."
            };
        }

        var projects = new[]
        {
            new BuildTarget(
                "agent",
                Path.Combine(sourceRoot, "agent", "RustOpsAgent", "RustOpsAgent.csproj"),
                Path.Combine(outputRoot, "agent", "RustOpsAgent")),
            new BuildTarget(
                "api",
                Path.Combine(sourceRoot, "api", "rustmgrapi.csproj"),
                Path.Combine(outputRoot, "api")),
            new BuildTarget(
                "steambot",
                Path.Combine(sourceRoot, "SteamBot", "OpsSteamBot", "OpsSteamBot.csproj"),
                Path.Combine(outputRoot, "SteamBot", "OpsSteamBot"))
        };

        var targetResults = new List<BuildTargetResult>();
        foreach (var project in projects)
        {
            if (!File.Exists(project.ProjectPath))
            {
                targetResults.Add(new BuildTargetResult
                {
                    Name = project.Name,
                    Success = false,
                    ExitCode = -1,
                    Message = $"Project not found: {project.ProjectPath}"
                });
                continue;
            }

            Directory.CreateDirectory(project.OutputPath);
            var args = new[]
            {
                "publish",
                project.ProjectPath,
                "-c", normalizedConfiguration,
                "-r", normalizedRuntime,
                "-o", project.OutputPath,
                "--nologo"
            };

            var result = await ExecuteProcessAsync("dotnet", args, sourceRoot, TimeSpan.FromMinutes(20));
            targetResults.Add(new BuildTargetResult
            {
                Name = project.Name,
                Success = result.Ok,
                ExitCode = result.ExitCode,
                Message = BuildLifecycleMessage(result, "build result unavailable")
            });
        }

        var failedTargets = targetResults.Where(target => !target.Success).ToList();
        var summary = failedTargets.Count == 0
            ? $"Published {targetResults.Count} targets to {outputRoot}."
            : $"Build failed for: {string.Join(", ", failedTargets.Select(target => target.Name))}.";

        return new BuildFromSourceResult
        {
            Success = failedTargets.Count == 0,
            Configuration = normalizedConfiguration,
            Runtime = normalizedRuntime,
            Targets = targetResults,
            Summary = summary
        };
    }

    private async Task<RestartServicesResult> RestartManagedServicesAsync()
    {
        var units = new[]
        {
            "rustmgrapi.service",
            "rustopsagent.service",
            "opssteambot.service"
        };

        var serviceResults = new List<ServiceRestartResult>();
        foreach (var unit in units)
        {
            var restart = await ExecuteProcessAsync(
                "systemctl",
                new[] { "restart", unit },
                _selfRepairScopeRootPath,
                TimeSpan.FromSeconds(45));

            if (!restart.Ok)
            {
                restart = await ExecuteProcessAsync(
                    "sudo",
                    new[] { "systemctl", "restart", unit },
                    _selfRepairScopeRootPath,
                    TimeSpan.FromSeconds(60));
            }

            serviceResults.Add(new ServiceRestartResult
            {
                Unit = unit,
                Success = restart.Ok,
                ExitCode = restart.ExitCode,
                Message = BuildLifecycleMessage(restart, "restart result unavailable")
            });
        }

        var failures = serviceResults.Where(result => !result.Success).ToList();
        return new RestartServicesResult
        {
            Success = failures.Count == 0,
            Services = serviceResults,
            Summary = failures.Count == 0
                ? "Managed services restarted."
                : $"Failed to restart: {string.Join(", ", failures.Select(f => f.Unit))}."
        };
    }

    private static async Task<CommandExecutionResult> ExecuteProcessAsync(
        string fileName,
        IEnumerable<string> args,
        string workingDirectory,
        TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return new CommandExecutionResult
                {
                    Ok = false,
                    ExitCode = -1,
                    Arguments = new[] { fileName }.Concat(args),
                    StdErr = $"Failed to start '{fileName}'."
                };
            }

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(true);
                }
                catch
                {
                }

                return new CommandExecutionResult
                {
                    Ok = false,
                    ExitCode = -1,
                    TimedOut = true,
                    Arguments = new[] { fileName }.Concat(args),
                    StdErr = $"Command timed out after {timeout.TotalSeconds:0}s."
                };
            }

            return new CommandExecutionResult
            {
                Ok = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Arguments = new[] { fileName }.Concat(args),
                StdOut = (await stdOutTask).Trim(),
                StdErr = (await stdErrTask).Trim()
            };
        }
        catch (Exception ex)
        {
            return new CommandExecutionResult
            {
                Ok = false,
                ExitCode = -1,
                Arguments = new[] { fileName }.Concat(args),
                StdErr = ex.Message
            };
        }
    }

    private ChatToolExecutionResult ExecuteDiagnoseAgentRuntimeTool()
    {
        var diagnostics = new
        {
            ok = true,
            utc = DateTime.UtcNow,
            recentErrors = _memory.AgentErrors.TakeLast(12).ToList(),
            recentFailedActions = _memory.ActionHistory
                .Where(entry => !entry.Success)
                .OrderByDescending(entry => entry.ExecutedAtUtc)
                .Take(12)
                .Select(entry => new
                {
                    atUtc = entry.ExecutedAtUtc,
                    actionType = entry.ActionType,
                    serverName = entry.ServerName,
                    summary = entry.Summary
                })
                .ToList(),
            capabilityGaps = _memory.CapabilityGaps
                .OrderByDescending(gap => gap.LastObservedAtUtc)
                .Take(12)
                .ToList(),
            selfRepair = new
            {
                enabled = _config.SelfRepair.Enabled,
                scopeRootPath = _selfRepairScopeRootPath,
                workspacePath = _selfRepairWorkspacePath,
                intervalSeconds = _config.SelfRepair.IntervalSeconds,
                maxActionsPerCycle = _config.SelfRepair.MaxActionsPerCycle,
                recentRuns = _memory.SelfRepairHistory
                    .OrderByDescending(run => run.AtUtc)
                    .Take(8)
                    .ToList()
            },
            commandExecution = new
            {
                enabled = _config.CommandExecution.Enabled,
                freeMode = _config.CommandExecution.FreeMode,
                allowList = _config.CommandExecution.AllowList
            },
            gitOps = new
            {
                enabled = _config.GitOps.Enabled,
                repoPath = _config.GitOps.RepoPath,
                remote = _config.GitOps.RemoteName,
                baseBranch = _config.GitOps.BaseBranch,
                branchPrefix = _config.GitOps.PushBranchPrefix,
                allowPush = _config.GitOps.AllowPush,
                allowManualPullRebuild = _config.GitOps.AllowManualPullRebuild,
                autoPullEnabled = _config.GitOps.AutoPullEnabled,
                autoPullIntervalMinutes = _config.GitOps.AutoPullIntervalMinutes,
                autoPullRebuild = _config.GitOps.AutoPullRebuild,
                autoRestartAfterPullRebuild = _config.GitOps.AutoRestartAfterPullRebuild,
                requireCleanWorktreeForPull = _config.GitOps.RequireCleanWorktreeForPull
            }
        };

        return ChatToolExecutionResult.Success(SerializeToolPayload(diagnostics));
    }

    private ChatToolExecutionResult ExecuteListScopeFilesTool(JsonElement arguments)
    {
        var limit = ReadToolIntArgument(arguments, "limit", 20, 1, 80);
        var files = EnumerateScopeFilePreviews()
            .Take(limit)
            .Select(file => new
            {
                relativePath = file.RelativePath,
                sizeBytes = file.SizeBytes,
                lastWriteAtUtc = file.LastWriteAtUtc,
                preview = file.Preview
            })
            .ToList();

        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            scopeRootPath = _selfRepairScopeRootPath,
            files
        }));
    }

    private ChatToolExecutionResult ExecuteReadScopeFileTool(JsonElement arguments)
    {
        var relativePath = ReadToolArgument(arguments, "relativePath");
        if (string.IsNullOrWhiteSpace(relativePath))
            return ChatToolExecutionResult.Error("relativePath is required.");

        var maxChars = ReadToolIntArgument(arguments, "maxChars", 4000, 200, 20_000);
        string fullPath;
        try
        {
            fullPath = ResolveScopePath(relativePath);
        }
        catch (Exception ex)
        {
            return ChatToolExecutionResult.Error(ex.Message);
        }

        if (!File.Exists(fullPath))
        {
            return ChatToolExecutionResult.Success(SerializeToolPayload(new
            {
                ok = true,
                exists = false,
                relativePath = relativePath.Replace('\\', '/')
            }));
        }

        var content = File.ReadAllText(fullPath);
        if (content.Length > maxChars)
            content = content[..maxChars];

        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            exists = true,
            relativePath = Path.GetRelativePath(_selfRepairScopeRootPath, fullPath).Replace('\\', '/'),
            content
        }));
    }

    private ChatToolExecutionResult ExecuteWriteScopeFileTool(JsonElement arguments)
    {
        if (!_config.SelfRepair.AllowScopeFileWrites)
            return ChatToolExecutionResult.Error("Scope file writes are disabled by config.");

        var relativePath = ReadToolArgument(arguments, "relativePath");
        var content = ReadToolArgument(arguments, "content");
        if (string.IsNullOrWhiteSpace(relativePath))
            return ChatToolExecutionResult.Error("relativePath is required.");

        try
        {
            ApplyWriteScopeFile(relativePath, content);
            return ChatToolExecutionResult.Success(SerializeToolPayload(new
            {
                ok = true,
                relativePath = relativePath.Replace('\\', '/'),
                bytes = Encoding.UTF8.GetByteCount(content ?? string.Empty)
            }));
        }
        catch (Exception ex)
        {
            return ChatToolExecutionResult.Error(ex.Message);
        }
    }

    private ChatToolExecutionResult ExecuteListAgentWorkspaceFilesTool(JsonElement arguments)
    {
        var limit = ReadToolIntArgument(arguments, "limit", 20, 1, 60);
        var files = EnumerateWorkspaceFilePreviews()
            .Take(limit)
            .Select(file => new
            {
                relativePath = file.RelativePath,
                sizeBytes = file.SizeBytes,
                lastWriteAtUtc = file.LastWriteAtUtc,
                preview = file.Preview
            })
            .ToList();

        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            workspacePath = _selfRepairWorkspacePath,
            files
        }));
    }

    private ChatToolExecutionResult ExecuteReadAgentWorkspaceFileTool(JsonElement arguments)
    {
        var relativePath = ReadToolArgument(arguments, "relativePath");
        if (string.IsNullOrWhiteSpace(relativePath))
            return ChatToolExecutionResult.Error("relativePath is required.");

        var maxChars = ReadToolIntArgument(arguments, "maxChars", 4000, 200, 20_000);
        string fullPath;
        try
        {
            fullPath = ResolveWorkspacePath(relativePath);
        }
        catch (Exception ex)
        {
            return ChatToolExecutionResult.Error(ex.Message);
        }

        if (!File.Exists(fullPath))
        {
            return ChatToolExecutionResult.Success(SerializeToolPayload(new
            {
                ok = true,
                exists = false,
                relativePath = relativePath.Replace('\\', '/')
            }));
        }

        var content = File.ReadAllText(fullPath);
        if (content.Length > maxChars)
            content = content[..maxChars];

        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            exists = true,
            relativePath = Path.GetRelativePath(_selfRepairWorkspacePath, fullPath).Replace('\\', '/'),
            content
        }));
    }

    private ChatToolExecutionResult ExecuteWriteAgentWorkspaceFileTool(JsonElement arguments)
    {
        var relativePath = ReadToolArgument(arguments, "relativePath");
        var content = ReadToolArgument(arguments, "content");
        if (string.IsNullOrWhiteSpace(relativePath))
            return ChatToolExecutionResult.Error("relativePath is required.");

        try
        {
            ApplyWriteWorkspaceFile(relativePath, content);
            return ChatToolExecutionResult.Success(SerializeToolPayload(new
            {
                ok = true,
                relativePath = relativePath.Replace('\\', '/'),
                bytes = Encoding.UTF8.GetByteCount(content ?? string.Empty)
            }));
        }
        catch (Exception ex)
        {
            return ChatToolExecutionResult.Error(ex.Message);
        }
    }

    private ChatToolExecutionResult ExecuteUpdateLogRulesTool(JsonElement arguments)
    {
        if (!_config.SelfRepair.ApplyLogRuleUpdates)
            return ChatToolExecutionResult.Error("Log-rule updates are disabled by config.");

        var action = new SelfRepairAction
        {
            Type = "merge_log_rules",
            IgnoreContains = ReadToolStringArray(arguments, "ignoreContains"),
            StartupIgnoreContains = ReadToolStringArray(arguments, "startupIgnoreContains"),
            IncidentContains = ReadToolStringArray(arguments, "incidentContains")
        };

        try
        {
            MergeLogRules(action);
            return ChatToolExecutionResult.Success(SerializeToolPayload(new
            {
                ok = true,
                ignoreContains = action.IgnoreContains?.Count ?? 0,
                startupIgnoreContains = action.StartupIgnoreContains?.Count ?? 0,
                incidentContains = action.IncidentContains?.Count ?? 0
            }));
        }
        catch (Exception ex)
        {
            return ChatToolExecutionResult.Error(ex.Message);
        }
    }

    private ChatToolExecutionResult ExecuteUpdateReplyStyleTool(JsonElement arguments)
    {
        if (!_config.SelfRepair.ApplyReplyStyleUpdates)
            return ChatToolExecutionResult.Error("Reply-style updates are disabled by config.");

        var content = ReadToolArgument(arguments, "content");
        if (string.IsNullOrWhiteSpace(content))
            return ChatToolExecutionResult.Error("content is required.");

        try
        {
            ApplyReplyStyleUpdate(content);
            return ChatToolExecutionResult.Success(SerializeToolPayload(new
            {
                ok = true,
                path = Path.GetRelativePath(_selfRepairWorkspacePath, _replyStylePath).Replace('\\', '/'),
                bytes = Encoding.UTF8.GetByteCount(content)
            }));
        }
        catch (Exception ex)
        {
            return ChatToolExecutionResult.Error(ex.Message);
        }
    }

    private ChatToolExecutionResult ExecuteRecentIncidentsTool(JsonElement arguments)
    {
        var requestedServer = ReadToolArgument(arguments, "serverName");
        var limit = ReadToolLimit(arguments, 6);
        var incidents = _memory.Servers
            .SelectMany(server => server.Incidents.Select(incident => new { server.Name, Incident = incident }))
            .Where(item => string.IsNullOrWhiteSpace(requestedServer) || string.Equals(item.Name, requestedServer, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Incident.CreatedAtUtc)
            .Take(limit)
            .Select(item => new
            {
                serverName = item.Name,
                createdAtUtc = item.Incident.CreatedAtUtc,
                title = item.Incident.Title,
                category = item.Incident.Category,
                summary = item.Incident.Summary
            })
            .ToList();

        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            serverName = requestedServer,
            incidents
        }), requestedServer);
    }

    private ChatToolExecutionResult ExecuteRecentActionsTool(JsonElement arguments)
    {
        var requestedServer = ReadToolArgument(arguments, "serverName");
        var limit = ReadToolLimit(arguments, 6);
        var actions = _memory.ActionHistory
            .Where(action => string.IsNullOrWhiteSpace(requestedServer) || string.Equals(action.ServerName, requestedServer, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(action => action.ExecutedAtUtc)
            .Take(limit)
            .Select(action => new
            {
                actionId = action.ActionId,
                serverName = action.ServerName,
                actionType = action.ActionType,
                executedAtUtc = action.ExecutedAtUtc,
                success = action.Success,
                trigger = action.Trigger,
                summary = action.Summary
            })
            .ToList();

        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            serverName = requestedServer,
            actions
        }), requestedServer);
    }

    private ChatToolExecutionResult ExecutePendingActionsTool(JsonElement arguments)
    {
        var requestedServer = ReadToolArgument(arguments, "serverName");
        var limit = ReadToolLimit(arguments, 6);
        var pending = _memory.PendingActions
            .Where(action => action.Status == ActionStatus.Pending)
            .Where(action => string.IsNullOrWhiteSpace(requestedServer) || string.Equals(action.ServerName, requestedServer, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(action => action.CreatedAtUtc)
            .Take(limit)
            .Select(action => new
            {
                id = action.Id,
                serverName = action.ServerName,
                actionType = action.ActionType,
                createdAtUtc = action.CreatedAtUtc,
                requiresApproval = action.RequiresApproval,
                status = action.Status.ToString(),
                summary = action.Summary
            })
            .ToList();

        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            serverName = requestedServer,
            pendingActions = pending
        }), requestedServer);
    }

    private async Task<ChatToolExecutionResult> ExecuteInspectNetworkToolAsync(string adminId, DateTime utcNow)
    {
        var interpretation = new ChatInterpretation { Intent = "inspect-host-network" };
        var summary = await ExecuteChatUtilityActionAsync(adminId, interpretation, utcNow, "inspect-host-network", new List<ServerSnapshot>());
        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            actionType = "inspect-host-network",
            summary
        }));
    }

    private async Task<ChatToolExecutionResult> ExecuteActionBackedToolAsync(
        string adminId,
        JsonElement arguments,
        List<ServerSnapshot> servers,
        DateTime utcNow,
        string actionType)
    {
        var intent = actionType switch
        {
            "validate-oxide" => "validate-oxide",
            _ => "unknown"
        };

        var resolution = ResolveToolServer(arguments, servers, intent);
        if (!resolution.Matched)
            return resolution.Result;

        var interpretation = new ChatInterpretation
        {
            Intent = actionType,
            ServerName = resolution.Server!.Name
        };

        var summary = await ExecuteChatUtilityActionAsync(adminId, interpretation, utcNow, actionType, servers);
        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            actionType,
            serverName = resolution.Server.Name,
            summary
        }), resolution.Server.Name);
    }

    private async Task<ChatToolExecutionResult> ExecuteLifecycleToolAsync(
        string adminId,
        JsonElement arguments,
        List<ServerSnapshot> servers,
        DateTime utcNow,
        HashSet<string> lifecycleMutations,
        string actionType)
    {
        var resolution = ResolveToolServer(arguments, servers, actionType);
        if (!resolution.Matched)
            return resolution.Result;

        var mutationKey = $"{resolution.Server!.Name}|lifecycle";
        if (!lifecycleMutations.Add(mutationKey))
        {
            return ChatToolExecutionResult.Error(
                $"A lifecycle action has already been executed for {resolution.Server.Name} in this turn.",
                resolution.Server.Name,
                actionType);
        }

        var interpretation = new ChatInterpretation
        {
            Intent = actionType,
            ServerName = resolution.Server.Name
        };

        var summary = await ExecuteChatLifecycleActionAsync(adminId, interpretation, utcNow, actionType, servers);
        return ChatToolExecutionResult.Success(SerializeToolPayload(new
        {
            ok = true,
            actionType,
            serverName = resolution.Server.Name,
            summary
        }), resolution.Server.Name);
    }

    private ToolServerResolution ResolveToolServer(JsonElement arguments, List<ServerSnapshot> servers, string intent)
    {
        var requestedServer = ReadToolArgument(arguments, "serverName");
        var resolvedServerName = ResolveServerName(requestedServer, servers);
        var server = ResolveServer(resolvedServerName, servers);
        if (server is not null)
            return ToolServerResolution.Resolved(server);

        var question = BuildClarificationQuestion(intent, servers);
        return ToolServerResolution.NeedsClarification(
            ChatToolExecutionResult.Error(question, pendingClarificationIntent: intent),
            intent);
    }

    private static JsonElement ParseToolArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return JsonDocument.Parse("{}").RootElement.Clone();

        try
        {
            return JsonDocument.Parse(argumentsJson).RootElement.Clone();
        }
        catch
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    private static string ReadToolArgument(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return string.Empty;

        foreach (var property in arguments.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()?.Trim() ?? string.Empty
                : property.Value.ToString();
        }

        return string.Empty;
    }

    private static int ReadToolLimit(JsonElement arguments, int fallback)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return fallback;

        foreach (var property in arguments.EnumerateObject())
        {
            if (!string.Equals(property.Name, "limit", StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var numericLimit))
                return Math.Clamp(numericLimit, 1, 12);

            if (property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), out numericLimit))
                return Math.Clamp(numericLimit, 1, 12);
        }

        return fallback;
    }

    private static int ReadToolIntArgument(JsonElement arguments, string name, int fallback, int min, int max)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return fallback;

        foreach (var property in arguments.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var numeric))
                return Math.Clamp(numeric, min, max);

            if (property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), out numeric))
                return Math.Clamp(numeric, min, max);
        }

        return fallback;
    }

    private static bool ReadToolBooleanArgument(JsonElement arguments, string name, bool fallback)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return fallback;

        foreach (var property in arguments.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind == JsonValueKind.True)
                return true;
            if (property.Value.ValueKind == JsonValueKind.False)
                return false;
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    return fallback;
                return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("on", StringComparison.OrdinalIgnoreCase);
            }
        }

        return fallback;
    }

    private static List<string> ReadToolStringArray(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return new List<string>();

        foreach (var property in arguments.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind != JsonValueKind.Array)
                return new List<string>();

            return property.Value.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return new List<string>();
    }

    private static string NormalizeChatToolName(string? name) =>
        (name ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "list-servers" => "list_servers",
            "get-server-status" => "get_server_status",
            "get-server-health" => "get_server_health",
            "get-recent-incidents" => "get_recent_incidents",
            "get-recent-actions" => "get_recent_actions",
            "get-pending-actions" => "get_pending_actions",
            "validate-oxide" => "validate_oxide",
            "inspect-host-network" => "inspect_host_network",
            "start-server" => "start_server",
            "stop-server" => "stop_server",
            "restart-server" => "restart_server",
            "get-server-players" => "get_server_players",
            "get-server-events" => "get_server_events",
            "exec-server-command" => "execute_server_command",
            "execute-server-command" => "execute_server_command",
            "get-server-command-memory" => "get_server_command_memory",
            "teach-server-command" => "teach_server_command",
            "list-server-plugins" => "list_server_plugins",
            "check-plugin-updates" => "check_plugin_updates",
            "git-push-branch" => "git_push_branch",
            "git-pull-rebuild" => "git_pull_rebuild",
            "diagnose-agent-runtime" => "diagnose_agent_runtime",
            "list-scope-files" => "list_scope_files",
            "read-scope-file" => "read_scope_file",
            "write-scope-file" => "write_scope_file",
            "list-agent-workspace-files" => "list_agent_workspace_files",
            "read-agent-workspace-file" => "read_agent_workspace_file",
            "write-agent-workspace-file" => "write_agent_workspace_file",
            "update-log-rules" => "update_log_rules",
            "update-reply-style" => "update_reply_style",
            _ => (name ?? string.Empty).Trim().ToLowerInvariant()
        };

    private static string SerializeToolPayload(object payload) =>
        JsonSerializer.Serialize(payload, JsonOptions.Default);

    private static string FormatPromptList(IEnumerable<string> items)
    {
        var values = items
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(8)
            .ToList();

        return values.Count == 0 ? "none" : string.Join('\n', values.Select(item => $"- {item}"));
    }

    private ChatInterpretation InterpretChatRequestHeuristically(string message, List<ServerSnapshot> servers, AdminConversationState conversation)
    {
        var normalized = message.Trim();
        var lowered = normalized.ToLowerInvariant();
        var serverName = ResolveServerName(normalized, servers);

        if (lowered is "help" or "/help" or "?")
            return new ChatInterpretation { Intent = "help", Confidence = 1.0 };

        if (lowered is "ping" or "/ping")
            return new ChatInterpretation { Intent = "ping", Confidence = 1.0 };

        if (lowered.Contains("pending", StringComparison.Ordinal))
            return new ChatInterpretation { Intent = "pending-actions", Confidence = 0.9 };

        if (lowered.Contains("recent", StringComparison.Ordinal) && lowered.Contains("action", StringComparison.Ordinal))
            return new ChatInterpretation { Intent = "recent-actions", Confidence = 0.9 };

        if (lowered.Contains("incident", StringComparison.Ordinal) ||
            lowered.Contains("what happened", StringComparison.Ordinal) ||
            lowered.Contains("anything wrong", StringComparison.Ordinal))
        {
            return new ChatInterpretation { Intent = "recent-incidents", Confidence = 0.8 };
        }

        if ((lowered.Contains("server", StringComparison.Ordinal) || lowered.Contains("servers", StringComparison.Ordinal)) &&
            (lowered.Contains("list", StringComparison.Ordinal) ||
             lowered.Contains("running", StringComparison.Ordinal) ||
             lowered.Contains("show", StringComparison.Ordinal) ||
             lowered.Contains("what", StringComparison.Ordinal)))
        {
            return new ChatInterpretation { Intent = "list-servers", Confidence = 0.8 };
        }

        if (lowered.StartsWith("status", StringComparison.Ordinal) ||
            lowered.Contains("status of", StringComparison.Ordinal) ||
            lowered.Contains("how is", StringComparison.Ordinal))
        {
            return new ChatInterpretation { Intent = "server-status", ServerName = serverName, Confidence = 0.8 };
        }

        if (lowered.StartsWith("health", StringComparison.Ordinal) ||
            lowered.Contains("health of", StringComparison.Ordinal) ||
            lowered.Contains("errors on", StringComparison.Ordinal))
        {
            return new ChatInterpretation { Intent = "server-health", ServerName = serverName, Confidence = 0.8 };
        }

        if ((lowered.Contains("plugin", StringComparison.Ordinal) || lowered.Contains("plugins", StringComparison.Ordinal)) &&
            (lowered.Contains("update", StringComparison.Ordinal) || lowered.Contains("upgrade", StringComparison.Ordinal) || lowered.Contains("new version", StringComparison.Ordinal)))
        {
            return new ChatInterpretation { Intent = "check-plugin-updates", ServerName = serverName, Confidence = 0.9 };
        }

        if ((lowered.Contains("list plugin", StringComparison.Ordinal) ||
             lowered.Contains("show plugin", StringComparison.Ordinal) ||
             lowered.StartsWith("plugins", StringComparison.Ordinal) ||
             lowered.Contains("plugin list", StringComparison.Ordinal)))
        {
            return new ChatInterpretation { Intent = "list-server-plugins", ServerName = serverName, Confidence = 0.9 };
        }

        if ((lowered.Contains("validate", StringComparison.Ordinal) ||
             lowered.Contains("check", StringComparison.Ordinal) ||
             lowered.Contains("inspect", StringComparison.Ordinal)) &&
            (lowered.Contains("oxide", StringComparison.Ordinal) ||
             lowered.Contains("plugin", StringComparison.Ordinal)))
        {
            return new ChatInterpretation { Intent = "validate-oxide", ServerName = serverName, Confidence = 0.85 };
        }

        if (lowered.Contains("network", StringComparison.Ordinal) ||
            lowered.Contains("throughput", StringComparison.Ordinal) ||
            lowered.Contains("bandwidth", StringComparison.Ordinal) ||
            lowered.Contains("mbps", StringComparison.Ordinal) ||
            lowered.Contains("gbps", StringComparison.Ordinal) ||
            lowered.Contains("spike", StringComparison.Ordinal))
        {
            return new ChatInterpretation { Intent = "inspect-host-network", Confidence = 0.8 };
        }

        if (ContainsActionWord(lowered, "restart", "reboot", "cycle"))
            return new ChatInterpretation { Intent = "restart-server", ServerName = serverName, Confidence = 0.85 };

        if (ContainsActionWord(lowered, "start", "boot", "launch"))
            return new ChatInterpretation { Intent = "start-server", ServerName = serverName, Confidence = 0.85 };

        if (ContainsActionWord(lowered, "stop", "shutdown", "shut down"))
            return new ChatInterpretation { Intent = "stop-server", ServerName = serverName, Confidence = 0.85 };

        if (ShouldUseLastServer(message) && !string.IsNullOrWhiteSpace(conversation.LastServerName))
        {
            if (lowered.Contains("status", StringComparison.Ordinal))
                return new ChatInterpretation { Intent = "server-status", ServerName = conversation.LastServerName, Confidence = 0.75, UseLastServer = true };
            if (lowered.Contains("health", StringComparison.Ordinal) || lowered.Contains("error", StringComparison.Ordinal))
                return new ChatInterpretation { Intent = "server-health", ServerName = conversation.LastServerName, Confidence = 0.75, UseLastServer = true };
            if (ContainsActionWord(lowered, "restart", "reboot", "cycle"))
                return new ChatInterpretation { Intent = "restart-server", ServerName = conversation.LastServerName, Confidence = 0.75, UseLastServer = true };
            if (ContainsActionWord(lowered, "start", "boot", "launch"))
                return new ChatInterpretation { Intent = "start-server", ServerName = conversation.LastServerName, Confidence = 0.75, UseLastServer = true };
            if (ContainsActionWord(lowered, "stop", "shutdown", "shut down"))
                return new ChatInterpretation { Intent = "stop-server", ServerName = conversation.LastServerName, Confidence = 0.75, UseLastServer = true };
        }

        return new ChatInterpretation
        {
            Intent = "unknown",
            Confidence = 0.0,
            ReplyText = "I couldn't map that to a safe operation. Try: 'status vanilla', 'health modded', 'check plugin updates modded', 'check plugin compile issues modded', 'restart onegrid', or 'what happened recently'."
        };
    }

    private void ApplyConversationContext(ChatInterpretation plan, string message, List<ServerSnapshot> servers, AdminConversationState conversation)
    {
        if (string.IsNullOrWhiteSpace(plan.ServerName) &&
            plan.UseLastServer &&
            !string.IsNullOrWhiteSpace(conversation.LastServerName))
        {
            plan.ServerName = ResolveServerName(conversation.LastServerName, servers);
        }

        if (string.IsNullOrWhiteSpace(plan.ServerName) &&
            IsServerScopedIntent(plan.Intent) &&
            servers.Count == 1)
        {
            plan.ServerName = servers[0].Name;
        }

        if (string.IsNullOrWhiteSpace(plan.ServerName) &&
            IsServerScopedIntent(plan.Intent) &&
            !string.IsNullOrWhiteSpace(conversation.LastServerName) &&
            ShouldUseLastServer(message))
        {
            plan.ServerName = ResolveServerName(conversation.LastServerName, servers);
            plan.UseLastServer = !string.IsNullOrWhiteSpace(plan.ServerName);
        }

        if (string.IsNullOrWhiteSpace(plan.ServerName) && IsServerScopedIntent(plan.Intent))
        {
            plan.NeedsClarification = true;
            plan.ClarificationQuestion ??= BuildClarificationQuestion(plan.Intent, servers);
        }
    }

    private ChatInterpretation? TryResolvePendingClarification(string message, List<ServerSnapshot> servers, AdminConversationState conversation)
    {
        if (conversation.PendingClarification is null)
            return null;

        var serverName = ResolveServerName(message, servers);
        if (string.IsNullOrWhiteSpace(serverName))
            return null;

        return new ChatInterpretation
        {
            Intent = conversation.PendingClarification.Intent,
            ServerName = serverName,
            Confidence = 0.95,
            ReplyText = $"Using {serverName}."
        };
    }

    private async Task<string> ExecuteChatPlanAsync(string adminId, ChatInterpretation plan, List<ServerSnapshot> servers, DateTime utcNow)
    {
        if (string.Equals(plan.Intent, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            _memory.RecordCapabilityGap("chat-intent", "Chat intent resolved to unknown; response fell back to generic guidance.");
        }

        return plan.Intent switch
        {
            "help" => BuildChatHelpText(),
            "ping" => "pong",
            "list-servers" => BuildServerSummaryReply(servers),
            "pending-actions" => BuildPendingActionsReply(),
            "recent-actions" => BuildRecentActionsReply(),
            "recent-incidents" => BuildRecentIncidentsReply(),
            "server-status" => await BuildServerStatusReplyAsync(plan.ServerName, servers),
            "server-health" => await BuildServerHealthReplyAsync(plan.ServerName, servers),
            "list-server-plugins" => await BuildServerPluginsReplyAsync(plan.ServerName, servers),
            "check-plugin-updates" => await BuildPluginUpdatesReplyAsync(plan.ServerName, servers),
            "validate-oxide" => await ExecuteChatUtilityActionAsync(adminId, plan, utcNow, "validate-oxide", servers),
            "inspect-host-network" => await ExecuteChatUtilityActionAsync(adminId, plan, utcNow, "inspect-host-network", servers),
            "start-server" => await ExecuteChatLifecycleActionAsync(adminId, plan, utcNow, "start-server", servers),
            "stop-server" => await ExecuteChatLifecycleActionAsync(adminId, plan, utcNow, "stop-server", servers),
            "restart-server" => await ExecuteChatLifecycleActionAsync(adminId, plan, utcNow, "restart-server", servers),
            _ => string.IsNullOrWhiteSpace(plan.ReplyText)
                ? "I couldn't infer a safe action from that yet. Ask for server status, health, plugin updates, plugin compile checks, pending actions, recent actions, or restart/start/stop for a named server."
                : plan.ReplyText!
        };
    }

    private async Task<string> BuildServerStatusReplyAsync(string? requestedServer, List<ServerSnapshot> servers)
    {
        var server = ResolveServer(requestedServer, servers);
        if (server is null)
            return BuildServerNotFoundReply(servers);

        using var json = await _api.GetJsonAsync($"/servers/{Uri.EscapeDataString(server.Name)}/status");
        var state = json.RootElement.GetProperty("state").GetString() ?? "unknown";
        var online = json.RootElement.GetProperty("online").GetBoolean() ? "online" : "offline";
        var autoRestart = json.RootElement.GetProperty("autoRestart").GetBoolean() ? "on" : "off";
        var pid = json.RootElement.TryGetProperty("pid", out var pidNode) && pidNode.ValueKind == JsonValueKind.Number
            ? pidNode.GetInt32().ToString()
            : "-";
        return $"{server.Name}: {state} ({online}), autorestart={autoRestart}, pid={pid}";
    }

    private async Task<string> BuildServerHealthReplyAsync(string? requestedServer, List<ServerSnapshot> servers)
    {
        var server = ResolveServer(requestedServer, servers);
        if (server is null)
            return BuildServerNotFoundReply(servers);

        using var json = await _api.GetJsonAsync($"/servers/{Uri.EscapeDataString(server.Name)}/health");
        var status = json.RootElement.GetProperty("status");
        var state = status.GetProperty("state").GetString() ?? "unknown";
        var errorCount = json.RootElement.GetProperty("recentErrors").GetArrayLength();
        var lastRestart = json.RootElement.TryGetProperty("lastRestartEvent", out var restartNode) && restartNode.ValueKind == JsonValueKind.String
            ? restartNode.GetString()
            : "none";

        var builder = new StringBuilder();
        builder.AppendLine($"{server.Name}: {state}, recentErrors={errorCount}");
        builder.Append($"lastRestart={lastRestart ?? "none"}");

        if (errorCount > 0)
        {
            var latestError = json.RootElement.GetProperty("recentErrors")[errorCount - 1].GetString();
            if (!string.IsNullOrWhiteSpace(latestError))
            {
                builder.AppendLine();
                builder.Append($"latestError={latestError}");
            }
        }

        return builder.ToString();
    }

    private async Task<string> BuildServerPluginsReplyAsync(string? requestedServer, List<ServerSnapshot> servers)
    {
        var server = ResolveServer(requestedServer, servers);
        if (server is null)
            return BuildServerNotFoundReply(servers);

        List<ServerPluginInfo> plugins;
        try
        {
            plugins = await ReadServerPluginsAsync(server.Name);
        }
        catch (Exception ex)
        {
            return $"{server.Name}: failed to list plugins: {TrimSingleLine(ex.Message, 180)}";
        }

        if (plugins.Count == 0)
            return $"{server.Name}: no Oxide plugins detected.";

        var lines = plugins
            .Take(20)
            .Select(plugin => $"{plugin.Name} {plugin.Version} ({plugin.Author})")
            .ToList();

        var suffix = plugins.Count > 20 ? $"\n...and {plugins.Count - 20} more." : string.Empty;
        return $"{server.Name}: {plugins.Count} plugin(s)\n{string.Join('\n', lines)}{suffix}";
    }

    private async Task<string> BuildPluginUpdatesReplyAsync(string? requestedServer, List<ServerSnapshot> servers)
    {
        var server = ResolveServer(requestedServer, servers);
        if (server is null)
            return BuildServerNotFoundReply(servers);

        List<PluginUpdateCandidate> updates;
        try
        {
            updates = await GetPluginUpdatesAsync(server.Name);
        }
        catch (Exception ex)
        {
            return $"{server.Name}: failed to check plugin updates: {TrimSingleLine(ex.Message, 180)}";
        }

        if (updates.Count == 0)
            return $"{server.Name}: no plugin updates found.";

        var lines = updates
            .Take(12)
            .Select(update => $"{update.Name}: {update.LocalVersion} -> {update.RemoteVersion}")
            .ToList();

        var suffix = updates.Count > 12 ? $"\n...and {updates.Count - 12} more." : string.Empty;
        return $"{server.Name}: {updates.Count} update(s) available\n{string.Join('\n', lines)}{suffix}";
    }

    private async Task<string> BuildOxideCompileIssuesReplyAsync(string? requestedServer, List<ServerSnapshot> servers)
    {
        var server = ResolveServer(requestedServer, servers);
        if (server is null)
            return BuildServerNotFoundReply(servers);

        using var json = await _api.GetJsonAsync($"/servers/{Uri.EscapeDataString(server.Name)}/oxide/validate");
        var pluginIssues = new List<string>();
        if (json.RootElement.TryGetProperty("plugins", out var pluginsNode) && pluginsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in pluginsNode.EnumerateArray())
            {
                var ok = entry.TryGetProperty("ok", out var okNode) && okNode.ValueKind == JsonValueKind.True;
                if (ok)
                    continue;

                var label =
                    (entry.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String ? nameNode.GetString() : null) ??
                    (entry.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String ? titleNode.GetString() : null) ??
                    (entry.TryGetProperty("file", out var fileNode) && fileNode.ValueKind == JsonValueKind.String ? fileNode.GetString() : null) ??
                    "plugin";

                var detail = string.Empty;
                if (entry.TryGetProperty("errors", out var errorsNode) && errorsNode.ValueKind == JsonValueKind.Array)
                {
                    detail = string.Join(" | ", errorsNode.EnumerateArray().Select(node => node.ToString()).Where(value => !string.IsNullOrWhiteSpace(value)).Take(2));
                }
                else if (entry.TryGetProperty("message", out var messageNode) && messageNode.ValueKind == JsonValueKind.String)
                {
                    detail = messageNode.GetString() ?? string.Empty;
                }

                pluginIssues.Add($"{label}: {TrimSingleLine(string.IsNullOrWhiteSpace(detail) ? "compile/validation issue" : detail, 180)}");
            }
        }

        if (pluginIssues.Count == 0)
            return $"{server.Name}: no plugin compile issues detected in Oxide validation.";

        return $"{server.Name}: {pluginIssues.Count} plugin compile issue(s)\n{string.Join('\n', pluginIssues.Take(12))}";
    }

    private async Task<string> ExecuteChatLifecycleActionAsync(string adminId, ChatInterpretation interpretation, DateTime utcNow, string actionType, List<ServerSnapshot> servers)
    {
        var server = ResolveServer(interpretation.ServerName, servers);
        if (server is null)
            return BuildServerNotFoundReply(servers);

        var proposal = new ActionProposal
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = utcNow,
            LastUpdatedAtUtc = utcNow,
            ServerName = server.Name,
            ActionType = actionType,
            Summary = interpretation.ReplyText ?? $"{actionType} requested from chat",
            Reason = $"Requested via chat by admin {adminId}.",
            RequiresApproval = false,
            Status = ActionStatus.Executed,
            DecisionBy = adminId
        };

        var outcome = await ExecuteActionAsync(proposal, utcNow, $"chat:{adminId}");
        proposal.Status = outcome.Success ? ActionStatus.Executed : ActionStatus.Failed;
        proposal.DecisionNote = outcome.Summary;
        proposal.LastUpdatedAtUtc = utcNow;
        _memory.ActionHistory.Add(outcome);
        RememberActionOutcome(outcome);
        RecordActionMetric(proposal.ActionType, outcome.Success, "chat");
        TrimActionHistory();
        return BuildOutcomeMessage(proposal, outcome);
    }

    private async Task<string> ExecuteChatUtilityActionAsync(string adminId, ChatInterpretation interpretation, DateTime utcNow, string actionType, List<ServerSnapshot> servers)
    {
        var serverName = string.Empty;
        if (actionType == "validate-oxide")
        {
            var server = ResolveServer(interpretation.ServerName, servers);
            if (server is null)
                return BuildServerNotFoundReply(servers);

            serverName = server.Name;
        }

        var proposal = new ActionProposal
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = utcNow,
            LastUpdatedAtUtc = utcNow,
            ServerName = serverName,
            ActionType = actionType,
            Summary = interpretation.ReplyText ?? $"{actionType} requested from chat",
            Reason = $"Requested via chat by admin {adminId}.",
            RequiresApproval = false,
            Status = ActionStatus.Executed,
            DecisionBy = adminId
        };

        var outcome = await ExecuteActionAsync(proposal, utcNow, $"chat:{adminId}");
        proposal.Status = outcome.Success ? ActionStatus.Executed : ActionStatus.Failed;
        proposal.DecisionNote = outcome.Summary;
        proposal.LastUpdatedAtUtc = utcNow;
        _memory.ActionHistory.Add(outcome);
        RememberActionOutcome(outcome);
        RecordActionMetric(proposal.ActionType, outcome.Success, "chat");
        TrimActionHistory();
        return BuildOutcomeMessage(proposal, outcome);
    }

    private string BuildServerSummaryReply(List<ServerSnapshot> servers)
    {
        if (servers.Count == 0)
            return "No configured servers were returned by the API.";

        return string.Join('\n', servers.Select(server =>
        {
            var details = new List<string> { server.State };
            if (server.Pid.HasValue)
                details.Add($"pid={server.Pid.Value}");
            if (server.CurrentPlayers.HasValue || server.MaxPlayers.HasValue)
                details.Add($"players={server.CurrentPlayers?.ToString() ?? "?"}/{server.MaxPlayers?.ToString() ?? "?"}");
            if (!string.IsNullOrWhiteSpace(server.Map))
                details.Add($"map={server.Map}");
            if (server.Framerate.HasValue)
                details.Add($"fps={server.Framerate.Value:0.#}");
            return $"{server.Name}: {string.Join(", ", details)}";
        }));
    }

    private string BuildPendingActionsReply()
    {
        var lines = _memory.PendingActions
            .Where(a => a.Status == ActionStatus.Pending)
            .TakeLast(8)
            .Select(a => $"{a.Id}: {a.ActionType} on {a.ServerName}")
            .ToList();

        return lines.Count == 0 ? "No pending actions." : string.Join('\n', lines);
    }

    private string BuildRecentActionsReply()
    {
        var lines = _memory.ActionHistory
            .TakeLast(8)
            .Select(a => $"{a.ServerName}: {a.ActionType} - {TrimSingleLine(a.Summary, 140)}")
            .ToList();

        return lines.Count == 0 ? "No recent actions." : string.Join('\n', lines);
    }

    private string BuildRecentIncidentsReply()
    {
        var incidents = _memory.Servers
            .SelectMany(server => server.Incidents.Select(incident => new { server.Name, Incident = incident }))
            .OrderByDescending(item => item.Incident.CreatedAtUtc)
            .Take(8)
            .ToList();

        if (incidents.Count == 0)
            return "No recent incidents recorded.";

        return string.Join('\n', incidents.Select(item =>
            $"{item.Name}: {TrimSingleLine(item.Incident.Title, 120)}"));
    }

    private string BuildChatHelpText()
    {
        return string.Join('\n',
            "I can take natural-language admin requests.",
            "Examples:",
            "- what servers are running",
            "- status vanilla",
            "- health modded",
            "- players vanilla",
            "- recent events onegrid",
            "- run command oxide.plugins on modded",
            "- what does command oxide.plugins do on modded",
            "- teach command oxide.plugins on modded: lists loaded plugins",
            "- validate oxide sandbox",
            "- list plugins modded",
            "- check plugin updates modded",
            "- push my self-repair changes to git",
            "- pull latest git updates and rebuild",
            "- inspect host network throughput",
            "- restart onegrid",
            "- show pending actions",
            "- what happened recently",
            "- diagnose agent runtime",
            "Direct approval commands still work: approve <id>, reject <id>, feedback <id> <good|bad|note> [text]");
    }

    private static bool ContainsActionWord(string text, params string[] words) =>
        words.Any(word => text.Contains(word, StringComparison.Ordinal));

    private static bool IsServerScopedIntent(string? intent) =>
        intent is "server-status" or "server-health" or "validate-oxide" or
            "start-server" or "stop-server" or "restart-server" or
            "list-server-plugins" or "check-plugin-updates";

    private static bool LooksLikeCapabilityDenial(string content)
    {
        var lowered = content.ToLowerInvariant();
        return lowered.Contains("don't have the capability", StringComparison.Ordinal) ||
               lowered.Contains("do not have the capability", StringComparison.Ordinal) ||
               lowered.Contains("my available functions are limited", StringComparison.Ordinal) ||
               lowered.Contains("i don’t have info on plugin updates", StringComparison.Ordinal) ||
               lowered.Contains("i don't have info on plugin updates", StringComparison.Ordinal) ||
               lowered.Contains("i cannot directly control", StringComparison.Ordinal);
    }

    private static bool ShouldUseLastServer(string message)
    {
        var lowered = message.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lowered))
            return false;

        if (lowered.Contains(" it", StringComparison.Ordinal) ||
            lowered.StartsWith("it ", StringComparison.Ordinal) ||
            lowered.Contains(" that", StringComparison.Ordinal) ||
            lowered.Contains("same", StringComparison.Ordinal))
        {
            return true;
        }

        var words = lowered.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 3;
    }

    private static string BuildClarificationQuestion(string? intent, List<ServerSnapshot> servers)
    {
        var knownServers = servers.Count == 0
            ? "I don't currently have any configured servers."
            : $"Known servers: {string.Join(", ", servers.Select(server => server.Name))}.";

        return intent switch
        {
            "restart-server" => $"Which server should I restart? {knownServers}",
            "start-server" => $"Which server should I start? {knownServers}",
            "stop-server" => $"Which server should I stop? {knownServers}",
            "server-health" => $"Which server do you want health details for? {knownServers}",
            "validate-oxide" => $"Which server should I validate Oxide for? {knownServers}",
            "list-server-plugins" => $"Which server should I list plugins for? {knownServers}",
            "check-plugin-updates" => $"Which server should I check plugin updates for? {knownServers}",
            _ => $"Which server do you mean? {knownServers}"
        };
    }

    private static bool IsKnownChatIntent(string? intent) =>
        intent is "help" or "ping" or "list-servers" or "server-status" or "server-health" or
            "pending-actions" or "recent-actions" or "recent-incidents" or
            "validate-oxide" or "inspect-host-network" or "list-server-plugins" or "check-plugin-updates" or
            "start-server" or "stop-server" or "restart-server" or "unknown";

    private static ServerSnapshot? ResolveServer(string? requestedServer, List<ServerSnapshot> servers)
    {
        if (string.IsNullOrWhiteSpace(requestedServer))
            return null;

        return servers.FirstOrDefault(server =>
            string.Equals(server.Name, requestedServer, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveServerName(string? text, List<ServerSnapshot> servers)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var direct = servers.FirstOrDefault(server =>
            string.Equals(server.Name, text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
            return direct.Name;

        var lowered = text.ToLowerInvariant();
        return servers
            .Select(server => server.Name)
            .FirstOrDefault(name => lowered.Contains(name.ToLowerInvariant(), StringComparison.Ordinal));
    }

    private static string BuildServerNotFoundReply(List<ServerSnapshot> servers)
    {
        if (servers.Count == 0)
            return "I couldn't find any configured servers to target.";

        return $"I couldn't match that to a known server. Known servers: {string.Join(", ", servers.Select(server => server.Name))}.";
    }

    private static bool IsReplyWorkflowQuestion(string message)
    {
        var lowered = message.Trim().ToLowerInvariant();
        return lowered.Contains("how did you get to this reply", StringComparison.Ordinal) ||
               lowered.Contains("how did you get this reply", StringComparison.Ordinal) ||
               lowered.Contains("did you look yourself", StringComparison.Ordinal) ||
               lowered.Contains("used console", StringComparison.Ordinal) ||
               lowered.Contains("what did you use", StringComparison.Ordinal) ||
               lowered.Contains("how did you decide", StringComparison.Ordinal);
    }

    private string? TryExplainLastReplyWorkflow(AdminConversationState conversation, string message)
    {
        if (!IsReplyWorkflowQuestion(message))
            return null;

        var trace = conversation.LastTrace;
        if (trace is null)
            return "I don't have a stored trace for the previous reply yet. I can store this from now on.";

        var tools = trace.UsedTools is { Count: > 0 }
            ? string.Join(", ", trace.UsedTools)
            : "none";
        var note = string.IsNullOrWhiteSpace(trace.Note) ? "No extra note." : trace.Note;
        var previousAssistantReply = conversation.History
            .Where(entry => string.Equals(entry.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.AtUtc)
            .FirstOrDefault()?.Text;
        var replyPreview = string.IsNullOrWhiteSpace(previousAssistantReply)
            ? "No previous assistant reply snapshot."
            : $"Previous reply preview: {TrimSingleLine(previousAssistantReply, 180)}";
        return $"Previous reply trace: source={trace.Source}, intent={trace.Intent}, tools={tools}, at={trace.AtUtc:O}. {note} {replyPreview}";
    }

    private static void SetConversationTrace(
        AdminConversationState conversation,
        DateTime atUtc,
        string source,
        string intent,
        IReadOnlyCollection<string>? usedTools,
        string? note)
    {
        var trace = new ChatTurnTrace
        {
            AtUtc = atUtc,
            Source = source,
            Intent = intent,
            UsedTools = usedTools?.Where(tool => !string.IsNullOrWhiteSpace(tool)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>(),
            Note = note
        };

        conversation.LastTrace = trace;
        conversation.TraceHistory.Add(trace);
        conversation.TraceHistory = conversation.TraceHistory
            .OrderByDescending(entry => entry.AtUtc)
            .Take(24)
            .ToList();
    }

    private void RecordLearningIncident(string adminId, string message, string intent, string reply, ChatTurnTrace? trace, string category = "unknown")
    {
        try
        {
            Directory.CreateDirectory(_learningInboxPath);
            var incident = new LearningIncidentRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                AtUtc = DateTime.UtcNow,
                AdminId = adminId,
                Message = message,
                Intent = intent,
                Category = string.IsNullOrWhiteSpace(category) ? "unknown" : category.Trim(),
                Reply = TrimSingleLine(reply, 400),
                TraceSource = trace?.Source,
                TraceIntent = trace?.Intent,
                TraceTools = trace?.UsedTools?.ToList() ?? new List<string>(),
                TraceNote = trace?.Note
            };

            var fileName = $"{incident.AtUtc:yyyyMMddHHmmssfff}-learning-{incident.Id}.json";
            var path = Path.Combine(_learningInboxPath, fileName);
            File.WriteAllText(path, JsonSerializer.Serialize(incident, JsonOptions.Default));
            _memory.RecordCapabilityGap("chat-learning", $"Unmapped chat message persisted for learning: {TrimSingleLine(message, 180)}");
        }
        catch (Exception ex)
        {
            _memory.RecordAgentError($"learning-incident write failed: {ex.Message}");
        }
    }

    private static string TrimSingleLine(string input, int maxLength)
    {
        var singleLine = input.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maxLength
            ? singleLine
            : $"{singleLine[..(maxLength - 3)]}...";
    }

    private static void RememberChatTurn(AdminConversationState conversation, string role, string text, DateTime utcNow)
    {
        conversation.History.Add(new ChatHistoryEntry
        {
            Role = role,
            Text = text,
            AtUtc = utcNow
        });

        conversation.History = conversation.History.TakeLast(12).ToList();
    }

    private static void UpdateConversationState(AdminConversationState conversation, ChatInterpretation plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.ServerName))
            conversation.LastServerName = plan.ServerName;
    }

    private void ApplyFeedbackLearning(FeedbackEntry feedback, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(feedback.ServerName) || string.IsNullOrWhiteSpace(feedback.ActionId))
            return;

        var serverMemory = _memory.GetOrCreateServer(feedback.ServerName);
        var proposal = _memory.PendingActions.FirstOrDefault(a => string.Equals(a.Id, feedback.ActionId, StringComparison.OrdinalIgnoreCase));
        var actionType = proposal?.ActionType
            ?? _memory.ActionHistory.LastOrDefault(a => string.Equals(a.ActionId, feedback.ActionId, StringComparison.OrdinalIgnoreCase))?.ActionType;

        if (string.IsNullOrWhiteSpace(actionType))
            return;

        var verdict = feedback.Verdict.ToLowerInvariant();
        if (verdict is "bad" or "wrong" or "reject")
        {
            serverMemory.LearnedActionRules.Add(new LearnedActionRule
            {
                ActionType = actionType,
                Guidance = "avoid-auto",
                Note = feedback.Note ?? "Negative feedback received.",
                AdminId = feedback.AdminId,
                LearnedAtUtc = utcNow
            });
        }
        else if (verdict is "good" or "correct" or "approve")
        {
            serverMemory.LearnedActionRules.Add(new LearnedActionRule
            {
                ActionType = actionType,
                Guidance = "prefer",
                Note = feedback.Note ?? "Positive feedback received.",
                AdminId = feedback.AdminId,
                LearnedAtUtc = utcNow
            });
        }

        serverMemory.LearnedActionRules = serverMemory.LearnedActionRules
            .TakeLast(50)
            .ToList();
    }

    private async Task QueueOrExecuteActionAsync(ServerMemory serverMemory, ServerSnapshot server, IncidentMemory incident, DateTime utcNow)
    {
        var proposal = BuildActionProposal(serverMemory, server, incident, utcNow);
        if (proposal is null)
            return;

        if (_memory.PendingActions.Any(a =>
                a.Status is ActionStatus.Pending or ActionStatus.Approved &&
                string.Equals(a.ServerName, proposal.ServerName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.ActionType, proposal.ActionType, StringComparison.OrdinalIgnoreCase)))
            return;

        if (!proposal.RequiresApproval)
        {
            var outcome = await ExecuteActionAsync(proposal, utcNow, trigger: "auto");
            proposal.Status = outcome.Success ? ActionStatus.AutoExecuted : ActionStatus.Failed;
            proposal.LastUpdatedAtUtc = utcNow;
            proposal.DecisionNote = outcome.Summary;
            _memory.PendingActions.Add(proposal);
            _memory.ActionHistory.Add(outcome);
            RememberActionOutcome(outcome);
            RecordActionMetric(proposal.ActionType, outcome.Success, "auto");
            if (ShouldNotifyActionOutcome(proposal, outcome))
            {
                WriteOutboxMessage(new AdapterMessage
                {
                    CreatedAtUtc = utcNow,
                    Kind = "action-outcome",
                    ServerName = proposal.ServerName,
                    ActionId = proposal.Id,
                    Audience = "admins",
                    Message = BuildOutcomeMessage(proposal, outcome)
                });
            }
            TrimActionHistory();
            return;
        }

        proposal.Status = ActionStatus.Pending;
        _memory.PendingActions.Add(proposal);
        WriteOutboxMessage(new AdapterMessage
        {
            CreatedAtUtc = utcNow,
            Kind = "action-proposal",
            ServerName = proposal.ServerName,
            ActionId = proposal.Id,
            Audience = "admins",
            Message = BuildProposalMessage(proposal)
        });
    }

    private ActionProposal? BuildActionProposal(ServerMemory serverMemory, ServerSnapshot server, IncidentMemory incident, DateTime utcNow)
    {
        string? actionType = NormalizeRecommendedAction(incident.Recommendation?.SuggestedAction)
            ?? incident.Category switch
        {
            "plugin-compile" => "validate-oxide",
            "plugin" => "validate-oxide",
            "network" => "inspect-host-network",
            "state-change" when string.Equals(server.State, "offline", StringComparison.OrdinalIgnoreCase) => "restart-server",
            "runtime-error" => "restart-server",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(actionType))
            return null;

        if (!IsKnownAction(actionType))
            return null;

        if (HasAvoidAutoRule(serverMemory, actionType))
        {
            return new ActionProposal
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = utcNow,
                LastUpdatedAtUtc = utcNow,
                ServerName = server.Name,
                ActionType = actionType,
                Reason = $"Learned rule prevents automatic '{actionType}'.",
                Summary = incident.Recommendation?.AdminMessage ?? incident.Title,
                RequiresApproval = true,
                Evidence = incident.Evidence.ToList(),
                Confidence = incident.Recommendation?.Confidence,
                AdminMessage = incident.Recommendation?.AdminMessage,
                Status = ActionStatus.Pending
            };
        }

        var requiresApproval = _config.Policy.RequiresApproval(actionType) || !_config.Policy.IsAutoAllowed(actionType);

        return new ActionProposal
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = utcNow,
            LastUpdatedAtUtc = utcNow,
            ServerName = server.Name,
            ActionType = actionType,
            Reason = incident.Summary,
            Summary = incident.Recommendation?.AdminMessage ?? incident.Title,
            RequiresApproval = requiresApproval,
            Evidence = incident.Evidence.ToList(),
            Confidence = incident.Recommendation?.Confidence,
            AdminMessage = incident.Recommendation?.AdminMessage,
            Status = requiresApproval ? ActionStatus.Pending : ActionStatus.AutoExecuted
        };
    }

    private static string? NormalizeRecommendedAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return null;

        var value = action.Trim().ToLowerInvariant();
        return value switch
        {
            "start" or "start-server" => "start-server",
            "stop" or "stop-server" => "stop-server",
            "restart" or "restart-server" => "restart-server",
            "validate-oxide" or "validate_plugin" or "validate-plugin" or "validate" => "validate-oxide",
            "inspect-network" or "inspect_host_network" or "inspect-host-network" => "inspect-host-network",
            "none" or "notify-only" or "notify" => null,
            _ => value
        };
    }

    private static bool IsKnownAction(string actionType) =>
        actionType is "start-server" or "stop-server" or "restart-server" or "validate-oxide" or "inspect-host-network";

    private bool HasAvoidAutoRule(ServerMemory serverMemory, string actionType)
    {
        return serverMemory.LearnedActionRules.Any(r =>
            string.Equals(r.ActionType, actionType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Guidance, "avoid-auto", StringComparison.OrdinalIgnoreCase));
    }

    private async Task ExecuteApprovedActionsAsync(DateTime utcNow)
    {
        var ready = _memory.PendingActions
            .Where(a => a.Status == ActionStatus.Approved)
            .ToList();

        foreach (var proposal in ready)
        {
            var outcome = await ExecuteActionAsync(proposal, utcNow, trigger: "approved");
            proposal.Status = outcome.Success ? ActionStatus.Executed : ActionStatus.Failed;
            proposal.LastUpdatedAtUtc = utcNow;
            proposal.DecisionNote = outcome.Summary;
            _memory.ActionHistory.Add(outcome);
            RememberActionOutcome(outcome);
            RecordActionMetric(proposal.ActionType, outcome.Success, "approved");
            WriteOutboxMessage(new AdapterMessage
            {
                CreatedAtUtc = utcNow,
                Kind = "action-outcome",
                ServerName = proposal.ServerName,
                ActionId = proposal.Id,
                Audience = "admins",
                Message = BuildOutcomeMessage(proposal, outcome)
            });
        }

        TrimActionHistory();
    }

    private async Task<ActionExecutionRecord> ExecuteActionAsync(ActionProposal proposal, DateTime utcNow, string trigger)
    {
        RustOpsSentry.AddBreadcrumb($"Executing action '{proposal.ActionType}' for '{proposal.ServerName}'.", "agent.action");
        try
        {
            return proposal.ActionType switch
            {
                "start-server" => await ExecuteServerLifecycleActionAsync(proposal, utcNow, trigger, "start", "Start requested."),
                "stop-server" => await ExecuteServerLifecycleActionAsync(proposal, utcNow, trigger, "stop", "Stop requested."),
                "validate-oxide" => await ExecuteValidateOxideAsync(proposal, utcNow, trigger),
                "inspect-host-network" => await ExecuteInspectNetworkAsync(proposal, utcNow, trigger),
                "restart-server" => await ExecuteServerLifecycleActionAsync(proposal, utcNow, trigger, "restart", "Restart requested."),
                _ => BuildMissingExecutorOutcome(proposal, utcNow, trigger)
            };
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
            return new ActionExecutionRecord
            {
                ActionId = proposal.Id,
                ActionType = proposal.ActionType,
                ServerName = proposal.ServerName,
                ExecutedAtUtc = utcNow,
                Success = false,
                Trigger = trigger,
                Summary = ex.Message
            };
        }
    }

    private ActionExecutionRecord BuildMissingExecutorOutcome(ActionProposal proposal, DateTime utcNow, string trigger)
    {
        _memory.RecordCapabilityGap("action-executor", $"No executor implemented for action '{proposal.ActionType}'.");
        return new ActionExecutionRecord
        {
            ActionId = proposal.Id,
            ActionType = proposal.ActionType,
            ServerName = proposal.ServerName,
            ExecutedAtUtc = utcNow,
            Success = false,
            Trigger = trigger,
            Summary = $"No executor implemented for '{proposal.ActionType}'."
        };
    }

    private string BuildProposalMessage(ActionProposal proposal)
    {
        var parts = new List<string>
        {
            $"Agent detected an issue on {proposal.ServerName}.",
            $"Suggested action: {proposal.ActionType}."
        };

        if (!string.IsNullOrWhiteSpace(proposal.AdminMessage))
            parts.Add(proposal.AdminMessage!);

        if (proposal.Confidence.HasValue)
            parts.Add($"Confidence: {proposal.Confidence.Value:0.00}.");

        if (proposal.RequiresApproval)
            parts.Add($"Approval required. Action id: {proposal.Id}.");

        return string.Join(' ', parts);
    }

    private static string BuildOutcomeMessage(ActionProposal proposal, ActionExecutionRecord outcome)
    {
        var status = outcome.Success ? "completed" : "failed";
        return $"Agent {status} action '{proposal.ActionType}' on {proposal.ServerName}. {outcome.Summary}";
    }

    private bool ShouldNotifyActionOutcome(ActionProposal proposal, ActionExecutionRecord outcome)
    {
        if (!outcome.Success)
            return true;

        return !IsSilentAction(proposal.ActionType);
    }

    private static bool IsSilentAction(string? actionType) =>
        actionType is "validate-oxide" or "inspect-host-network";

    private void RecordActionMetric(string actionType, bool success, string source)
    {
        var metric = _memory.ActionMetrics.FirstOrDefault(m =>
            string.Equals(m.ActionType, actionType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.Source, source, StringComparison.OrdinalIgnoreCase));

        if (metric is null)
        {
            metric = new ActionMetric
            {
                ActionType = actionType,
                Source = source
            };
            _memory.ActionMetrics.Add(metric);
        }

        metric.Count++;
        metric.LastAttemptAtUtc = DateTime.UtcNow;
        if (success)
        {
            metric.SuccessCount++;
            metric.LastSuccessAtUtc = metric.LastAttemptAtUtc;
        }
        else
        {
            metric.FailureCount++;
            metric.LastFailureAtUtc = metric.LastAttemptAtUtc;
        }

        _memory.ActionMetrics = _memory.ActionMetrics
            .OrderBy(m => m.ActionType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Source, StringComparer.OrdinalIgnoreCase)
            .TakeLast(100)
            .ToList();
    }

    private void RememberActionOutcome(ActionExecutionRecord outcome)
    {
        var serverMemory = _memory.GetOrCreateServer(outcome.ServerName);
        serverMemory.ActionOutcomes.Add($"{outcome.ExecutedAtUtc:O} {outcome.ActionType} {(outcome.Success ? "success" : "failed")}: {TrimSingleLine(outcome.Summary, 160)}");
        serverMemory.ActionOutcomes = serverMemory.ActionOutcomes.TakeLast(20).ToList();
    }

    private void WriteOutboxMessage(AdapterMessage message)
    {
        var fileName = $"{message.CreatedAtUtc:yyyyMMddHHmmssfff}-{message.Kind}-{Guid.NewGuid():N}.json";
        var path = Path.Combine(_config.Outbox.MessageOutboxPath, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(message, JsonOptions.Default));
    }

    private async Task<ActionExecutionRecord> ExecuteValidateOxideAsync(ActionProposal proposal, DateTime utcNow, string trigger)
    {
        using var json = await _api.GetJsonAsync($"/servers/{Uri.EscapeDataString(proposal.ServerName)}/oxide/validate");
        var ok = json.RootElement.GetProperty("ok").GetBoolean();
        var pluginCount = json.RootElement.TryGetProperty("pluginCount", out var pluginCountNode) && pluginCountNode.ValueKind == JsonValueKind.Number
            ? pluginCountNode.GetInt32()
            : json.RootElement.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array
                ? plugins.GetArrayLength()
                : 0;
        var configCount = json.RootElement.TryGetProperty("jsonConfigCount", out var configCountNode) && configCountNode.ValueKind == JsonValueKind.Number
            ? configCountNode.GetInt32()
            : json.RootElement.TryGetProperty("jsonConfigs", out var jsonConfigs) && jsonConfigs.ValueKind == JsonValueKind.Array
                ? jsonConfigs.GetArrayLength()
                : 0;
        var pluginIssues = json.RootElement.TryGetProperty("plugins", out var pluginResults) && pluginResults.ValueKind == JsonValueKind.Array
            ? pluginResults.EnumerateArray().Count(entry => entry.TryGetProperty("ok", out var okNode) && okNode.ValueKind == JsonValueKind.False)
            : 0;
        var configIssues = json.RootElement.TryGetProperty("jsonConfigs", out var configResults) && configResults.ValueKind == JsonValueKind.Array
            ? configResults.EnumerateArray().Count(entry => entry.TryGetProperty("ok", out var okNode) && okNode.ValueKind == JsonValueKind.False)
            : 0;
        var searchedRoots = json.RootElement.TryGetProperty("searchedPaths", out var searchedPaths) &&
                            searchedPaths.TryGetProperty("oxideRoots", out var oxideRoots) &&
                            oxideRoots.ValueKind == JsonValueKind.Array
            ? oxideRoots.EnumerateArray()
                .Select(node => node.GetString())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .Take(3)
                .ToList()
            : new List<string>();

        var summary = ok
            ? $"Oxide validation passed ({pluginCount} plugins, {configCount} json configs)."
            : $"Oxide validation found issues ({pluginIssues} plugin issues, {configIssues} config issues).";

        if (pluginCount == 0 && configCount == 0 && searchedRoots.Count > 0)
            summary = $"Oxide validation found no plugin/config files. Searched: {string.Join(" | ", searchedRoots)}";
        else if (searchedRoots.Count > 0)
            summary = $"{summary} Searched: {string.Join(" | ", searchedRoots)}";

        return new ActionExecutionRecord
        {
            ActionId = proposal.Id,
            ActionType = proposal.ActionType,
            ServerName = proposal.ServerName,
            ExecutedAtUtc = utcNow,
            Success = true,
            Trigger = trigger,
            Summary = summary,
            OutputSnippet = json.RootElement.ToString()
        };
    }

    private async Task<ActionExecutionRecord> ExecuteInspectNetworkAsync(ActionProposal proposal, DateTime utcNow, string trigger)
    {
        using var json = await _api.GetJsonAsync("/host/network/summary");
        var summary = "Host network counters inspected.";
        var sampleSeconds = json.RootElement.TryGetProperty("sampleSeconds", out var sampleSecondsNode) && sampleSecondsNode.ValueKind == JsonValueKind.Number
            ? sampleSecondsNode.GetDouble()
            : (double?)null;

        if (json.RootElement.TryGetProperty("topThroughputInterfaces", out var topInterfaces) &&
            topInterfaces.ValueKind == JsonValueKind.Array &&
            topInterfaces.GetArrayLength() > 0)
        {
            var details = topInterfaces.EnumerateArray()
                .Take(3)
                .Select(entry =>
                {
                    var name = entry.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : "iface";
                    var combined = entry.TryGetProperty("combinedRateMbps", out var combinedNode) && combinedNode.ValueKind == JsonValueKind.Number
                        ? $"{combinedNode.GetDouble():0.##} Mbps"
                        : "rate n/a";
                    var rx = entry.TryGetProperty("rxRateMiBps", out var rxNode) && rxNode.ValueKind == JsonValueKind.Number
                        ? $"{rxNode.GetDouble():0.###} MiB/s rx"
                        : null;
                    var tx = entry.TryGetProperty("txRateMiBps", out var txNode) && txNode.ValueKind == JsonValueKind.Number
                        ? $"{txNode.GetDouble():0.###} MiB/s tx"
                        : null;
                    var spike = entry.TryGetProperty("spikeDetected", out var spikeNode) && spikeNode.ValueKind == JsonValueKind.True
                        ? "spike"
                        : null;
                    var extras = new[] { rx, tx, spike }.Where(value => !string.IsNullOrWhiteSpace(value));
                    return $"{name}: {combined}{(extras.Any() ? $" ({string.Join(", ", extras)})" : string.Empty)}";
                })
                .ToList();

            summary = sampleSeconds.HasValue
                ? $"Host throughput over the last {sampleSeconds.Value:0.##}s: {string.Join(" | ", details)}"
                : $"Host throughput counters are primed. Waiting for the next sample to calculate rates. Top interfaces: {string.Join(" | ", details)}";
        }

        if (json.RootElement.TryGetProperty("interestingInterfaces", out var interesting) &&
            interesting.ValueKind == JsonValueKind.Array &&
            interesting.GetArrayLength() > 0)
        {
            var details = interesting.EnumerateArray()
                .Take(3)
                .Select(entry =>
                {
                    var name = entry.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : "iface";
                    var combined = entry.TryGetProperty("combinedRateMbps", out var combinedNode) && combinedNode.ValueKind == JsonValueKind.Number
                        ? $"{combinedNode.GetDouble():0.##} Mbps"
                        : null;
                    var rxErrors = entry.TryGetProperty("rxErrors", out var rxErrorsNode) ? rxErrorsNode.GetInt64() : 0;
                    var txErrors = entry.TryGetProperty("txErrors", out var txErrorsNode) ? txErrorsNode.GetInt64() : 0;
                    var rxDrops = entry.TryGetProperty("rxDropped", out var rxDropsNode) ? rxDropsNode.GetInt64() : 0;
                    var txDrops = entry.TryGetProperty("txDropped", out var txDropsNode) ? txDropsNode.GetInt64() : 0;
                    var spike = entry.TryGetProperty("spikeDetected", out var spikeNode) && spikeNode.ValueKind == JsonValueKind.True ? ", spike" : string.Empty;
                    return $"{name}: {(combined is null ? string.Empty : $"{combined}, ")}rxErr={rxErrors}, txErr={txErrors}, rxDrop={rxDrops}, txDrop={txDrops}{spike}";
                });
            summary = sampleSeconds.HasValue
                ? $"Host network activity over the last {sampleSeconds.Value:0.##}s on {interesting.GetArrayLength()} interface(s). {string.Join(" | ", details)}"
                : $"Host network activity detected on {interesting.GetArrayLength()} interface(s). {string.Join(" | ", details)}";
        }
        else if (json.RootElement.TryGetProperty("interfaces", out var interfacesNode) &&
                 interfacesNode.ValueKind == JsonValueKind.Array)
        {
            summary = sampleSeconds.HasValue
                ? $"Host network counters inspected across {interfacesNode.GetArrayLength()} interface(s) over {sampleSeconds.Value:0.##}s; no spikes or error-heavy interfaces detected."
                : $"Host network counters inspected across {interfacesNode.GetArrayLength()} interface(s); waiting for a follow-up sample to calculate throughput.";
        }

        return new ActionExecutionRecord
        {
            ActionId = proposal.Id,
            ActionType = proposal.ActionType,
            ServerName = proposal.ServerName,
            ExecutedAtUtc = utcNow,
            Success = true,
            Trigger = trigger,
            Summary = summary,
            OutputSnippet = json.RootElement.ToString()
        };
    }

    private async Task<ActionExecutionRecord> ExecuteServerLifecycleActionAsync(
        ActionProposal proposal,
        DateTime utcNow,
        string trigger,
        string operation,
        string fallbackMessage)
    {
        var result = await _executor.ExecuteLifecycleAsync(proposal.ServerName, operation);
        var message = BuildLifecycleMessage(result, fallbackMessage);

        var verification = await VerifyExpectedServerStateAsync(proposal.ServerName, operation);
        var success = DetermineLifecycleSuccess(result, operation, verification);
        var summary = BuildLifecycleSummary(message, result, operation, verification, success);

        return new ActionExecutionRecord
        {
            ActionId = proposal.Id,
            ActionType = proposal.ActionType,
            ServerName = proposal.ServerName,
            ExecutedAtUtc = utcNow,
            Success = success,
            Trigger = trigger,
            Summary = summary
        };
    }

    private async Task<LifecycleVerificationResult?> VerifyExpectedServerStateAsync(string serverName, string operation)
    {
        return await _executor.VerifyExpectedServerStateAsync(serverName, operation);
    }

    private static string BuildLifecycleMessage(CommandExecutionResult result, string fallbackMessage)
    {
        if (!string.IsNullOrWhiteSpace(result.Message))
            return result.Message!;

        if (!string.IsNullOrWhiteSpace(result.StdOut))
            return result.StdOut!;

        if (!string.IsNullOrWhiteSpace(result.StdErr))
            return result.StdErr!;

        return fallbackMessage;
    }

    private static bool DetermineLifecycleSuccess(
        CommandExecutionResult result,
        string operation,
        LifecycleVerificationResult? verification)
    {
        if (verification?.ReachedExpectedState == true)
            return true;

        if (verification is null)
            return result.Ok;

        if (!result.Ok)
            return false;

        return operation switch
        {
            "start" or "restart" when verification.ProgressObserved => true,
            "start" or "restart" => false,
            _ => result.Ok
        };
    }

    private static string BuildLifecycleSummary(
        string message,
        CommandExecutionResult result,
        string operation,
        LifecycleVerificationResult? verification,
        bool success)
    {
        if (verification?.ReachedExpectedState == true)
            return !result.Ok ? $"{message} Later verification observed the expected server state." : message;

        if (success && operation is "start" or "restart")
        {
            var observed = DescribeObservedStatus(verification?.LastStatus);
            if (verification?.ProcessObserved == true && verification?.ReachedExpectedState != true)
                return $"{message} RustDedicated was observed during startup, but the final running state has not been confirmed yet. Latest observed status: {observed}.";

            return verification?.ProgressObserved == true
                ? $"{message} Startup is still settling. Latest observed status: {observed}."
                : $"{message} Immediate verification did not confirm the final running state yet.";
        }

        if (success)
            return message;

        if (verification?.ProcessObserved == true)
        {
            var observed = DescribeObservedStatus(verification.LastStatus);
            return $"{message} RustDedicated was observed, but it did not settle into the expected server state. Latest observed status: {observed}.";
        }

        var suffix = verification?.LastStatus is not null
            ? $" Latest observed status: {DescribeObservedStatus(verification.LastStatus)}."
            : string.Empty;

        return $"{message} Verification did not observe the expected server state.{suffix}";
    }

    private static string DescribeObservedStatus(RustMgrStatusSnapshot? status)
    {
        if (status is null)
            return "status unavailable";

        return $"state={status.State}, session={(status.Session ? "yes" : "no")}, autorestart={(status.AutoRestart ? "yes" : "no")}, pid={(status.Pid?.ToString() ?? "-")}";
    }

    private void TrimActionHistory()
    {
        _memory.ActionHistory = _memory.ActionHistory.TakeLast(200).ToList();
        _memory.PendingActions = _memory.PendingActions.TakeLast(200).ToList();
    }

    private static string BuildCommandOutputSummary(string stdout, string stderr)
    {
        var stdoutLines = (stdout ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(3)
            .ToList();
        var stderrLines = (stderr ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2)
            .ToList();

        var parts = new List<string>();
        if (stdoutLines.Count > 0)
            parts.Add($"stdout: {string.Join(" | ", stdoutLines.Select(line => TrimSingleLine(line, 180)))}");
        if (stderrLines.Count > 0)
            parts.Add($"stderr: {string.Join(" | ", stderrLines.Select(line => TrimSingleLine(line, 180)))}");
        if (parts.Count == 0)
            return "command executed with no output";
        return string.Join(" || ", parts);
    }

    private static List<string> ExtractTraceEventMessages(JsonElement root, int takeLast)
    {
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return events.EnumerateArray()
            .Select(entry => entry.TryGetProperty("message", out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => message!.Trim())
            .TakeLast(takeLast)
            .ToList();
    }

    private void RecordCommandKnowledge(
        string serverName,
        string commandText,
        string summary,
        bool success,
        string adminId,
        DateTime utcNow,
        string? purpose = null,
        string? usefulWhen = null)
    {
        var commandKey = NormalizeCommandKey(commandText);
        if (string.IsNullOrWhiteSpace(commandKey))
            return;

        var memory = _memory.GetOrCreateServer(serverName);
        memory.KnownConsoleCommands ??= new List<KnownConsoleCommand>();
        memory.CommandInteractions ??= new List<ConsoleCommandInteraction>();
        var known = memory.KnownConsoleCommands
            .FirstOrDefault(entry => string.Equals(entry.Command, commandKey, StringComparison.OrdinalIgnoreCase));

        if (known is null)
        {
            known = new KnownConsoleCommand
            {
                Command = commandKey
            };
            memory.KnownConsoleCommands.Add(known);
        }

        known.Observations++;
        known.LastObservedAtUtc = utcNow;
        if (!string.IsNullOrWhiteSpace(purpose))
            known.Purpose = TrimSingleLine(purpose, 220);
        if (!string.IsNullOrWhiteSpace(usefulWhen))
            known.UsefulWhen = TrimSingleLine(usefulWhen, 220);
        if (!string.IsNullOrWhiteSpace(summary))
            known.LastOutputSummary = TrimSingleLine(summary, 220);

        memory.KnownConsoleCommands = memory.KnownConsoleCommands
            .OrderByDescending(entry => entry.LastObservedAtUtc)
            .Take(120)
            .ToList();

        memory.CommandInteractions.Add(new ConsoleCommandInteraction
        {
            AtUtc = utcNow,
            CommandKey = commandKey,
            RawCommand = commandText.Trim(),
            Success = success,
            Summary = TrimSingleLine(summary, 260),
            AdminId = adminId
        });
        memory.CommandInteractions = memory.CommandInteractions
            .OrderByDescending(entry => entry.AtUtc)
            .Take(200)
            .ToList();
    }

    private static string NormalizeCommandKey(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            return string.Empty;

        var command = commandText.Trim();
        var firstToken = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? command;
        return firstToken.Trim().ToLowerInvariant();
    }

    private bool IsCommandAllowed(string command, out string? error)
    {
        if (!_config.CommandExecution.Enabled)
        {
            error = "Server command execution is disabled by config.";
            return false;
        }

        if (_config.CommandExecution.FreeMode)
        {
            error = null;
            return true;
        }

        if (_config.CommandExecution.AllowList.Any(allowed =>
                string.Equals(allowed, NormalizeCommandKey(command), StringComparison.OrdinalIgnoreCase)))
        {
            error = null;
            return true;
        }

        error = $"Command '{NormalizeCommandKey(command)}' is blocked by command allowlist.";
        return false;
    }

    private static string? NormalizeCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var normalized = command.Trim();
        if (normalized.Length > 256 || normalized.Contains('\n') || normalized.Contains('\r'))
            return null;

        return normalized;
    }

    private static List<string> ExtractCommandOutputMessages(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var outputNode) || outputNode.ValueKind != JsonValueKind.Object)
            return new List<string>();
        if (!outputNode.TryGetProperty("messages", out var messagesNode) || messagesNode.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return messagesNode.EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .TakeLast(240)
            .ToList();
    }

    private async Task TryCheckPluginUpdatesAsync(string serverName, ServerMemory serverMemory, DateTime utcNow)
    {
        if (!_config.PluginUpdates.Enabled)
            return;

        var intervalMinutes = Math.Max(5, _config.PluginUpdates.CheckIntervalMinutes);
        serverMemory.KnownPlugins ??= new List<KnownPluginRecord>();
        if (serverMemory.LastPluginUpdateCheckAtUtc.HasValue &&
            utcNow - serverMemory.LastPluginUpdateCheckAtUtc.Value < TimeSpan.FromMinutes(intervalMinutes))
        {
            return;
        }

        serverMemory.LastPluginUpdateCheckAtUtc = utcNow;
        List<PluginUpdateCandidate> updates;
        try
        {
            updates = await GetPluginUpdatesAsync(serverName);
        }
        catch (Exception ex)
        {
            _memory.RecordAgentError($"plugin update check failed for {serverName}: {ex.Message}");
            SentrySdk.CaptureException(ex);
            return;
        }
        var signature = BuildPluginUpdateSignature(updates);
        if (updates.Count == 0 || string.Equals(signature, serverMemory.LastPluginUpdateSignature, StringComparison.Ordinal))
            return;

        serverMemory.LastPluginUpdateSignature = signature;
        if (!_config.PluginUpdates.NotifyAdmins)
            return;

        WriteOutboxMessage(new AdapterMessage
        {
            CreatedAtUtc = utcNow,
            Kind = "plugin-update",
            ServerName = serverName,
            Audience = "admins",
            Message = BuildPluginUpdateAdminMessage(serverName, updates)
        });
    }

    private static string BuildPluginUpdateSignature(IReadOnlyCollection<PluginUpdateCandidate> updates) =>
        updates.Count == 0
            ? string.Empty
            : string.Join('|', updates
                .OrderBy(update => update.Name, StringComparer.OrdinalIgnoreCase)
                .Select(update => $"{update.Name}:{update.LocalVersion}->{update.RemoteVersion}"));

    private static string BuildPluginUpdateAdminMessage(string serverName, IReadOnlyCollection<PluginUpdateCandidate> updates)
    {
        var lines = updates
            .Take(8)
            .Select(update => $"{update.Name} {update.LocalVersion} -> {update.RemoteVersion}")
            .ToList();
        return lines.Count == 0
            ? $"Plugin update scan finished for {serverName}. No updates found."
            : $"Plugin updates found for {serverName}:\n- {string.Join("\n- ", lines)}\nReply with your preferred action.";
    }

    private async Task<List<PluginUpdateCandidate>> GetPluginUpdatesAsync(string serverName)
    {
        var plugins = await ReadServerPluginsAsync(serverName);
        if (plugins.Count == 0)
            return new List<PluginUpdateCandidate>();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var updates = new List<PluginUpdateCandidate>();
        foreach (var plugin in plugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.Name) || string.IsNullOrWhiteSpace(plugin.Version))
                continue;

            var query = Uri.EscapeDataString(plugin.Name);
            var filterValue = Uri.EscapeDataString(_config.PluginUpdates.SearchFilter ?? string.Empty);
            var url = string.Format(_config.PluginUpdates.SearchUrlTemplate, query, filterValue);
            string jsonText;
            try
            {
                jsonText = await client.GetStringAsync(url);
            }
            catch
            {
                continue;
            }

            JsonDocument? json = null;
            try
            {
                json = JsonDocument.Parse(jsonText);
            }
            catch
            {
            }

            if (json is null ||
                !json.RootElement.TryGetProperty("data", out var dataNode) ||
                dataNode.ValueKind != JsonValueKind.Array)
            {
                json?.Dispose();
                continue;
            }

            var match = dataNode.EnumerateArray()
                .Select(item => new
                {
                    Title = item.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String ? titleNode.GetString() : null,
                    Name = item.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String ? nameNode.GetString() : null,
                    RemoteVersion = item.TryGetProperty("latest_release_version", out var versionNode) && versionNode.ValueKind == JsonValueKind.String ? versionNode.GetString() : null,
                    Url = item.TryGetProperty("url", out var urlNode) && urlNode.ValueKind == JsonValueKind.String ? urlNode.GetString() : null,
                    DownloadUrl = item.TryGetProperty("download_url", out var downloadNode) && downloadNode.ValueKind == JsonValueKind.String ? downloadNode.GetString() : null
                })
                .FirstOrDefault(item =>
                    string.Equals(item.Title, plugin.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Name, plugin.Name.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase));

            json.Dispose();
            if (match is null || string.IsNullOrWhiteSpace(match.RemoteVersion))
                continue;

            if (CompareSemanticVersion(match.RemoteVersion!, plugin.Version) <= 0)
                continue;

            updates.Add(new PluginUpdateCandidate
            {
                Name = plugin.Name,
                Author = plugin.Author,
                LocalVersion = plugin.Version,
                RemoteVersion = match.RemoteVersion!,
                SourceUrl = match.Url ?? string.Empty,
                DownloadUrl = match.DownloadUrl ?? string.Empty
            });
        }

        return updates
            .OrderBy(update => update.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<ServerPluginInfo>> ReadServerPluginsAsync(string serverName)
    {
        using var validationJson = await _api.GetJsonAsync($"/servers/{Uri.EscapeDataString(serverName)}/oxide/validate");
        if (!validationJson.RootElement.TryGetProperty("plugins", out var pluginsNode) || pluginsNode.ValueKind != JsonValueKind.Array)
            return new List<ServerPluginInfo>();

        var plugins = new List<ServerPluginInfo>();
        foreach (var entry in pluginsNode.EnumerateArray())
        {
            if (!entry.TryGetProperty("path", out var pathNode) || pathNode.ValueKind != JsonValueKind.String)
                continue;

            var path = pathNode.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;

            var fileName = Path.GetFileNameWithoutExtension(path);
            var text = File.ReadAllText(path);
            var match = OxideInfoAttributePattern.Match(text);
            var plugin = new ServerPluginInfo
            {
                Name = match.Success ? (match.Groups["title"].Value.Trim()) : fileName,
                Author = match.Success ? (match.Groups["author"].Value.Trim()) : string.Empty,
                Version = match.Success ? (match.Groups["version"].Value.Trim()) : string.Empty,
                Path = path,
                HasInfoAttribute = match.Success
            };
            plugins.Add(plugin);
        }

        return plugins
            .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int CompareSemanticVersion(string left, string right)
    {
        static int[] ParseParts(string value)
        {
            var normalized = value.Trim().TrimStart('v', 'V');
            var cleaned = new string(normalized.Select(ch => char.IsDigit(ch) || ch == '.' ? ch : '.').ToArray());
            return cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => int.TryParse(part, out var numeric) ? numeric : 0)
                .ToArray();
        }

        var leftParts = ParseParts(left);
        var rightParts = ParseParts(right);
        var max = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < max; i++)
        {
            var l = i < leftParts.Length ? leftParts[i] : 0;
            var r = i < rightParts.Length ? rightParts[i] : 0;
            if (l > r) return 1;
            if (l < r) return -1;
        }

        return 0;
    }

    private static string BuildIncidentTitle(ServerSnapshot server, List<string> evidence)
    {
        var first = evidence.FirstOrDefault() ?? "Issue detected";
        var shortened = first.Length <= 90 ? first : $"{first[..87]}...";
        return $"{server.Name}: {shortened}";
    }

    private static string BuildFallbackSummary(ServerSnapshot server, HealthSnapshot health, List<string> evidence)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Server '{server.Name}' is in state '{server.State}'.");
        builder.AppendLine($"Recent error count: {health.RecentErrors.Count}.");
        if (!string.IsNullOrWhiteSpace(health.LastRestartEvent))
            builder.AppendLine($"Last restart event: {health.LastRestartEvent}");
        builder.AppendLine("Evidence:");
        foreach (var line in evidence.Take(5))
            builder.AppendLine($"- {line}");
        return builder.ToString().Trim();
    }

    private static string InferCategory(IEnumerable<string> evidence)
    {
        var all = string.Join('\n', evidence).ToLowerInvariant();
        if (all.Contains("error while compiling") || all.Contains("failed to compile") || all.Contains("compilation failed"))
            return "plugin-compile";
        if (all.Contains("oxide") || all.Contains("plugin")) return "plugin";
        if (all.Contains("network") || all.Contains("socket")) return "network";
        if (all.Contains("exception") || all.Contains("nullreference")) return "runtime-error";
        return "console-alert";
    }

    private static List<string> ParseLogMessages(JsonElement root)
    {
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return new();

        return entries.EnumerateArray()
            .Select(e => e.TryGetProperty("message", out var msg) ? msg.GetString() : null)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!)
            .ToList();
    }

    private LogSignalLevel ClassifyLogLine(ServerMemory serverMemory, string line, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(line))
            return LogSignalLevel.Ignore;

        var lowered = line.Trim().ToLowerInvariant();

        if (MatchesContains(_logRules.IgnoreContains, lowered))
            return LogSignalLevel.Ignore;

        if (IsWithinStartupIgnoreWindow(serverMemory, utcNow) &&
            MatchesContains(_logRules.StartupIgnoreContains, lowered))
        {
            return LogSignalLevel.Ignore;
        }

        if (MatchesContains(_logRules.IncidentContains, lowered) || CompileErrorPattern.IsMatch(line))
            return LogSignalLevel.Incident;

        if (lowered.Contains("nullreferenceexception") ||
            lowered.Contains("missingmethodexception") ||
            lowered.Contains("exception while calling hook") ||
            lowered.Contains("stack trace") ||
            lowered.Contains("fatal"))
        {
            return LogSignalLevel.Incident;
        }

        if (lowered.Contains("error") || lowered.Contains("failed") || lowered.Contains("exception"))
            return LogSignalLevel.Interesting;

        return LogSignalLevel.Ignore;
    }

    private bool IsWithinStartupIgnoreWindow(ServerMemory serverMemory, DateTime utcNow) =>
        serverMemory.LastStartedAtUtc.HasValue &&
        utcNow - serverMemory.LastStartedAtUtc.Value < TimeSpan.FromSeconds(Math.Max(0, _config.Monitor.StartupIgnoreSeconds));

    private static bool MatchesContains(IEnumerable<string> patterns, string loweredLine) =>
        patterns.Any(pattern => !string.IsNullOrWhiteSpace(pattern) &&
                                loweredLine.Contains(pattern.Trim().ToLowerInvariant(), StringComparison.Ordinal));

    private static string BuildLiveLogPath(string serverName, long? offset, int logLinesPerScan)
    {
        var maxBytes = Math.Clamp(logLinesPerScan * 256, 8 * 1024, 256 * 1024);
        var builder = new StringBuilder($"/servers/{Uri.EscapeDataString(serverName)}/logs/read?maxBytes={maxBytes}");
        if (offset.HasValue)
            builder.Append($"&offset={offset.Value}");
        return builder.ToString();
    }

}

internal sealed class RustMgrApiClient : IDisposable
{
    private readonly HttpClient _http;

    public RustMgrApiClient(ApiSettings settings)
    {
        _http = new HttpClient { BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/')) };
        _http.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<JsonDocument> GetJsonAsync(string path)
    {
        using var response = await _http.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(FormatApiError(response.StatusCode, body));
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    public async Task<JsonDocument> PostJsonAsync(string path, object? payload = null)
    {
        using var content = payload is null
            ? new StringContent(string.Empty)
            : new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(path, content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(FormatApiError(response.StatusCode, body));
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private static string FormatApiError(HttpStatusCode statusCode, string body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;

                if (root.TryGetProperty("message", out var messageNode) && messageNode.ValueKind == JsonValueKind.String)
                    return $"{(int)statusCode} {statusCode}: {messageNode.GetString()}";

                if (root.TryGetProperty("stdErr", out var stdErrNode) && stdErrNode.ValueKind == JsonValueKind.String)
                    return $"{(int)statusCode} {statusCode}: {stdErrNode.GetString()}";

                if (root.TryGetProperty("error", out var errorNode) && errorNode.ValueKind == JsonValueKind.String)
                    return $"{(int)statusCode} {statusCode}: {errorNode.GetString()}";
            }
            catch
            {
            }
        }

        return $"{(int)statusCode} {statusCode}";
    }
}

internal sealed class LlmClient : IDisposable
{
    private readonly LlmSettings _settings;
    private readonly Action<LlmInteractionRecord>? _interactionRecorder;
    private readonly List<LlmEndpointRuntime> _endpoints = new();
    private string? _lastEndpointFailure;

    public LlmClient(LlmSettings settings, Action<LlmInteractionRecord>? interactionRecorder = null)
    {
        _settings = settings;
        _interactionRecorder = interactionRecorder;

        _endpoints.Add(CreateEndpoint("primary", settings.BaseUrl, settings.Model, settings.ApiKey, settings.HttpReferer, settings.AppTitle));
        if (settings.Secondary.Enabled)
            _endpoints.Add(CreateEndpoint(
                "secondary",
                settings.Secondary.BaseUrl,
                settings.Secondary.Model,
                settings.Secondary.ApiKey,
                string.IsNullOrWhiteSpace(settings.Secondary.HttpReferer) ? settings.HttpReferer : settings.Secondary.HttpReferer,
                string.IsNullOrWhiteSpace(settings.Secondary.AppTitle) ? settings.AppTitle : settings.Secondary.AppTitle));
    }

    public async Task<string?> TrySummarizeIncidentAsync(
        string serverName,
        ServerMemory memory,
        HealthSnapshot health,
        List<string> evidence)
    {
        if (!_settings.Enabled)
            return null;

        var prompt = $"""
You are a local Rust server operations assistant.
Summarize the issue compactly for an admin.

Server: {serverName}
Known patterns: {string.Join(" | ", memory.KnownPatterns.Take(8))}
Recent errors: {health.RecentErrors.Count}
Evidence:
{string.Join('\n', evidence.Select(e => $"- {e}"))}

Return 3 short lines:
1. probable issue
2. likely cause
3. suggested next check
""";

        return await GenerateAsync(prompt, "incident-summary", serverName);
    }

    public async Task<RecommendationResult?> TryRecommendActionAsync(
        string serverName,
        ServerMemory memory,
        HealthSnapshot health,
        List<string> evidence)
    {
        if (!_settings.Enabled || !_settings.UseForRecommendations)
            return null;

        var prompt = $$"""
        You are a local Rust server operations assistant.
        Recommend one bounded action for the agent.

        Allowed actions:
        - start-server
        - stop-server
        - restart-server
        - validate-oxide
        - inspect-host-network
        - notify-only

        Server: {{serverName}}
        Last known patterns: {{string.Join(" | ", memory.KnownPatterns.Take(8))}}
        Recent action outcomes: {{string.Join(" | ", memory.ActionOutcomes.TakeLast(6))}}
        Learned rules: {{string.Join(" | ", memory.LearnedActionRules.OrderByDescending(rule => rule.LearnedAtUtc).Take(6).Select(rule => $"{rule.ActionType}:{rule.Guidance}:{rule.Note}"))}}
        Recent error count: {{health.RecentErrors.Count}}
        Evidence:
        {{string.Join('\n', evidence.Select(e => $"- {e}"))}}

        Return JSON only. Use empty strings instead of nulls.
        """;

        return await GenerateJsonAsync<RecommendationResult>(prompt, "action-recommendation", serverName, "recommend_action", BuildRecommendationSchema());
    }

    public async Task<ChatInterpretation?> TryInterpretChatRequestAsync(string message, List<string> serverNames)
    {
        if (!_settings.Enabled)
            return null;

        var prompt = $$"""
You are a local Rust server operations assistant.
Classify one admin chat request into a bounded intent.

Known servers:
{{string.Join(", ", serverNames)}}

        Allowed intents:
        - help
        - ping
        - list-servers
        - server-status
        - server-health
        - pending-actions
        - recent-actions
        - recent-incidents
        - list-server-plugins
        - check-plugin-updates
        - validate-oxide
        - inspect-host-network
        - start-server
        - stop-server
        - restart-server
        - unknown

Admin message:
{{message}}

Respond as strict JSON:
{
  "intent": "help|ping|list-servers|server-status|server-health|pending-actions|recent-actions|recent-incidents|list-server-plugins|check-plugin-updates|validate-oxide|inspect-host-network|start-server|stop-server|restart-server|unknown",
  "serverName": "exact known server name or empty",
  "replyText": "short optional admin-facing clarification",
  "confidence": 0.0
}
""";

        var responseText = await GenerateAsync(prompt, "chat-interpret", message);
        if (string.IsNullOrWhiteSpace(responseText))
            return null;

        var jsonPayload = ExtractJsonObject(responseText);
        if (jsonPayload is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<ChatInterpretation>(jsonPayload, JsonOptions.Default);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ChatInterpretation?> TryPlanChatTurnAsync(string message, AdminConversationState conversation, ChatPlanningContext planningContext)
    {
        if (!_settings.Enabled)
            return null;

        var history = conversation.History
            .TakeLast(6)
            .Select(entry => $"{entry.Role}: {entry.Text}")
            .ToList();

        var pendingClarification = conversation.PendingClarification is null
            ? "none"
            : $"{conversation.PendingClarification.Intent} | question={conversation.PendingClarification.Question}";

        var prompt = $$"""
        You are a local Rust server operations assistant talking to an admin in chat.
        Choose one bounded tool/action for this turn. Prefer clarification over guessing for destructive operations.

        Known servers:
        {{string.Join(", ", planningContext.KnownServers)}}

        Current server states:
        {{FormatContextList(planningContext.ServerStates)}}

        Conversation history:
        {{string.Join('\n', history)}}

        Last server in context:
        {{conversation.LastServerName ?? "none"}}

        Relevant server in context:
        {{planningContext.RelevantServerName ?? "none"}}

        Relevant server memory:
        {{FormatContextList(planningContext.RelevantServerMemory)}}

        Admin preferences:
        {{FormatContextList(planningContext.AdminPreferences)}}

        Learned rules:
        {{FormatContextList(planningContext.LearnedRules)}}

        Pending actions:
        {{FormatContextList(planningContext.PendingActions)}}

        Recent actions:
        {{FormatContextList(planningContext.RecentActions)}}

        Recent incidents:
        {{FormatContextList(planningContext.RecentIncidents)}}

        Known command behavior:
        {{FormatContextList(planningContext.CommandKnowledge)}}

        Pending clarification:
        {{pendingClarification}}

        Allowed intents:
        - help
        - ping
        - list-servers
        - server-status
        - server-health
        - pending-actions
        - recent-actions
        - recent-incidents
        - list-server-plugins
        - check-plugin-updates
        - validate-oxide
        - inspect-host-network
        - start-server
        - stop-server
        - restart-server
        - unknown

        Current admin message:
        {{message}}

        Return JSON only. Use empty strings instead of nulls.
        """;

        return await GenerateJsonAsync<ChatInterpretation>(prompt, "chat-plan", message, "chat_plan", BuildChatInterpretationSchema());
    }

    public async Task<string?> TryDraftAdminReplyAsync(
        string request,
        string deterministicReply,
        ChatInterpretation plan,
        ChatPlanningContext planningContext)
    {
        if (!_settings.Enabled)
            return deterministicReply;

        var prompt = $$"""
        You are the admin-facing voice of a local Rust server operations agent.
        Rewrite the reply to be concise, direct, and useful.
        Do not invent facts, server states, actions, or outcomes beyond the draft reply.
        Keep the same operational meaning. If the draft asks a clarification question, keep it as a question.
        Output plain text only.

        Reply style guidance:
        {{(string.IsNullOrWhiteSpace(planningContext.ReplyStyleGuidance) ? "Use concise, natural admin language." : planningContext.ReplyStyleGuidance)}}

        Current server states:
        {{FormatContextList(planningContext.ServerStates)}}

        Admin preferences:
        {{FormatContextList(planningContext.AdminPreferences)}}

        Recent incidents:
        {{FormatContextList(planningContext.RecentIncidents)}}

        Admin request:
        {{request}}

        Planned intent:
        {{plan.Intent}}

        Deterministic draft reply:
        {{deterministicReply}}
        """;

        return await GenerateAsync(prompt, "chat-reply-draft", request);
    }

    public async Task<SelfRepairPlan?> TryCreateSelfRepairPlanAsync(SelfRepairContext context, IReadOnlyCollection<string> knownTools)
    {
        if (!_settings.Enabled)
            return null;

        var prompt = $$"""
        You are a capability-evolution planner for a local Rust operations agent.
        Treat self-repair as planned capability growth, not emergency-only mitigation.
        Create only bounded, low-risk changes that stay inside the agent self-repair workspace/scope.
        Never suggest writes outside the allowed scope root or dangerous host-level actions.
        Use only these action types:
        - write_file
        - write_scope_file
        - merge_log_rules
        - update_reply_style
        - build_from_source
        - restart_managed_services
        - git_push_branch
        - git_pull_rebuild
        - record_capability_gap

        Known chat tools:
        {{string.Join(", ", knownTools)}}

        Allowed scope root:
        {{context.ScopeRootPath}}

        Self-repair workspace:
        {{context.WorkspacePath}}

        Current reply style guidance:
        {{(string.IsNullOrWhiteSpace(context.CurrentReplyStyle) ? "none" : context.CurrentReplyStyle)}}

        Recent agent errors:
        {{FormatContextList(context.RecentErrors)}}

        Recent failed actions:
        {{FormatContextList(context.RecentFailures)}}

        Recent capability gaps:
        {{FormatContextList(context.CapabilityGaps)}}

        Recent incidents:
        {{FormatContextList(context.RecentIncidents)}}

        Learning backlog incidents (treat each as a failure case to analyze and improve):
        {{FormatContextList(context.LearningIncidents)}}

        For each learning incident above, reason through:
        - What the admin intended (even if phrased ambiguously)
        - Why the agent failed: wrong server resolution, missing tool capability, intent not mapped, LLM threw instead of using tools, or capability denial
        - What one bounded change would prevent this exact failure: update_reply_style for wording/tone problems, record_capability_gap for missing capabilities, merge_log_rules for noise that caused false positives, write_file for a guidance note the agent can reference
        If the learning backlog is non-empty it MUST drive at least one action. Prefer update_reply_style or record_capability_gap for chat failures. Do not produce an empty actions list when learning incidents exist.

        Existing workspace files:
        {{FormatContextList(context.WorkspaceFiles.Select(file => $"{file.RelativePath} ({file.SizeBytes} bytes) preview={TrimForPreview(file.Preview, 120)}"))}}

        Notable scope files:
        {{FormatContextList(context.ScopeFiles.Select(file => $"{file.RelativePath} ({file.SizeBytes} bytes) preview={TrimForPreview(file.Preview, 120)}"))}}

        Requirements:
        - Prefer at most 3 actions.
        - write_file must use a relative path and include full file content.
        - write_scope_file must use a relative path inside scope root and include full file content.
        - merge_log_rules should only add short contains patterns.
        - update_reply_style should produce practical wording guidance for natural admin chat replies.
        - build_from_source should only be used when a scope-file change requires compile verification.
        - restart_managed_services should only be used when build/deploy changes need service reload.
        - git_push_branch should include a short commit summary in description.
        - git_pull_rebuild may include "restart services" in description when needed.
        - If no useful repair is needed, return an empty actions list.

        Return JSON only. Use empty strings instead of nulls.
        """;

        return await GenerateJsonAsync<SelfRepairPlan>(
            prompt,
            "self-repair-plan",
            string.Join(" | ", context.RecentErrors.Take(3)),
            "self_repair_plan",
            BuildSelfRepairPlanSchema());
    }

    private async Task<string?> GenerateAsync(string prompt, string interactionType, string? context = null)
    {
        string? lastFailure = null;

        var contentText = await TryGenerateWithEndpointAsync(
            "/v1/chat/completions",
            new
            {
                model = _settings.Model,
                stream = false,
                temperature = 0.2,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            },
            ExtractChatCompletionsText);

        if (contentText is null)
        {
            lastFailure = BuildStepFailure("chat-completions unavailable");
            contentText = await TryGenerateWithEndpointAsync(
                "/v1/responses",
                new
                {
                    model = _settings.Model,
                    input = prompt
                },
                ExtractResponsesText);
        }

        if (contentText is null)
        {
            lastFailure = BuildStepFailure("responses unavailable");
            contentText = await TryGenerateWithEndpointAsync(
                "/api/v1/chat",
                new
                {
                    model = _settings.Model,
                    input = prompt
                },
                ExtractNativeChatText);
        }

        if (contentText is null)
            lastFailure = BuildStepFailure(lastFailure ?? "native-chat unavailable");

        RecordInteraction(interactionType, !string.IsNullOrWhiteSpace(contentText), context, contentText ?? lastFailure);
        return contentText;
    }

    private async Task<T?> GenerateJsonAsync<T>(string prompt, string interactionType, string? context, string schemaName, object schema)
        where T : class
    {
        var structuredText = await TryGenerateWithEndpointAsync(
            "/v1/chat/completions",
            new
            {
                model = _settings.Model,
                stream = false,
                temperature = 0.1,
                response_format = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = schemaName,
                        strict = true,
                        schema
                    }
                },
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            },
            ExtractChatCompletionsText);

        if (!string.IsNullOrWhiteSpace(structuredText))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<T>(structuredText, JsonOptions.Default);
                RecordInteraction(interactionType, parsed is not null, context, structuredText);
                if (parsed is not null)
                    return parsed;
            }
            catch
            {
                RecordInteraction(interactionType, false, context, structuredText);
            }
        }

        var fallbackText = await GenerateAsync(prompt, interactionType, context);
        if (string.IsNullOrWhiteSpace(fallbackText))
            return null;

        var jsonPayload = ExtractJsonObject(fallbackText);
        if (jsonPayload is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(jsonPayload, JsonOptions.Default);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LlmToolChatResponse?> RequestToolChatTurnAsync(string systemPrompt, IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<object> tools)
    {
        if (!_settings.Enabled)
            return null;

        var payload = new
        {
            model = _settings.Model,
            stream = false,
            temperature = 0.1,
            tool_choice = "auto",
            messages = BuildChatMessagesPayload(systemPrompt, messages),
            tools
        };

        LlmToolChatResponse? result = null;
        var failures = new List<string>();
        foreach (var endpoint in _endpoints)
        {
            var httpResult = await PostWithDynamicEndpointAsync(endpoint, "/v1/chat/completions", payload);
            if (!httpResult.Ok || string.IsNullOrWhiteSpace(httpResult.Body))
            {
                failures.Add(DescribeHttpFailure(httpResult));
                continue;
            }

            var body = httpResult.Body.TrimStart();
            if (!body.StartsWith("{", StringComparison.Ordinal) &&
                !body.StartsWith("[", StringComparison.Ordinal))
            {
                failures.Add($"{endpoint.Name}: non-JSON response (status {httpResult.StatusCode}): {TrimForPreview(httpResult.Body, 140)}");
                continue;
            }

            try
            {
                using var json = JsonDocument.Parse(httpResult.Body);
                result = ExtractToolChatResponse(json.RootElement);
                if (result is not null)
                    break;

                failures.Add($"{endpoint.Name}: JSON response missing expected tool call fields.");
            }
            catch (Exception ex)
            {
                failures.Add($"{endpoint.Name}: JSON parse failed: {TrimForPreview(ex.Message, 140)}");
            }
        }

        _lastEndpointFailure = result is null && failures.Count > 0
            ? string.Join(" | ", failures.Distinct(StringComparer.OrdinalIgnoreCase).Take(3))
            : null;

        RecordInteraction(
            "chat-tool-turn",
            result is not null,
            messages.LastOrDefault(message => message.Role == "user")?.Content,
            result is null
                ? BuildStepFailure("tool response parse failed")
                : result.ToolCalls.Count > 0
                    ? $"tool calls: {string.Join(", ", result.ToolCalls.Select(call => call.Name))}"
                    : result.Content);

        return result;
    }

    public async Task<string?> RequestChatCompletionAsync(string systemPrompt, IReadOnlyList<LlmChatMessage> messages)
    {
        if (!_settings.Enabled)
            return null;

        var content = await TryGenerateWithEndpointAsync(
            "/v1/chat/completions",
            new
            {
                model = _settings.Model,
                stream = false,
                temperature = 0.2,
                messages = BuildChatMessagesPayload(systemPrompt, messages)
            },
            ExtractChatCompletionsText);

        RecordInteraction(
            "chat-tool-final",
            !string.IsNullOrWhiteSpace(content),
            messages.LastOrDefault(message => message.Role == "user")?.Content,
            content ?? BuildStepFailure("chat completion unavailable"));

        return content;
    }

    private static object[] BuildChatMessagesPayload(string systemPrompt, IReadOnlyList<LlmChatMessage> messages)
    {
        var payload = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            payload.Add(new { role = "system", content = systemPrompt });
        }

        foreach (var message in messages)
        {
            if (message.Role == "assistant" && message.ToolCalls.Count > 0)
            {
                payload.Add(new
                {
                    role = "assistant",
                    content = string.IsNullOrWhiteSpace(message.Content) ? string.Empty : message.Content,
                    tool_calls = message.ToolCalls.Select(call => new
                    {
                        id = call.Id,
                        type = "function",
                        function = new
                        {
                            name = call.Name,
                            arguments = call.ArgumentsJson
                        }
                    }).ToArray()
                });
                continue;
            }

            if (message.Role == "tool")
            {
                payload.Add(new
                {
                    role = "tool",
                    tool_call_id = message.ToolCallId,
                    name = message.Name,
                    content = message.Content ?? string.Empty
                });
                continue;
            }

            payload.Add(new
            {
                role = message.Role,
                content = message.Content ?? string.Empty
            });
        }

        return payload.ToArray();
    }

    private async Task<string?> TryGenerateWithEndpointAsync(string path, object payload, Func<JsonElement, string?> extractor)
    {
        var failures = new List<string>();
        if (_settings.RequestStrategy.Equals("race", StringComparison.OrdinalIgnoreCase) && _endpoints.Count > 1)
        {
            var tasks = _endpoints.Select(endpoint => TryGenerateWithEndpointAsync(endpoint, path, payload, extractor)).ToList();
            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks);
                tasks.Remove(completed);
                var attempt = await completed;
                if (!string.IsNullOrWhiteSpace(attempt.Content))
                {
                    _lastEndpointFailure = null;
                    return attempt.Content;
                }

                if (!string.IsNullOrWhiteSpace(attempt.Failure))
                    failures.Add(attempt.Failure!);
            }

            _lastEndpointFailure = failures.Count == 0
                ? null
                : string.Join(" | ", failures.Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
            return null;
        }

        foreach (var endpoint in _endpoints)
        {
            var attempt = await TryGenerateWithEndpointAsync(endpoint, path, payload, extractor);
            if (!string.IsNullOrWhiteSpace(attempt.Content))
            {
                _lastEndpointFailure = null;
                return attempt.Content;
            }

            if (!string.IsNullOrWhiteSpace(attempt.Failure))
                failures.Add(attempt.Failure!);
        }

        _lastEndpointFailure = failures.Count == 0
            ? null
            : string.Join(" | ", failures.Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
        return null;
    }

    private async Task<LlmAttemptResult> TryGenerateWithEndpointAsync(
        LlmEndpointRuntime endpoint,
        string path,
        object payload,
        Func<JsonElement, string?> extractor)
    {
        var httpResult = await PostWithDynamicEndpointAsync(endpoint, path, payload);
        if (!httpResult.Ok || string.IsNullOrWhiteSpace(httpResult.Body))
        {
            return new LlmAttemptResult
            {
                Failure = DescribeHttpFailure(httpResult)
            };
        }

        var body = httpResult.Body.TrimStart();
        if (!body.StartsWith("{", StringComparison.Ordinal) &&
            !body.StartsWith("[", StringComparison.Ordinal))
        {
            return new LlmAttemptResult
            {
                Failure = $"{endpoint.Name}: non-JSON response (status {httpResult.StatusCode}): {TrimForPreview(httpResult.Body, 140)}"
            };
        }

        try
        {
            using var json = JsonDocument.Parse(httpResult.Body);
            var extracted = extractor(json.RootElement);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                return new LlmAttemptResult
                {
                    Content = extracted
                };
            }

            return new LlmAttemptResult
            {
                Failure = $"{endpoint.Name}: JSON response missing expected completion fields."
            };
        }
        catch (Exception ex)
        {
            return new LlmAttemptResult
            {
                Failure = $"{endpoint.Name}: JSON parse failed: {TrimForPreview(ex.Message, 140)}"
            };
        }
    }

    private async Task<LlmHttpResult> PostWithDynamicEndpointAsync(LlmEndpointRuntime endpoint, string canonicalPath, object payload)
    {
        var body = BuildEndpointRequestBody(payload, endpoint.Model);
        var candidates = BuildEndpointCandidates(endpoint.Client.BaseAddress, canonicalPath).ToList();

        LlmHttpResult? lastFailure = null;
        foreach (var candidate in candidates)
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            try
            {
                using var response = await endpoint.Client.PostAsync(candidate, content);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    return new LlmHttpResult
                    {
                        Ok = true,
                        StatusCode = (int)response.StatusCode,
                        Body = responseBody,
                        EndpointName = endpoint.Name,
                        EndpointModel = endpoint.Model
                    };
                }

                lastFailure = new LlmHttpResult
                {
                    Ok = false,
                    StatusCode = (int)response.StatusCode,
                    Body = responseBody,
                    EndpointName = endpoint.Name,
                    EndpointModel = endpoint.Model
                };
            }
            catch (Exception ex)
            {
                lastFailure = new LlmHttpResult
                {
                    Ok = false,
                    StatusCode = 0,
                    Body = ex.Message,
                    EndpointName = endpoint.Name,
                    EndpointModel = endpoint.Model
                };
            }
        }

        return lastFailure ?? new LlmHttpResult { Ok = false, StatusCode = 0, Body = null };
    }

    private static string BuildEndpointRequestBody(object payload, string model)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(payload));
        if (node is JsonObject obj)
            obj["model"] = model;

        return node?.ToJsonString() ?? JsonSerializer.Serialize(payload);
    }

    private static IEnumerable<string> BuildEndpointCandidates(Uri? baseAddress, string canonicalPath)
    {
        var normalized = canonicalPath.Trim();
        normalized = normalized.TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "v1/chat/completions";

        var candidates = new List<string>();
        void Add(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                candidate = string.Empty;
            if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                candidates.Add(candidate);
        }

        var basePath = baseAddress?.AbsolutePath ?? "/";
        if (!string.IsNullOrWhiteSpace(basePath) && !string.Equals(basePath, "/", StringComparison.Ordinal))
        {
            var trimmedBasePath = basePath.TrimEnd('/');

            // Base can already point at a full endpoint.
            if (trimmedBasePath.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase))
                Add(string.Empty);

            // Prefix style, e.g. /openai + /v1/chat/completions
            if (!(normalized.StartsWith("v1/", StringComparison.OrdinalIgnoreCase) &&
                  trimmedBasePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)))
            {
                Add($"{trimmedBasePath}/{normalized}");
            }

            // Base already ends with /v1 (or /api/v1) and normalized also starts with v1/.
            if (normalized.StartsWith("v1/", StringComparison.OrdinalIgnoreCase) &&
                trimmedBasePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                Add($"{trimmedBasePath}/{normalized["v1/".Length..]}");
            }

            var v1Index = trimmedBasePath.IndexOf("/v1/", StringComparison.OrdinalIgnoreCase);
            if (v1Index >= 0)
            {
                var prefix = trimmedBasePath[..v1Index].TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(prefix))
                    Add($"{prefix}/{normalized}");
            }

            // If configured directly as .../chat/completions, derive responses endpoint.
            if (normalized.Equals("v1/responses", StringComparison.OrdinalIgnoreCase) &&
                trimmedBasePath.EndsWith("chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                Add(trimmedBasePath[..^"chat/completions".Length] + "responses");
            }
        }

        // Absolute and relative defaults.
        Add("/" + normalized);
        Add(normalized);

        return candidates;
    }

    private static LlmEndpointRuntime CreateEndpoint(
        string name,
        string baseUrl,
        string model,
        string? apiKey,
        string? httpReferer,
        string? appTitle)
    {
        var normalizedBaseUrl = baseUrl.Trim();
        if (!normalizedBaseUrl.EndsWith("/", StringComparison.Ordinal))
            normalizedBaseUrl += "/";

        var client = new HttpClient
        {
            BaseAddress = new Uri(normalizedBaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromMinutes(2)
        };

        var normalizedToken = NormalizeBearerToken(apiKey);
        if (!string.IsNullOrWhiteSpace(normalizedToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", normalizedToken);

        var isOpenRouter = normalizedBaseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);
        var resolvedReferer = string.IsNullOrWhiteSpace(httpReferer) && isOpenRouter ? "http://localhost" : httpReferer;
        var resolvedTitle = string.IsNullOrWhiteSpace(appTitle) && isOpenRouter ? "RustOpsAgent" : appTitle;

        if (!string.IsNullOrWhiteSpace(resolvedReferer))
            client.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", resolvedReferer);
        if (!string.IsNullOrWhiteSpace(resolvedTitle))
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-OpenRouter-Title", resolvedTitle);

        return new LlmEndpointRuntime
        {
            Name = name,
            Model = model,
            Client = client
        };
    }

    private sealed class LlmHttpResult
    {
        public bool Ok { get; set; }
        public int StatusCode { get; set; }
        public string? Body { get; set; }
        public string? EndpointName { get; set; }
        public string? EndpointModel { get; set; }
    }

    private sealed class LlmAttemptResult
    {
        public string? Content { get; set; }
        public string? Failure { get; set; }
    }

    private sealed class LlmEndpointRuntime
    {
        public string Name { get; init; } = "primary";
        public string Model { get; init; } = string.Empty;
        public HttpClient Client { get; init; } = new();
    }

    private void RecordInteraction(string interactionType, bool success, string? context, string? response)
    {
        _interactionRecorder?.Invoke(new LlmInteractionRecord
        {
            AtUtc = DateTime.UtcNow,
            Type = interactionType,
            Model = _settings.Model,
            Success = success,
            Context = string.IsNullOrWhiteSpace(context) ? null : TrimForPreview(context, 120),
            ResponsePreview = string.IsNullOrWhiteSpace(response) ? null : TrimForPreview(response, 180)
        });
    }

    private string BuildStepFailure(string baseMessage)
    {
        var detail = string.IsNullOrWhiteSpace(_lastEndpointFailure) ? null : _lastEndpointFailure;
        return string.IsNullOrWhiteSpace(detail) ? baseMessage : $"{baseMessage}: {detail}";
    }

    private static string DescribeHttpFailure(LlmHttpResult result)
    {
        var endpoint = string.IsNullOrWhiteSpace(result.EndpointName) ? "endpoint" : result.EndpointName;
        if (string.IsNullOrWhiteSpace(result.Body))
            return $"{endpoint}: HTTP {result.StatusCode}";

        return $"{endpoint}: HTTP {result.StatusCode} {TrimForPreview(result.Body, 140)}";
    }

    private static string? NormalizeBearerToken(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var token = apiKey.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim();

        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static string TrimForPreview(string input, int maxLength)
    {
        var singleLine = input.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maxLength ? singleLine : $"{singleLine[..Math.Max(0, maxLength - 3)]}...";
    }

    private static string FormatContextList(IEnumerable<string> items)
    {
        var values = items
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(8)
            .ToList();

        return values.Count == 0 ? "none" : string.Join('\n', values.Select(item => $"- {item}"));
    }

    private static object BuildRecommendationSchema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            suggestedAction = new
            {
                type = "string",
                @enum = new[] { "start-server", "stop-server", "restart-server", "validate-oxide", "inspect-host-network", "notify-only" }
            },
            confidence = new { type = "number" },
            adminMessage = new { type = "string" },
            reasoning = new { type = "string" }
        },
        required = new[] { "suggestedAction", "confidence", "adminMessage", "reasoning" }
    };

    private static object BuildChatInterpretationSchema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            intent = new
            {
                type = "string",
                @enum = new[]
                {
                    "help", "ping", "list-servers", "server-status", "server-health", "pending-actions",
                    "recent-actions", "recent-incidents", "list-server-plugins", "check-plugin-updates",
                    "validate-oxide", "inspect-host-network",
                    "start-server", "stop-server", "restart-server", "unknown"
                }
            },
            serverName = new { type = "string" },
            replyText = new { type = "string" },
            confidence = new { type = "number" },
            needsClarification = new { type = "boolean" },
            clarificationQuestion = new { type = "string" },
            useLastServer = new { type = "boolean" }
        },
        required = new[] { "intent", "serverName", "replyText", "confidence", "needsClarification", "clarificationQuestion", "useLastServer" }
    };

    private static object BuildSelfRepairPlanSchema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            summary = new { type = "string" },
            reasoning = new { type = "string" },
            actions = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        type = new { type = "string", @enum = new[] { "write_file", "write_scope_file", "merge_log_rules", "update_reply_style", "build_from_source", "restart_managed_services", "git_push_branch", "git_pull_rebuild", "record_capability_gap" } },
                        relativePath = new { type = "string" },
                        content = new { type = "string" },
                        description = new { type = "string" },
                        ignoreContains = new { type = "array", items = new { type = "string" } },
                        startupIgnoreContains = new { type = "array", items = new { type = "string" } },
                        incidentContains = new { type = "array", items = new { type = "string" } }
                    },
                    required = new[] { "type" }
                }
            }
        },
        required = new[] { "summary", "reasoning", "actions" }
    };

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        var candidate = text[start..(end + 1)];
        try
        {
            using var _ = JsonDocument.Parse(candidate);
            return candidate;
        }
        catch
        {
            return null;
        }
    }

    private static LlmToolChatResponse? ExtractToolChatResponse(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return null;

        var first = choices[0];
        if (!first.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return null;

        var toolCalls = new List<LlmToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCallsNode) && toolCallsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCallsNode.EnumerateArray())
            {
                if (!call.TryGetProperty("function", out var functionNode) || functionNode.ValueKind != JsonValueKind.Object)
                    continue;

                var name = ReadStringProperty(functionNode, "name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var arguments = ReadStringProperty(functionNode, "arguments") ?? "{}";
                toolCalls.Add(new LlmToolCall
                {
                    Id = ReadStringProperty(call, "id") ?? Guid.NewGuid().ToString("N"),
                    Name = name!,
                    ArgumentsJson = arguments
                });
            }
        }

        return new LlmToolChatResponse
        {
            Content = ReadContentText(message, "content"),
            ToolCalls = toolCalls
        };
    }

    private static string? ExtractChatCompletionsText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return null;

        var first = choices[0];
        if (!first.TryGetProperty("message", out var message))
            return null;

        return ReadContentText(message, "content");
    }

    private static string? ExtractResponsesText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            return outputText.GetString();

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in output.EnumerateArray())
        {
            var content = ReadContentText(item, "content");
            if (!string.IsNullOrWhiteSpace(content))
                return content;
        }

        return null;
    }

    private static string? ExtractNativeChatText(JsonElement root)
    {
        return ReadContentText(root, "content")
            ?? ReadContentText(root, "message")
            ?? ReadStringProperty(root, "output_text")
            ?? ReadStringProperty(root, "text")
            ?? ReadStringProperty(root, "response");
    }

    private static string? ReadContentText(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var content))
            return null;

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Join("\n", content.EnumerateArray()
                .Select(item => item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object => ReadStringProperty(item, "text") ?? ReadStringProperty(item, "content"),
                    _ => null
                })
                .Where(text => !string.IsNullOrWhiteSpace(text))),
            JsonValueKind.Object => ReadStringProperty(content, "text") ?? ReadStringProperty(content, "content"),
            _ => null
        };
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    public void Dispose()
    {
        foreach (var endpoint in _endpoints)
            endpoint.Client.Dispose();
    }
}

internal sealed class AgentMemoryStore
{
    public DateTime LastSavedAtUtc { get; set; }
    public AgentRuntimeStatus RuntimeStatus { get; set; } = new();
    public List<ServerMemory> Servers { get; set; } = new();
    public List<string> AgentErrors { get; set; } = new();
    public List<AdminPreference> AdminPreferences { get; set; } = new();
    public List<ActionProposal> PendingActions { get; set; } = new();
    public List<ActionExecutionRecord> ActionHistory { get; set; } = new();
    public List<ActionMetric> ActionMetrics { get; set; } = new();
    public List<FeedbackEntry> FeedbackHistory { get; set; } = new();
    public List<LlmInteractionRecord> LlmInteractions { get; set; } = new();
    public List<CapabilityGapRecord> CapabilityGaps { get; set; } = new();
    public List<SelfRepairRunRecord> SelfRepairHistory { get; set; } = new();

    public static AgentMemoryStore Load(string path)
    {
        if (!File.Exists(path))
            return new AgentMemoryStore();

        return JsonSerializer.Deserialize<AgentMemoryStore>(File.ReadAllText(path), JsonOptions.Default)
            ?? new AgentMemoryStore();
    }

    public ServerMemory GetOrCreateServer(string name)
    {
        var server = Servers.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (server is not null)
            return server;

        server = new ServerMemory { Name = name };
        Servers.Add(server);
        return server;
    }

    public AdminPreference GetOrCreateAdmin(string adminId)
    {
        var admin = AdminPreferences.FirstOrDefault(a => string.Equals(a.AdminId, adminId, StringComparison.OrdinalIgnoreCase));
        if (admin is not null)
            return admin;

        admin = new AdminPreference { AdminId = adminId };
        AdminPreferences.Add(admin);
        return admin;
    }

    public void RecordAgentError(string message)
    {
        AgentErrors.Add($"[{DateTime.UtcNow:O}] {message}");
        AgentErrors = AgentErrors.TakeLast(40).ToList();
    }

    public void RecordCapabilityGap(string category, string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return;

        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? "unknown" : category.Trim();
        var normalizedDescription = description.Trim();
        var existing = CapabilityGaps.FirstOrDefault(gap =>
            string.Equals(gap.Category, normalizedCategory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(gap.Description, normalizedDescription, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            CapabilityGaps.Add(new CapabilityGapRecord
            {
                Category = normalizedCategory,
                Description = normalizedDescription,
                Count = 1,
                FirstObservedAtUtc = DateTime.UtcNow,
                LastObservedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            existing.Count++;
            existing.LastObservedAtUtc = DateTime.UtcNow;
        }

        CapabilityGaps = CapabilityGaps
            .OrderByDescending(gap => gap.LastObservedAtUtc)
            .Take(80)
            .ToList();
    }

    public void UpdateRuntimeStatus(bool llmEnabled, string? provider, string? model, string? baseUrl, string? logRulesPath)
    {
        RuntimeStatus.LlmEnabled = llmEnabled;
        RuntimeStatus.LlmProvider = provider;
        RuntimeStatus.LlmModel = model;
        RuntimeStatus.LlmBaseUrl = baseUrl;
        RuntimeStatus.LogRulesPath = logRulesPath;
        RuntimeStatus.UpdatedAtUtc = DateTime.UtcNow;
    }

    public void RecordLlmInteraction(LlmInteractionRecord interaction)
    {
        RuntimeStatus.LastLlmInteractionAtUtc = interaction.AtUtc;
        LlmInteractions.Add(interaction);
        LlmInteractions = LlmInteractions
            .OrderByDescending(item => item.AtUtc)
            .Take(40)
            .ToList();
    }

    public void RecordSelfRepairRun(SelfRepairRunRecord run)
    {
        SelfRepairHistory.Add(run);
        SelfRepairHistory = SelfRepairHistory
            .OrderByDescending(item => item.AtUtc)
            .Take(80)
            .ToList();
    }

    public void Save(string path)
    {
        LastSavedAtUtc = DateTime.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions.Default));
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed class AgentConfig
{
    public ApiSettings Api { get; set; } = new();
    public MonitorSettings Monitor { get; set; } = new();
    public MemorySettings Memory { get; set; } = new();
    public InboxSettings Inbox { get; set; } = new();
    public OutboxSettings Outbox { get; set; } = new();
    public PolicySettings Policy { get; set; } = new();
    public SelfRepairSettings SelfRepair { get; set; } = new();
    [JsonPropertyName("gitOps")] public GitOpsSettings GitOps { get; set; } = new();
    [JsonPropertyName("commandExecution")] public CommandExecutionSettings CommandExecution { get; set; } = new();
    [JsonPropertyName("pluginUpdates")] public PluginUpdateSettings PluginUpdates { get; set; } = new();
    [JsonPropertyName("llm")] public LlmSettings Llm { get; set; } = new();
    [JsonPropertyName("ollama")] public LlmSettings? LegacyOllama { get; set; }
    [JsonIgnore] public string BaseDirectory { get; set; } = string.Empty;
}

internal sealed class ApiSettings
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:2077";
    public string ApiKey { get; set; } = "changeme";
}

internal sealed class MonitorSettings
{
    public int PollSeconds { get; set; } = 20;
    public int ControlPollSeconds { get; set; } = 2;
    public int LogLinesPerScan { get; set; } = 120;
    public int HealthCooldownMinutes { get; set; } = 10;
    public int StartupIgnoreSeconds { get; set; } = 180;
    public int PluginUpdateCheckMinutes { get; set; } = 180;
    public string LogRulesPath { get; set; } = "agent-log-rules.json";
}

internal sealed class MemorySettings
{
    public string StatePath { get; set; } = "data/agent-state.json";
}

internal sealed class InboxSettings
{
    public string FeedbackInboxPath { get; set; } = "data/feedback-inbox";
    public string DecisionInboxPath { get; set; } = "data/decision-inbox";
    public string ChatInboxPath { get; set; } = "data/chat-inbox";
}

internal sealed class PolicySettings
{
    public List<string> AutoAllowedActions { get; set; } = new();
    public List<string> ApprovalRequiredActions { get; set; } = new();
    public bool AllowAnyServerCommand { get; set; }
    public List<string> AllowedServerCommandPrefixes { get; set; } = new()
    {
        "oxide.",
        "o.",
        "status",
        "version",
        "plugins",
        "global.",
        "serverinfo"
    };

    public bool IsAutoAllowed(string actionType) =>
        AutoAllowedActions.Contains(actionType, StringComparer.OrdinalIgnoreCase);

    public bool RequiresApproval(string actionType) =>
        ApprovalRequiredActions.Contains(actionType, StringComparer.OrdinalIgnoreCase);

    public bool IsServerCommandAllowed(string commandText)
    {
        if (AllowAnyServerCommand)
            return true;

        var command = commandText?.Trim() ?? string.Empty;
        var commandKey = command
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim()
            .ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandKey))
            return false;

        return AllowedServerCommandPrefixes.Any(prefix =>
        {
            var normalized = (prefix ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
                return false;
            return commandKey.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
        });
    }
}

internal sealed class OutboxSettings
{
    public string MessageOutboxPath { get; set; } = "data/message-outbox";
}

internal sealed class SelfRepairSettings
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 180;
    public int MaxActionsPerCycle { get; set; } = 4;
    public int MaxFileBytes { get; set; } = 32 * 1024;
    public string ScopeRootPath { get; set; } = "/opt/rust-manager";
    public string WorkspacePath { get; set; } = "data/self-repair";
    public string SourceRootPath { get; set; } = "/opt/rust-manager/src";
    public string BuildOutputPath { get; set; } = "/opt/rust-manager";
    public bool AllowScopeFileWrites { get; set; } = true;
    public bool ApplyLogRuleUpdates { get; set; } = true;
    public bool ApplyReplyStyleUpdates { get; set; } = true;
    public bool AllowSourceBuilds { get; set; } = true;
    public bool AllowServiceRestarts { get; set; } = true;
    public bool NotifyAdmins { get; set; } = false;
}

internal sealed class GitOpsSettings
{
    public bool Enabled { get; set; }
    public string RepoPath { get; set; } = "/opt/rust-manager/src";
    public string RemoteName { get; set; } = "origin";
    public string BaseBranch { get; set; } = "main";
    public string PushBranchPrefix { get; set; } = "agent/";
    public bool AllowPush { get; set; }
    public bool AllowManualPullRebuild { get; set; } = true;
    public bool AutoPullEnabled { get; set; }
    public int AutoPullIntervalMinutes { get; set; } = 15;
    public bool AutoPullRebuild { get; set; } = true;
    public bool AutoRestartAfterPullRebuild { get; set; }
    public bool RequireCleanWorktreeForPull { get; set; } = true;
}

internal sealed class CommandExecutionSettings
{
    public bool Enabled { get; set; } = true;
    public bool FreeMode { get; set; }
    public int DefaultWaitMs { get; set; } = 2500;
    public int MaxWaitMs { get; set; } = 12_000;
    public int MaxOutputChars { get; set; } = 8000;
    public List<string> AllowList { get; set; } = new()
    {
        "playerlist",
        "serverinfo",
        "bans",
        "oxide.plugins",
        "status",
        "version"
    };
}

internal sealed class PluginUpdateSettings
{
    public bool Enabled { get; set; } = true;
    public int CheckIntervalMinutes { get; set; } = 60;
    public bool NotifyAdmins { get; set; } = true;
    public string SearchUrlTemplate { get; set; } = "https://umod.org/plugins/search.json?query={0}&page=1&sort=title&sortdir=asc&filter={1}";
    public string SearchFilter { get; set; } = "rust";
}

internal sealed class LlmSettings
{
    public string Provider { get; set; } = "lmstudio";
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:1234";
    public string Model { get; set; } = "llmster";
    public string ApiKey { get; set; } = string.Empty;
    public string HttpReferer { get; set; } = "http://localhost";
    public string AppTitle { get; set; } = "RustOpsAgent";
    public bool UseForRecommendations { get; set; } = true;
    public string RequestStrategy { get; set; } = "fallback";
    public SecondaryLlmSettings Secondary { get; set; } = new();
    public bool UseChatSystemPrompt { get; set; } = true;
    public string ChatSystemPrompt { get; set; } = """
You are a local Rust server operations agent talking to an admin.
Use the provided tools to inspect state and perform bounded operations.
Prefer using tools over guessing.
For start, stop, restart, and validate-oxide you must target a known server.
If the server is unclear, ask a concise clarification question instead of guessing.
Use recent memory, incidents, and action history to explain what is happening.
Reply naturally, with concrete operational language.
Start with the direct answer, then key evidence or next action.
Do not invent facts.
You may use self-diagnostics and workspace tools to improve your own behavior.
Any file writes must stay inside the configured self-repair scope root.
If an admin asks to execute a server console command, use execute_server_command.
If an admin asks what a command does, use get_server_command_memory.
If an admin teaches command behavior, use teach_server_command.
If an admin asks about plugins or updates, use list_server_plugins and check_plugin_updates.
If an admin asks to push source changes to git, use git_push_branch.
If an admin asks to pull latest source updates, use git_pull_rebuild.
""";
}

internal sealed class SecondaryLlmSettings
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string HttpReferer { get; set; } = string.Empty;
    public string AppTitle { get; set; } = string.Empty;
}

internal sealed class ServerSnapshot
{
    public string Name { get; init; } = string.Empty;
    public string State { get; init; } = "unknown";
    public int? Pid { get; init; }
    public int? CurrentPlayers { get; init; }
    public int? MaxPlayers { get; init; }
    public string? Map { get; init; }
    public double? Framerate { get; init; }
    public int? RecentWarningCount { get; init; }

    public static ServerSnapshot FromSummary(JsonElement element)
    {
        return new ServerSnapshot
        {
            Name = element.GetProperty("name").GetString() ?? "unknown",
            State = element.GetProperty("state").GetString() ?? "unknown",
            Pid = element.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number ? pid.GetInt32() : null,
            CurrentPlayers = element.TryGetProperty("currentPlayers", out var currentPlayers) && currentPlayers.ValueKind == JsonValueKind.Number ? currentPlayers.GetInt32() : null,
            MaxPlayers = element.TryGetProperty("maxPlayers", out var maxPlayers) && maxPlayers.ValueKind == JsonValueKind.Number ? maxPlayers.GetInt32() : null,
            Map = element.TryGetProperty("map", out var map) && map.ValueKind == JsonValueKind.String ? map.GetString() : null,
            Framerate = element.TryGetProperty("framerate", out var framerate) && framerate.ValueKind == JsonValueKind.Number ? framerate.GetDouble() : null,
            RecentWarningCount = element.TryGetProperty("recentWarningCount", out var warningCount) && warningCount.ValueKind == JsonValueKind.Number ? warningCount.GetInt32() : null
        };
    }
}

internal sealed class HealthSnapshot
{
    public string Server { get; init; } = string.Empty;
    public List<string> RecentErrors { get; init; } = new();
    public string? LastRestartEvent { get; init; }

    public static HealthSnapshot FromJson(string server, JsonElement root)
    {
        var errors = root.TryGetProperty("recentErrors", out var recentErrors) && recentErrors.ValueKind == JsonValueKind.Array
            ? recentErrors.EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToList()
            : new List<string>();

        return new HealthSnapshot
        {
            Server = server,
            RecentErrors = errors,
            LastRestartEvent = root.TryGetProperty("lastRestartEvent", out var restart) && restart.ValueKind == JsonValueKind.String
                ? restart.GetString()
                : null
        };
    }
}

internal sealed class ServerMemory
{
    public string Name { get; set; } = string.Empty;
    public string? LastStatus { get; set; }
    public int? LastKnownPid { get; set; }
    public long? LastLogOffset { get; set; }
    public DateTime? LastStartedAtUtc { get; set; }
    public DateTime? LastObservedAtUtc { get; set; }
    public DateTime? LastObservedIssueAtUtc { get; set; }
    public List<string> KnownPatterns { get; set; } = new();
    public List<string> ActionOutcomes { get; set; } = new();
    public List<LearnedActionRule> LearnedActionRules { get; set; } = new();
    public List<IncidentMemory> Incidents { get; set; } = new();
    public List<KnownConsoleCommand> KnownConsoleCommands { get; set; } = new();
    public List<ConsoleCommandInteraction> CommandInteractions { get; set; } = new();
    public DateTime? LastPluginUpdateCheckAtUtc { get; set; }
    public string? LastPluginUpdateSignature { get; set; }
    public List<KnownPluginRecord> KnownPlugins { get; set; } = new();
}

internal sealed class KnownConsoleCommand
{
    public string Command { get; set; } = string.Empty;
    public string? Purpose { get; set; }
    public string? UsefulWhen { get; set; }
    public string? LastOutputSummary { get; set; }
    public int Observations { get; set; }
    public DateTime? LastObservedAtUtc { get; set; }
}

internal sealed class ConsoleCommandInteraction
{
    public DateTime AtUtc { get; set; }
    public string CommandKey { get; set; } = string.Empty;
    public string RawCommand { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? AdminId { get; set; }
}

internal sealed class KnownPluginRecord
{
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime LastSeenAtUtc { get; set; }
}

internal sealed class ServerPluginInfo
{
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool HasInfoAttribute { get; set; }
}

internal sealed class PluginUpdateCandidate
{
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string LocalVersion { get; set; } = string.Empty;
    public string RemoteVersion { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
}

internal enum LogSignalLevel
{
    Ignore = 0,
    Interesting = 1,
    Incident = 2
}

internal sealed class AgentLogRules
{
    public List<string> IgnoreContains { get; set; } = new();
    public List<string> StartupIgnoreContains { get; set; } = new();
    public List<string> IncidentContains { get; set; } = new();

    public void ApplyDefaults(AgentLogRules defaults)
    {
        IgnoreContains = Merge(defaults.IgnoreContains, IgnoreContains);
        StartupIgnoreContains = Merge(defaults.StartupIgnoreContains, StartupIgnoreContains);
        IncidentContains = Merge(defaults.IncidentContains, IncidentContains);
    }

    public static AgentLogRules CreateDefault() => new()
    {
        IgnoreContains = new List<string>
        {
            "shader soft mask/textmeshpro/distance field shader is not supported on this gpu",
            "shader unsupported: 'soft mask/textmeshpro/distance field' - all subshaders removed"
        },
        StartupIgnoreContains = new List<string>
        {
            "shader is not supported on this gpu",
            "all subshaders removed"
        },
        IncidentContains = new List<string>
        {
            "error while compiling",
            "failed to compile",
            "compilation failed",
            "error while compiling ",
            "exception while calling hook"
        }
    };

    private static List<string> Merge(IEnumerable<string> defaults, IEnumerable<string> overrides) =>
        defaults
            .Concat(overrides)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

internal sealed class AgentRuntimeStatus
{
    public bool LlmEnabled { get; set; }
    public string? LlmProvider { get; set; }
    public string? LlmModel { get; set; }
    public string? LlmBaseUrl { get; set; }
    public string? LogRulesPath { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? LastLlmInteractionAtUtc { get; set; }
}

internal sealed class LlmInteractionRecord
{
    public DateTime AtUtc { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Context { get; set; }
    public string? ResponsePreview { get; set; }
}

internal sealed class CapabilityGapRecord
{
    public string Category { get; set; } = "unknown";
    public string Description { get; set; } = string.Empty;
    public int Count { get; set; }
    public DateTime FirstObservedAtUtc { get; set; }
    public DateTime LastObservedAtUtc { get; set; }
}

internal sealed class SelfRepairRunRecord
{
    public DateTime AtUtc { get; set; }
    public string Summary { get; set; } = string.Empty;
    public int AppliedActions { get; set; }
    public int RejectedActions { get; set; }
    public List<string> Notes { get; set; } = new();
    public string? RawModelReasoning { get; set; }
}

internal sealed class SelfRepairContext
{
    public DateTime AtUtc { get; set; }
    public bool ShouldAttemptRepair { get; set; }
    public string ScopeRootPath { get; set; } = string.Empty;
    public string WorkspacePath { get; set; } = string.Empty;
    public string CurrentReplyStyle { get; set; } = string.Empty;
    public List<string> KnownChatTools { get; set; } = new();
    public List<string> RecentErrors { get; set; } = new();
    public List<string> RecentFailures { get; set; } = new();
    public List<string> CapabilityGaps { get; set; } = new();
    public List<string> RecentIncidents { get; set; } = new();
    public List<string> LearningIncidents { get; set; } = new();
    public List<SelfRepairWorkspaceFilePreview> WorkspaceFiles { get; set; } = new();
    public List<SelfRepairWorkspaceFilePreview> ScopeFiles { get; set; } = new();
}

internal sealed class SelfRepairWorkspaceFilePreview
{
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastWriteAtUtc { get; set; }
    public string Preview { get; set; } = string.Empty;
}

internal sealed class SelfRepairPlan
{
    public string Summary { get; set; } = string.Empty;
    public string? Reasoning { get; set; }
    public List<SelfRepairAction> Actions { get; set; } = new();
}

internal sealed class SelfRepairAction
{
    public string Type { get; set; } = string.Empty;
    public string? RelativePath { get; set; }
    public string? Content { get; set; }
    public string? Description { get; set; }
    public List<string>? IgnoreContains { get; set; }
    public List<string>? StartupIgnoreContains { get; set; }
    public List<string>? IncidentContains { get; set; }
}

internal sealed class BuildFromSourceResult
{
    public bool Success { get; set; }
    public string Configuration { get; set; } = "Release";
    public string Runtime { get; set; } = "linux-x64";
    public List<BuildTargetResult> Targets { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
}

internal sealed class BuildTarget
{
    public BuildTarget(string name, string projectPath, string outputPath)
    {
        Name = name;
        ProjectPath = projectPath;
        OutputPath = outputPath;
    }

    public string Name { get; }
    public string ProjectPath { get; }
    public string OutputPath { get; }
}

internal sealed class BuildTargetResult
{
    public string Name { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Message { get; set; } = string.Empty;
}

internal sealed class RestartServicesResult
{
    public bool Success { get; set; }
    public List<ServiceRestartResult> Services { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
}

internal sealed class ServiceRestartResult
{
    public string Unit { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Message { get; set; } = string.Empty;
}

internal sealed class GitPushResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string PreviousBranch { get; set; } = string.Empty;
    public string CommitMessage { get; set; } = string.Empty;
    public List<string> StagedFiles { get; set; } = new();

    public static GitPushResult Fail(string summary, string branchName = "") => new()
    {
        Success = false,
        Summary = summary,
        BranchName = branchName
    };
}

internal sealed class GitSyncResult
{
    public bool Success { get; set; }
    public bool Updated { get; set; }
    public string Summary { get; set; } = string.Empty;
    public BuildFromSourceResult? Build { get; set; }
    public RestartServicesResult? Restart { get; set; }

    public static GitSyncResult Fail(
        string summary,
        bool updated = false,
        BuildFromSourceResult? build = null,
        RestartServicesResult? restart = null) => new()
    {
        Success = false,
        Updated = updated,
        Summary = summary,
        Build = build,
        Restart = restart
    };
}

internal sealed class IncidentMemory
{
    public string Id { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public List<string> Evidence { get; set; } = new();
    public RecommendationResult? Recommendation { get; set; }
}

internal sealed class AdminPreference
{
    public string AdminId { get; set; } = string.Empty;
    public List<string> Preferences { get; set; } = new();
    public DateTime? LastUpdatedAtUtc { get; set; }
    public AdminConversationState Conversation { get; set; } = new();
}

internal sealed class FeedbackInboxItem
{
    public string? AdminId { get; set; }
    public string? ServerName { get; set; }
    public string? ActionId { get; set; }
    public string? Verdict { get; set; }
    public string? Note { get; set; }
    public string? Preference { get; set; }
}

internal sealed class DecisionInboxItem
{
    public string? AdminId { get; set; }
    public string? ActionId { get; set; }
    public string? Decision { get; set; }
    public string? Note { get; set; }
}

internal sealed class ChatInboxItem
{
    public string? RequestId { get; set; }
    public string? AdminId { get; set; }
    public string? Message { get; set; }
    public string? Channel { get; set; }
}

internal sealed class FeedbackEntry
{
    public DateTime ReceivedAtUtc { get; set; }
    public string AdminId { get; set; } = string.Empty;
    public string? ServerName { get; set; }
    public string? ActionId { get; set; }
    public string Verdict { get; set; } = "note";
    public string? Note { get; set; }
    public string? Preference { get; set; }
}

internal enum ActionStatus
{
    Pending,
    Approved,
    Rejected,
    Executed,
    AutoExecuted,
    Failed
}

internal sealed class ActionProposal
{
    public string Id { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastUpdatedAtUtc { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool RequiresApproval { get; set; }
    public ActionStatus Status { get; set; }
    public string? DecisionBy { get; set; }
    public string? DecisionNote { get; set; }
    public double? Confidence { get; set; }
    public string? AdminMessage { get; set; }
    public List<string> Evidence { get; set; } = new();
}

internal sealed class ActionExecutionRecord
{
    public string ActionId { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public DateTime ExecutedAtUtc { get; set; }
    public bool Success { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? OutputSnippet { get; set; }
}

internal sealed class ActionMetric
{
    public string ActionType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int Count { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }
    public DateTime? LastFailureAtUtc { get; set; }
}

internal sealed class LearnedActionRule
{
    public string ActionType { get; set; } = string.Empty;
    public string Guidance { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string? AdminId { get; set; }
    public DateTime LearnedAtUtc { get; set; }
}

internal sealed class RecommendationResult
{
    public string? SuggestedAction { get; set; }
    public double? Confidence { get; set; }
    public string? AdminMessage { get; set; }
    public string? Reasoning { get; set; }
}

internal sealed class ChatPlanningContext
{
    public List<string> KnownServers { get; set; } = new();
    public List<string> ServerStates { get; set; } = new();
    public string? RelevantServerName { get; set; }
    public List<string> RelevantServerMemory { get; set; } = new();
    public List<string> AdminPreferences { get; set; } = new();
    public List<string> LearnedRules { get; set; } = new();
    public List<string> PendingActions { get; set; } = new();
    public List<string> RecentActions { get; set; } = new();
    public List<string> RecentIncidents { get; set; } = new();
    public List<string> CommandKnowledge { get; set; } = new();
    public string ReplyStyleGuidance { get; set; } = string.Empty;
}

internal sealed class ChatInterpretation
{
    public string Intent { get; set; } = "unknown";
    public string? ServerName { get; set; }
    public string? ReplyText { get; set; }
    public double? Confidence { get; set; }
    public bool NeedsClarification { get; set; }
    public string? ClarificationQuestion { get; set; }
    public bool UseLastServer { get; set; }
}

internal sealed class ToolDrivenChatReply
{
    public string Reply { get; set; } = string.Empty;
    public string? LastServerName { get; set; }
    public string? PendingClarificationIntent { get; set; }
    public List<string> UsedTools { get; set; } = new();
}

internal sealed class ChatToolExecutionResult
{
    public string Content { get; set; } = "{}";
    public string? ResolvedServerName { get; set; }
    public string? PendingClarificationIntent { get; set; }

    public static ChatToolExecutionResult Success(string content, string? resolvedServerName = null) => new()
    {
        Content = content,
        ResolvedServerName = resolvedServerName
    };

    public static ChatToolExecutionResult Error(
        string message,
        string? resolvedServerName = null,
        string? pendingClarificationIntent = null) => new()
    {
        Content = JsonSerializer.Serialize(new
        {
            ok = false,
            error = message
        }, JsonOptions.Default),
        ResolvedServerName = resolvedServerName,
        PendingClarificationIntent = pendingClarificationIntent
    };
}

internal sealed class ToolServerResolution
{
    public bool Matched { get; init; }
    public ServerSnapshot? Server { get; init; }
    public ChatToolExecutionResult Result { get; init; } = ChatToolExecutionResult.Error("Tool resolution failed.");

    public static ToolServerResolution Resolved(ServerSnapshot server) => new()
    {
        Matched = true,
        Server = server,
        Result = ChatToolExecutionResult.Success("{}", server.Name)
    };

    public static ToolServerResolution NeedsClarification(ChatToolExecutionResult result, string _intent) => new()
    {
        Matched = false,
        Result = result
    };
}

internal sealed class LlmChatMessage
{
    public string Role { get; init; } = "user";
    public string? Content { get; init; }
    public string? ToolCallId { get; init; }
    public string? Name { get; init; }
    public List<LlmToolCall> ToolCalls { get; init; } = new();

    public static LlmChatMessage Simple(string role, string? content) => new()
    {
        Role = role,
        Content = content
    };

    public static LlmChatMessage Assistant(string? content, IEnumerable<LlmToolCall> toolCalls) => new()
    {
        Role = "assistant",
        Content = content,
        ToolCalls = toolCalls.ToList()
    };

    public static LlmChatMessage Tool(string toolCallId, string name, string content) => new()
    {
        Role = "tool",
        ToolCallId = toolCallId,
        Name = name,
        Content = content
    };
}

internal sealed class LlmToolCall
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
}

internal sealed class LlmToolChatResponse
{
    public string? Content { get; set; }
    public List<LlmToolCall> ToolCalls { get; set; } = new();
}

internal sealed class AdminConversationState
{
    public string? LastServerName { get; set; }
    public PendingChatClarification? PendingClarification { get; set; }
    public List<ChatHistoryEntry> History { get; set; } = new();
    public ChatTurnTrace? LastTrace { get; set; }
    public List<ChatTurnTrace> TraceHistory { get; set; } = new();
}

internal sealed class ChatTurnTrace
{
    public DateTime AtUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Intent { get; set; } = "unknown";
    public List<string> UsedTools { get; set; } = new();
    public string? Note { get; set; }
}

internal sealed class PendingChatClarification
{
    public string Intent { get; set; } = "unknown";
    public string OriginalMessage { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
}

internal sealed class ChatHistoryEntry
{
    public string Role { get; set; } = "user";
    public string Text { get; set; } = string.Empty;
    public DateTime AtUtc { get; set; }
}

internal sealed class LearningIncidentRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime AtUtc { get; set; }
    public string AdminId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Intent { get; set; } = "unknown";
    // Category tags what kind of failure this is:
    // "unknown-intent" – agent couldn't map the message to a known action
    // "tool-failure"   – LLM tool loop threw or exhausted retries
    // "capability-denial" – LLM said it couldn't do something instead of using tools
    // "processing-error" – exception while processing the chat inbox file
    public string Category { get; set; } = "unknown";
    public string Reply { get; set; } = string.Empty;
    public string? TraceSource { get; set; }
    public string? TraceIntent { get; set; }
    public List<string> TraceTools { get; set; } = new();
    public string? TraceNote { get; set; }
}

internal sealed class AdapterMessage
{
    public DateTime CreatedAtUtc { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Audience { get; set; } = "admins";
    public string? TargetAdminId { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public string? ActionId { get; set; }
    public string Message { get; set; } = string.Empty;
}



