using System.Text.Json;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Infrastructure.Memory;

internal sealed class LegacyAgentStateStore
{
    private readonly string _path;
    private readonly LegacyAgentState _state;

    public LegacyAgentStateStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        if (File.Exists(_path))
        {
            try
            {
                _state = JsonSerializer.Deserialize<LegacyAgentState>(File.ReadAllText(_path), JsonDefaults.Default) ?? new LegacyAgentState();
            }
            catch
            {
                _state = new LegacyAgentState();
            }
        }
        else
        {
            _state = new LegacyAgentState();
        }

        _state.RuntimeStatus ??= new LegacyRuntimeStatus();
    }

    public void UpdateRuntimeStatus(LlmSettings llm)
    {
        _state.RuntimeStatus ??= new LegacyRuntimeStatus();
        _state.RuntimeStatus.LlmEnabled = llm.Enabled;
        _state.RuntimeStatus.LlmProvider = llm.Provider;
        _state.RuntimeStatus.LlmModel = llm.Model;
        _state.RuntimeStatus.LlmBaseUrl = llm.BaseUrl;
        _state.RuntimeStatus.UpdatedAtUtc = DateTime.UtcNow;
    }

    public void RecordAction(string actionId, string actionType, bool success, string summary, string? serverName, string trigger)
    {
        _state.ActionHistory.Add(new LegacyActionHistoryEntry
        {
            ActionId = actionId,
            ActionType = actionType,
            Success = success,
            Summary = summary,
            ServerName = serverName,
            Trigger = trigger,
            ExecutedAtUtc = DateTime.UtcNow
        });

        _state.ActionHistory = _state.ActionHistory
            .OrderByDescending(a => a.ExecutedAtUtc)
            .Take(200)
            .ToList();
    }

    public void RecordIncident(string? serverName, string category, string summary)
    {
        var resolvedServer = string.IsNullOrWhiteSpace(serverName) ? "general" : serverName;
        var server = _state.Servers.FirstOrDefault(s => string.Equals(s.Name, resolvedServer, StringComparison.OrdinalIgnoreCase));
        if (server is null)
        {
            server = new LegacyServerEntry { Name = resolvedServer! };
            _state.Servers.Add(server);
        }

        server.Incidents.Add(new LegacyIncidentEntry
        {
            CreatedAtUtc = DateTime.UtcNow,
            Category = category,
            Title = category,
            Summary = summary
        });

        server.Incidents = server.Incidents
            .OrderByDescending(i => i.CreatedAtUtc)
            .Take(60)
            .ToList();
    }

    public void RecordFeedback(string? adminId, string? actionId, string? verdict, string? note, string? serverName)
    {
        _state.FeedbackHistory.Add(new LegacyFeedbackEntry
        {
            AdminId = adminId,
            ActionId = actionId,
            Verdict = verdict,
            Note = note,
            ServerName = serverName,
            ReceivedAtUtc = DateTime.UtcNow
        });

        _state.FeedbackHistory = _state.FeedbackHistory
            .OrderByDescending(f => f.ReceivedAtUtc)
            .Take(200)
            .ToList();
    }

    public void RecordAgentError(string message)
    {
        _state.AgentErrors.Add($"{DateTime.UtcNow:O} {message}");
        _state.AgentErrors = _state.AgentErrors.TakeLast(80).ToList();
    }

    public void Save()
    {
        _state.LastSavedAtUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(_state, JsonDefaults.Default);
        File.WriteAllText(_path, json);
    }
}

internal sealed class LegacyAgentState
{
    public DateTime LastSavedAtUtc { get; set; } = DateTime.UtcNow;
    public LegacyRuntimeStatus? RuntimeStatus { get; set; }
    public List<LegacyServerEntry> Servers { get; set; } = new();
    public List<LegacyActionHistoryEntry> ActionHistory { get; set; } = new();
    public List<LegacyPendingActionEntry> PendingActions { get; set; } = new();
    public List<LegacyFeedbackEntry> FeedbackHistory { get; set; } = new();
    public List<string> AgentErrors { get; set; } = new();
    public List<LegacyLlmInteractionEntry> LlmInteractions { get; set; } = new();
    public List<LegacyCapabilityGapEntry> CapabilityGaps { get; set; } = new();
    public List<LegacySelfRepairHistoryEntry> SelfRepairHistory { get; set; } = new();
}

internal sealed class LegacyRuntimeStatus
{
    public bool LlmEnabled { get; set; }
    public string? LlmProvider { get; set; }
    public string? LlmModel { get; set; }
    public string? LlmBaseUrl { get; set; }
    public string? LogRulesPath { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? LastLlmInteractionAtUtc { get; set; }
}

internal sealed class LegacyServerEntry
{
    public string Name { get; set; } = "general";
    public List<LegacyIncidentEntry> Incidents { get; set; } = new();
}

internal sealed class LegacyIncidentEntry
{
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? Title { get; set; }
    public string? Category { get; set; }
    public string? Summary { get; set; }
}

internal sealed class LegacyActionHistoryEntry
{
    public string ActionId { get; set; } = Guid.NewGuid().ToString("N");
    public string? ServerName { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public DateTime ExecutedAtUtc { get; set; } = DateTime.UtcNow;
    public bool Success { get; set; }
    public string? Trigger { get; set; }
    public string Summary { get; set; } = string.Empty;
}

internal sealed class LegacyPendingActionEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ServerName { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? Summary { get; set; }
    public string Status { get; set; } = "Pending";
}

internal sealed class LegacyFeedbackEntry
{
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public string? AdminId { get; set; }
    public string? ServerName { get; set; }
    public string? ActionId { get; set; }
    public string? Verdict { get; set; }
    public string? Note { get; set; }
}

internal sealed class LegacyLlmInteractionEntry
{
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string? Type { get; set; }
    public string? Model { get; set; }
    public bool Success { get; set; }
    public string? Context { get; set; }
    public string? ResponsePreview { get; set; }
}

internal sealed class LegacyCapabilityGapEntry
{
    public string? Category { get; set; }
    public string? Description { get; set; }
    public int Count { get; set; } = 1;
    public DateTime? FirstObservedAtUtc { get; set; }
    public DateTime? LastObservedAtUtc { get; set; }
}

internal sealed class LegacySelfRepairHistoryEntry
{
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string? Summary { get; set; }
    public int AppliedActions { get; set; }
    public int RejectedActions { get; set; }
    public string? RawModelReasoning { get; set; }
}
