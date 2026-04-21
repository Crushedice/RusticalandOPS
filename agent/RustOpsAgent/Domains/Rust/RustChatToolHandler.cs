using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Domains.Rust;

internal sealed class RustChatToolHandler : IToolHandler
{
    public string Name => "rust.chat.reply";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.Chat, AdminIntentType.Clarification, AdminIntentType.FileEdit };

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var reply = context.Route.Intent switch
        {
            AdminIntentType.FileEdit => "File edits are gated through the evolution workflow and GitOps branch policy (agent/*). Please specify the intended change and target path.",
            AdminIntentType.Clarification => context.Route.ClarificationQuestion ?? "Please clarify what action you want and which server it targets.",
            _ => "Ready. Ask for status, server control, player lookup, RCON command, logs, plugins, or focused network inspection."
        };

        return Task.FromResult(new ToolExecutionResult(true, reply, context.SelectionState.LastServerName));
    }
}