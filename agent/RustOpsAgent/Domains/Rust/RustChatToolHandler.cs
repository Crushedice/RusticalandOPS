using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Infrastructure;
using RustOpsAgent.Infrastructure.Memory;

namespace RustOpsAgent.Domains.Rust;

internal sealed class RustChatToolHandler : IToolHandler
{
    private readonly NeoCortexStore _memory;
    private readonly AutoPullService? _autoPull;

    public RustChatToolHandler(NeoCortexStore memory, AutoPullService? autoPull = null)
    {
        _memory = memory;
        _autoPull = autoPull;
    }

    public string Name => "rust.chat.reply";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.Chat, AdminIntentType.Clarification };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Route.Intent == AdminIntentType.Clarification)
        {
            var question = context.Route.ClarificationQuestion
                ?? "Could you clarify what you need? Which server and what action?";
            return new ToolExecutionResult(true, question, context.SelectionState.LastServerName);
        }

        // Detect and handle git pull/rebuild operations
        var messageLowered = context.Message.ToLowerInvariant();
        if (IsGitPullRebuildRequest(messageLowered) && _autoPull != null)
        {
            try
            {
                var status = await _autoPull.TriggerAsync(cancellationToken);
                var resultMessage = $"Pull/rebuild: {status.Phase}. {status.Output}";
                return new ToolExecutionResult(
                    status.Phase != "error",
                    resultMessage,
                    null,
                    Payload: new { autoPullPhase = status.Phase, autoPullOutput = status.Output, autoPullError = status.Error });
            }
            catch (Exception ex)
            {
                return new ToolExecutionResult(
                    false,
                    $"Pull/rebuild failed: {ex.Message}",
                    null,
                    Payload: new { autoPullError = ex.Message });
            }
        }

        object? payload = null;
        try
        {
            var ops = _memory.LoadOperations();
            var logs = _memory.LoadLogs();

            var recentActions = ops.RecentActions
                .TakeLast(5)
                .Select(a => $"{a.Intent} on {a.ServerName ?? "?"}: {a.Result} ({FormatAge(a.TimestampUtc)})")
                .ToList();

            var highImportanceLogs = logs.RecentEntries
                .Where(e => e.Importance >= 3)
                .TakeLast(4)
                .Select(e => $"[{e.ServerName}] {e.Line}")
                .ToList();

            var openIncidents = 0;
            try
            {
                var review = _memory.ReviewAsync(CancellationToken.None).GetAwaiter().GetResult();
                openIncidents = review.OpenIncidents.Count;
            }
            catch { /* non-critical */ }

            payload = new
            {
                recentActions,
                highImportanceLogs,
                openIncidents,
                lastServer = context.SelectionState.LastServerName ?? "none",
                lastIntent = context.SelectionState.LastIntent ?? "none",
                llmEnabled = ops.RuntimeStatus?.LlmEnabled ?? false
            };
        }
        catch
        {
            // Compose with no payload — LLM will still answer.
        }

        return new ToolExecutionResult(
            true,
            "Ready.",
            null,  // Chat operations are not server-specific
            Payload: payload);
    }

    private static bool IsGitPullRebuildRequest(string loweredMessage) =>
        (loweredMessage.Contains("pull", StringComparison.Ordinal) && loweredMessage.Contains("main", StringComparison.Ordinal)) ||
        (loweredMessage.Contains("git", StringComparison.Ordinal) && loweredMessage.Contains("pull", StringComparison.Ordinal)) ||
        (loweredMessage.Contains("rebuild", StringComparison.Ordinal) && (loweredMessage.Contains("pull", StringComparison.Ordinal) || loweredMessage.Contains("code", StringComparison.Ordinal) || loweredMessage.Contains("agent", StringComparison.Ordinal)));

    private static string FormatAge(DateTime ts)
    {
        var age = DateTime.UtcNow - ts;
        if (age.TotalMinutes < 2) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}
