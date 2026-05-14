using System.Text.Json.Serialization;

namespace RustOpsAgent.Core.Contracts;

internal sealed class ActiveOperationalState
{
    [JsonPropertyName("runtimeStatus")] public RuntimeStatus RuntimeStatus { get; set; } = new();
    [JsonPropertyName("recentActions")] public List<ActionRecord> RecentActions { get; set; } = new();
    [JsonPropertyName("llmInteractions")] public List<LlmInteractionRecord> LlmInteractions { get; set; } = new();
}

internal sealed class RuntimeStatus
{
    [JsonPropertyName("llmEnabled")] public bool LlmEnabled { get; set; }
    [JsonPropertyName("llmProvider")] public string? LlmProvider { get; set; }
    [JsonPropertyName("lastLlmInteractionAtUtc")] public DateTime? LastLlmInteractionAtUtc { get; set; }
    [JsonPropertyName("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class ActionRecord
{
    [JsonPropertyName("timestampUtc")] public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("intent")] public string Intent { get; set; } = string.Empty;
    [JsonPropertyName("result")] public string Result { get; set; } = string.Empty;
    [JsonPropertyName("serverName")] public string? ServerName { get; set; }
}

internal sealed class LlmInteractionRecord
{
    [JsonPropertyName("atUtc")] public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("type")] public string Type { get; set; } = "intent-routing";
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("context")] public string? Context { get; set; }
    [JsonPropertyName("responsePreview")] public string? ResponsePreview { get; set; }
}

internal sealed class SelectionSessionState
{
    [JsonPropertyName("conversations")] public List<ConversationSelectionState> Conversations { get; set; } = new();
}

internal sealed class LogKnowledgeState
{
    [JsonPropertyName("ignorePatterns")] public List<string> IgnorePatterns { get; set; } = new();
    [JsonPropertyName("importanceRules")] public List<string> ImportanceRules { get; set; } = new();
    [JsonPropertyName("recentEntries")] public List<LogObservation> RecentEntries { get; set; } = new();
    [JsonPropertyName("lastDigestDateUtc")] public DateTime? LastDigestDateUtc { get; set; }
}

internal sealed class LogObservation
{
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = string.Empty;
    [JsonPropertyName("line")] public string Line { get; set; } = string.Empty;
    [JsonPropertyName("importance")] public int Importance { get; set; }
    [JsonPropertyName("capturedAtUtc")] public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class IgnoreFeedbackState
{
    [JsonPropertyName("partialMatches")] public List<string> PartialMatches { get; set; } = new();
}

internal sealed class DomainCacheState
{
    [JsonPropertyName("serverCache")] public Dictionary<string, string> ServerCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class EvolutionIncidentRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("request")] public string Request { get; set; } = string.Empty;
    [JsonPropertyName("intendedOutcome")] public string IntendedOutcome { get; set; } = string.Empty;
    [JsonPropertyName("failureReason")] public string FailureReason { get; set; } = string.Empty;
    [JsonPropertyName("missingCapability")] public string MissingCapability { get; set; } = string.Empty;
    [JsonPropertyName("recurrencePrevention")] public string RecurrencePrevention { get; set; } = string.Empty;
    [JsonPropertyName("classification")] public string Classification { get; set; } = "unknown";
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("resolved")] public bool Resolved { get; set; }
}

internal sealed class EvolutionReviewResult
{
    public List<EvolutionIncidentRecord> OpenIncidents { get; set; } = new();
    public List<EvolutionIncidentRecord> RecentlyResolved { get; set; } = new();
}

internal sealed class CommandPolicyState
{
    [JsonPropertyName("commands")] public Dictionary<string, CommandRecord> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class CommandRecord
{
    [JsonPropertyName("command")] public string Command { get; set; } = string.Empty;
    [JsonPropertyName("successCount")] public int SuccessCount { get; set; }
    [JsonPropertyName("failCount")] public int FailCount { get; set; }
    [JsonPropertyName("autoAllowed")] public bool AutoAllowed { get; set; }
    [JsonPropertyName("requiresApproval")] public bool RequiresApproval { get; set; }
    [JsonPropertyName("lastUsedUtc")] public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
}

internal interface IEvolutionStore
{
    Task RecordIncidentAsync(EvolutionIncidentRecord incident, CancellationToken cancellationToken);
    Task<EvolutionReviewResult> ReviewAsync(CancellationToken cancellationToken);
}

// Console monitor types — track errors, warnings, repeating messages per server.
internal sealed class ConsoleMonitorState
{
    [JsonPropertyName("servers")] public Dictionary<string, ServerConsoleState> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class ServerConsoleState
{
    [JsonPropertyName("recentErrors")] public List<ConsoleErrorEntry> RecentErrors { get; set; } = new();
    [JsonPropertyName("repeatingMessages")] public Dictionary<string, int> RepeatingMessages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("lastAlertAtUtc")] public DateTime? LastAlertAtUtc { get; set; }
    [JsonPropertyName("errorCountSinceLastAlert")] public int ErrorCountSinceLastAlert { get; set; }
    [JsonPropertyName("totalErrorsIngested")] public int TotalErrorsIngested { get; set; }
}

internal sealed class ConsoleErrorEntry
{
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("sampleLine")] public string SampleLine { get; set; } = string.Empty;
    [JsonPropertyName("count")] public int Count { get; set; } = 1;
    [JsonPropertyName("firstSeenAtUtc")] public DateTime FirstSeenAtUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("lastSeenAtUtc")] public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("category")] public string Category { get; set; } = "error";
    [JsonPropertyName("reviewPromptedAtUtc")] public DateTime? ReviewPromptedAtUtc { get; set; }
}

// Player chat knowledge — store player messages and derived sentiment.
internal sealed class PlayerChatKnowledge
{
    [JsonPropertyName("recentMessages")] public List<PlayerChatEntry> RecentMessages { get; set; } = new();
    [JsonPropertyName("sentimentScore")] public double? SentimentScore { get; set; }
    [JsonPropertyName("sentimentLabel")] public string? SentimentLabel { get; set; }
    [JsonPropertyName("sentimentSummary")] public string? SentimentSummary { get; set; }
    [JsonPropertyName("keyThemes")] public List<string> KeyThemes { get; set; } = new();
    [JsonPropertyName("constructiveFeedback")] public List<string> ConstructiveFeedback { get; set; } = new();
    [JsonPropertyName("analysedAtUtc")] public DateTime? AnalysedAtUtc { get; set; }
    [JsonPropertyName("dailySummaries")] public List<DailyChatSummary> DailySummaries { get; set; } = new();
    [JsonPropertyName("perServerVolume")] public Dictionary<string, ServerChatVolume> PerServerVolume { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("adminCalls")] public List<AdminCallEvent> AdminCalls { get; set; } = new();
    [JsonPropertyName("lastDailySummaryDateUtc")] public DateTime? LastDailySummaryDateUtc { get; set; }
}

internal sealed class DailyChatSummary
{
    [JsonPropertyName("dateUtc")] public DateTime DateUtc { get; set; } = DateTime.UtcNow.Date.ToUniversalTime();
    [JsonPropertyName("messageCount")] public int MessageCount { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("keyTopics")] public List<string> KeyTopics { get; set; } = new();
}

internal sealed class ServerChatVolume
{
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = string.Empty;
    [JsonPropertyName("todayMessageCount")] public int TodayMessageCount { get; set; }
    [JsonPropertyName("totalMessageCount")] public int TotalMessageCount { get; set; }
    [JsonPropertyName("lastMessageAtUtc")] public DateTime? LastMessageAtUtc { get; set; }
    [JsonPropertyName("activePlayerCount")] public int ActivePlayerCount { get; set; }
}

internal sealed class AdminCallEvent
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = string.Empty;
    [JsonPropertyName("playerName")] public string PlayerName { get; set; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("callType")] public string CallType { get; set; } = string.Empty;
    [JsonPropertyName("capturedAtUtc")] public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("acknowledged")] public bool Acknowledged { get; set; }
}

internal sealed class PlayerChatEntry
{
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = string.Empty;
    [JsonPropertyName("playerName")] public string PlayerName { get; set; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("capturedAtUtc")] public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("isAdminCall")] public bool IsAdminCall { get; set; }
}

// Classifier learning types — persist admin corrections and synthesized routing rules.

internal sealed class MisclassificationRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("originalMessage")] public string OriginalMessage { get; set; } = string.Empty;
    [JsonPropertyName("agentReply")] public string AgentReply { get; set; } = string.Empty;
    [JsonPropertyName("detectedIntent")] public string DetectedIntent { get; set; } = string.Empty;
    [JsonPropertyName("feedbackNote")] public string? FeedbackNote { get; set; }
    [JsonPropertyName("adminId")] public string? AdminId { get; set; }
    [JsonPropertyName("capturedAtUtc")] public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("processed")] public bool Processed { get; set; }
}

// Scheduler — admin-defined deferred and recurring tasks (e.g. "wipe every Friday").
internal sealed class ScheduledTaskState
{
    [JsonPropertyName("tasks")] public List<ScheduledTask> Tasks { get; set; } = new();
}

internal sealed class ScheduledTask
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("adminId")] public string AdminId { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("originalMessage")] public string OriginalMessage { get; set; } = string.Empty;

    // Cadence: "once" | "daily" | "weekly" | "interval"
    [JsonPropertyName("cadence")] public string Cadence { get; set; } = "once";
    [JsonPropertyName("dayOfWeek")] public string? DayOfWeek { get; set; }     // weekly: monday..sunday
    [JsonPropertyName("timeOfDay")] public string? TimeOfDay { get; set; }     // "HH:mm" UTC
    [JsonPropertyName("intervalMinutes")] public int? IntervalMinutes { get; set; } // interval cadence

    [JsonPropertyName("steps")] public List<ScheduledStep> Steps { get; set; } = new();
    [JsonPropertyName("randomizeSeed")] public bool RandomizeSeed { get; set; }

    [JsonPropertyName("createdAtUtc")] public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("nextFireAtUtc")] public DateTime? NextFireAtUtc { get; set; }
    [JsonPropertyName("lastFiredAtUtc")] public DateTime? LastFiredAtUtc { get; set; }
    [JsonPropertyName("fireCount")] public int FireCount { get; set; }
    [JsonPropertyName("paused")] public bool Paused { get; set; }
    [JsonPropertyName("completed")] public bool Completed { get; set; } // once-tasks after firing
    [JsonPropertyName("lastResult")] public string? LastResult { get; set; }
}

internal sealed class ScheduledStep
{
    [JsonPropertyName("intent")] public string Intent { get; set; } = string.Empty;
    [JsonPropertyName("targetRef")] public string? TargetRef { get; set; }
    [JsonPropertyName("serverName")] public string? ServerName { get; set; }
    [JsonPropertyName("commandText")] public string? CommandText { get; set; }
    [JsonPropertyName("configKey")] public string? ConfigKey { get; set; }
    [JsonPropertyName("configValue")] public string? ConfigValue { get; set; }
}

internal sealed class LearnedClassifierRule
{
    [JsonPropertyName("rule")] public string Rule { get; set; } = string.Empty;
    [JsonPropertyName("rationale")] public string Rationale { get; set; } = string.Empty;
    [JsonPropertyName("learnedAtUtc")] public DateTime LearnedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class ClassifierKnowledgeState
{
    [JsonPropertyName("learnedRules")] public List<LearnedClassifierRule> LearnedRules { get; set; } = new();
    [JsonPropertyName("pendingMisclassifications")] public List<MisclassificationRecord> PendingMisclassifications { get; set; } = new();
    [JsonPropertyName("lastEvolutionAtUtc")] public DateTime? LastEvolutionAtUtc { get; set; }
    [JsonPropertyName("evolutionCycleCount")] public int EvolutionCycleCount { get; set; }
}
