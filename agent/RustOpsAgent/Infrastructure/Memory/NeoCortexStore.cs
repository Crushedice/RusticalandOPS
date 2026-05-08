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
    private readonly string _consolePath;
    private readonly string _playerChatPath;
    private readonly string _classifierPath;
    private readonly SemaphoreSlim _incidentWriteLock = new(1, 1);

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
        _consolePath = Path.Combine(root, "console", "monitor.json");
        _playerChatPath = Path.Combine(root, "chat", "knowledge.json");
        _classifierPath = Path.Combine(root, "classifier", "knowledge.json");

        Directory.CreateDirectory(Path.GetDirectoryName(_operationsPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_selectionPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_logsPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_evolutionPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_policyPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_consolePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_playerChatPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_classifierPath)!);
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

    public ConsoleMonitorState LoadConsoleMonitor() => LoadJson(_consolePath, new ConsoleMonitorState());
    public void SaveConsoleMonitor(ConsoleMonitorState state) => SaveJson(_consolePath, state);

    public PlayerChatKnowledge LoadPlayerChat() => LoadJson(_playerChatPath, new PlayerChatKnowledge());
    public void SavePlayerChat(PlayerChatKnowledge knowledge) => SaveJson(_playerChatPath, knowledge);

    public ClassifierKnowledgeState LoadClassifierKnowledge() => LoadJson(_classifierPath, new ClassifierKnowledgeState());
    public void SaveClassifierKnowledge(ClassifierKnowledgeState state) => SaveJson(_classifierPath, state);

    /// <summary>
    /// Moves all current log entries to a dated digest file and resets RecentEntries.
    /// Called once per UTC day to prevent unbounded log accumulation.
    /// Keeps the last 7 daily digest files.
    /// </summary>
    public void ArchiveAndResetLogs(string isoDate)
    {
        var logs = LoadLogs();
        if (logs.RecentEntries.Count > 0)
        {
            var digestDir = Path.Combine(_root, "logs", "digests");
            Directory.CreateDirectory(digestDir);
            var digestPath = Path.Combine(digestDir, $"{isoDate}.jsonl");
            var lines = logs.RecentEntries.Select(e => JsonSerializer.Serialize(e, JsonlOptions));
            File.AppendAllLines(digestPath, lines);

            // Prune digests older than 7 days
            foreach (var old in Directory.GetFiles(digestDir, "*.jsonl").OrderByDescending(f => f).Skip(7))
                try { File.Delete(old); } catch { /* ignore prune failures */ }
        }

        logs.RecentEntries.Clear();
        logs.LastDigestDateUtc = DateTime.UtcNow;
        SaveLogs(logs);
    }

    // Compact (non-indented) options for JSONL — one object per line is required.
    private static readonly JsonSerializerOptions JsonlOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async Task RecordIncidentAsync(EvolutionIncidentRecord incident, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(incident, JsonlOptions) + "\n";
        await _incidentWriteLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_evolutionPath, line, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            _incidentWriteLock.Release();
        }
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
            // Skip obviously malformed JSONL lines — partial writes, leftover braces, etc.
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                continue;

            EvolutionIncidentRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<EvolutionIncidentRecord>(trimmed, JsonDefaults.Default);
            }
            catch
            {
                // Silently skip malformed lines — corrupt entries are non-recoverable.
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
        Directory.CreateDirectory(Path.GetDirectoryName(_consolePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_playerChatPath)!);
    }

    private static T LoadJson<T>(string path, T fallback)
    {
        if (!File.Exists(path))
            return fallback;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch
        {
            return fallback;
        }

        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        try
        {
            var value = JsonSerializer.Deserialize<T>(text, JsonDefaults.Default);
            return value ?? fallback;
        }
        catch (JsonException)
        {
            // File may be partially written (race condition). Try to salvage by stripping trailing garbage.
            var end = text.LastIndexOf('}');
            if (end > 0 && end < text.Length - 1)
            {
                try
                {
                    var trimmed = text[..(end + 1)];
                    var value = JsonSerializer.Deserialize<T>(trimmed, JsonDefaults.Default);
                    return value ?? fallback;
                }
                catch { /* fall through to fallback */ }
            }

            return fallback;
        }
    }

    private static void SaveJson<T>(string path, T state)
    {
        var json = JsonSerializer.Serialize(state, JsonDefaults.Default);
        // Atomic write: write to temp file, then rename to replace target.
        // Prevents readers from seeing a partially-written file.
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }
}
