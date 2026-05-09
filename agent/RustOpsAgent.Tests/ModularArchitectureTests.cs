using System.Text.Json;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Core.Interaction;
using RustOpsAgent.Domains.Rust;
using RustOpsAgent.Infrastructure;
using RustOpsAgent.Infrastructure.GitOps;
using RustOpsAgent.Infrastructure.Memory;

namespace RustOpsAgent.Tests;

public class ModularArchitectureTests
{
    // ── GitOps ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GitOps_Rejects_Main_Push()
    {
        var service = new GitOpsService(new GitOpsSettings
        {
            RepoPath = Path.GetTempPath(),
            RemoteName = "origin",
            BaseBranch = "main",
            PushBranchPrefix = "agent/"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PushAsync("main", CancellationToken.None));
    }

    [Fact]
    public void Program_Wires_Single_SemanticMemoryService_Into_Live_Pipeline()
    {
        var programPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "RustOpsAgent", "Program.cs"));
        var source = File.ReadAllText(programPath);

        Assert.Equal(1, CountOccurrences(source, "new SemanticMemoryService("));
        Assert.Contains("new AdminIntentClassifier(kernel, config.Llm, neoCortex, semanticMemory);", source);
        Assert.Contains("new ActionExecutor(registry, semanticMemory);", source);
        Assert.Contains("new RustChatToolHandler(neoCortex, semanticMemory, autoPull, serverKnowledge, memoryImport, pluginReferenceIndexer, catalogIndexStore),", source);
        Assert.Contains("new AgentRuntime(config, classifier, executor, composer, neoCortex, legacyState, semanticMemory, gitOps, autoPull, apiClient, deepKernel, playerStore);", source);
    }

    // ── NeoCortex ──────────────────────────────────────────────────────────────

    [Fact]
    public void NeoCortex_Migration_Creates_Banks()
    {
        var root = TempRoot();
        var legacy = Path.Combine(root, "legacy-state.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(legacy, "{\"runtimeStatus\":{\"llmEnabled\":true,\"llmProvider\":\"test\"}}");

        var store = new NeoCortexStore(Path.Combine(root, "NeoCortex"), legacy);
        store.EnsureMigrated();

        Assert.True(File.Exists(Path.Combine(root, "NeoCortex", "operations", "active-state.json")));
        Assert.True(File.Exists(Path.Combine(root, "NeoCortex", "selection", "session-state.json")));
        Assert.True(File.Exists(Path.Combine(root, "NeoCortex", "evolution", "incidents.jsonl")));
    }

    [Fact]
    public void NeoCortex_Migration_Does_Not_Overwrite_Existing_Files_When_Marker_Is_Missing()
    {
        var root = TempRoot();
        var neoRoot = Path.Combine(root, "NeoCortex");
        var legacy = Path.Combine(root, "legacy-state.json");
        var operationsDir = Path.Combine(neoRoot, "operations");
        var operationsPath = Path.Combine(operationsDir, "active-state.json");
        Directory.CreateDirectory(operationsDir);
        File.WriteAllText(legacy, "{}");
        File.WriteAllText(operationsPath, """{"runtimeStatus":{"llmEnabled":true,"llmProvider":"preserve"},"recentActions":[]}""");

        var store = new NeoCortexStore(neoRoot, legacy);
        store.EnsureMigrated();

        using var doc = JsonDocument.Parse(File.ReadAllText(operationsPath));
        Assert.True(doc.RootElement.TryGetProperty("runtimeStatus", out var runtimeStatus));
        Assert.Equal("preserve", runtimeStatus.GetProperty("llmProvider").GetString());
    }

    [Fact]
    public void NeoCortex_CommandPolicy_Persists_And_Loads()
    {
        var root = TempRoot();
        var store = MakeStore(root);
        store.EnsureMigrated();

        var policy = store.LoadCommandPolicy();
        Assert.Empty(policy.Commands);

        policy.Commands["status"] = new CommandRecord
        {
            Command = "status",
            SuccessCount = 5,
            AutoAllowed = true,
            LastUsedUtc = DateTime.UtcNow
        };
        store.SaveCommandPolicy(policy);

        var reloaded = store.LoadCommandPolicy();
        Assert.True(reloaded.Commands.ContainsKey("status"));
        Assert.True(reloaded.Commands["status"].AutoAllowed);
        Assert.Equal(5, reloaded.Commands["status"].SuccessCount);
    }

    [Fact]
    public async Task NeoCortex_Records_And_Reviews_Incidents()
    {
        var root = TempRoot();
        var store = MakeStore(root);
        store.EnsureMigrated();

        await store.RecordIncidentAsync(new EvolutionIncidentRecord
        {
            Request = "restart alpha",
            IntendedOutcome = "ServerControl",
            FailureReason = "No config",
            Classification = "missing_config",
            Resolved = false
        }, CancellationToken.None);

        await store.RecordIncidentAsync(new EvolutionIncidentRecord
        {
            Request = "restart beta",
            IntendedOutcome = "ServerControl",
            FailureReason = "Timeout",
            Classification = "timeout",
            Resolved = true
        }, CancellationToken.None);

        var review = await store.ReviewAsync(CancellationToken.None);
        Assert.Single(review.OpenIncidents);
        Assert.Single(review.RecentlyResolved);
        Assert.Equal("missing_config", review.OpenIncidents[0].Classification);
    }

    // ── ToolRegistry ───────────────────────────────────────────────────────────

    [Fact]
    public void ToolRegistry_Filters_By_Intent()
    {
        using var api = TestApi();
        var neo = MakeStore(TempRoot());
        neo.EnsureMigrated();

        var handlers = new IToolHandler[]
        {
            new RustServerControlToolHandler(api),
            new RustStatusToolHandler(api),
            new RustChatToolHandler(neo, MakeSemanticMemory(root: TempRoot()))
        };

        var registry = new ToolRegistry(handlers);
        var route = new AdminIntentRoute(
            AdminIntentType.ServerControl,
            new AdminIntentSlots("alpha", null, null, null, null),
            0.9, false, null, null);

        var eligible = registry.ResolveEligible(route);
        Assert.Contains(eligible, h => h.Name == "rust.server.control");
        Assert.DoesNotContain(eligible, h => h.Name == "rust.status.check");
    }

    [Fact]
    public void ToolRegistry_Uses_TargetRef_For_Diagnostics()
    {
        using var api = TestApi();
        var neo = MakeStore(TempRoot());
        neo.EnsureMigrated();

        var handlers = new IToolHandler[]
        {
            new RustStatusToolHandler(api),
            new RustLogsToolHandler(api, neo),
            new RustPluginToolHandler(api),
            new RustNetworkToolHandler(api)
        };

        var registry = new ToolRegistry(handlers);
        var route = new AdminIntentRoute(
            AdminIntentType.Troubleshooting,
            new AdminIntentSlots("alpha", null, null, null, null),
            0.9, false, null, "rust.network.inspect");

        var selected = registry.ResolveSingle(new ToolExecutionContext("admin", "check alpha", route, new ConversationSelectionState(), DateTime.UtcNow));
        Assert.NotNull(selected);
        Assert.Equal("rust.network.inspect", selected!.Name);
    }

    [Fact]
    public async Task IntentClassifier_Routes_ServerConfig_Read_To_FileEdit()
    {
        var classifier = new AdminIntentClassifier(kernel: null, settings: new LlmSettings { Enabled = false }, neoCortex: null);
        var state = new ConversationSelectionState { AdminId = "admin" };
        var route = await classifier.ClassifyAsync(
            "show me the serverconfig for cotton server",
            state,
            new[] { "cotton", "monthly" },
            CancellationToken.None);

        Assert.Equal(AdminIntentType.FileEdit, route.Intent);
        Assert.Equal("rust.file.edit", route.TargetRef);
        Assert.Equal("cotton", route.Slots.ServerName, StringComparer.OrdinalIgnoreCase);
    }

    // ── ConfigLoader ───────────────────────────────────────────────────────────

    [Fact]
    public void ConfigLoader_Rejects_Unresolved_Required_Values()
    {
        var root = TempRoot();
        var path = Path.Combine(root, "agentsettings.json");
        File.WriteAllText(path, """
        {
          "api": { "baseUrl": "${RUSTOPS_API_BASE_URL}", "apiKey": "${RUSTMGR_API_KEY}" },
          "memory": { "statePath": "${RUSTOPS_AGENT_STATE_PATH}", "neoCortexRoot": "data/NeoCortex" },
          "inbox": { "feedbackInboxPath": "data/feedback-inbox", "decisionInboxPath": "data/decision-inbox", "chatInboxPath": "data/chat-inbox" },
          "outbox": { "messageOutboxPath": "data/message-outbox" },
          "monitor": { "pollSeconds": 1 },
          "gitOps": { "enabled": false, "repoPath": ".", "remoteName": "origin", "baseBranch": "main", "pushBranchPrefix": "agent/", "allowPush": false },
          "llm": { "enabled": false, "provider": "x", "baseUrl": "http://127.0.0.1:11434/v1", "model": "m" }
        }
        """);

        Environment.SetEnvironmentVariable("RUSTOPS_API_BASE_URL", null);
        Environment.SetEnvironmentVariable("RUSTMGR_API_KEY", null);
        Environment.SetEnvironmentVariable("RUSTOPS_AGENT_STATE_PATH", null);

        Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path));
    }

    // ── LegacyStateStore ───────────────────────────────────────────────────────

    [Fact]
    public void LegacyStateStore_Writes_Api_Compatible_Action_History()
    {
        var root = TempRoot();
        var statePath = Path.Combine(root, "agent-state.json");
        var store = new LegacyAgentStateStore(statePath);
        store.UpdateRuntimeStatus(new LlmSettings { Enabled = true, Provider = "test", Model = "m", BaseUrl = "http://localhost" });
        store.RecordAction("a-1", "status_check", true, "ok", "alpha", "chat");
        store.RecordFeedback("42", "a-1", "good", "nice", "alpha");
        store.RecordIncident("alpha", "execution_failure", "failed to run");
        store.Save();

        using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
        Assert.True(doc.RootElement.TryGetProperty("actionHistory", out var actions));
        Assert.Equal(1, actions.GetArrayLength());
        Assert.True(doc.RootElement.TryGetProperty("servers", out var servers));
        Assert.Equal(1, servers.GetArrayLength());
    }

    [Fact]
    public void LegacyStateStore_Does_Not_Write_Deprecated_SelfRepair_Schema()
    {
        var root = TempRoot();
        var statePath = Path.Combine(root, "agent-state.json");
        var store = new LegacyAgentStateStore(statePath);
        store.RecordFeedback("42", "a-1", "good", "nice", "alpha");
        store.Save();

        using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
        Assert.False(doc.RootElement.TryGetProperty("llmInteractions", out _));
        Assert.False(doc.RootElement.TryGetProperty("capabilityGaps", out _));
        Assert.False(doc.RootElement.TryGetProperty("selfRepairHistory", out _));
    }

    // ── ActionExecutor ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ActionExecutor_FileEdit_Without_GitOps_Returns_Not_Configured()
    {
        // FileEdit now routes to a real handler — without GitOps enabled it returns not_configured.
        using var api = TestApi();
        var root = TempRoot();
        var neo = MakeStore(root);
        neo.EnsureMigrated();
        var gitOps = new GitOpsService(new GitOpsSettings { Enabled = false, RepoPath = root, PushBranchPrefix = "agent/" });
        var fileEditHandler = new RustFileEditToolHandler(api, gitOps, new GitOpsSettings { Enabled = false, RepoPath = root, PushBranchPrefix = "agent/" });
        var registry = new ToolRegistry(new IToolHandler[] { new RustChatToolHandler(neo, MakeSemanticMemory(root)), fileEditHandler });
        var executor = new ActionExecutor(registry);

        var route = new AdminIntentRoute(
            AdminIntentType.FileEdit,
            new AdminIntentSlots(null, null, null, null, null),
            0.9, false, null, null);

        var result = await executor.ExecuteAsync(
            new ToolExecutionContext("admin", "edit server.cfg", route, new ConversationSelectionState(), DateTime.UtcNow),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("not_configured", result.ErrorCode);
    }

    [Fact]
    public async Task ActionExecutor_Does_Not_Block_Status_Handler_On_Clarification_Flag()
    {
        var handler = new PassThroughStatusHandler();
        var registry = new ToolRegistry(new IToolHandler[] { handler });
        var executor = new ActionExecutor(registry);
        var route = new AdminIntentRoute(
            AdminIntentType.StatusCheck,
            new AdminIntentSlots(null, null, null, null, null, ServerScopeKind.All, new[] { "monthly", "weekly" }),
            0.7, true, "Which server?", "rust.status.check");

        var result = await executor.ExecuteAsync(
            new ToolExecutionContext("admin", "all servers?", route, new ConversationSelectionState(), DateTime.UtcNow),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("status-handler-ran", result.Message);
    }

    // ── ResponseComposer ───────────────────────────────────────────────────────

    [Fact]
    public async Task ResponseComposer_Formats_Aggregate_Status_Directly()
    {
        var composer = new ResponseComposer(kernel: null, new LlmSettings { Enabled = false });
        var route = new AdminIntentRoute(
            AdminIntentType.StatusCheck,
            new AdminIntentSlots(null, null, null, null, null, ServerScopeKind.All, new[] { "monthly", "weekly", "sandbox", "staging" }),
            0.9, false, null, "rust.status.check");
        var context = new ToolExecutionContext("admin", "are all servers online?", route, new ConversationSelectionState(), DateTime.UtcNow);
        var payload = new AggregateStatusPayload(
            ServerScopeKind.All,
            new[] { "monthly", "weekly", "sandbox", "staging" },
            3,
            new[] { "sandbox" },
            new[] { "staging" },
            new[]
            {
                new AggregateStatusServerResult("monthly", "running", true, true),
                new AggregateStatusServerResult("weekly", "running", true, true),
                new AggregateStatusServerResult("sandbox", "offline", false, true),
                new AggregateStatusServerResult("staging", "unknown", false, false, "timeout")
            });

        var composed = await composer.ComposeAsync(
            context,
            new ToolExecutionResult(true, "ignored", Payload: payload, SelectedServers: payload.TargetServers, ScopeKind: ServerScopeKind.All),
            CancellationToken.None);

        Assert.StartsWith("3/4 servers are online.", composed.Message);
        Assert.Contains("Offline: sandbox.", composed.Message);
        Assert.Contains("Couldn't check: staging.", composed.Message);
    }

    [Fact]
    public async Task ResponseComposer_Includes_Conversation_History_In_Fallback_Path()
    {
        var composer = new ResponseComposer(kernel: null, new LlmSettings { Enabled = false });
        var state = new ConversationSelectionState { AdminId = "admin" };
        state.RecentMessages.Add(new ConversationMessage { Role = "user", Text = "restart alpha" });
        state.RecentMessages.Add(new ConversationMessage { Role = "assistant", Text = "Restart initiated for alpha." });

        var route = new AdminIntentRoute(
            AdminIntentType.Chat,
            new AdminIntentSlots(null, null, null, null, null),
            0.6, false, null, "rust.chat.reply");
        var context = new ToolExecutionContext("admin", "did it work?", route, state, DateTime.UtcNow);

        // LLM disabled — should fall back to template without crashing.
        var composed = await composer.ComposeAsync(
            context,
            new ToolExecutionResult(true, "Ready."),
            CancellationToken.None);

        Assert.NotEmpty(composed.Message);
        Assert.Equal("template_no_payload", composed.Source);
    }

    // ── Classifier ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Classifier_Does_Not_Recycle_Last_Server_For_Generic_It_Phrasing()
    {
        var classifier = new AdminIntentClassifier(kernel: null);
        var state = new ConversationSelectionState { AdminId = "admin", LastServerName = "alpha" };

        var route = await classifier.ClassifyAsync("what is it doing now", state, new[] { "alpha", "beta" }, CancellationToken.None);

        Assert.Null(route.Slots.ServerName);
        Assert.Equal(ServerScopeKind.Unspecified, route.Slots.ScopeKind);
    }

    [Fact]
    public async Task Classifier_Extracts_Server_Hint_From_From_Phrasing()
    {
        var classifier = new AdminIntentClassifier(kernel: null);
        var state = new ConversationSelectionState { AdminId = "admin" };

        var route = await classifier.ClassifyAsync(
            "can you give me the current playerlist from monthly ?",
            state,
            new[] { "monthly", "weekly", "alpha", "beta" },
            CancellationToken.None);

        Assert.Equal(AdminIntentType.PlayerLookup, route.Intent);
        Assert.Equal("monthly", route.Slots.ServerName);
        Assert.Equal(ServerScopeKind.Single, route.Slots.ScopeKind);
        Assert.False(route.LlmAttempted);
    }

    [Fact]
    public async Task Classifier_Resolves_All_Servers_For_Collective_Status_Request()
    {
        var classifier = new AdminIntentClassifier(kernel: null);
        var state = new ConversationSelectionState { AdminId = "admin" };
        var known = new[] { "monthly", "weekly", "sandbox", "staging" };

        var route = await classifier.ClassifyAsync("are all servers online?", state, known, CancellationToken.None);

        Assert.Equal(AdminIntentType.StatusCheck, route.Intent);
        Assert.Equal(ServerScopeKind.All, route.Slots.ScopeKind);
        Assert.Equal(4, route.Slots.ServerNames!.Count);
        Assert.False(route.NeedsClarification);
    }

    [Fact]
    public async Task Classifier_Preserves_Previous_Intent_On_Scope_Correction_FollowUp()
    {
        var classifier = new AdminIntentClassifier(kernel: null);
        var state = new ConversationSelectionState
        {
            AdminId = "admin",
            LastIntent = AdminIntentType.StatusCheck.ToString(),
            PendingClarification = new ConversationPendingClarification
            {
                Intent = AdminIntentType.StatusCheck.ToString(),
                Question = "Which server should I check?"
            }
        };
        var known = new[] { "monthly", "weekly", "sandbox", "staging" };

        var route = await classifier.ClassifyAsync("no, I meant all 4 servers", state, known, CancellationToken.None);

        Assert.Equal(AdminIntentType.StatusCheck, route.Intent);
        Assert.Equal(ServerScopeKind.All, route.Slots.ScopeKind);
        Assert.False(route.NeedsClarification);
    }

    [Fact]
    public async Task Classifier_Clarifies_For_Ambiguous_Single_Server_Status_Question()
    {
        var classifier = new AdminIntentClassifier(kernel: null);
        var state = new ConversationSelectionState { AdminId = "admin" };

        var route = await classifier.ClassifyAsync(
            "is server online?",
            state,
            new[] { "monthly", "weekly", "sandbox", "staging" },
            CancellationToken.None);

        Assert.Equal(AdminIntentType.StatusCheck, route.Intent);
        Assert.True(route.NeedsClarification);
        Assert.Equal(ServerScopeKind.Unspecified, route.Slots.ScopeKind);
    }

    // ── RustToolHelper ─────────────────────────────────────────────────────────

    [Fact]
    public void RustToolHelper_Parses_KnownServers_From_String_And_Object_Api_Shapes()
    {
        using var stringDoc = JsonDocument.Parse("""["monthly","weekly"]""");
        using var objectDoc = JsonDocument.Parse("""[{"name":"monthly","configExists":true},{"name":"weekly","configExists":true}]""");

        var fromStrings = RustToolHelper.ParseKnownServers(stringDoc.RootElement);
        var fromObjects = RustToolHelper.ParseKnownServers(objectDoc.RootElement);

        Assert.Equal(new[] { "monthly", "weekly" }, fromStrings);
        Assert.Equal(new[] { "monthly", "weekly" }, fromObjects);
    }

    // ── Network handler ────────────────────────────────────────────────────────

    [Fact]
    public void NetworkToolHandler_Uses_Custom_Interface_List()
    {
        // Verify constructor accepts custom interfaces without throwing.
        using var api = TestApi();
        var handler = new RustNetworkToolHandler(api, new[] { "ens3", "wg0" });
        Assert.Equal("rust.network.inspect", handler.Name);
    }

    // ── CommandPolicy ─────────────────────────────────────────────────────────

    [Fact]
    public void CommandRecord_AutoAllows_After_Threshold_Successes()
    {
        var record = new CommandRecord { Command = "status", SuccessCount = 4 };
        Assert.False(record.AutoAllowed);

        record.SuccessCount++;
        if (record.SuccessCount >= 5 && !record.RequiresApproval)
            record.AutoAllowed = true;

        Assert.True(record.AutoAllowed);
    }

    [Fact]
    public void CommandRecord_Requires_Approval_After_Threshold_Failures()
    {
        var record = new CommandRecord { Command = "dangerouscommand", FailCount = 1 };
        Assert.False(record.RequiresApproval);

        record.FailCount++;
        if (record.FailCount >= 2)
            record.RequiresApproval = true;

        Assert.True(record.RequiresApproval);
    }

    // ── ConversationMessage ────────────────────────────────────────────────────

    [Fact]
    public void ConversationSelectionState_Has_RecentMessages()
    {
        var state = new ConversationSelectionState { AdminId = "admin" };
        state.RecentMessages.Add(new ConversationMessage { Role = "user", Text = "hello" });
        state.RecentMessages.Add(new ConversationMessage { Role = "assistant", Text = "hi there" });

        Assert.Equal(2, state.RecentMessages.Count);
        Assert.Equal("user", state.RecentMessages[0].Role);
        Assert.Equal("assistant", state.RecentMessages[1].Role);
    }

    [Fact]
    public void ConversationSelectionState_RecentMessages_Serializes_Round_Trip()
    {
        var state = new ConversationSelectionState { AdminId = "admin" };
        state.RecentMessages.Add(new ConversationMessage { Role = "user", Text = "restart all", AtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) });

        var json = JsonSerializer.Serialize(state, JsonDefaults.Default);
        var deserialized = JsonSerializer.Deserialize<ConversationSelectionState>(json, JsonDefaults.Default);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized!.RecentMessages);
        Assert.Equal("restart all", deserialized.RecentMessages[0].Text);
    }

    // ── AutoPull ───────────────────────────────────────────────────────────────

    [Fact]
    public void AutoPullService_Initial_Status_Is_Idle()
    {
        var svc = new AutoPullService(new AutoPullSettings { Enabled = false });
        Assert.Equal("idle", svc.LastStatus.Phase);
    }

    [Fact]
    public async Task AutoPullService_Does_Not_Tick_When_Disabled()
    {
        var svc = new AutoPullService(new AutoPullSettings { Enabled = false });
        // Should not throw or change state when disabled.
        await svc.TickAsync(CancellationToken.None);
        Assert.Equal("idle", svc.LastStatus.Phase);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "rustops-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static NeoCortexStore MakeStore(string root)
    {
        return new NeoCortexStore(Path.Combine(root, "NeoCortex"), Path.Combine(root, "legacy.json"));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static SemanticMemoryService MakeSemanticMemory(string root)
    {
        var settings = new MemorySettings
        {
            DatabasePath = Path.Combine(root, "semantic.db"),
            SearchEnabled = true,
            WriteEnabled = true
        };
        return new SemanticMemoryService(
            settings,
            new SqliteMemoryStore(settings.DatabasePath),
            new FakeEmbeddingProvider(),
            Path.Combine(root, "legacy-state.json"),
            Path.Combine(root, "NeoCortex"));
    }

    private static RustOpsApiClient TestApi() =>
        new RustOpsApiClient(new ApiSettings { BaseUrl = "http://localhost:2077", ApiKey = "x" });

    private sealed class PassThroughStatusHandler : IToolHandler
    {
        public string Name => "rust.status.check";
        public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.StatusCheck };

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolExecutionResult(true, "status-handler-ran"));
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public string ModelName => "test-embedding";
        public int? Dimensions => 4;

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Embed(text));

        public Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToList());

        private static float[] Embed(string text)
        {
            var chars = (text ?? string.Empty).ToLowerInvariant().ToCharArray();
            var vector = new float[4];
            foreach (var ch in chars)
            {
                vector[ch % 4] += ch;
            }

            return vector;
        }
    }
}
