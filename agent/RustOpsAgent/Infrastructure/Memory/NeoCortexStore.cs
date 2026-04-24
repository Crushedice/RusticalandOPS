using System.Text;
using System.Text.Json;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Infrastructure.Memory;

internal sealed class NeoCortexStore : IEvolutionStore
{
    private readonly string _root;
    private readonly string _legacyStatePath;
    private readonly string _operationsPath;
    private readonly string _selectionPath;
    private readonly string _logsPath;
    private readonly string _evolutionPath;
    private readonly string _policyPath;
    private readonly string _commandPolicyPath;
    private readonly string _cachePath;
    private readonly string _migrationMarkerPath;

    public NeoCortexStore(string root, string legacyStatePath)
    {
        _root = root;
        _legacyStatePath = legacyStatePath;
        _operationsPath = Path.Combine(root, "operations", "active-state.json");
        _selectionPath = Path.Combine(root, "selection", "session-state.json");
        _logsPath = Path.Combine(root, "logs", "log-knowledge.json");
        _evolutionPath = Path.Combine(root, "evolution", "incidents.jsonl");
        _policyPath = Path.Combine(root, "policy", "ignore-feedback.json");
        _commandPolicyPath = Path.Combine(root, "policy", "command-policy.json");
        _cachePath = Path.Combine(root, "cache", "domain-cache.json");
        _migrationMarkerPath = Path.Combine(root, ".migration-complete");

        Directory.CreateDirectory(Path.GetDirectoryName(_operationsPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_selectionPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_logsPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_evolutionPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_policyPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
    }

    public void EnsureMigrated()
    {
        if (File.Exists(_migrationMarkerPath))
        {
            EnsureSeedFiles();
            return;
        }

        var active = new ActiveOperationalState();
        var selection = new SelectionSessionState();
        var logs = new LogKnowledgeState();
        var policy = new IgnoreFeedbackState();
        var cache = new DomainCacheState();

        if (File.Exists(_legacyStatePath))
        {
            using var legacy = JsonDocument.Parse(File.ReadAllText(_legacyStatePath));
            var root = legacy.RootElement;

            if (root.TryGetProperty("runtimeStatus", out var runtime))
            {
                active.RuntimeStatus.LlmEnabled = runtime.TryGetProperty("llmEnabled", out var llmEnabled) && llmEnabled.ValueKind == JsonValueKind.True;
                active.RuntimeStatus.LlmProvider = runtime.TryGetProperty("llmProvider", out var provider) ? provider.GetString() : null;
            }

            if (root.TryGetProperty("actionHistory", out var actionHistory) && actionHistory.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in actionHistory.EnumerateArray().TakeLast(60))
                {
                    active.RecentActions.Add(new ActionRecord
                    {
                        Intent = entry.TryGetProperty("actionType", out var actionType) ? actionType.GetString() ?? "unknown" : "unknown",
                        Result = entry.TryGetProperty("summary", out var summary) ? summary.GetString() ?? string.Empty : string.Empty,
                        ServerName = entry.TryGetProperty("serverName", out var server) ? server.GetString() : null,
                        TimestampUtc = entry.TryGetProperty("executedAtUtc", out var executed) && executed.ValueKind == JsonValueKind.String && DateTime.TryParse(executed.GetString(), out var parsed)
                            ? parsed
                            : DateTime.UtcNow
                    });
                }
            }

            if (root.TryGetProperty("adminPreferences", out var adminPreferences) && adminPreferences.ValueKind == JsonValueKind.Array)
            {
                foreach (var admin in adminPreferences.EnumerateArray())
                {
                    var adminId = admin.TryGetProperty("adminId", out var adminIdNode) ? adminIdNode.GetString() : null;
                    if (string.IsNullOrWhiteSpace(adminId))
                    {
                        continue;
                    }

                    string? lastServer = null;
                    if (admin.TryGetProperty("conversation", out var conversation) && conversation.ValueKind == JsonValueKind.Object)
                    {
                        if (conversation.TryGetProperty("lastSelectedServer", out var serverNode))
                        {
                            lastServer = serverNode.GetString();
                        }
                    }

                    selection.Conversations.Add(new ConversationSelectionState
                    {
                        AdminId = adminId!,
                        LastServerName = lastServer,
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                }
            }

            if (root.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
            {
                foreach (var server in servers.EnumerateArray())
                {
                    var name = server.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    cache.ServerCache[name!] = "known";
                }
            }
        }

        if (!File.Exists(_operationsPath)) SaveJson(_operationsPath, active);
        if (!File.Exists(_selectionPath)) SaveJson(_selectionPath, selection);
        if (!File.Exists(_logsPath)) SaveJson(_logsPath, logs);
        if (!File.Exists(_policyPath)) SaveJson(_policyPath, policy);
        if (!File.Exists(_cachePath)) SaveJson(_cachePath, cache);
        if (!File.Exists(_evolutionPath))
        {
            File.WriteAllText(_evolutionPath, string.Empty);
        }

        File.WriteAllText(_migrationMarkerPath, DateTime.UtcNow.ToString("O"));
    }

    public ActiveOperationalState LoadOperations() => LoadJson(_operationsPath, new ActiveOperationalState());
    public SelectionSessionState LoadSelection() => LoadJson(_selectionPath, new SelectionSessionState());
    public LogKnowledgeState LoadLogs() => LoadJson(_logsPath, new LogKnowledgeState());
    public IgnoreFeedbackState LoadIgnoreFeedback() => LoadJson(_policyPath, new IgnoreFeedbackState());
    public CommandPolicyState LoadCommandPolicy() => LoadJson(_commandPolicyPath, new CommandPolicyState());
    public DomainCacheState LoadCache() => LoadJson(_cachePath, new DomainCacheState());

    public void SaveOperations(ActiveOperationalState state) => SaveJson(_operationsPath, state);
    public void SaveSelection(SelectionSessionState state) => SaveJson(_selectionPath, state);
    public void SaveLogs(LogKnowledgeState state) => SaveJson(_logsPath, state);
    public void SaveIgnoreFeedback(IgnoreFeedbackState state) => SaveJson(_policyPath, state);
    public void SaveCommandPolicy(CommandPolicyState state) => SaveJson(_commandPolicyPath, state);
    public void SaveCache(DomainCacheState state) => SaveJson(_cachePath, state);

    // Compact (non-indented) options for JSONL — one object per line is required.
    private static readonly JsonSerializerOptions JsonlOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async Task RecordIncidentAsync(EvolutionIncidentRecord incident, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(incident, JsonlOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(_evolutionPath, line, Encoding.UTF8, cancellationToken);
    }

    public async Task<EvolutionReviewResult> ReviewAsync(CancellationToken cancellationToken)
    {
        var review = new EvolutionReviewResult();
        if (!File.Exists(_evolutionPath))
        {
            return review;
        }

        var lines = await File.ReadAllLinesAsync(_evolutionPath, cancellationToken);
        foreach (var line in lines.Where(static l => !string.IsNullOrWhiteSpace(l)))
        {
            EvolutionIncidentRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<EvolutionIncidentRecord>(line, JsonDefaults.Default);
            }
            catch (Exception ex)
            {
                RustOpsSentry.CaptureException(
                    ex,
                    "Failed to deserialize NeoCortex incident record.",
                    "agent.memory",
                    extras: new Dictionary<string, object?> { ["linePreview"] = line.Length > 500 ? line[..500] : line });
                continue;
            }

            if (record is null)
            {
                continue;
            }

            if (record.Resolved)
            {
                review.RecentlyResolved.Add(record);
            }
            else
            {
                review.OpenIncidents.Add(record);
            }
        }

        review.OpenIncidents = review.OpenIncidents
            .OrderByDescending(i => i.Timestamp)
            .ToList();

        review.RecentlyResolved = review.RecentlyResolved
            .OrderByDescending(i => i.Timestamp)
            .Take(20)
            .ToList();

        return review;
    }

    private void EnsureSeedFiles()
    {
        if (!File.Exists(_operationsPath)) SaveJson(_operationsPath, new ActiveOperationalState());
        if (!File.Exists(_selectionPath)) SaveJson(_selectionPath, new SelectionSessionState());
        if (!File.Exists(_logsPath)) SaveJson(_logsPath, new LogKnowledgeState());
        if (!File.Exists(_policyPath)) SaveJson(_policyPath, new IgnoreFeedbackState());
        if (!File.Exists(_cachePath)) SaveJson(_cachePath, new DomainCacheState());
        if (!File.Exists(_evolutionPath)) File.WriteAllText(_evolutionPath, string.Empty);
    }

    private static T LoadJson<T>(string path, T fallback)
    {
        if (!File.Exists(path))
        {
            return fallback;
        }

        var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Default);
        return value ?? fallback;
    }

    private static void SaveJson<T>(string path, T state)
    {
        var json = JsonSerializer.Serialize(state, JsonDefaults.Default);
        File.WriteAllText(path, json);
    }
}
