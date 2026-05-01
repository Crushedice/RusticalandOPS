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
        config.Memory.Provider =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_MEMORY_PROVIDER")
            ?? RustOpsEnv.ResolvePlaceholders(config.Memory.Provider);
        config.Memory.DatabasePath =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_MEMORY_DATABASE_PATH")
            ?? config.Memory.DatabasePath;
        config.Memory.SearchEnabled =
            RustOpsEnv.GetBoolean("RUSTOPS_MEMORY_SEARCH_ENABLED", config.Memory.SearchEnabled);
        config.Memory.WriteEnabled =
            RustOpsEnv.GetBoolean("RUSTOPS_MEMORY_WRITE_ENABLED", config.Memory.WriteEnabled);
        config.Memory.DebugLoggingEnabled =
            RustOpsEnv.GetBoolean("RUSTOPS_MEMORY_DEBUG_LOGGING_ENABLED", config.Memory.DebugLoggingEnabled);
        config.Memory.SimilarityThreshold =
            RustOpsEnv.GetDouble("RUSTOPS_MEMORY_SIMILARITY_THRESHOLD", config.Memory.SimilarityThreshold);
        config.Memory.MaxRetrievedMemoriesPerStep =
            RustOpsEnv.GetInt32("RUSTOPS_MEMORY_MAX_RETRIEVED_PER_STEP", config.Memory.MaxRetrievedMemoriesPerStep);
        config.Memory.MaxSearchCandidates =
            RustOpsEnv.GetInt32("RUSTOPS_MEMORY_MAX_SEARCH_CANDIDATES", config.Memory.MaxSearchCandidates);
        config.Memory.MaxInjectedMemoryCharacters =
            RustOpsEnv.GetInt32("RUSTOPS_MEMORY_MAX_INJECTED_CHARACTERS", config.Memory.MaxInjectedMemoryCharacters);
        config.Memory.MaxWritesPerWorkflowStep =
            RustOpsEnv.GetInt32("RUSTOPS_MEMORY_MAX_WRITES_PER_STEP", config.Memory.MaxWritesPerWorkflowStep);
        config.Memory.PruneLowImportanceThreshold =
            RustOpsEnv.GetDouble("RUSTOPS_MEMORY_PRUNE_LOW_IMPORTANCE_THRESHOLD", config.Memory.PruneLowImportanceThreshold);
        config.Memory.PruneLowConfidenceThreshold =
            RustOpsEnv.GetDouble("RUSTOPS_MEMORY_PRUNE_LOW_CONFIDENCE_THRESHOLD", config.Memory.PruneLowConfidenceThreshold);
        config.Memory.PruneOlderThanDays =
            RustOpsEnv.GetInt32("RUSTOPS_MEMORY_PRUNE_OLDER_THAN_DAYS", config.Memory.PruneOlderThanDays);
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
        config.Memory.DatabasePath = RustOpsEnv.ResolvePlaceholders(config.Memory.DatabasePath);
        config.Inbox.FeedbackInboxPath = RustOpsEnv.ResolvePlaceholders(config.Inbox.FeedbackInboxPath);
        config.Inbox.DecisionInboxPath = RustOpsEnv.ResolvePlaceholders(config.Inbox.DecisionInboxPath);
        config.Inbox.ChatInboxPath = RustOpsEnv.ResolvePlaceholders(config.Inbox.ChatInboxPath);
        config.Outbox.MessageOutboxPath = RustOpsEnv.ResolvePlaceholders(config.Outbox.MessageOutboxPath);

        config.Memory.Embedding.Provider =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_EMBEDDING_PROVIDER")
            ?? RustOpsEnv.ResolvePlaceholders(config.Memory.Embedding.Provider);
        config.Memory.Embedding.BaseUrl =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_EMBEDDING_BASE_URL")
            ?? RustOpsEnv.ResolvePlaceholders(config.Memory.Embedding.BaseUrl);
        config.Memory.Embedding.ApiKeyEnvVarName =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_EMBEDDING_API_KEY_ENV_VAR")
            ?? RustOpsEnv.ResolvePlaceholders(config.Memory.Embedding.ApiKeyEnvVarName);
        config.Memory.Embedding.ApiKey =
            RustOpsEnv.FirstNonEmptyEnvironment(config.Memory.Embedding.ApiKeyEnvVarName, "RUSTOPS_EMBEDDING_API_KEY")
            ?? RustOpsEnv.ResolvePlaceholders(config.Memory.Embedding.ApiKey ?? string.Empty);
        config.Memory.Embedding.RequireApiKey =
            RustOpsEnv.GetBoolean("RUSTOPS_EMBEDDING_REQUIRE_API_KEY", config.Memory.Embedding.RequireApiKey);
        config.Memory.Embedding.Model =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_EMBEDDING_MODEL")
            ?? RustOpsEnv.ResolvePlaceholders(config.Memory.Embedding.Model);
        config.Memory.Embedding.TimeoutSeconds =
            RustOpsEnv.GetInt32("RUSTOPS_EMBEDDING_TIMEOUT_SECONDS", config.Memory.Embedding.TimeoutSeconds);
        config.Memory.Embedding.BatchSize =
            RustOpsEnv.GetInt32("RUSTOPS_EMBEDDING_BATCH_SIZE", config.Memory.Embedding.BatchSize);

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

        // Deep LLM — used for background analysis, classifier evolution, and incident review.
        // Falls back to the fast LLM if not configured.
        config.LlmDeep.Enabled =
            RustOpsEnv.GetBoolean("RUSTOPS_LLM_DEEP_ENABLED", config.LlmDeep.Enabled);
        config.LlmDeep.Provider =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_DEEP_PROVIDER")
            ?? RustOpsEnv.ResolvePlaceholders(config.LlmDeep.Provider);
        config.LlmDeep.BaseUrl =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_DEEP_BASE_URL")
            ?? RustOpsEnv.ResolvePlaceholders(config.LlmDeep.BaseUrl);
        config.LlmDeep.Model =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_DEEP_MODEL")
            ?? RustOpsEnv.ResolvePlaceholders(config.LlmDeep.Model);
        config.LlmDeep.ApiKey =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_DEEP_API_KEY")
            ?? RustOpsEnv.ResolvePlaceholders(config.LlmDeep.ApiKey ?? string.Empty);
        config.LlmDeep.HttpReferer =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_DEEP_HTTP_REFERER")
            ?? RustOpsEnv.ResolvePlaceholders(config.LlmDeep.HttpReferer ?? string.Empty);
        config.LlmDeep.AppTitle =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_DEEP_APP_TITLE")
            ?? RustOpsEnv.ResolvePlaceholders(config.LlmDeep.AppTitle ?? string.Empty);
        config.LlmDeep.UseForRecommendations =
            RustOpsEnv.GetBoolean("RUSTOPS_LLM_DEEP_USE_FOR_RECOMMENDATIONS", config.LlmDeep.UseForRecommendations);

        // Compose LLM — dedicated model for response generation (natural language, not JSON routing).
        // Falls back to the fast LLM if not configured.
        config.LlmCompose.Enabled =
            RustOpsEnv.GetBoolean("RUSTOPS_LLM_COMPOSE_ENABLED", config.LlmCompose.Enabled);
        config.LlmCompose.Provider =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_COMPOSE_PROVIDER")
            ?? RustOpsEnv.ResolvePlaceholders(config.LlmCompose.Provider);
        config.LlmCompose.BaseUrl =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_COMPOSE_BASE_URL")
            ?? RustOpsEnv.ResolvePlaceholders(config.LlmCompose.BaseUrl);
        config.LlmCompose.Model =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_COMPOSE_MODEL")
            ?? RustOpsEnv.ResolvePlaceholders(config.LlmCompose.Model);
        config.LlmCompose.ApiKey =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_COMPOSE_API_KEY")
            ?? RustOpsEnv.ResolvePlaceholders(config.LlmCompose.ApiKey ?? string.Empty);
        config.LlmCompose.HttpReferer =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_COMPOSE_HTTP_REFERER")
            ?? RustOpsEnv.ResolvePlaceholders(config.LlmCompose.HttpReferer ?? string.Empty);
        config.LlmCompose.AppTitle =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_COMPOSE_APP_TITLE")
            ?? RustOpsEnv.ResolvePlaceholders(config.LlmCompose.AppTitle ?? string.Empty);
        config.LlmCompose.UseForRecommendations =
            RustOpsEnv.GetBoolean("RUSTOPS_LLM_COMPOSE_USE_FOR_RECOMMENDATIONS", config.LlmCompose.UseForRecommendations);
        config.LlmCompose.UseChatSystemPrompt =
            RustOpsEnv.GetBoolean("RUSTOPS_LLM_COMPOSE_USE_CHAT_SYSTEM_PROMPT", config.LlmCompose.UseChatSystemPrompt);
        config.LlmCompose.ChatSystemPrompt =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_LLM_COMPOSE_CHAT_SYSTEM_PROMPT")
            ?? RustOpsEnv.ResolvePlaceholders(config.LlmCompose.ChatSystemPrompt ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(config.Monitor.LogRulesPath))
            config.Monitor.LogRulesPath = ResolvePath(config.Monitor.LogRulesPath, root);

        config.Memory.StatePath = ResolvePath(config.Memory.StatePath, root);
        config.Memory.NeoCortexRoot = ResolvePath(config.Memory.NeoCortexRoot, root);
        config.Memory.DatabasePath = ResolvePath(config.Memory.DatabasePath, root);
        config.Inbox.ChatInboxPath = ResolvePath(config.Inbox.ChatInboxPath, root);
        config.Inbox.DecisionInboxPath = ResolvePath(config.Inbox.DecisionInboxPath, root);
        config.Inbox.FeedbackInboxPath = ResolvePath(config.Inbox.FeedbackInboxPath, root);
        config.Outbox.MessageOutboxPath = ResolvePath(config.Outbox.MessageOutboxPath, root);

        config.GitOps.GithubToken =
            RustOpsEnv.FirstNonEmptyEnvironment("GITHUB_TOKEN", "RUSTOPS_GITOPS_GITHUB_TOKEN")
            ?? RustOpsEnv.ResolvePlaceholders(config.GitOps.GithubToken ?? string.Empty);
        config.GitOps.Enabled =
            RustOpsEnv.GetBoolean("RUSTOPS_GITOPS_ENABLED", config.GitOps.Enabled);
        config.GitOps.RepoPath =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_GITOPS_REPO_PATH")
            ?? RustOpsEnv.ResolvePlaceholders(config.GitOps.RepoPath);
        config.GitOps.RemoteName =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_GITOPS_REMOTE")
            ?? RustOpsEnv.ResolvePlaceholders(config.GitOps.RemoteName);
        config.GitOps.BaseBranch =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_GITOPS_BASE_BRANCH")
            ?? RustOpsEnv.ResolvePlaceholders(config.GitOps.BaseBranch);
        config.GitOps.AllowPush =
            RustOpsEnv.GetBoolean("RUSTOPS_GITOPS_ALLOW_PUSH", config.GitOps.AllowPush);

        config.AutoPull.GithubToken =
            RustOpsEnv.FirstNonEmptyEnvironment("GITHUB_TOKEN", "RUSTOPS_GITOPS_GITHUB_TOKEN")
            ?? RustOpsEnv.ResolvePlaceholders(config.AutoPull.GithubToken ?? string.Empty);
        config.AutoPull.Enabled =
            RustOpsEnv.GetBoolean("RUSTOPS_GITOPS_AUTO_PULL_ENABLED", config.AutoPull.Enabled);
        config.AutoPull.IntervalMinutes =
            RustOpsEnv.GetInt32("RUSTOPS_GITOPS_AUTO_PULL_INTERVAL_MINUTES", config.AutoPull.IntervalMinutes);
        config.AutoPull.RepoPath =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_GITOPS_REPO_PATH")
            ?? RustOpsEnv.ResolvePlaceholders(config.AutoPull.RepoPath);
        config.AutoPull.RemoteName =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_GITOPS_REMOTE")
            ?? RustOpsEnv.ResolvePlaceholders(config.AutoPull.RemoteName);
        config.AutoPull.BranchName =
            RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_GITOPS_AUTO_PULL_BRANCH", "RUSTOPS_GITOPS_BASE_BRANCH")
            ?? RustOpsEnv.ResolvePlaceholders(config.AutoPull.BranchName);
        config.AutoPull.BuildEnabled =
            RustOpsEnv.GetBoolean("RUSTOPS_GITOPS_AUTO_PULL_REBUILD", config.AutoPull.BuildEnabled);
        config.AutoPull.RestartEnabled =
            RustOpsEnv.GetBoolean("RUSTOPS_GITOPS_AUTO_RESTART_AFTER_PULL_REBUILD", config.AutoPull.RestartEnabled);

        if (!config.GitOps.PushBranchPrefix.StartsWith("agent/", StringComparison.OrdinalIgnoreCase))
        {
            config.GitOps.PushBranchPrefix = "agent/";
        }

        config.CommandExecution.Enabled =
            RustOpsEnv.GetBoolean("RUSTOPS_COMMANDS_ENABLED", config.CommandExecution.Enabled);
        config.CommandExecution.FreeMode =
            RustOpsEnv.GetBoolean("RUSTOPS_COMMANDS_FREE_MODE", config.CommandExecution.FreeMode);
        config.CommandExecution.DefaultWaitMs =
            RustOpsEnv.GetInt32("RUSTOPS_COMMANDS_DEFAULT_WAIT_MS", config.CommandExecution.DefaultWaitMs);
        config.CommandExecution.MaxWaitMs =
            RustOpsEnv.GetInt32("RUSTOPS_COMMANDS_MAX_WAIT_MS", config.CommandExecution.MaxWaitMs);
        config.CommandExecution.MaxOutputChars =
            RustOpsEnv.GetInt32("RUSTOPS_COMMANDS_MAX_OUTPUT_CHARS", config.CommandExecution.MaxOutputChars);
        var commandAllowList = RustOpsEnv.FirstNonEmptyEnvironment("RUSTOPS_COMMANDS_ALLOWLIST");
        if (!string.IsNullOrWhiteSpace(commandAllowList))
            config.CommandExecution.AllowList = commandAllowList
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList();

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
        if (string.IsNullOrWhiteSpace(config.Memory.DatabasePath) || RustOpsEnv.HasUnresolvedPlaceholder(config.Memory.DatabasePath))
            throw new InvalidOperationException("memory.databasePath is required.");
        if (config.Memory.MaxRetrievedMemoriesPerStep <= 0)
            throw new InvalidOperationException("memory.maxRetrievedMemoriesPerStep must be > 0.");
        if (config.Memory.MaxSearchCandidates <= 0)
            throw new InvalidOperationException("memory.maxSearchCandidates must be > 0.");
        if (config.Memory.MaxInjectedMemoryCharacters < 0)
            throw new InvalidOperationException("memory.maxInjectedMemoryCharacters must be >= 0.");
        if (config.Memory.MaxWritesPerWorkflowStep < 0)
            throw new InvalidOperationException("memory.maxWritesPerWorkflowStep must be >= 0.");
        if (config.Memory.SimilarityThreshold < 0 || config.Memory.SimilarityThreshold > 1)
            throw new InvalidOperationException("memory.similarityThreshold must be between 0 and 1.");
        if (config.Memory.PruneLowImportanceThreshold < 0 || config.Memory.PruneLowImportanceThreshold > 1)
            throw new InvalidOperationException("memory.pruneLowImportanceThreshold must be between 0 and 1.");
        if (config.Memory.PruneLowConfidenceThreshold < 0 || config.Memory.PruneLowConfidenceThreshold > 1)
            throw new InvalidOperationException("memory.pruneLowConfidenceThreshold must be between 0 and 1.");
        if (config.Memory.PruneOlderThanDays < 0)
            throw new InvalidOperationException("memory.pruneOlderThanDays must be >= 0.");
    }
}
