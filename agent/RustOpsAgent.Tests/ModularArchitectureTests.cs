using System.Text.Json;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Core.Interaction;
using RustOpsAgent.Domains.Integrations;
using RustOpsAgent.Infrastructure.Connectors;
using RustOpsAgent.Infrastructure;
using RustOpsAgent.Infrastructure.GitOps;
using RustOpsAgent.Infrastructure.Memory;

namespace RustOpsAgent.Tests;

public class ModularArchitectureTests
{
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
    public void NeoCortex_Migration_Creates_Banks()
    {
        var root = Path.Combine(Path.GetTempPath(), "neo-" + Guid.NewGuid().ToString("N"));
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
        var root = Path.Combine(Path.GetTempPath(), "neo-" + Guid.NewGuid().ToString("N"));
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
    public void ToolRegistry_Filters_By_Intent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "neo-" + Guid.NewGuid().ToString("N"));
        var neo = new NeoCortexStore(Path.Combine(tempRoot, "NeoCortex"), Path.Combine(tempRoot, "legacy.json"));
        neo.EnsureMigrated();

        var handlers = new IToolHandler[]
        {
            new ConnectorStatusToolHandler(Array.Empty<IConnectorLogSource>()),
            new ConnectorLogsToolHandler(Array.Empty<IConnectorLogSource>(), neo),
            new AgentChatToolHandler()
        };

        var registry = new ToolRegistry(handlers);
        var route = new AdminIntentRoute(
            AdminIntentType.StatusCheck,
            new AdminIntentSlots("alpha", null, null, null, null),
            0.9,
            false,
            null,
            null);

        var eligible = registry.ResolveEligible(route);
        Assert.Contains(eligible, h => h.Name == "integrations.connector.status");
        Assert.DoesNotContain(eligible, h => h.Name == "agent.chat.reply");
    }

    [Fact]
    public void ToolRegistry_Uses_TargetRef_For_Diagnostics()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "neo-" + Guid.NewGuid().ToString("N"));
        var neo = new NeoCortexStore(Path.Combine(tempRoot, "NeoCortex"), Path.Combine(tempRoot, "legacy.json"));
        neo.EnsureMigrated();

        var handlers = new IToolHandler[]
        {
            new ConnectorStatusToolHandler(Array.Empty<IConnectorLogSource>()),
            new ConnectorLogsToolHandler(Array.Empty<IConnectorLogSource>(), neo),
            new AgentChatToolHandler()
        };

        var registry = new ToolRegistry(handlers);
        var route = new AdminIntentRoute(
            AdminIntentType.Troubleshooting,
            new AdminIntentSlots("alpha", null, null, null, null),
            0.9,
            false,
            null,
            "integrations.logs.inspect");

        var selected = registry.ResolveSingle(new ToolExecutionContext("admin", "check alpha", route, new ConversationSelectionState(), DateTime.UtcNow));
        Assert.NotNull(selected);
        Assert.Equal("integrations.logs.inspect", selected!.Name);
    }

    [Fact]
    public void ConfigLoader_Rejects_Unresolved_Required_Values()
    {
        var root = Path.Combine(Path.GetTempPath(), "cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
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

    [Fact]
    public void LegacyStateStore_Writes_Api_Compatible_Action_History()
    {
        var root = Path.Combine(Path.GetTempPath(), "legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
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
        var root = Path.Combine(Path.GetTempPath(), "legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var statePath = Path.Combine(root, "agent-state.json");
        var store = new LegacyAgentStateStore(statePath);
        store.RecordFeedback("42", "a-1", "good", "nice", "alpha");
        store.Save();

        using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
        Assert.False(doc.RootElement.TryGetProperty("llmInteractions", out _));
        Assert.False(doc.RootElement.TryGetProperty("capabilityGaps", out _));
        Assert.False(doc.RootElement.TryGetProperty("selfRepairHistory", out _));
    }

    [Fact]
    public async Task ActionExecutor_Returns_Explicit_NotImplemented_For_FileEdit()
    {
        var registry = new ToolRegistry(new IToolHandler[] { new AgentChatToolHandler() });
        var executor = new ActionExecutor(registry);
        var route = new AdminIntentRoute(
            AdminIntentType.FileEdit,
            new AdminIntentSlots(null, null, null, null, null),
            0.9,
            false,
            null,
            null);

        var result = await executor.ExecuteAsync(
            new ToolExecutionContext("admin", "edit server.cfg", route, new ConversationSelectionState(), DateTime.UtcNow),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("not_implemented", result.ErrorCode);
    }

    [Fact]
    public async Task Classifier_Does_Not_Recycle_Last_Server_For_Generic_It_Phrasing()
    {
        var classifier = new AdminIntentClassifier(kernel: null);
        var state = new ConversationSelectionState { AdminId = "admin", LastServerName = "alpha" };

        var route = await classifier.ClassifyAsync("what is it doing now", state, CancellationToken.None);

        Assert.Null(route.Slots.ServerName);
    }

    [Fact]
    public async Task Classifier_Extracts_Source_Hint_From_From_Phrasing()
    {
        var classifier = new AdminIntentClassifier(kernel: null);
        var state = new ConversationSelectionState { AdminId = "admin" };

        var route = await classifier.ClassifyAsync("can you inspect logs from monthly for failures?", state, CancellationToken.None);

        Assert.Equal(AdminIntentType.Troubleshooting, route.Intent);
        Assert.Equal("monthly", route.Slots.ServerName);
        Assert.False(route.LlmAttempted);
    }
}
