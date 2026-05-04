using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Infrastructure;
using RustOpsAgent.Infrastructure.Memory;

namespace RustOpsAgent.Domains.Rust;

internal sealed class RustChatToolHandler : IToolHandler
{
    private readonly NeoCortexStore _memory;
    private readonly ISemanticMemoryService _semanticMemory;
    private readonly IMemoryImportService? _memoryImport;
    private readonly PluginReferenceIndexer? _pluginReferenceIndexer;
    private readonly AutoPullService? _autoPull;
    private readonly ServerKnowledgeCatalog _knowledge;

    public RustChatToolHandler(
        NeoCortexStore memory,
        ISemanticMemoryService semanticMemory,
        AutoPullService? autoPull = null,
        ServerKnowledgeCatalog? knowledge = null,
        IMemoryImportService? memoryImport = null,
        PluginReferenceIndexer? pluginReferenceIndexer = null)
    {
        _memory = memory;
        _semanticMemory = semanticMemory;
        _memoryImport = memoryImport;
        _pluginReferenceIndexer = pluginReferenceIndexer;
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

        var pluginIndexCommandResult = await TryHandlePluginIndexCommandAsync(context, cancellationToken);
        if (pluginIndexCommandResult is not null)
        {
            return pluginIndexCommandResult;
        }

        var pluginReferenceResult = await TryHandlePluginReferenceQuestionAsync(context, cancellationToken);
        if (pluginReferenceResult is not null)
        {
            return pluginReferenceResult;
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
        var message = context.Message.Trim().TrimStart('/');
        var lowered = message.ToLowerInvariant();
        if (!lowered.StartsWith("memory", StringComparison.Ordinal))
        {
            return null;
        }

        if (lowered.StartsWith("memory import ", StringComparison.Ordinal) &&
            !lowered.StartsWith("memory import server catalog", StringComparison.Ordinal) &&
            !lowered.StartsWith("memory import convar catalog", StringComparison.Ordinal))
        {
            if (_memoryImport is null)
            {
                return new ToolExecutionResult(false, "Memory import service is not configured.", ErrorCode: "not_configured");
            }

            var args = message["memory import ".Length..].Trim();
            var trusted = args.Contains("--trusted", StringComparison.OrdinalIgnoreCase);
            var dryRun = args.Contains("--dry-run", StringComparison.OrdinalIgnoreCase);
            var folder = args
                .Replace("--trusted", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("--dry-run", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(folder))
            {
                return new ToolExecutionResult(false, "Use: /memory import <folderPath> [--trusted] [--dry-run]");
            }

            var report = await _memoryImport.ImportFolderAsync(new MemoryImportOptions { FolderPath = folder, Trusted = trusted, DryRun = dryRun }, cancellationToken);
            var suffix = report.Messages.Count == 0 ? string.Empty : "\n" + string.Join('\n', report.Messages.Take(5));
            return new ToolExecutionResult(report.Errors == 0, $"Memory import complete: {report.ToSummary()}{suffix}", MutatedState: report.Imported > 0);
        }

        if (lowered.StartsWith("memory pending", StringComparison.Ordinal))
        {
            var records = await _semanticMemory.ListPendingAsync(25, cancellationToken);
            if (records.Count == 0)
            {
                return new ToolExecutionResult(true, "No pending memory records.");
            }

            var lines = records.Select(record => $"{record.Id} [{record.Source}] {record.Summary} confidence={record.Confidence:F2} source={record.SourcePath}");
            return new ToolExecutionResult(true, string.Join('\n', lines));
        }

        if (lowered.StartsWith("memory approve ", StringComparison.Ordinal))
        {
            var id = message["memory approve ".Length..].Trim();
            var updated = await _semanticMemory.SetApprovalStateAsync(id, MemoryApprovalState.Active, cancellationToken);
            return updated
                ? new ToolExecutionResult(true, $"Approved memory record '{id}'.", MutatedState: true)
                : new ToolExecutionResult(false, $"No memory record found for id '{id}'.");
        }

        if (lowered.StartsWith("memory reject ", StringComparison.Ordinal))
        {
            var id = message["memory reject ".Length..].Trim();
            var updated = await _semanticMemory.SetApprovalStateAsync(id, MemoryApprovalState.Rejected, cancellationToken);
            return updated
                ? new ToolExecutionResult(true, $"Rejected memory record '{id}'.", MutatedState: true)
                : new ToolExecutionResult(false, $"No memory record found for id '{id}'.");
        }

        if (lowered.StartsWith("memory forget ", StringComparison.Ordinal))
        {
            var id = message["memory forget ".Length..].Trim();
            await _semanticMemory.DeleteAsync(id, cancellationToken);
            return new ToolExecutionResult(true, $"Forgot memory record '{id}'.", MutatedState: true);
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

    private async Task<ToolExecutionResult?> TryHandlePluginIndexCommandAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var message = context.Message.Trim().TrimStart('/');
        var lowered = message.ToLowerInvariant();
        if (!lowered.StartsWith("plugin-index", StringComparison.Ordinal))
        {
            return null;
        }

        if (_pluginReferenceIndexer is null)
        {
            return new ToolExecutionResult(false, "Plugin reference index is not configured.", ErrorCode: "not_configured");
        }

        if (lowered.StartsWith("plugin-index refresh", StringComparison.Ordinal))
        {
            var report = await _pluginReferenceIndexer.RefreshAllAsync(cancellationToken);
            var suffix = report.Messages.Count == 0 ? string.Empty : "\n" + string.Join('\n', report.Messages.Take(5));
            return new ToolExecutionResult(report.Errors == 0, $"Plugin index refresh complete: {report.ToSummary()}{suffix}", MutatedState: report.Indexed > 0);
        }

        if (lowered.StartsWith("plugin-index search ", StringComparison.Ordinal))
        {
            var query = message["plugin-index search ".Length..].Trim();
            var matches = await _pluginReferenceIndexer.SearchAsync(query, cancellationToken);
            return new ToolExecutionResult(true, FormatPluginSearch(matches, includeAdmin: true));
        }

        if (lowered.StartsWith("plugin-index commands", StringComparison.Ordinal))
        {
            var pluginName = message.Length > "plugin-index commands".Length ? message["plugin-index commands".Length..].Trim() : string.Empty;
            var records = string.IsNullOrWhiteSpace(pluginName)
                ? await _pluginReferenceIndexer.ListAsync(cancellationToken)
                : await _pluginReferenceIndexer.SearchAsync(pluginName, cancellationToken);
            var lines = records
                .Where(record => string.IsNullOrWhiteSpace(pluginName) || record.PluginName.Contains(pluginName, StringComparison.OrdinalIgnoreCase))
                .SelectMany(record => record.Commands.Select(command => $"{record.PluginName}: {FormatCommand(command, includeAdmin: true)}"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(80)
                .ToList();
            return new ToolExecutionResult(true, lines.Count == 0 ? "No plugin commands indexed." : string.Join('\n', lines));
        }

        if (lowered.StartsWith("plugin-index permissions ", StringComparison.Ordinal) ||
            lowered.StartsWith("plugin-index hooks ", StringComparison.Ordinal))
        {
            var isHooks = lowered.StartsWith("plugin-index hooks ", StringComparison.Ordinal);
            var prefix = isHooks ? "plugin-index hooks " : "plugin-index permissions ";
            var pluginName = message[prefix.Length..].Trim();
            var records = await _pluginReferenceIndexer.SearchAsync(pluginName, cancellationToken);
            var lines = records
                .Where(record => record.PluginName.Contains(pluginName, StringComparison.OrdinalIgnoreCase))
                .Select(record => $"{record.PluginName}: {string.Join(", ", isHooks ? record.Hooks : record.Permissions)}")
                .ToList();
            return new ToolExecutionResult(true, lines.Count == 0 ? "No matching plugin reference records found." : string.Join('\n', lines));
        }

        return new ToolExecutionResult(false, "Unknown plugin-index command. Try: /plugin-index refresh | search <query> | commands [pluginName] | permissions <pluginName> | hooks <pluginName>");
    }

    private async Task<ToolExecutionResult?> TryHandlePluginReferenceQuestionAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (_pluginReferenceIndexer is null)
            return null;

        var intent = ParsePluginQueryIntent(context.Message);
        if (intent.Kind == PluginQueryKind.None)
            return null;

        // Extract meaningful keywords (filter stopwords, intent words) so we search with what the
        // user actually wants, not the full sentence which produces tons of false positives.
        var keywords = ExtractMeaningfulKeywords(context.Message);
        if (keywords.Count == 0)
            return null; // Question is too vague — let LLM handle it instead of dumping random data

        // Search the index with each keyword and merge results (avoids the "search the whole sentence" anti-pattern)
        var allRecords = new Dictionary<string, PluginReferenceRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyword in keywords)
        {
            var hits = await _pluginReferenceIndexer.SearchAsync(keyword, cancellationToken);
            foreach (var hit in hits)
                allRecords.TryAdd(hit.Id, hit);
        }

        if (allRecords.Count == 0)
        {
            return new ToolExecutionResult(
                true,
                $"I don't see any indexed plugin matching: {string.Join(", ", keywords)}. Try `/plugin-index refresh` if the index is stale, or rephrase with the plugin name.",
                ErrorCode: "authoritative_catalog");
        }

        // Score relevance: how many keywords appear in plugin name / commands / description
        var scored = allRecords.Values
            .Select(record => new
            {
                Record = record,
                Score = ScorePluginRelevance(record, keywords, intent)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Take(5)
            .Select(item => item.Record)
            .ToList();

        if (scored.Count == 0)
        {
            return new ToolExecutionResult(
                true,
                $"Found {allRecords.Count} indexed plugin(s) but none had a strong match for: {string.Join(", ", keywords)}. Be more specific or use the plugin name.",
                ErrorCode: "authoritative_catalog");
        }

        var formatted = FormatByIntent(scored, intent, keywords);
        return new ToolExecutionResult(
            true,
            formatted,
            Payload: new { source = "plugin-reference-index", matched = scored.Count, total = allRecords.Count, intent = intent.Kind.ToString() },
            ErrorCode: "authoritative_catalog");
    }

    // ---- Intent parsing ----------------------------------------------------------------------

    private enum PluginQueryKind { None, ChatCommands, ConsoleCommands, AnyCommand, Permissions, Hooks, ConfigKeys, PluginSummary }

    private sealed record PluginQueryIntent(PluginQueryKind Kind, bool PlayerFacingOnly);

    private static PluginQueryIntent ParsePluginQueryIntent(string message)
    {
        var lowered = message.ToLowerInvariant();

        // Must be a question or a request
        var isQuestion =
            lowered.Contains("?", StringComparison.Ordinal) ||
            lowered.StartsWith("what", StringComparison.Ordinal) ||
            lowered.StartsWith("which", StringComparison.Ordinal) ||
            lowered.StartsWith("is there", StringComparison.Ordinal) ||
            lowered.StartsWith("are there", StringComparison.Ordinal) ||
            lowered.StartsWith("does ", StringComparison.Ordinal) ||
            lowered.StartsWith("do ", StringComparison.Ordinal) ||
            lowered.StartsWith("can ", StringComparison.Ordinal) ||
            lowered.StartsWith("how ", StringComparison.Ordinal) ||
            lowered.Contains("show me", StringComparison.Ordinal) ||
            lowered.Contains("list ", StringComparison.Ordinal);

        // Must reference plugin / oxide concepts OR a feature class. Note: "command" alone is
        // accepted because questions like "what commands can players use from X" are common.
        // The relevance-scoring step filters out plugins that don't actually match the keywords.
        var hasPluginContext =
            lowered.Contains("plugin", StringComparison.Ordinal) ||
            lowered.Contains("oxide", StringComparison.Ordinal) ||
            lowered.Contains("umod", StringComparison.Ordinal) ||
            lowered.Contains("command", StringComparison.Ordinal) ||
            lowered.Contains("hook", StringComparison.Ordinal) ||
            lowered.Contains("permission", StringComparison.Ordinal) ||
            lowered.Contains("config key", StringComparison.Ordinal);

        if (!isQuestion || !hasPluginContext)
            return new PluginQueryIntent(PluginQueryKind.None, false);

        // Don't fire for live-server-state questions ("is plugin X loaded?")
        if (lowered.Contains("loaded", StringComparison.Ordinal) ||
            lowered.Contains("installed", StringComparison.Ordinal) ||
            lowered.Contains("running", StringComparison.Ordinal) ||
            lowered.Contains("enabled", StringComparison.Ordinal) ||
            lowered.Contains("active on", StringComparison.Ordinal))
            return new PluginQueryIntent(PluginQueryKind.None, false);

        var playerFacing =
            lowered.Contains("player-facing", StringComparison.Ordinal) ||
            lowered.Contains("player safe", StringComparison.Ordinal) ||
            lowered.Contains("players use", StringComparison.Ordinal) ||
            lowered.Contains("what commands can players", StringComparison.Ordinal);

        // Pick the most specific category
        if (lowered.Contains("chat command", StringComparison.Ordinal))
            return new PluginQueryIntent(PluginQueryKind.ChatCommands, playerFacing);
        if (lowered.Contains("console command", StringComparison.Ordinal))
            return new PluginQueryIntent(PluginQueryKind.ConsoleCommands, playerFacing);
        if (lowered.Contains("permission", StringComparison.Ordinal))
            return new PluginQueryIntent(PluginQueryKind.Permissions, playerFacing);
        if (lowered.Contains("hook", StringComparison.Ordinal))
            return new PluginQueryIntent(PluginQueryKind.Hooks, playerFacing);
        if (lowered.Contains("config key", StringComparison.Ordinal) || lowered.Contains("config option", StringComparison.Ordinal))
            return new PluginQueryIntent(PluginQueryKind.ConfigKeys, playerFacing);
        if (lowered.Contains("command", StringComparison.Ordinal))
            return new PluginQueryIntent(PluginQueryKind.AnyCommand, playerFacing);

        return new PluginQueryIntent(PluginQueryKind.PluginSummary, playerFacing);
    }

    // ---- Keyword extraction (semantic search instead of full-sentence search) -----------------

    private static readonly HashSet<string> PluginQueryStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","is","are","was","were","be","been","being",
        "do","does","did","done","can","could","should","would","may","might","must",
        "have","has","had","get","got","getting","make","made",
        "what","which","who","when","where","why","how","whose","whom",
        "this","that","these","those","there","here",
        "and","or","but","not","no","yes","so","if","then","than","as","also","too","very","just","only","still","even",
        "i","me","my","mine","you","your","yours","we","us","our","they","them","their",
        "to","of","in","on","at","by","for","with","from","into","over","under","up","down","out","off","about",
        "it","its","one","some","any","all","each","every","other","another","such","same",
        "command","commands","plugin","plugins","permission","permissions","hook","hooks","config","key","keys",
        "chat","console","oxide","umod","admin","player","players","server",
        "show","tell","list","find","know","want","need","like",
        "use","using","run","running","work","works","working"
    };

    private static List<string> ExtractMeaningfulKeywords(string message)
    {
        var tokens = System.Text.RegularExpressions.Regex
            .Split(message, @"[^A-Za-z0-9_\.\-]+")
            .Select(token => token.Trim())
            .Where(token => token.Length >= 3)
            .Where(token => !PluginQueryStopwords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        return tokens;
    }

    // ---- Relevance scoring -------------------------------------------------------------------

    private static int ScorePluginRelevance(PluginReferenceRecord record, IReadOnlyList<string> keywords, PluginQueryIntent intent)
    {
        var score = 0;
        foreach (var keyword in keywords)
        {
            // Plugin name match — strongest signal
            if (record.PluginName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                score += 10;

            // Command-name match — strong signal
            foreach (var command in record.Commands)
            {
                if (command.Command.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    score += 6;
            }

            // Description match — medium signal
            if (!string.IsNullOrWhiteSpace(record.Description) &&
                record.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                score += 3;

            // Permission / hook / config match — weak signal
            if (record.Permissions.Any(permission => permission.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                score += 2;
            if (record.Hooks.Any(hook => hook.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                score += 1;
            if (record.ConfigKeys.Any(configKey => configKey.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                score += 1;
        }

        // Bonus: plugin actually has the kind of thing being asked about
        switch (intent.Kind)
        {
            case PluginQueryKind.ChatCommands:
                if (record.Commands.Any(command => command.Type == "ChatCommand")) score += 2;
                break;
            case PluginQueryKind.ConsoleCommands:
                if (record.Commands.Any(command => command.Type == "ConsoleCommand")) score += 2;
                break;
            case PluginQueryKind.Permissions:
                if (record.Permissions.Count > 0) score += 2;
                break;
            case PluginQueryKind.Hooks:
                if (record.Hooks.Count > 0) score += 2;
                break;
            case PluginQueryKind.ConfigKeys:
                if (record.ConfigKeys.Count > 0) score += 2;
                break;
        }

        return score;
    }

    // ---- Formatting (intent-aware, ordered, deduplicated) ------------------------------------

    private static string FormatByIntent(IReadOnlyList<PluginReferenceRecord> records, PluginQueryIntent intent, IReadOnlyList<string> keywords)
    {
        var sections = new List<string>();
        var keywordHint = $"(searched for: {string.Join(", ", keywords)})";

        foreach (var record in records)
        {
            var block = FormatRecordForIntent(record, intent, keywords);
            if (!string.IsNullOrWhiteSpace(block))
                sections.Add(block);
        }

        if (sections.Count == 0)
            return $"No matching {DescribeKind(intent.Kind)} found {keywordHint}.";

        var header = intent.Kind switch
        {
            PluginQueryKind.ChatCommands => "Matching chat commands",
            PluginQueryKind.ConsoleCommands => "Matching console commands",
            PluginQueryKind.AnyCommand => "Matching commands",
            PluginQueryKind.Permissions => "Matching permissions",
            PluginQueryKind.Hooks => "Matching hooks",
            PluginQueryKind.ConfigKeys => "Matching config keys",
            _ => "Matching plugins"
        };

        return $"{header} {keywordHint}:\n\n{string.Join("\n\n", sections)}";
    }

    private static string FormatRecordForIntent(PluginReferenceRecord record, PluginQueryIntent intent, IReadOnlyList<string> keywords)
    {
        // For each section, prefer items that match a keyword (highlights why the result was chosen)
        bool MatchesKeyword(string text) => keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        var pluginHeader = $"**{record.PluginName}**" + (string.IsNullOrWhiteSpace(record.Version) ? string.Empty : $" v{record.Version}");

        switch (intent.Kind)
        {
            case PluginQueryKind.ChatCommands:
            {
                var commands = record.Commands
                    .Where(command => command.Type == "ChatCommand")
                    .Where(command => !intent.PlayerFacingOnly || !PluginReferenceIndexer.LooksAdminOnly(command))
                    .OrderByDescending(command => MatchesKeyword(command.Command))
                    .ThenBy(command => command.Command, StringComparer.OrdinalIgnoreCase)
                    .Distinct()
                    .Take(8)
                    .Select(command => "  - /" + command.Command.TrimStart('/') +
                        (string.IsNullOrWhiteSpace(command.RequiredPermission) ? string.Empty : $" (perm: {command.RequiredPermission})"))
                    .ToList();
                return commands.Count == 0 ? string.Empty : $"{pluginHeader}\n{string.Join('\n', commands)}";
            }

            case PluginQueryKind.ConsoleCommands:
            {
                var commands = record.Commands
                    .Where(command => command.Type == "ConsoleCommand")
                    .Where(command => !intent.PlayerFacingOnly || !PluginReferenceIndexer.LooksAdminOnly(command))
                    .OrderByDescending(command => MatchesKeyword(command.Command))
                    .ThenBy(command => command.Command, StringComparer.OrdinalIgnoreCase)
                    .Distinct()
                    .Take(8)
                    .Select(command => "  - " + command.Command.TrimStart('/'))
                    .ToList();
                return commands.Count == 0 ? string.Empty : $"{pluginHeader}\n{string.Join('\n', commands)}";
            }

            case PluginQueryKind.AnyCommand:
            {
                var chatCmds = record.Commands
                    .Where(command => command.Type == "ChatCommand")
                    .Where(command => !intent.PlayerFacingOnly || !PluginReferenceIndexer.LooksAdminOnly(command))
                    .Select(command => "/" + command.Command.TrimStart('/'))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var consoleCmds = record.Commands
                    .Where(command => command.Type == "ConsoleCommand")
                    .Where(command => !intent.PlayerFacingOnly || !PluginReferenceIndexer.LooksAdminOnly(command))
                    .Select(command => command.Command.TrimStart('/'))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (chatCmds.Count == 0 && consoleCmds.Count == 0) return string.Empty;
                var lines = new List<string> { pluginHeader };
                if (chatCmds.Count > 0) lines.Add("  chat: " + string.Join(", ", chatCmds.Take(6)));
                if (consoleCmds.Count > 0) lines.Add("  console: " + string.Join(", ", consoleCmds.Take(6)));
                return string.Join('\n', lines);
            }

            case PluginQueryKind.Permissions:
            {
                var perms = record.Permissions
                    .OrderByDescending(MatchesKeyword)
                    .ThenBy(permission => permission, StringComparer.OrdinalIgnoreCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .Select(permission => "  - " + permission)
                    .ToList();
                return perms.Count == 0 ? string.Empty : $"{pluginHeader}\n{string.Join('\n', perms)}";
            }

            case PluginQueryKind.Hooks:
            {
                var hooks = record.Hooks
                    .OrderByDescending(MatchesKeyword)
                    .ThenBy(hook => hook, StringComparer.OrdinalIgnoreCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .Select(hook => "  - " + hook)
                    .ToList();
                return hooks.Count == 0 ? string.Empty : $"{pluginHeader}\n{string.Join('\n', hooks)}";
            }

            case PluginQueryKind.ConfigKeys:
            {
                var configKeys = record.ConfigKeys
                    .OrderByDescending(MatchesKeyword)
                    .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .Select(key => "  - " + key)
                    .ToList();
                return configKeys.Count == 0 ? string.Empty : $"{pluginHeader}\n{string.Join('\n', configKeys)}";
            }

            default:
            {
                var description = string.IsNullOrWhiteSpace(record.Description) ? "(no description in plugin metadata)" : record.Description;
                var chatCount = record.Commands.Count(command => command.Type == "ChatCommand");
                var consoleCount = record.Commands.Count(command => command.Type == "ConsoleCommand");
                return $"{pluginHeader}\n  {description}\n  chat-cmds={chatCount}, console-cmds={consoleCount}, perms={record.Permissions.Count}";
            }
        }
    }

    private static string DescribeKind(PluginQueryKind kind) => kind switch
    {
        PluginQueryKind.ChatCommands => "chat commands",
        PluginQueryKind.ConsoleCommands => "console commands",
        PluginQueryKind.AnyCommand => "commands",
        PluginQueryKind.Permissions => "permissions",
        PluginQueryKind.Hooks => "hooks",
        PluginQueryKind.ConfigKeys => "config keys",
        _ => "plugins"
    };

    // Kept for /plugin-index slash commands (admin-only debugging path) — formats raw search results.
    private static string FormatPluginSearch(IReadOnlyList<PluginReferenceRecord> records, bool includeAdmin)
    {
        var lines = new List<string>();
        foreach (var record in records.Take(8))
        {
            var commands = record.Commands
                .Where(command => includeAdmin || !PluginReferenceIndexer.LooksAdminOnly(command))
                .Select(command => FormatCommand(command, includeAdmin))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
            var permissions = includeAdmin
                ? string.Join(", ", record.Permissions.Take(10))
                : string.Empty;
            var hooks = includeAdmin && record.Hooks.Count > 0 ? $" Hooks: {string.Join(", ", record.Hooks.Take(8))}." : string.Empty;
            var permissionText = includeAdmin && !string.IsNullOrWhiteSpace(permissions) ? $" Permissions: {permissions}." : string.Empty;
            lines.Add($"{record.PluginName}: commands {FormatList(commands)}.{permissionText}{hooks}");
        }

        return lines.Count == 0 ? "No matching plugin reference records found." : string.Join('\n', lines);
    }

    private static string FormatCommand(PluginCommandReference command, bool includeAdmin)
    {
        var prefix = command.Type == "ConsoleCommand" ? string.Empty : "/";
        var text = $"{prefix}{command.Command.TrimStart('/')}";
        return includeAdmin && !string.IsNullOrWhiteSpace(command.RequiredPermission)
            ? $"{text} ({command.Type}, permission {command.RequiredPermission})"
            : $"{text} ({command.Type})";
    }

    private static string FormatList(IReadOnlyList<string> items) =>
        items.Count == 0 ? "none detected" : string.Join(", ", items);

    private ToolExecutionResult? TryHandleCatalogQuestion(ToolExecutionContext context)
    {
        var lowered = context.Message.ToLowerInvariant();
        var looksLikeConvarQuestion =
            lowered.Contains("convar", StringComparison.Ordinal) ||
            lowered.Contains("server variable", StringComparison.Ordinal) ||
            lowered.Contains("variables", StringComparison.Ordinal);
        var looksLikeCommandQuestion =
            lowered.Contains("server command", StringComparison.Ordinal) ||
            lowered.Contains("server commands", StringComparison.Ordinal);

        if (!looksLikeConvarQuestion && !looksLikeCommandQuestion)
        {
            return null;
        }

        var snapshot = _knowledge.GetSnapshot();
        var variableMatches = looksLikeConvarQuestion
            ? _knowledge.SearchVariables(context.Message, 10)
            : Array.Empty<ServerVariableDefinition>();
        var commandMatches = looksLikeCommandQuestion
            ? _knowledge.SearchCommands(context.Message, 10)
            : Array.Empty<ServerCommandDefinition>();

        if (variableMatches.Count == 0 && commandMatches.Count == 0)
        {
            return new ToolExecutionResult(
                true,
                $"I couldn't find matching server catalog entries. Variables: `{snapshot.VariablesPath ?? "not found"}`, commands: `{snapshot.CommandsPath ?? "not found"}`.",
                ErrorCode: "authoritative_catalog");
        }

        var lines = new List<string>();
        if (variableMatches.Count > 0)
        {
            lines.Add("Matching convars:");
            lines.AddRange(variableMatches.Select(FormatVariableLine));
        }

        if (commandMatches.Count > 0)
        {
            if (lines.Count > 0)
                lines.Add(string.Empty);
            lines.Add("Matching server commands:");
            lines.AddRange(commandMatches.Select(FormatCommandLine));
        }

        var message = "From the local server catalog:\n" + string.Join('\n', lines);
        return new ToolExecutionResult(
            true,
            message,
            Payload: new
            {
                source = "server-catalog",
                variablesPath = snapshot.VariablesPath,
                commandsPath = snapshot.CommandsPath,
                variables = variableMatches.Count,
                commands = commandMatches.Count
            },
            ErrorCode: "authoritative_catalog");
    }

    private async Task<ToolExecutionResult> ImportServerCatalogMemoryAsync(string message, string lowered, CancellationToken cancellationToken)
    {
        var dryRun = lowered.Contains("dry-run", StringComparison.Ordinal) || lowered.Contains("dry run", StringComparison.Ordinal);
        var limit = TryExtractLimit(message);
        var snapshot = _knowledge.GetSnapshot();
        var variables = snapshot.Variables.Values.OrderBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var commands = snapshot.Commands.Values.OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var entries = variables.Select(variable => (type: MemoryRecordType.ServerConvar, summary: $"Rust server convar: {variable.Name}", text: BuildVariableMemoryText(variable), tags: new[] { "server-catalog", "convar", variable.Name.ToLowerInvariant() }))
            .Concat(commands.Select(command => (type: MemoryRecordType.ServerCommand, summary: $"Rust server command: {command.Name}", text: BuildCommandMemoryText(command), tags: new[] { "server-catalog", "command", command.Name.ToLowerInvariant() })))
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
                Type = entry.type,
                Scope = MemoryScope.Global,
                Source = MemorySource.ServerCatalog,
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

    private static string FormatCommandLine(ServerCommandDefinition command)
    {
        var description = string.IsNullOrWhiteSpace(command.Description) ? "No description in catalog." : command.Description;
        var risk = string.IsNullOrWhiteSpace(command.RiskLevel) ? string.Empty : $"; risk `{command.RiskLevel}`";
        return $"- `{command.Name}` - {description}{risk}";
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
