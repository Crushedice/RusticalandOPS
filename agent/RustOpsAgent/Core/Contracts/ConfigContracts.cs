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

internal sealed class LlmSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("provider")] public string Provider { get; set; } = "openai-compatible";
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "http://127.0.0.1:11434/v1";
    [JsonPropertyName("model")] public string Model { get; set; } = "gpt-4o-mini";
    [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }
    [JsonPropertyName("httpReferer")] public string? HttpReferer { get; set; }
    [JsonPropertyName("appTitle")] public string? AppTitle { get; set; }
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
