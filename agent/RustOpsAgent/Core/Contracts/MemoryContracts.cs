using System.Text.Json.Serialization;

namespace RustOpsAgent.Core.Contracts;

internal sealed class ActiveOperationalState
{
    [JsonPropertyName("runtimeStatus")] public RuntimeStatus RuntimeStatus { get; set; } = new();
    [JsonPropertyName("recentActions")] public List<ActionRecord> RecentActions { get; set; } = new();
}

internal sealed class RuntimeStatus
{
    [JsonPropertyName("llmEnabled")] public bool LlmEnabled { get; set; }
    [JsonPropertyName("llmProvider")] public string? LlmProvider { get; set; }
    [JsonPropertyName("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class ActionRecord
{
    [JsonPropertyName("timestampUtc")] public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("intent")] public string Intent { get; set; } = string.Empty;
    [JsonPropertyName("result")] public string Result { get; set; } = string.Empty;
    [JsonPropertyName("serverName")] public string? ServerName { get; set; }
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

internal interface IEvolutionStore
{
    Task RecordIncidentAsync(EvolutionIncidentRecord incident, CancellationToken cancellationToken);
    Task<EvolutionReviewResult> ReviewAsync(CancellationToken cancellationToken);
}