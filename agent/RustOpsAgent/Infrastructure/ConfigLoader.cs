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
        config.Inbox.LogInboxPath =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LOG_INBOX_PATH")
            ?? config.Inbox.LogInboxPath;
        config.Outbox.MessageOutboxPath =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_MESSAGE_OUTBOX_PATH")
            ?? config.Outbox.MessageOutboxPath;

        config.Memory.StatePath = RustOpsEnv.ResolvePlaceholders(config.Memory.StatePath);
        config.Memory.NeoCortexRoot = RustOpsEnv.ResolvePlaceholders(config.Memory.NeoCortexRoot);
        config.Inbox.FeedbackInboxPath = RustOpsEnv.ResolvePlaceholders(config.Inbox.FeedbackInboxPath);
        config.Inbox.DecisionInboxPath = RustOpsEnv.ResolvePlaceholders(config.Inbox.DecisionInboxPath);
        config.Inbox.ChatInboxPath = RustOpsEnv.ResolvePlaceholders(config.Inbox.ChatInboxPath);
        config.Inbox.LogInboxPath = RustOpsEnv.ResolvePlaceholders(config.Inbox.LogInboxPath);
        config.Outbox.MessageOutboxPath = RustOpsEnv.ResolvePlaceholders(config.Outbox.MessageOutboxPath);

        ApplyConnectorEnvironment(config.Integrations.Autotask, "OPS_AUTOTASK");
        ApplyConnectorEnvironment(config.Integrations.DattoRmm, "OPS_DATTO_RMM");

        config.Llm.Provider =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_PROVIDER", "RUSTOPS_OLLAMA_PROVIDER")
            ?? RustOpsEnv.ResolvePlaceholders(config.Llm.Provider);
        config.Llm.Enabled =
            RustOpsEnv.GetBoolean("RUSTOPS_LLM_ENABLED",
                RustOpsEnv.GetBoolean("RUSTOPS_OLLAMA_ENABLED", config.Llm.Enabled));
        config.Llm.BaseUrl =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_BASE_URL", "RUSTOPS_OLLAMA_BASE_URL")
            ?? RustOpsEnv.ResolvePlaceholders(config.Llm.BaseUrl);
        config.Llm.Model =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_MODEL", "RUSTOPS_OLLAMA_MODEL")
            ?? RustOpsEnv.ResolvePlaceholders(config.Llm.Model);
        config.Llm.ApiKey =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_API_KEY", "RUSTOPS_OLLAMA_API_KEY")
            ?? RustOpsEnv.ResolvePlaceholders(config.Llm.ApiKey ?? string.Empty);
        config.Llm.HttpReferer =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_HTTP_REFERER", "RUSTOPS_OLLAMA_HTTP_REFERER")
            ?? RustOpsEnv.ResolvePlaceholders(config.Llm.HttpReferer ?? string.Empty);
        config.Llm.AppTitle =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_APP_TITLE", "RUSTOPS_OLLAMA_APP_TITLE")
            ?? RustOpsEnv.ResolvePlaceholders(config.Llm.AppTitle ?? string.Empty);
        config.Llm.UseForRecommendations =
            RustOpsEnv.GetBoolean(
                "RUSTOPS_LLM_USE_FOR_RECOMMENDATIONS",
                RustOpsEnv.GetBoolean("RUSTOPS_OLLAMA_USE_FOR_RECOMMENDATIONS", config.Llm.UseForRecommendations));
        config.Llm.RequestStrategy =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_REQUEST_STRATEGY", "RUSTOPS_OLLAMA_REQUEST_STRATEGY")
            ?? RustOpsEnv.ResolvePlaceholders(config.Llm.RequestStrategy ?? string.Empty);
        config.Llm.UseChatSystemPrompt =
            RustOpsEnv.GetBoolean(
                "RUSTOPS_LLM_USE_CHAT_SYSTEM_PROMPT",
                RustOpsEnv.GetBoolean("RUSTOPS_OLLAMA_USE_CHAT_SYSTEM_PROMPT", config.Llm.UseChatSystemPrompt));
        config.Llm.ChatSystemPrompt =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_CHAT_SYSTEM_PROMPT", "RUSTOPS_OLLAMA_CHAT_SYSTEM_PROMPT")
            ?? RustOpsEnv.ResolvePlaceholders(config.Llm.ChatSystemPrompt ?? string.Empty);

        config.Memory.StatePath = ResolvePath(config.Memory.StatePath, root);
        config.Memory.NeoCortexRoot = ResolvePath(config.Memory.NeoCortexRoot, root);
        config.Inbox.ChatInboxPath = ResolvePath(config.Inbox.ChatInboxPath, root);
        config.Inbox.LogInboxPath = ResolvePath(config.Inbox.LogInboxPath, root);
        config.Inbox.DecisionInboxPath = ResolvePath(config.Inbox.DecisionInboxPath, root);
        config.Inbox.FeedbackInboxPath = ResolvePath(config.Inbox.FeedbackInboxPath, root);
        config.Outbox.MessageOutboxPath = ResolvePath(config.Outbox.MessageOutboxPath, root);

        // Keep API settings optional for independent mode.
        if (string.IsNullOrWhiteSpace(config.Api.BaseUrl) || RustOpsEnv.HasUnresolvedPlaceholder(config.Api.BaseUrl))
        {
            config.Api.BaseUrl = "http://127.0.0.1:2077";
        }

        if (string.IsNullOrWhiteSpace(config.Api.ApiKey) || RustOpsEnv.HasUnresolvedPlaceholder(config.Api.ApiKey))
        {
            config.Api.ApiKey = "local-dev";
        }

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
        if (string.IsNullOrWhiteSpace(config.Memory.StatePath) || RustOpsEnv.HasUnresolvedPlaceholder(config.Memory.StatePath))
            throw new InvalidOperationException("memory.statePath is required.");
        if (string.IsNullOrWhiteSpace(config.Memory.NeoCortexRoot) || RustOpsEnv.HasUnresolvedPlaceholder(config.Memory.NeoCortexRoot))
            throw new InvalidOperationException("memory.neoCortexRoot is required.");

        ValidateConnector(config.Integrations.Autotask);
        ValidateConnector(config.Integrations.DattoRmm);
    }

    private static void ApplyConnectorEnvironment(ApiConnectorSettings connector, string envPrefix)
    {
        connector.Enabled = RustOpsEnv.GetBoolean($"{envPrefix}_ENABLED", connector.Enabled);
        connector.BaseUrl = RustOpsEnv.FirstNonEmptyEnvironment($"{envPrefix}_BASE_URL")
                            ?? RustOpsEnv.ResolvePlaceholders(connector.BaseUrl);
        connector.AccessToken = RustOpsEnv.FirstNonEmptyEnvironment($"{envPrefix}_ACCESS_TOKEN")
                                ?? RustOpsEnv.ResolvePlaceholders(connector.AccessToken ?? string.Empty);
        connector.ApiKey = RustOpsEnv.FirstNonEmptyEnvironment($"{envPrefix}_API_KEY")
                           ?? RustOpsEnv.ResolvePlaceholders(connector.ApiKey ?? string.Empty);
        connector.ApiSecret = RustOpsEnv.FirstNonEmptyEnvironment($"{envPrefix}_API_SECRET")
                              ?? RustOpsEnv.ResolvePlaceholders(connector.ApiSecret ?? string.Empty);
        connector.IntegrationCode = RustOpsEnv.FirstNonEmptyEnvironment($"{envPrefix}_INTEGRATION_CODE")
                                    ?? RustOpsEnv.ResolvePlaceholders(connector.IntegrationCode ?? string.Empty);
        connector.Username = RustOpsEnv.FirstNonEmptyEnvironment($"{envPrefix}_USERNAME")
                             ?? RustOpsEnv.ResolvePlaceholders(connector.Username ?? string.Empty);
        connector.Password = RustOpsEnv.FirstNonEmptyEnvironment($"{envPrefix}_PASSWORD")
                             ?? RustOpsEnv.ResolvePlaceholders(connector.Password ?? string.Empty);
        connector.LogsEndpointPath = RustOpsEnv.FirstNonEmptyEnvironment($"{envPrefix}_LOGS_ENDPOINT_PATH")
                                     ?? RustOpsEnv.ResolvePlaceholders(connector.LogsEndpointPath);
        connector.StatusEndpointPath = RustOpsEnv.FirstNonEmptyEnvironment($"{envPrefix}_STATUS_ENDPOINT_PATH")
                                       ?? RustOpsEnv.ResolvePlaceholders(connector.StatusEndpointPath);
    }

    private static void ValidateConnector(ApiConnectorSettings connector)
    {
        if (!connector.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(connector.BaseUrl) || RustOpsEnv.HasUnresolvedPlaceholder(connector.BaseUrl))
        {
            throw new InvalidOperationException($"integrations.{connector.Name}.baseUrl is required when connector is enabled.");
        }

        if (!Uri.TryCreate(connector.BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"integrations.{connector.Name}.baseUrl must be an absolute http/https URL.");
        }
    }
}
