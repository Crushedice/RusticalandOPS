internal sealed class NeoCortexMemoryBank
{
    private readonly string _rootPath;

    public NeoCortexMemoryBank(string statePath)
    {
        var stateDirectory = Path.GetDirectoryName(statePath) ?? AppContext.BaseDirectory;
        _rootPath = Path.Combine(stateDirectory, "NeoCortex");
    }

    public AgentMemoryStore Load(string legacyStatePath)
    {
        Directory.CreateDirectory(_rootPath);
        var operationalPath = Path.Combine(_rootPath, "operational-state.json");
        var selectionPath = Path.Combine(_rootPath, "selection-state.json");
        var incidentsPath = Path.Combine(_rootPath, "incidents-evolution.json");

        if (!File.Exists(operationalPath))
        {
            if (File.Exists(legacyStatePath))
                return JsonSerializer.Deserialize<AgentMemoryStore>(File.ReadAllText(legacyStatePath), JsonOptions.Default) ?? new AgentMemoryStore();
            return new AgentMemoryStore();
        }

        var store = new AgentMemoryStore();
        var operational = JsonSerializer.Deserialize<NeoCortexOperationalMemory>(File.ReadAllText(operationalPath), JsonOptions.Default);
        var selection = File.Exists(selectionPath)
            ? JsonSerializer.Deserialize<NeoCortexSelectionMemory>(File.ReadAllText(selectionPath), JsonOptions.Default)
            : null;
        var incidents = File.Exists(incidentsPath)
            ? JsonSerializer.Deserialize<NeoCortexIncidentMemory>(File.ReadAllText(incidentsPath), JsonOptions.Default)
            : null;

        if (operational is not null)
        {
            store.RuntimeStatus = operational.RuntimeStatus ?? new AgentRuntimeStatus();
            store.Servers = operational.Servers ?? new List<ServerMemory>();
            store.AgentErrors = operational.AgentErrors ?? new List<string>();
            store.ActionHistory = operational.ActionHistory ?? new List<ActionExecutionRecord>();
            store.PendingActions = operational.PendingActions ?? new List<ActionProposal>();
            store.ActionMetrics = operational.ActionMetrics ?? new List<ActionMetric>();
            store.LlmInteractions = operational.LlmInteractions ?? new List<LlmInteractionRecord>();
        }

        if (selection is not null)
        {
            store.AdminPreferences = selection.AdminPreferences ?? new List<AdminPreference>();
            store.FeedbackHistory = selection.FeedbackHistory ?? new List<FeedbackEntry>();
        }

        if (incidents is not null)
        {
            store.CapabilityGaps = incidents.CapabilityGaps ?? new List<CapabilityGapRecord>();
            store.SelfRepairHistory = incidents.EvolutionHistory ?? new List<SelfRepairRunRecord>();
        }

        return store;
    }

    public void Save(AgentMemoryStore store, string legacyStatePath)
    {
        Directory.CreateDirectory(_rootPath);
        var operationalPath = Path.Combine(_rootPath, "operational-state.json");
        var selectionPath = Path.Combine(_rootPath, "selection-state.json");
        var incidentsPath = Path.Combine(_rootPath, "incidents-evolution.json");

        File.WriteAllText(operationalPath, JsonSerializer.Serialize(new NeoCortexOperationalMemory
        {
            RuntimeStatus = store.RuntimeStatus,
            Servers = store.Servers,
            AgentErrors = store.AgentErrors,
            ActionHistory = store.ActionHistory,
            PendingActions = store.PendingActions,
            ActionMetrics = store.ActionMetrics,
            LlmInteractions = store.LlmInteractions
        }, JsonOptions.Default));

        File.WriteAllText(selectionPath, JsonSerializer.Serialize(new NeoCortexSelectionMemory
        {
            AdminPreferences = store.AdminPreferences,
            FeedbackHistory = store.FeedbackHistory
        }, JsonOptions.Default));

        File.WriteAllText(incidentsPath, JsonSerializer.Serialize(new NeoCortexIncidentMemory
        {
            CapabilityGaps = store.CapabilityGaps,
            EvolutionHistory = store.SelfRepairHistory
        }, JsonOptions.Default));

        Directory.CreateDirectory(Path.GetDirectoryName(legacyStatePath)!);
        File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(store, AgentMemoryStore.StateFileOptions));
    }
}

internal sealed class NeoCortexOperationalMemory
{
    public AgentRuntimeStatus? RuntimeStatus { get; set; }
    public List<ServerMemory>? Servers { get; set; }
    public List<string>? AgentErrors { get; set; }
    public List<ActionProposal>? PendingActions { get; set; }
    public List<ActionExecutionRecord>? ActionHistory { get; set; }
    public List<ActionMetric>? ActionMetrics { get; set; }
    public List<LlmInteractionRecord>? LlmInteractions { get; set; }
}

internal sealed class NeoCortexSelectionMemory
{
    public List<AdminPreference>? AdminPreferences { get; set; }
    public List<FeedbackEntry>? FeedbackHistory { get; set; }
}

internal sealed class NeoCortexIncidentMemory
{
    public List<CapabilityGapRecord>? CapabilityGaps { get; set; }
    public List<SelfRepairRunRecord>? EvolutionHistory { get; set; }
}
