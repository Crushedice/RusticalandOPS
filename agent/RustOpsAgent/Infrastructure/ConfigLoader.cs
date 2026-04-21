using System.Text.Json;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Infrastructure;

internal static class ConfigLoader
{
    public static AgentConfig Load(string configPath)
    {
        var raw = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<AgentConfig>(raw, JsonDefaults.Default)
                     ?? throw new InvalidOperationException("Failed to parse agent config.");

        var root = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? AppContext.BaseDirectory;

        config.Api.BaseUrl =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_API_BASE_URL")
            ?? RustOpsEnv.ResolvePlaceholders(config.Api.BaseUrl);
        config.Api.ApiKey =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTMGR_API_KEY", "RUSTOPS_API_KEY")
            ?? RustOpsEnv.ResolvePlaceholders(config.Api.ApiKey);

        config.Memory.StatePath =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_AGENT_STATE_PATH")
            ?? config.Memory.StatePath;
        config.Memory.NeoCortexRoot =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_AGENT_NEOCORTEX_ROOT")
            ?? config.Memory.NeoCortexRoot;
        config.Inbox.FeedbackInboxPath =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_FEEDBACK_INBOX_PATH")
            ?? config.Inbox.FeedbackInboxPath;
        config.Inbox.DecisionInboxPath =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_DECISION_INBOX_PATH")
            ?? config.Inbox.DecisionInboxPath;
        config.Inbox.ChatInboxPath =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_CHAT_INBOX_PATH")
            ?? config.Inbox.ChatInboxPath;
        config.Outbox.MessageOutboxPath =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_MESSAGE_OUTBOX_PATH")
            ?? config.Outbox.MessageOutboxPath;

        config.Memory.StatePath = RustOpsEnv.ResolvePlaceholders(config.Memory.StatePath);
        config.Memory.NeoCortexRoot = RustOpsEnv.ResolvePlaceholders(config.Memory.NeoCortexRoot);
        config.Inbox.FeedbackInboxPath = RustOpsEnv.ResolvePlaceholders(config.Inbox.FeedbackInboxPath);
        config.Inbox.DecisionInboxPath = RustOpsEnv.ResolvePlaceholders(config.Inbox.DecisionInboxPath);
        config.Inbox.ChatInboxPath = RustOpsEnv.ResolvePlaceholders(config.Inbox.ChatInboxPath);
        config.Outbox.MessageOutboxPath = RustOpsEnv.ResolvePlaceholders(config.Outbox.MessageOutboxPath);

        config.Llm.Provider =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_PROVIDER")
            ?? RustOpsEnv.ResolvePlaceholders(config.Llm.Provider);
        config.Llm.BaseUrl =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_BASE_URL")
            ?? RustOpsEnv.ResolvePlaceholders(config.Llm.BaseUrl);
        config.Llm.Model =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_MODEL")
            ?? RustOpsEnv.ResolvePlaceholders(config.Llm.Model);
        config.Llm.ApiKey =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_API_KEY")
            ?? RustOpsEnv.ResolvePlaceholders(config.Llm.ApiKey ?? string.Empty);
        config.Llm.HttpReferer =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_HTTP_REFERER")
            ?? RustOpsEnv.ResolvePlaceholders(config.Llm.HttpReferer ?? string.Empty);
        config.Llm.AppTitle =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_APP_TITLE")
            ?? RustOpsEnv.ResolvePlaceholders(config.Llm.AppTitle ?? string.Empty);

        config.Memory.StatePath = ResolvePath(config.Memory.StatePath, root);
        config.Memory.NeoCortexRoot = ResolvePath(config.Memory.NeoCortexRoot, root);
        config.Inbox.ChatInboxPath = ResolvePath(config.Inbox.ChatInboxPath, root);
        config.Inbox.DecisionInboxPath = ResolvePath(config.Inbox.DecisionInboxPath, root);
        config.Inbox.FeedbackInboxPath = ResolvePath(config.Inbox.FeedbackInboxPath, root);
        config.Outbox.MessageOutboxPath = ResolvePath(config.Outbox.MessageOutboxPath, root);

        if (!config.GitOps.PushBranchPrefix.StartsWith("agent/", StringComparison.OrdinalIgnoreCase))
        {
            config.GitOps.PushBranchPrefix = "agent/";
        }

        ValidateResolvedConfig(config);

        return config;
    }

    private static string ResolvePath(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return root;
        }

        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));
    }

    private static void ValidateResolvedConfig(AgentConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Api.BaseUrl) || RustOpsEnv.HasUnresolvedPlaceholder(config.Api.BaseUrl))
            throw new InvalidOperationException("api.baseUrl is missing or unresolved.");
        if (!Uri.TryCreate(config.Api.BaseUrl, UriKind.Absolute, out var apiUri) || (apiUri.Scheme != Uri.UriSchemeHttp && apiUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("api.baseUrl must be an absolute http/https URL.");
        if (string.IsNullOrWhiteSpace(config.Api.ApiKey) || RustOpsEnv.HasUnresolvedPlaceholder(config.Api.ApiKey))
            throw new InvalidOperationException("api.apiKey is missing or unresolved.");

        if (string.IsNullOrWhiteSpace(config.Memory.StatePath) || RustOpsEnv.HasUnresolvedPlaceholder(config.Memory.StatePath))
            throw new InvalidOperationException("memory.statePath is required.");
        if (string.IsNullOrWhiteSpace(config.Memory.NeoCortexRoot) || RustOpsEnv.HasUnresolvedPlaceholder(config.Memory.NeoCortexRoot))
            throw new InvalidOperationException("memory.neoCortexRoot is required.");
    }
}
