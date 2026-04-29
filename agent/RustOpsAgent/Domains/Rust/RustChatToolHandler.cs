using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Infrastructure;
using RustOpsAgent.Infrastructure.Memory;

namespace RustOpsAgent.Domains.Rust;

internal sealed class RustChatToolHandler : IToolHandler
{
    private readonly NeoCortexStore _memory;
    private readonly ISemanticMemoryService _semanticMemory;
    private readonly AutoPullService? _autoPull;
    private readonly ServerKnowledgeCatalog _knowledge;

    public RustChatToolHandler(NeoCortexStore memory, ISemanticMemoryService semanticMemory, AutoPullService? autoPull = null, ServerKnowledgeCatalog? knowledge = null)
    {
        _memory = memory;
        _semanticMemory = semanticMemory;
        _autoPull = autoPull;
        _knowledge = knowledge ?? new ServerKnowledgeCatalog();
    }

    public string Name => "rust.chat.reply";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.Chat, AdminIntentType.Clarification };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Route.Intent == AdminIntentType.Clarification)
        {
            var question = context.Route.ClarificationQuestion
                ?? "Could you clarify what you need? Which server and what action?";
            return new ToolExecutionResult(true, question, context.SelectionState.LastServerName);
        }

        var memoryCommandResult = await TryHandleMemoryCommandAsync(context, cancellationToken);
        if (memoryCommandResult is not null)
        {
            return memoryCommandResult;
        }

        var catalogQuestionResult = TryHandleCatalogQuestion(context);
        if (catalogQuestionResult is not null)
        {
            return catalogQuestionResult;
        }

        // Detect and handle git pull/rebuild operations
        var messageLowered = context.Message.ToLowerInvariant();
        if (IsGitPullRebuildRequest(messageLowered) && _autoPull != null)
        {
            try
            {
                var status = await _autoPull.TriggerAsync(cancellationToken);
                var resultMessage = $"Pull/rebuild: {status.Phase}. {status.Output}";
                return new ToolExecutionResult(
                    status.Phase != "error",
                    resultMessage,
                    null,
                    Payload: new { autoPullPhase = status.Phase, autoPullOutput = status.Output, autoPullError = status.Error });
            }
            catch (Exception ex)
            {
                return new ToolExecutionResult(
                    false,
                    $"Pull/rebuild failed: {ex.Message}",
                    null,
                    Payload: new { autoPullError = ex.Message });
            }
        }

        // Detect mentioned server convars/commands and inject knowledge
        var knowledgeMatch = _knowledge.FindMentionedEntry(context.Message);
        if (knowledgeMatch is not null)
        {
            await TryInjectCatalogFactAsync(knowledgeMatch, cancellationToken);
        }

        object? payload = null;
        try
        {
            var ops = _memory.LoadOperations();
            var logs = _memory.LoadLogs();

            var recentActions = ops.RecentActions
                .TakeLast(5)
                .Select(a => $"{a.Intent} on {a.ServerName ?? "?"}: {a.Result} ({FormatAge(a.TimestampUtc)})")
                .ToList();

            var highImportanceLogs = logs.RecentEntries
                .Where(e => e.Importance >= 3)
                .TakeLast(4)
                .Select(e => $"[{e.ServerName}] {e.Line}")
                .ToList();

            var openIncidents = 0;
            try
            {
                var review = _memory.ReviewAsync(CancellationToken.None).GetAwaiter().GetResult();
                openIncidents = review.OpenIncidents.Count;
            }
            catch { /* non-critical */ }

            var knownConvarOrCommand = knowledgeMatch is not null ? $"{knowledgeMatch.EntryType}: {knowledgeMatch.Name}" : null;

            payload = new
            {
                recentActions,
                highImportanceLogs,
                openIncidents,
                lastServer = context.SelectionState.LastServerName ?? "none",
                lastIntent = context.SelectionState.LastIntent ?? "none",
                llmEnabled = ops.RuntimeStatus?.LlmEnabled ?? false,
                knownConvarOrCommand
            };
        }
        catch
        {
            // Compose with no payload — LLM will still answer.
        }

        return new ToolExecutionResult(
            true,
            "Ready.",
            null,  // Chat operations are not server-specific
            Payload: payload);
    }

    private static bool IsGitPullRebuildRequest(string loweredMessage) =>
        (loweredMessage.Contains("pull", StringComparison.Ordinal) && loweredMessage.Contains("main", StringComparison.Ordinal)) ||
        (loweredMessage.Contains("git", StringComparison.Ordinal) && loweredMessage.Contains("pull", StringComparison.Ordinal)) ||
        (loweredMessage.Contains("rebuild", StringComparison.Ordinal) && (loweredMessage.Contains("pull", StringComparison.Ordinal) || loweredMessage.Contains("code", StringComparison.Ordinal) || loweredMessage.Contains("agent", StringComparison.Ordinal)));

    private async Task<ToolExecutionResult?> TryHandleMemoryCommandAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var message = context.Message.Trim();
        var lowered = message.ToLowerInvariant();
        if (!lowered.StartsWith("memory", StringComparison.Ordinal))
        {
            return null;
        }

        if (lowered.StartsWith("memory stats", StringComparison.Ordinal))
        {
            var stats = await _semanticMemory.GetStatsAsync(cancellationToken);
            var byType = string.Join(", ", stats.ByType.OrderByDescending(item => item.Value).Select(item => $"{item.Key}={item.Value}"));
            return new ToolExecutionResult(true, $"Memory stats: total={stats.TotalRecords}, active={stats.ActiveRecords}, expired={stats.ExpiredRecords}. Types: {byType}");
        }

        if (lowered.StartsWith("memory search ", StringComparison.Ordinal))
        {
            var query = message["memory search ".Length..].Trim();
            var results = await _semanticMemory.SearchAsync(query, 5, cancellationToken);
            if (results.Count == 0)
            {
                return new ToolExecutionResult(true, "No matching semantic memories found.");
            }

            var lines = results.Select(item => $"{item.MemoryRecord.Id} [{item.MemoryRecord.Type}/{item.MemoryRecord.Scope}] {item.MemoryRecord.Summary} (score {item.FinalScore:F2})");
            return new ToolExecutionResult(true, string.Join('\n', lines));
        }

        if (lowered.StartsWith("memory show ", StringComparison.Ordinal))
        {
            var id = message["memory show ".Length..].Trim();
            var record = await _semanticMemory.GetByIdAsync(id, cancellationToken);
            return record is null
                ? new ToolExecutionResult(false, $"No memory record found for id '{id}'.")
                : new ToolExecutionResult(true, $"Id: {record.Id}\nType: {record.Type}\nScope: {record.Scope}\nSummary: {record.Summary}\nText: {record.Text}");
        }

        if (lowered.StartsWith("memory delete ", StringComparison.Ordinal))
        {
            var id = message["memory delete ".Length..].Trim();
            await _semanticMemory.DeleteAsync(id, cancellationToken);
            return new ToolExecutionResult(true, $"Deleted memory record '{id}'.", MutatedState: true);
        }

        if (lowered.StartsWith("memory recent", StringComparison.Ordinal))
        {
            var records = await _semanticMemory.ListRecentAsync(10, cancellationToken);
            if (records.Count == 0)
            {
                return new ToolExecutionResult(true, "No memories stored yet.");
            }

            var lines = records.Select(record => $"{record.Id} [{record.Type}/{record.Scope}] {record.Summary}");
            return new ToolExecutionResult(true, string.Join('\n', lines));
        }

        if (lowered.StartsWith("memory repeated failures", StringComparison.Ordinal))
        {
            var groups = await _semanticMemory.ListRepeatedFailuresAsync(2, cancellationToken);
            if (groups.Count == 0)
            {
                return new ToolExecutionResult(true, "No repeated failure clusters found.");
            }

            var lines = groups.Select(group => $"{group.Count()}x {group.Key}: {group.First().Summary}");
            return new ToolExecutionResult(true, string.Join('\n', lines));
        }

        if (lowered.StartsWith("memory rebuild", StringComparison.Ordinal))
        {
            var rebuilt = await _semanticMemory.RebuildEmbeddingsAsync(cancellationToken);
            return new ToolExecutionResult(true, $"Rebuilt embeddings for {rebuilt} memory record(s).", MutatedState: rebuilt > 0);
        }

        if (lowered.StartsWith("memory migrate", StringComparison.Ordinal))
        {
            var dryRun = lowered.Contains("dry-run", StringComparison.Ordinal) || lowered.Contains("dry run", StringComparison.Ordinal);
            var report = await _semanticMemory.MigrateLegacyMemoryAsync(dryRun, cancellationToken);
            return new ToolExecutionResult(true, $"Migration complete: {report.ToSummary()}", MutatedState: report.RecordsImported > 0);
        }

        if (lowered.StartsWith("memory prune", StringComparison.Ordinal))
        {
            var pruned = await _semanticMemory.PruneAsync(cancellationToken);
            return new ToolExecutionResult(true, $"Pruned {pruned} memory record(s).", MutatedState: pruned > 0);
        }

        if (lowered.StartsWith("memory import server catalog", StringComparison.Ordinal) ||
            lowered.StartsWith("memory import convar catalog", StringComparison.Ordinal))
        {
            return await ImportServerCatalogMemoryAsync(message, lowered, cancellationToken);
        }

        if (lowered.StartsWith("memory add ", StringComparison.Ordinal))
        {
            var payload = message["memory add ".Length..].Trim();
            var separator = payload.IndexOf("::", StringComparison.Ordinal);
            if (separator < 0)
            {
                return new ToolExecutionResult(false, "Use: memory add <summary> :: <detail>");
            }

            var summary = payload[..separator].Trim();
            var detail = payload[(separator + 2)..].Trim();
            var record = await _semanticMemory.AddManualMemoryAsync(new ManualMemoryInput
            {
                Summary = summary,
                Text = detail
            }, cancellationToken);
            return new ToolExecutionResult(true, $"Added memory record {record.Id}.", MutatedState: true);
        }

        return new ToolExecutionResult(false, "Unknown memory command. Try: memory stats | memory search <query> | memory recent | memory show <id> | memory delete <id> | memory repeated failures | memory rebuild | memory migrate | memory prune | memory add <summary> :: <detail>");
    }

    private ToolExecutionResult? TryHandleCatalogQuestion(ToolExecutionContext context)
    {
        var lowered = context.Message.ToLowerInvariant();
        var looksLikeConvarQuestion =
            lowered.Contains("convar", StringComparison.Ordinal) ||
            lowered.Contains("server variable", StringComparison.Ordinal) ||
            lowered.Contains("variables", StringComparison.Ordinal);

        if (!looksLikeConvarQuestion)
        {
            return null;
        }

        var snapshot = _knowledge.GetSnapshot();
        var matches = _knowledge.SearchVariables(context.Message, 10);
        if (matches.Count == 0)
        {
            return new ToolExecutionResult(
                true,
                $"I couldn't find matching server variables in the local catalog. Catalog file: `{snapshot.VariablesPath ?? "not found"}`.",
                ErrorCode: "authoritative_catalog");
        }

        var lines = matches.Select(FormatVariableLine);
        var message = "From the local server variable catalog:\n" + string.Join('\n', lines);
        return new ToolExecutionResult(
            true,
            message,
            Payload: new { source = "server-variable-catalog", variablesPath = snapshot.VariablesPath, count = matches.Count },
            ErrorCode: "authoritative_catalog");
    }

    private async Task<ToolExecutionResult> ImportServerCatalogMemoryAsync(string message, string lowered, CancellationToken cancellationToken)
    {
        var dryRun = lowered.Contains("dry-run", StringComparison.Ordinal) || lowered.Contains("dry run", StringComparison.Ordinal);
        var limit = TryExtractLimit(message);
        var snapshot = _knowledge.GetSnapshot();
        var variables = snapshot.Variables.Values.OrderBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var commands = snapshot.Commands.Values.OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var entries = variables.Select(variable => (summary: $"Rust server convar: {variable.Name}", text: BuildVariableMemoryText(variable), tags: new[] { "server-catalog", "convar", variable.Name.ToLowerInvariant() }))
            .Concat(commands.Select(command => (summary: $"Rust server command: {command.Name}", text: BuildCommandMemoryText(command), tags: new[] { "server-catalog", "command", command.Name.ToLowerInvariant() })))
            .Take(limit ?? int.MaxValue)
            .ToList();

        if (entries.Count == 0)
        {
            return new ToolExecutionResult(false, $"No catalog entries were found. Variables: `{snapshot.VariablesPath ?? "not found"}`, commands: `{snapshot.CommandsPath ?? "not found"}`.", ErrorCode: "catalog_not_found");
        }

        if (dryRun)
        {
            return new ToolExecutionResult(true, $"Dry run: would import {entries.Count} catalog memory record(s). Variables={variables.Count}, commands={commands.Count}.", ErrorCode: "authoritative_catalog");
        }

        var before = await _semanticMemory.GetStatsAsync(cancellationToken);
        var attempted = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted++;
            await _semanticMemory.AddManualMemoryAsync(new ManualMemoryInput
            {
                Type = MemoryRecordType.Fact,
                Scope = MemoryScope.Global,
                Source = MemorySource.ManualImport,
                Summary = entry.summary,
                Text = entry.text,
                Tags = entry.tags.ToList(),
                RelatedEntityIds = entry.tags.ToList(),
                Importance = 0.9,
                Confidence = 0.98,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = "server-catalog",
                    ["variablesPath"] = snapshot.VariablesPath ?? string.Empty,
                    ["commandsPath"] = snapshot.CommandsPath ?? string.Empty
                }
            }, cancellationToken);
        }

        var after = await _semanticMemory.GetStatsAsync(cancellationToken);
        var imported = Math.Max(0, after.TotalRecords - before.TotalRecords);
        return new ToolExecutionResult(
            true,
            $"Catalog memory import attempted {attempted} record(s); {imported} new record(s) are now stored. Duplicates are skipped by content hash. Use `memory import server catalog limit 20 dry-run` for a small test batch.",
            MutatedState: imported > 0,
            ErrorCode: "authoritative_catalog");
    }

    private static int? TryExtractLimit(string message)
    {
        var match = System.Text.RegularExpressions.Regex.Match(message, @"\blimit\s+(?<limit>\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["limit"].Value, out var limit) && limit > 0 ? limit : null;
    }

    private static string FormatVariableLine(ServerVariableDefinition variable)
    {
        var description = string.IsNullOrWhiteSpace(variable.Description) ? "No description in catalog." : variable.Description;
        var generated = variable.Generated ? "Generated" : "Not generated";
        var typeLabel = string.IsNullOrWhiteSpace(variable.DefaultType) ? string.Empty : $", {variable.DefaultType}";
        var defaultValue = string.IsNullOrWhiteSpace(variable.DefaultValue) ? "unknown" : variable.DefaultValue;
        return $"- `{variable.Name}` - {description} (default `{defaultValue}`{typeLabel}; {generated})";
    }

    private static string BuildVariableMemoryText(ServerVariableDefinition variable) =>
        $"Type: Rust server convar\nName: {variable.Name}\nDescription: {variable.Description ?? "No description in catalog."}\nDefault: {variable.DefaultValue ?? "unknown"}\nDefaultType: {variable.DefaultType ?? "unknown"}\nGeneratedOnStartup: {variable.Generated}";

    private static string BuildCommandMemoryText(ServerCommandDefinition command) =>
        $"Type: Rust server command\nName: {command.Name}\nDescription: {command.Description ?? "No description in catalog."}\nRisk: {command.RiskLevel ?? "unknown"}\nGeneratedMetadata: {command.Generated}\nTags: {string.Join(", ", command.Tags ?? Array.Empty<string>())}";

    private static string FormatAge(DateTime ts)
    {
        var age = DateTime.UtcNow - ts;
        if (age.TotalMinutes < 2) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    private async Task TryInjectCatalogFactAsync(CatalogLookupMatch match, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(match.Description))
        {
            return;
        }

        try
        {
            var type = match.EntryType == CatalogEntryType.Variable ? "Server Variable" : "Server Command";
            var summary = $"Server knowledge: {type} '{match.Name}'";
            var text = $"Type: {type}\nName: {match.Name}\nDescription: {match.Description}";
            var tags = new[] { "server-knowledge", match.EntryType.ToString().ToLowerInvariant(), match.Name.ToLowerInvariant() };

            await _semanticMemory.RecordServerFactAsync(
                "server-reference",
                summary,
                text,
                tags,
                cancellationToken);
        }
        catch
        {
            // Non-critical — continue if memory injection fails
        }
    }
}
