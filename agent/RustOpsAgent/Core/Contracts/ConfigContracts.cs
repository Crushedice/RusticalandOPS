using System.Text.Json.Serialization;

namespace RustOpsAgent.Core.Contracts;

internal sealed class AgentConfig
{
    [JsonPropertyName("api")] public ApiSettings Api { get; set; } = new();
    [JsonPropertyName("memory")] public MemorySettings Memory { get; set; } = new();
    [JsonPropertyName("inbox")] public InboxSettings Inbox { get; set; } = new();
    [JsonPropertyName("outbox")] public OutboxSettings Outbox { get; set; } = new();
    [JsonPropertyName("monitor")] public MonitorSettings Monitor { get; set; } = new();
    [JsonPropertyName("gitOps")] public GitOpsSettings GitOps { get; set; } = new();
    [JsonPropertyName("llm")] public LlmSettings Llm { get; set; } = new();
    [JsonPropertyName("llmDeep")] public LlmSettings LlmDeep { get; set; } = new();
    [JsonPropertyName("autoPull")] public AutoPullSettings AutoPull { get; set; } = new();
    [JsonPropertyName("network")] public NetworkSettings Network { get; set; } = new();
    [JsonPropertyName("pluginUpdates")] public PluginUpdateSettings PluginUpdates { get; set; } = new();
    [JsonPropertyName("commandExecution")] public CommandExecutionSettings CommandExecution { get; set; } = new();
    [JsonPropertyName("cpuAffinity")] public CpuAffinitySettings CpuAffinity { get; set; } = new();
    [JsonPropertyName("consoleMonitor")] public ConsoleMonitorSettings ConsoleMonitor { get; set; } = new();
}

internal sealed class ApiSettings
{
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "http://127.0.0.1:2077";
    [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "changeme";
}

internal sealed class MemorySettings
{
    [JsonPropertyName("statePath")] public string StatePath { get; set; } = "data/agent-state.json";
    [JsonPropertyName("neoCortexRoot")] public string NeoCortexRoot { get; set; } = "data/NeoCortex";
}

internal sealed class InboxSettings
{
    [JsonPropertyName("feedbackInboxPath")] public string FeedbackInboxPath { get; set; } = "data/feedback-inbox";
    [JsonPropertyName("decisionInboxPath")] public string DecisionInboxPath { get; set; } = "data/decision-inbox";
    [JsonPropertyName("chatInboxPath")] public string ChatInboxPath { get; set; } = "data/chat-inbox";
}

internal sealed class OutboxSettings
{
    [JsonPropertyName("messageOutboxPath")] public string MessageOutboxPath { get; set; } = "data/message-outbox";
}

internal sealed class MonitorSettings
{
    [JsonPropertyName("pollSeconds")] public int PollSeconds { get; set; } = 10;
    [JsonPropertyName("incidentReviewIntervalMinutes")] public int IncidentReviewIntervalMinutes { get; set; } = 30;
    [JsonPropertyName("classifierEvolutionIntervalMinutes")] public int ClassifierEvolutionIntervalMinutes { get; set; } = 60;
}

internal sealed class CommandExecutionSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("freeMode")] public bool FreeMode { get; set; }
    [JsonPropertyName("allowList")] public List<string> AllowList { get; set; } = new() { "playerlist", "serverinfo", "bans", "oxide.plugins", "status", "version" };
    [JsonPropertyName("autoAllowAfterSuccesses")] public int AutoAllowAfterSuccesses { get; set; } = 5;
    [JsonPropertyName("requireApprovalAfterFailures")] public int RequireApprovalAfterFailures { get; set; } = 2;
}

internal sealed class GitOpsSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("repoPath")] public string RepoPath { get; set; } = "/opt/rust-manager/src";
    [JsonPropertyName("remoteName")] public string RemoteName { get; set; } = "origin";
    [JsonPropertyName("baseBranch")] public string BaseBranch { get; set; } = "main";
    [JsonPropertyName("pushBranchPrefix")] public string PushBranchPrefix { get; set; } = "agent/";
    [JsonPropertyName("allowPush")] public bool AllowPush { get; set; }
}

internal sealed class AutoPullSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("intervalMinutes")] public int IntervalMinutes { get; set; } = 60;
    [JsonPropertyName("repoPath")] public string RepoPath { get; set; } = "/opt/rust-manager/src";
    [JsonPropertyName("remoteName")] public string RemoteName { get; set; } = "origin";
    [JsonPropertyName("branchName")] public string BranchName { get; set; } = "main";
    [JsonPropertyName("buildEnabled")] public bool BuildEnabled { get; set; }
    [JsonPropertyName("buildScript")] public string BuildScript { get; set; } = "Agent-Build.sh";
    [JsonPropertyName("restartEnabled")] public bool RestartEnabled { get; set; }
    [JsonPropertyName("serviceName")] public string ServiceName { get; set; } = "rustopsagent";
}

internal sealed class NetworkSettings
{
    [JsonPropertyName("trackedInterfaces")] public List<string> TrackedInterfaces { get; set; } = new() { "eth0", "wt1", "wg1" };
}

internal sealed class PluginUpdateSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("checkIntervalMinutes")] public int CheckIntervalMinutes { get; set; } = 60;
    [JsonPropertyName("notifyAdmins")] public bool NotifyAdmins { get; set; } = true;
    [JsonPropertyName("searchUrlTemplate")] public string SearchUrlTemplate { get; set; } = "https://umod.org/plugins/search.json?query={0}&page=1&sort=title&sortdir=asc&filter={1}";
    [JsonPropertyName("searchFilter")] public string SearchFilter { get; set; } = "rust";
    [JsonPropertyName("downloadEnabled")] public bool DownloadEnabled { get; set; }
    [JsonPropertyName("stagingPath")] public string StagingPath { get; set; } = "data/plugin-staging";
}

internal sealed class CpuAffinitySettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    // Maps server name to CPU list for taskset, e.g. "0-3" or "0,1,2,3"
    [JsonPropertyName("servers")] public Dictionary<string, string> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class ConsoleMonitorSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("errorEscalationThreshold")] public int ErrorEscalationThreshold { get; set; } = 10;
    [JsonPropertyName("repeatThreshold")] public int RepeatThreshold { get; set; } = 5;
    [JsonPropertyName("sentimentAnalysisIntervalMinutes")] public int SentimentAnalysisIntervalMinutes { get; set; } = 30;
    [JsonPropertyName("maxChatMessages")] public int MaxChatMessages { get; set; } = 200;
    [JsonPropertyName("sentimentAlertThreshold")] public double SentimentAlertThreshold { get; set; } = 4.0;
    [JsonPropertyName("compileErrorSeedThreshold")] public int CompileErrorSeedThreshold { get; set; } = 5;
}

internal sealed class LlmSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("provider")] public string Provider { get; set; } = "openai-compatible";
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "http://127.0.0.1:11434/v1";
    [JsonPropertyName("model")] public string Model { get; set; } = "gpt-4o-mini";
    [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }
    [JsonPropertyName("httpReferer")] public string? HttpReferer { get; set; }
    [JsonPropertyName("appTitle")] public string? AppTitle { get; set; }
    [JsonPropertyName("useForRecommendations")] public bool UseForRecommendations { get; set; } = true;
    [JsonPropertyName("requestStrategy")] public string? RequestStrategy { get; set; } = "fallback";
    [JsonPropertyName("useChatSystemPrompt")] public bool UseChatSystemPrompt { get; set; } = true;
    [JsonPropertyName("chatSystemPrompt")] public string? ChatSystemPrompt { get; set; }
}

internal sealed class ChatInboxItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
    [JsonPropertyName("adminId")] public string AdminId { get; set; } = "admin";
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("channel")] public string? Channel { get; set; }
}

internal sealed class DecisionInboxItem
{
    [JsonPropertyName("adminId")] public string? AdminId { get; set; }
    [JsonPropertyName("actionId")] public string? ActionId { get; set; }
    [JsonPropertyName("decision")] public string? Decision { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

internal sealed class FeedbackInboxItem
{
    [JsonPropertyName("adminId")] public string? AdminId { get; set; }
    [JsonPropertyName("serverName")] public string? ServerName { get; set; }
    [JsonPropertyName("actionId")] public string? ActionId { get; set; }
    [JsonPropertyName("verdict")] public string? Verdict { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("preference")] public string? Preference { get; set; }
}

internal sealed class AdapterMessage
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("adminId")] public string? AdminId { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "chat-reply";
    [JsonPropertyName("audience")] public string Audience { get; set; } = "admins";
    [JsonPropertyName("targetAdminId")] public string? TargetAdminId { get; set; }
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = string.Empty;
    [JsonPropertyName("actionId")] public string? ActionId { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("createdAtUtc")] public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
