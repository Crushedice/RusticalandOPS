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
    public void ToolRegistry_Filters_By_Intent()
    {
        using var api = new RustOpsApiClient(new ApiSettings { BaseUrl = "http://localhost:2077", ApiKey = "x" });
        var tempRoot = Path.Combine(Path.GetTempPath(), "neo-" + Guid.NewGuid().ToString("N"));
        var neo = new NeoCortexStore(Path.Combine(tempRoot, "NeoCortex"), Path.Combine(tempRoot, "legacy.json"));
        neo.EnsureMigrated();

        var handlers = new IToolHandler[]
        {
            new RustServerControlToolHandler(api),
            new RustStatusToolHandler(api),
            new RustChatToolHandler()
        };

        var registry = new ToolRegistry(handlers);
        var route = new AdminIntentRoute(
            AdminIntentType.ServerControl,
            new AdminIntentSlots("alpha", null, null, null, null),
            0.9,
            false,
            null,
            null);

        var eligible = registry.ResolveEligible(route);
        Assert.Contains(eligible, h => h.Name == "rust.server.control");
        Assert.DoesNotContain(eligible, h => h.Name == "rust.status.check");
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
}
