namespace RusticalandOPS.Api.Models.Dashboard;

using System.Text.Json.Serialization;

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
    public string SemanticMemoryDbPath { get; set; } = string.Empty;
    public string PluginDbPath { get; set; } = string.Empty;
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
    [JsonPropertyName("plugins")] public AgentSettingsPluginsView? Plugins { get; set; }
    [JsonPropertyName("inbox")] public AgentSettingsInboxView? Inbox { get; set; }
    [JsonPropertyName("outbox")] public AgentSettingsOutboxView? Outbox { get; set; }
    [JsonPropertyName("monitor")] public AgentSettingsMonitorView? Monitor { get; set; }
    [JsonPropertyName("commandExecution")] public AgentSettingsCommandExecutionView? CommandExecution { get; set; }
    [JsonPropertyName("llm")] public AgentSettingsLlmView? Llm { get; set; }
    [JsonPropertyName("llmCompose")] public AgentSettingsLlmView? LlmCompose { get; set; }
    [JsonPropertyName("llmDeep")] public AgentSettingsLlmView? LlmDeep { get; set; }
    [JsonPropertyName("ollama")] public AgentSettingsLlmView? LegacyOllama { get; set; }
}

public sealed class AgentSettingsMemoryView
{
    [JsonPropertyName("statePath")] public string? StatePath { get; set; }
    [JsonPropertyName("neoCortexRoot")] public string? NeoCortexRoot { get; set; }
    [JsonPropertyName("databasePath")] public string? DatabasePath { get; set; }
}

public sealed class AgentSettingsPluginsView
{
    [JsonPropertyName("referenceIndexDatabasePath")] public string? ReferenceIndexDatabasePath { get; set; }
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
