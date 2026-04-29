using System.Text.Json.Nodes;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Core.Interaction;
using RustOpsAgent.Domains.Rust;

namespace RustOpsAgent.Tests;

public class CatalogAndConfigTests
{
    // ── JSONL variable parsing ────────────────────────────────────────────────

    [Fact]
    public void Catalog_ParseVariableEntry_Parses_Full_Jsonl_Line()
    {
        var line = """{"line":1,"convar":"ai.move","category":"ai","name":"move","generated_on_start":true,"default_raw":"True","default_type":"boolean","default_value":true,"description":"When enabled, AI entities move toward NavMesh destinations","raw":"ai.move (Generated) When enabled, AI entities move toward NavMesh destinations (True)"}""";

        var parsed = ServerKnowledgeCatalog.ParseVariableEntry(line);

        Assert.NotNull(parsed);
        Assert.Equal("ai.move", parsed!.Name);
        Assert.True(parsed.Generated);
        Assert.Equal("True", parsed.DefaultValue);
        Assert.Equal("boolean", parsed.DefaultType);
        Assert.Contains("AI entities move", parsed.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_ParseVariableEntry_Returns_Null_For_Empty_Line()
    {
        Assert.Null(ServerKnowledgeCatalog.ParseVariableEntry(string.Empty));
        Assert.Null(ServerKnowledgeCatalog.ParseVariableEntry("   "));
    }

    [Fact]
    public void Catalog_ParseVariableEntry_Returns_Null_For_Non_Json()
    {
        Assert.Null(ServerKnowledgeCatalog.ParseVariableEntry("not json at all"));
    }

    [Fact]
    public void Catalog_ParseVariableEntry_Handles_Missing_Description()
    {
        var line = """{"convar":"server.fps","generated_on_start":false,"default_raw":"256","default_type":"integer","default_value":256,"description":""}""";

        var parsed = ServerKnowledgeCatalog.ParseVariableEntry(line);

        Assert.NotNull(parsed);
        Assert.Equal("server.fps", parsed!.Name);
        Assert.Equal("256", parsed.DefaultValue);
        Assert.Null(parsed.Description);
    }

    // ── JSONL command parsing ─────────────────────────────────────────────────

    [Fact]
    public void Catalog_ParseCommandEntry_Parses_Full_Jsonl_Line()
    {
        var line = """{"line":1,"command":"global.kick","category":"global","name":"kick","signature":"global.kick()","arguments_declared_inside_parentheses":"","generated_command_metadata":true,"usage_or_argument_hint":"","argument_placeholders_in_text":[],"description":"Kicks a player with an optional reason","risk_level_inferred":"medium","tags":["generated","admin","risk:medium"],"raw_source_line":"global.kick(  ) (Generated) Kicks a player with an optional reason","parse_ok":true}""";

        var parsed = ServerKnowledgeCatalog.ParseCommandEntry(line);

        Assert.NotNull(parsed);
        Assert.Equal("global.kick", parsed!.Name);
        Assert.True(parsed.Generated);
        Assert.Equal("medium", parsed.RiskLevel);
        Assert.Contains("Kicks a player", parsed.Description, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(parsed.Tags);
        Assert.Contains("admin", parsed.Tags!);
    }

    [Fact]
    public void Catalog_ParseCommandEntry_Returns_Null_For_Empty_Line()
    {
        Assert.Null(ServerKnowledgeCatalog.ParseCommandEntry(string.Empty));
    }

    [Fact]
    public void Catalog_ParseCommandEntry_Handles_Missing_Description()
    {
        var line = """{"command":"baseboat.seconds_between_shore_drift","generated_command_metadata":false,"description":"","risk_level_inferred":"low","tags":[]}""";

        var parsed = ServerKnowledgeCatalog.ParseCommandEntry(line);

        Assert.NotNull(parsed);
        Assert.Equal("baseboat.seconds_between_shore_drift", parsed!.Name);
        Assert.Equal("low", parsed.RiskLevel);
        Assert.Null(parsed.Description);
    }

    // ── TXT fallback parsers (backward compat) ────────────────────────────────

    [Fact]
    public void Catalog_ParseVariableLine_Txt_Parses_Generated_With_Default()
    {
        var line = "ai.move (Generated) When enabled, AI entities move toward NavMesh destinations (True)";

        var parsed = ServerKnowledgeCatalog.ParseVariableLine(line);

        Assert.NotNull(parsed);
        Assert.Equal("ai.move", parsed!.Name);
        Assert.True(parsed.Generated);
        Assert.Equal("True", parsed.DefaultValue);
        Assert.Contains("AI entities move", parsed.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_ParseCommandLine_Txt_Parses_Generated_With_Description()
    {
        var line = "global.kick(  ) (Generated) Kicks a player with an optional reason";

        var parsed = ServerKnowledgeCatalog.ParseCommandLine(line);

        Assert.NotNull(parsed);
        Assert.Equal("global.kick", parsed!.Name);
        Assert.True(parsed.Generated);
        Assert.Contains("Kicks a player", parsed.Description, StringComparison.OrdinalIgnoreCase);
    }

    // ── RustFileEditToolHandler helpers ───────────────────────────────────────

    [Fact]
    public void FileEdit_ExtractsServerConfigKeyAlias()
    {
        var key = RustFileEditToolHandler.TryExtractConfigLookupKey("what's the worldsize of monthly server", includeAliases: true);

        Assert.Equal("server.worldsize", key);
    }

    [Fact]
    public void FileEdit_ReadsValueFromConfig()
    {
        var config = new JsonObject
        {
            ["server.worldsize"] = 4250
        };

        var found = RustFileEditToolHandler.TryReadConfigValue(config, "server.worldsize", out var resolved, out var value);

        Assert.True(found);
        Assert.Equal("server.worldsize", resolved);
        Assert.Equal("4250", value?.ToJsonString());
    }

    // ── AdminIntentClassifier routing ─────────────────────────────────────────

    [Fact]
    public async Task Classifier_Routes_Server_Variable_Question_To_Rcon()
    {
        var classifier = new AdminIntentClassifier(kernel: null, settings: new LlmSettings { Enabled = false }, neoCortex: null);
        var state = new ConversationSelectionState { AdminId = "admin" };

        var route = await classifier.ClassifyAsync(
            "what does ai.move do on monthly?",
            state,
            new[] { "monthly", "weekly" },
            CancellationToken.None);

        Assert.Equal(AdminIntentType.RconCommand, route.Intent);
        Assert.Equal("rust.rcon.command", route.TargetRef);
        Assert.Equal("monthly", route.Slots.ServerName, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Classifier_Routes_Worldsize_Value_Query_To_FileEdit()
    {
        var classifier = new AdminIntentClassifier(kernel: null, settings: new LlmSettings { Enabled = false }, neoCortex: null);
        var state = new ConversationSelectionState { AdminId = "admin" };

        var route = await classifier.ClassifyAsync(
            "whats the worldsize of monthly server",
            state,
            new[] { "monthly", "weekly" },
            CancellationToken.None);

        Assert.Equal(AdminIntentType.FileEdit, route.Intent);
        Assert.Equal("rust.file.edit", route.TargetRef);
        Assert.Equal("monthly", route.Slots.ServerName, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Classifier_Routes_Git_Pull_To_Chat()
    {
        var classifier = new AdminIntentClassifier(kernel: null, settings: new LlmSettings { Enabled = false }, neoCortex: null);
        var state = new ConversationSelectionState { AdminId = "admin" };

        var route = await classifier.ClassifyAsync(
            "can you pull from main and rebuild?",
            state,
            new[] { "monthly", "weekly" },
            CancellationToken.None);

        Assert.Equal(AdminIntentType.Chat, route.Intent);
        Assert.Equal("rust.chat.reply", route.TargetRef);
    }

    [Fact]
    public async Task Classifier_Routes_Compile_Errors_To_Troubleshooting()
    {
        var classifier = new AdminIntentClassifier(kernel: null, settings: new LlmSettings { Enabled = false }, neoCortex: null);
        var state = new ConversationSelectionState { AdminId = "admin" };

        var route = await classifier.ClassifyAsync(
            "compile errors on monthly",
            state,
            new[] { "monthly", "weekly" },
            CancellationToken.None);

        Assert.Equal(AdminIntentType.Troubleshooting, route.Intent);
        Assert.Equal("rust.plugins.verify", route.TargetRef);
    }

    [Fact]
    public void Catalog_SearchVariables_Finds_Pve_Convars_From_Broad_Query()
    {
        var root = Path.Combine(Path.GetTempPath(), "rustops-catalog-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var variablesPath = Path.Combine(root, "ServerVariables.agent-readable.jsonl");
        var commandsPath = Path.Combine(root, "ServerCommands.agent-readable.jsonl");
        File.WriteAllLines(variablesPath, new[]
        {
            """{"convar":"server.pve","generated_on_start":true,"default_raw":"False","default_type":"boolean","description":"Enables PvE mode - players cannot damage other players; they can still be killed by NPCs and the environment"}""",
            """{"convar":"server.pvebulletdamagemultiplier","generated_on_start":true,"default_raw":"1","default_type":"integer","description":"Additional bullet damage multiplier applied only when players shoot NPCs or animals, stacks with bulletdamage"}""",
            """{"convar":"server.fps","generated_on_start":false,"default_raw":"256","default_type":"integer","description":""}"""
        });
        File.WriteAllText(commandsPath, string.Empty);
        var catalog = new ServerKnowledgeCatalog(variablesPath, commandsPath);

        var matches = catalog.SearchVariables("what are the PVE convars", 5);

        Assert.Collection(
            matches.Take(2),
            first => Assert.Equal("server.pve", first.Name),
            second => Assert.Equal("server.pvebulletdamagemultiplier", second.Name));
    }
}
