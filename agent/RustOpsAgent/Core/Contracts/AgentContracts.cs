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
    Clarification
}

internal sealed record AdminIntentSlots(
    string? ServerName,
    string? PlayerName,
    string? CommandText,
    string? TimeRange,
    string? Severity);

internal sealed record AdminIntentRoute(
    AdminIntentType Intent,
    AdminIntentSlots Slots,
    double Confidence,
    bool NeedsClarification,
    string? ClarificationQuestion,
    string? TargetRef);

internal sealed record ToolExecutionContext(
    string AdminId,
    string Message,
    AdminIntentRoute Route,
    ConversationSelectionState SelectionState,
    DateTime UtcNow);

internal sealed record ToolExecutionResult(
    bool Success,
    string Message,
    string? SelectedServer = null,
    bool MutatedState = false,
    string? ErrorCode = null,
    object? Payload = null);

internal sealed record ToolEligibilityRule(AdminIntentType Intent, string ToolName);

internal interface IToolHandler
{
    string Name { get; }
    IReadOnlyCollection<AdminIntentType> EligibleIntents { get; }
    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken);
}

internal interface IIntentClassifier
{
    Task<AdminIntentRoute> ClassifyAsync(string message, ConversationSelectionState state, CancellationToken cancellationToken);
}

internal interface IActionExecutor
{
    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken);
}

internal interface IResponseComposer
{
    string Compose(ToolExecutionContext context, ToolExecutionResult result);
}

internal sealed class ConversationSelectionState
{
    [JsonPropertyName("adminId")] public string AdminId { get; set; } = string.Empty;
    [JsonPropertyName("lastServerName")] public string? LastServerName { get; set; }
    [JsonPropertyName("lastIntent")] public string? LastIntent { get; set; }
    [JsonPropertyName("lastCommandText")] public string? LastCommandText { get; set; }
    [JsonPropertyName("lastTimeRange")] public string? LastTimeRange { get; set; }
    [JsonPropertyName("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}