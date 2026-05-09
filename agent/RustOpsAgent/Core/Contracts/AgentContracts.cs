using System.Text.Json.Serialization;

namespace RustOpsAgent.Core.Contracts;

internal enum AdminIntentType
{
    Chat,
    ServerControl,
    PlayerLookup,
    RconCommand,
    FileEdit,
    StatusCheck,
    Troubleshooting,
    Clarification,
    ServerManagement,
    PlayerForcedManagement
}

internal enum ServerScopeKind
{
    Unspecified,
    Single,
    All,
    Subset
}

internal sealed record AdminIntentSlots(
    string? ServerName,
    string? PlayerName,
    string? CommandText,
    string? TimeRange,
    string? Severity,
    ServerScopeKind ScopeKind = ServerScopeKind.Unspecified,
    IReadOnlyList<string>? ServerNames = null,
    string? ConfigKey = null,
    string? ConfigValue = null);

internal sealed record AdminIntentRoute(
    AdminIntentType Intent,
    AdminIntentSlots Slots,
    double Confidence,
    bool NeedsClarification,
    string? ClarificationQuestion,
    string? TargetRef,
    WorkflowMemoryContext? PlanningMemoryContext = null,
    string ClassifierSource = "heuristic",
    bool LlmAttempted = false,
    bool LlmSucceeded = false,
    IReadOnlyList<AdminIntentStep>? Steps = null);

// One step of a multi-step admin request. The first step is also reflected in the
// top-level Intent/Slots/TargetRef of the parent AdminIntentRoute.
internal sealed record AdminIntentStep(
    AdminIntentType Intent,
    AdminIntentSlots Slots,
    string? TargetRef);

internal sealed record ToolExecutionContext(
    string AdminId,
    string Message,
    AdminIntentRoute Route,
    ConversationSelectionState SelectionState,
    DateTime UtcNow,
    WorkflowMemoryContext? PlanningMemoryContext = null,
    WorkflowMemoryContext? ExecutionMemoryContext = null);

internal sealed record ToolExecutionResult(
    bool Success,
    string Message,
    string? SelectedServer = null,
    bool MutatedState = false,
    string? ErrorCode = null,
    object? Payload = null,
    IReadOnlyList<string>? SelectedServers = null,
    ServerScopeKind ScopeKind = ServerScopeKind.Unspecified);

internal sealed record ComposedReply(
    string Message,
    string Type = "response-compose",
    bool LlmAttempted = false,
    bool LlmSucceeded = false,
    string Source = "template",
    string? ResponsePreview = null);

internal sealed record ToolEligibilityRule(AdminIntentType Intent, string ToolName);

internal interface IToolHandler
{
    string Name { get; }
    IReadOnlyCollection<AdminIntentType> EligibleIntents { get; }
    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken);
}

internal interface IIntentClassifier
{
    Task<AdminIntentRoute> ClassifyAsync(
        string message,
        ConversationSelectionState state,
        IReadOnlyList<string> knownServers,
        CancellationToken cancellationToken);
}

internal interface IActionExecutor
{
    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken);
}

internal interface IResponseComposer
{
    Task<ComposedReply> ComposeAsync(ToolExecutionContext context, ToolExecutionResult result, CancellationToken cancellationToken);
}

internal sealed class ConversationSelectionState
{
    [JsonPropertyName("adminId")] public string AdminId { get; set; } = string.Empty;
    [JsonPropertyName("lastServerName")] public string? LastServerName { get; set; }
    [JsonPropertyName("lastIntent")] public string? LastIntent { get; set; }
    [JsonPropertyName("lastScopeKind")] public ServerScopeKind LastScopeKind { get; set; } = ServerScopeKind.Unspecified;
    [JsonPropertyName("lastResolvedServers")] public List<string> LastResolvedServers { get; set; } = new();
    [JsonPropertyName("lastCommandText")] public string? LastCommandText { get; set; }
    [JsonPropertyName("lastTimeRange")] public string? LastTimeRange { get; set; }
    [JsonPropertyName("lastUserMessageSummary")] public string? LastUserMessageSummary { get; set; }
    [JsonPropertyName("pendingClarification")] public ConversationPendingClarification? PendingClarification { get; set; }
    [JsonPropertyName("recentMessages")] public List<ConversationMessage> RecentMessages { get; set; } = new();
    [JsonPropertyName("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class ConversationMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("atUtc")] public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class ConversationPendingClarification
{
    [JsonPropertyName("intent")] public string? Intent { get; set; }
    [JsonPropertyName("targetRef")] public string? TargetRef { get; set; }
    [JsonPropertyName("question")] public string? Question { get; set; }
    [JsonPropertyName("scopeKind")] public ServerScopeKind ScopeKind { get; set; } = ServerScopeKind.Unspecified;
    [JsonPropertyName("askedAtUtc")] public DateTime AskedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed record AggregateStatusServerResult(
    string Server,
    string State,
    bool Online,
    bool CheckSucceeded,
    string? Error = null,
    IReadOnlyList<string>? RecentErrors = null);

internal sealed record AggregateStatusPayload(
    ServerScopeKind ScopeKind,
    IReadOnlyList<string> TargetServers,
    int OnlineCount,
    IReadOnlyList<string> OfflineServers,
    IReadOnlyList<string> FailedServers,
    IReadOnlyList<AggregateStatusServerResult> Servers);
