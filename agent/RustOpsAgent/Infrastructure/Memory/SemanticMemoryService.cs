using System.Text;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Infrastructure.Memory;

internal sealed class SemanticMemoryService : ISemanticMemoryService
{
    private readonly MemorySettings _settings;
    private readonly IInspectableMemoryStore _store;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly LegacyMemoryMigrator _migrator;

    public SemanticMemoryService(
        MemorySettings settings,
        IInspectableMemoryStore store,
        IEmbeddingProvider? embeddingProvider,
        string legacyStatePath,
        string neoCortexRoot)
    {
        _settings = settings;
        _store = store;
        _embeddingProvider = embeddingProvider;
        _migrator = new LegacyMemoryMigrator(legacyStatePath, neoCortexRoot);
    }

    public async Task<WorkflowMemoryContext> RecallForPlanningAsync(
        string message,
        ConversationSelectionState state,
        IReadOnlyList<string> knownServers,
        CancellationToken cancellationToken)
    {
        var query = BuildPlanningQuery(message, state, knownServers);
        var relatedIds = DetectRelevantIds(message, state, knownServers, null);
        return await RecallAsync(
            query,
            relatedIds,
            null,
            new[]
            {
                MemoryRecordType.UserInstruction,
                MemoryRecordType.Fix,
                MemoryRecordType.Procedure,
                MemoryRecordType.Failure,
                MemoryRecordType.Fact,
                MemoryRecordType.ServerState,
                MemoryRecordType.Reflection
            },
            "planning",
            cancellationToken);
    }

    public async Task<WorkflowMemoryContext> RecallForExecutionAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var query = BuildExecutionQuery(context);
        var relatedIds = DetectRelevantIds(
            context.Message,
            context.SelectionState,
            context.Route.Slots.ServerNames ?? Array.Empty<string>(),
            context.Route);

        return await RecallAsync(
            query,
            relatedIds,
            context.Route.Slots.ServerName is not null ? MemoryScope.Server : null,
            new[]
            {
                MemoryRecordType.Fix,
                MemoryRecordType.Procedure,
                MemoryRecordType.Failure,
                MemoryRecordType.UserInstruction,
                MemoryRecordType.ToolObservation,
                MemoryRecordType.ServerState,
                MemoryRecordType.Reflection
            },
            "execution",
            cancellationToken);
    }

    public async Task RecordActionOutcomeAsync(
        ToolExecutionContext context,
        ToolExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (!_settings.WriteEnabled)
        {
            LogDebug("write skipped: disabled");
            return;
        }

        if (_settings.MaxWritesPerWorkflowStep <= 0)
        {
            LogDebug("write skipped: maxWritesPerWorkflowStep reached");
            return;
        }

        var server = result.SelectedServer ?? context.Route.Slots.ServerName ?? context.SelectionState.LastServerName;
        var routeIntent = context.Route.Intent.ToString();
        var targetRef = context.Route.TargetRef ?? string.Empty;
        var tags = new List<string> { "workflow", routeIntent.ToLowerInvariant(), targetRef.ToLowerInvariant() };
        if (!string.IsNullOrWhiteSpace(server))
        {
            tags.Add($"server:{server}");
        }

        var actionFingerprint = BuildActionFingerprint(context, result, server);
        var detail = BuildOutcomeDetail(context, result, server, actionFingerprint);
        var summary = BuildOutcomeSummary(context, result, server);

        if (IsTransientSuccessNoise(context, result, summary, detail))
        {
            LogDebug("write skipped: transient success noise");
            return;
        }

        if (!MemorySanitizer.LooksUseful(summary, detail))
        {
            LogDebug("write skipped: not useful");
            return;
        }

        var record = new MemoryRecord
        {
            Type = ResolveOutcomeType(context, result, server),
            Scope = ResolveOutcomeScope(context, server),
            Source = MemorySource.AgentAction,
            Summary = summary,
            Text = detail,
            Tags = tags,
            RelatedEntityIds = BuildRelatedIds(context, result, server),
            Importance = ResolveImportance(context, result),
            Confidence = result.Success ? 0.82 : 0.88,
            ExpiryUtc = ResolveExpiry(context, result),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["adminId"] = context.AdminId,
                ["intent"] = routeIntent,
                ["targetRef"] = targetRef,
                ["actionFingerprint"] = actionFingerprint,
                ["success"] = result.Success.ToString(),
                ["errorCode"] = result.ErrorCode ?? string.Empty,
                ["selectedServer"] = server ?? string.Empty
            }
        };

        if (context.ExecutionMemoryContext?.HasResults == true)
        {
            var knownFailure = context.ExecutionMemoryContext.Results
                .FirstOrDefault(item => item.MemoryRecord.Type == MemoryRecordType.Failure);
            if (knownFailure is not null)
            {
                record.Metadata["relatedFailureId"] = knownFailure.MemoryRecord.Id;
            }
        }

        var writeResult = await TryStoreRecordAsync(record, cancellationToken, allowMissingEmbeddings: false);
        LogDebug($"post-action writeback {(writeResult == MemoryImportDisposition.Imported ? "stored" : $"skipped:{writeResult}")}");
    }

    public async Task RecordUserInstructionAsync(string? adminId, string? serverName, string instruction, CancellationToken cancellationToken)
    {
        if (!_settings.WriteEnabled || string.IsNullOrWhiteSpace(instruction))
        {
            return;
        }

        var sanitized = MemorySanitizer.Sanitize(instruction);
        if (!MemorySanitizer.LooksUseful(sanitized, sanitized))
        {
            return;
        }

        var record = new MemoryRecord
        {
            Type = MemoryRecordType.UserInstruction,
            Scope = string.IsNullOrWhiteSpace(serverName) ? MemoryScope.User : MemoryScope.Server,
            Source = MemorySource.AdminCommand,
            Summary = TrimSingleLine(sanitized, 160),
            Text = sanitized,
            Tags = new List<string> { "instruction", "admin-feedback" },
            RelatedEntityIds = new List<string> { adminId ?? string.Empty, serverName ?? string.Empty }.Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
            Importance = 0.88,
            Confidence = 0.9,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["adminId"] = adminId ?? string.Empty,
                ["serverName"] = serverName ?? string.Empty
            }
        };

        await TryStoreRecordAsync(record, cancellationToken, allowMissingEmbeddings: false);
    }

    public async Task RecordReflectionAsync(string summary, string detail, IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        if (!_settings.WriteEnabled || !MemorySanitizer.LooksUseful(summary, detail))
        {
            return;
        }

        var record = new MemoryRecord
        {
            Type = MemoryRecordType.Reflection,
            Scope = MemoryScope.Project,
            Source = MemorySource.ReflectionLoop,
            Summary = TrimSingleLine(MemorySanitizer.Sanitize(summary), 160),
            Text = MemorySanitizer.Sanitize(detail),
            Tags = tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).ToList(),
            Importance = 0.78,
            Confidence = 0.82
        };

        await TryStoreRecordAsync(record, cancellationToken, allowMissingEmbeddings: false);
    }

    public async Task RecordServerFactAsync(
        string serverName,
        string summary,
        string detail,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        if (!_settings.WriteEnabled || string.IsNullOrWhiteSpace(serverName) || !MemorySanitizer.LooksUseful(summary, detail))
        {
            return;
        }

        var record = new MemoryRecord
        {
            Type = MemoryRecordType.ServerState,
            Scope = MemoryScope.Server,
            Source = MemorySource.ConfigScan,
            Summary = TrimSingleLine(MemorySanitizer.Sanitize(summary), 160),
            Text = MemorySanitizer.Sanitize(detail),
            Tags = tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).Append($"server:{serverName}").Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RelatedEntityIds = new List<string> { serverName },
            Importance = 0.72,
            Confidence = 0.84,
            ExpiryUtc = DateTime.UtcNow.AddDays(30)
        };

        await TryStoreRecordAsync(record, cancellationToken, allowMissingEmbeddings: false);
    }

    public Task<MemoryDebugStats> GetStatsAsync(CancellationToken cancellationToken) => _store.GetDebugStatsAsync(cancellationToken);

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var context = await RecallAsync(query, Array.Empty<string>(), null, null, "manual-search", cancellationToken, minSimilarityOverride: 0.2, maxResultsOverride: maxResults);
        return context.Results;
    }

    public Task<MemoryRecord?> GetByIdAsync(string id, CancellationToken cancellationToken) => _store.GetByIdAsync(id, cancellationToken);

    public Task DeleteAsync(string id, CancellationToken cancellationToken) => _store.DeleteAsync(id, cancellationToken);

    public Task<IReadOnlyList<MemoryRecord>> ListRecentAsync(int maxResults, CancellationToken cancellationToken) =>
        _store.ListRecentAsync(maxResults, cancellationToken);

    public async Task<IReadOnlyList<IGrouping<string, MemoryRecord>>> ListRepeatedFailuresAsync(int minOccurrences, CancellationToken cancellationToken)
    {
        var all = await _store.GetAllAsync(cancellationToken);
        return all
            .Where(record => record.Type == MemoryRecordType.Failure)
            .GroupBy(record => record.Metadata.TryGetValue("actionFingerprint", out var fingerprint) && !string.IsNullOrWhiteSpace(fingerprint)
                ? fingerprint
                : record.Summary,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= Math.Max(2, minOccurrences))
            .OrderByDescending(group => group.Count())
            .ToList();
    }

    public async Task<MemoryRecord> AddManualMemoryAsync(ManualMemoryInput input, CancellationToken cancellationToken)
    {
        var record = new MemoryRecord
        {
            Type = input.Type,
            Scope = input.Scope,
            Source = input.Source,
            Summary = MemorySanitizer.Sanitize(input.Summary),
            Text = MemorySanitizer.Sanitize(input.Text),
            Tags = input.Tags,
            RelatedEntityIds = input.RelatedEntityIds,
            Importance = input.Importance,
            Confidence = input.Confidence,
            Metadata = input.Metadata
        };

        await TryStoreRecordAsync(record, cancellationToken, allowMissingEmbeddings: false);
        return record;
    }

    public async Task<int> RebuildEmbeddingsAsync(CancellationToken cancellationToken)
    {
        if (_embeddingProvider is null)
        {
            LogDebug("rebuild skipped: embedding provider unavailable");
            return 0;
        }

        var records = await _store.GetAllAsync(cancellationToken);
        var rebuilt = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.HasEmbedding && string.Equals(record.EmbeddingModel, _embeddingProvider.ModelName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                record.Embedding = await _embeddingProvider.GenerateEmbeddingAsync(BuildEmbeddingText(record.Summary, record.Text), cancellationToken);
                record.EmbeddingModel = _embeddingProvider.ModelName;
                record.UpdatedAtUtc = DateTime.UtcNow;
                record.Normalize();
                await _store.UpsertAsync(record, cancellationToken);
                rebuilt++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[memory] rebuild failed for {record.Id}: {ex.Message}");
            }
        }

        return rebuilt;
    }

    public async Task<MemoryMigrationReport> MigrateLegacyMemoryAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var report = await _migrator.MigrateAsync(async record =>
        {
            if (await _store.ExistsByContentHashAsync(record.ContentHash, cancellationToken))
            {
                return MemoryImportDisposition.Duplicate;
            }

            return await TryStoreRecordAsync(
                record,
                cancellationToken,
                allowMissingEmbeddings: true);
        }, dryRun, cancellationToken);
        Console.WriteLine($"[memory] migration summary: {report.ToSummary()}");
        return report;
    }

    public Task<int> PruneAsync(CancellationToken cancellationToken) => _store.CompactOrPruneAsync(cancellationToken);

    private async Task<WorkflowMemoryContext> RecallAsync(
        string query,
        IReadOnlyList<string> relatedIds,
        MemoryScope? scope,
        IReadOnlyCollection<MemoryRecordType>? types,
        string origin,
        CancellationToken cancellationToken,
        double? minSimilarityOverride = null,
        int? maxResultsOverride = null)
    {
        if (!_settings.SearchEnabled)
        {
            LogDebug($"{origin} recall skipped: disabled");
            return new WorkflowMemoryContext { Query = query, RetrievalSkipped = true, SkipReason = "memory search disabled", RetrievalOrigin = origin };
        }

        if (_embeddingProvider is null)
        {
            LogDebug($"{origin} recall skipped: embedding provider unavailable");
            return new WorkflowMemoryContext { Query = query, RetrievalSkipped = true, SkipReason = "embedding provider unavailable", RetrievalOrigin = origin };
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            LogDebug($"{origin} recall skipped: empty query");
            return new WorkflowMemoryContext { Query = query, RetrievalSkipped = true, SkipReason = "empty query", RetrievalOrigin = origin };
        }

        try
        {
            var embedding = await _embeddingProvider.GenerateEmbeddingAsync(MemorySanitizer.Sanitize(query), cancellationToken);
            var request = new MemorySearchRequest
            {
                Query = query,
                QueryEmbedding = embedding,
                QueryEmbeddingModel = _embeddingProvider.ModelName,
                Scope = scope,
                Types = types?.ToList(),
                RelatedEntityIds = relatedIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                MaxResults = maxResultsOverride ?? _settings.MaxRetrievedMemoriesPerStep,
                MinSimilarity = minSimilarityOverride ?? _settings.SimilarityThreshold,
                IncludeExpired = false
            };

            var results = await _store.SearchAsync(request, cancellationToken);
            foreach (var result in results)
            {
                await _store.MarkAccessedAsync(result.MemoryRecord.Id, cancellationToken);
            }

            LogDebug($"{origin} recall query=\"{query}\" retrieved={results.Count} top={string.Join(", ", results.Take(3).Select(r => $"{r.MemoryRecord.Type}:{r.FinalScore:F2}"))}");

            return new WorkflowMemoryContext
            {
                Query = query,
                Results = results.ToList(),
                CompactContext = FormatRelevantMemories(results),
                RetrievalOrigin = origin
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[memory] search skipped: {ex.Message}");
            return new WorkflowMemoryContext
            {
                Query = query,
                RetrievalSkipped = true,
                SkipReason = ex.Message,
                RetrievalOrigin = origin
            };
        }
    }

    private async Task<MemoryImportDisposition> TryStoreRecordAsync(
        MemoryRecord record,
        CancellationToken cancellationToken,
        bool allowMissingEmbeddings)
    {
        try
        {
            record.Text = MemorySanitizer.Sanitize(record.Text);
            record.Summary = MemorySanitizer.Sanitize(record.Summary);
            record.Metadata = record.Metadata
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                .ToDictionary(entry => entry.Key, entry => MemorySanitizer.Sanitize(entry.Value), StringComparer.OrdinalIgnoreCase);

            if (_embeddingProvider is not null)
            {
                record.Embedding = await _embeddingProvider.GenerateEmbeddingAsync(BuildEmbeddingText(record.Summary, record.Text), cancellationToken);
                record.EmbeddingModel = _embeddingProvider.ModelName;
            }
            else if (!allowMissingEmbeddings)
            {
                Console.WriteLine("[memory] write skipped: embedding provider unavailable");
                return MemoryImportDisposition.EmbeddingFailure;
            }
            else
            {
                record.Embedding = Array.Empty<float>();
                record.EmbeddingModel = string.Empty;
            }

            if (_embeddingProvider?.Dimensions is int expectedDimensions &&
                record.HasEmbedding &&
                record.Embedding.Length != expectedDimensions)
            {
                throw new InvalidOperationException($"Embedding dimension mismatch. Expected {expectedDimensions}, received {record.Embedding.Length}.");
            }

            record.Normalize();
            await _store.UpsertAsync(record, cancellationToken);
            LogDebug($"write type={record.Type} scope={record.Scope} hash={record.ContentHash[..Math.Min(8, record.ContentHash.Length)]}");
            return MemoryImportDisposition.Imported;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[memory] write skipped: {ex.Message}");
            return ex is InvalidOperationException
                ? MemoryImportDisposition.Invalid
                : MemoryImportDisposition.EmbeddingFailure;
        }
    }

    private static MemoryRecordType ResolveOutcomeType(ToolExecutionContext context, ToolExecutionResult result, string? server)
    {
        if (!result.Success)
        {
            return MemoryRecordType.Failure;
        }

        if (result.MutatedState || context.Route.Intent is AdminIntentType.FileEdit or AdminIntentType.ServerManagement)
        {
            return MemoryRecordType.Fix;
        }

        if (context.Route.Intent is AdminIntentType.RconCommand or AdminIntentType.ServerControl)
        {
            return MemoryRecordType.Procedure;
        }

        return string.IsNullOrWhiteSpace(server) ? MemoryRecordType.ToolObservation : MemoryRecordType.ServerState;
    }

    private static MemoryScope ResolveOutcomeScope(ToolExecutionContext context, string? server)
    {
        if (!string.IsNullOrWhiteSpace(server))
        {
            return MemoryScope.Server;
        }

        return context.Route.Intent is AdminIntentType.Chat or AdminIntentType.Clarification
            ? MemoryScope.Project
            : MemoryScope.Tool;
    }

    private static double ResolveImportance(ToolExecutionContext context, ToolExecutionResult result)
    {
        if (!result.Success)
        {
            return 0.82;
        }

        if (result.MutatedState)
        {
            return 0.78;
        }

        return context.Route.Intent is AdminIntentType.StatusCheck ? 0.5 : 0.64;
    }

    private static DateTime? ResolveExpiry(ToolExecutionContext context, ToolExecutionResult result)
    {
        if (!result.Success || result.MutatedState)
        {
            return null;
        }

        return context.Route.Intent is AdminIntentType.StatusCheck or AdminIntentType.Chat
            ? DateTime.UtcNow.AddDays(14)
            : null;
    }

    private static string BuildPlanningQuery(string message, ConversationSelectionState state, IReadOnlyList<string> knownServers)
    {
        var builder = new StringBuilder();
        builder.AppendLine(message.Trim());
        if (!string.IsNullOrWhiteSpace(state.LastIntent))
        {
            builder.AppendLine($"previous intent: {state.LastIntent}");
        }

        if (!string.IsNullOrWhiteSpace(state.LastServerName))
        {
            builder.AppendLine($"last server: {state.LastServerName}");
        }

        if (state.PendingClarification is not null)
        {
            builder.AppendLine($"pending clarification: {state.PendingClarification.Intent} {state.PendingClarification.Question}");
        }

        if (knownServers.Count > 0)
        {
            builder.AppendLine($"known servers: {string.Join(", ", knownServers.Take(8))}");
        }

        return builder.ToString().Trim();
    }

    private static string BuildExecutionQuery(ToolExecutionContext context)
    {
        var command = context.Route.Slots.CommandText ?? string.Empty;
        var servers = context.Route.Slots.ServerNames is { Count: > 0 }
            ? string.Join(", ", context.Route.Slots.ServerNames)
            : context.Route.Slots.ServerName ?? context.SelectionState.LastServerName ?? "none";
        return $"{context.Route.Intent} target={context.Route.TargetRef} servers={servers} command={command} request={context.Message}".Trim();
    }

    private static IReadOnlyList<string> DetectRelevantIds(
        string message,
        ConversationSelectionState state,
        IReadOnlyList<string> knownServers,
        AdminIntentRoute? route)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in knownServers.Where(server => !string.IsNullOrWhiteSpace(server)))
        {
            if (message.Contains(server, StringComparison.OrdinalIgnoreCase))
            {
                ids.Add(server);
            }
        }

        if (!string.IsNullOrWhiteSpace(state.LastServerName))
        {
            ids.Add(state.LastServerName!);
        }

        if (route?.Slots.ServerNames is { Count: > 0 })
        {
            foreach (var server in route.Slots.ServerNames)
            {
                ids.Add(server);
            }
        }

        if (!string.IsNullOrWhiteSpace(route?.Slots.ServerName))
        {
            ids.Add(route.Slots.ServerName!);
        }

        if (!string.IsNullOrWhiteSpace(route?.TargetRef))
        {
            ids.Add(route.TargetRef!);
        }

        if (route is not null)
        {
            ids.Add(route.Intent.ToString());
        }

        return ids.ToList();
    }

    private static List<string> BuildRelatedIds(ToolExecutionContext context, ToolExecutionResult result, string? server)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(server))
        {
            ids.Add(server);
        }

        if (!string.IsNullOrWhiteSpace(context.Route.TargetRef))
        {
            ids.Add(context.Route.TargetRef!);
        }

        ids.Add(context.Route.Intent.ToString());
        var command = context.Route.Slots.CommandText?.Split(' ', 2)[0];
        if (!string.IsNullOrWhiteSpace(command))
        {
            ids.Add(command);
        }

        if (result.SelectedServers is not null)
        {
            foreach (var selected in result.SelectedServers.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                ids.Add(selected);
            }
        }

        return ids.ToList();
    }

    private static string BuildActionFingerprint(ToolExecutionContext context, ToolExecutionResult result, string? server)
    {
        var commandRoot = context.Route.Slots.CommandText?.Split(' ', 2)[0]?.Trim().ToLowerInvariant() ?? string.Empty;
        return $"{context.Route.Intent}|{context.Route.TargetRef}|{server}|{commandRoot}|{result.ErrorCode}".Trim('|');
    }

    private static string BuildOutcomeSummary(ToolExecutionContext context, ToolExecutionResult result, string? server)
    {
        var status = result.Success ? "Success" : "Failure";
        var target = server ?? context.Route.TargetRef ?? "general";
        return TrimSingleLine($"{status}: {context.Route.Intent} on {target} - {result.Message}", 180);
    }

    private static string BuildOutcomeDetail(ToolExecutionContext context, ToolExecutionResult result, string? server, string actionFingerprint)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Intent: {context.Route.Intent}");
        builder.AppendLine($"TargetRef: {context.Route.TargetRef}");
        builder.AppendLine($"Server: {server ?? "none"}");
        builder.AppendLine($"Request: {MemorySanitizer.Sanitize(context.Message)}");
        builder.AppendLine($"Command: {MemorySanitizer.Sanitize(context.Route.Slots.CommandText)}");
        builder.AppendLine($"Result: {MemorySanitizer.Sanitize(result.Message)}");
        builder.AppendLine($"ActionFingerprint: {actionFingerprint}");
        if (!string.IsNullOrWhiteSpace(result.ErrorCode))
        {
            builder.AppendLine($"ErrorCode: {result.ErrorCode}");
        }

        var nextStepHint = ExtractPayloadValue(result.Payload, "nextStepHint")
            ?? ExtractPayloadValue(result.Payload, "nextStep")
            ?? ExtractPayloadValue(result.Payload, "suggestedFix")
            ?? ExtractPayloadValue(result.Payload, "NextStepHint")
            ?? ExtractPayloadValue(result.Payload, "NextStep")
            ?? ExtractPayloadValue(result.Payload, "SuggestedFix");
        if (!string.IsNullOrWhiteSpace(nextStepHint))
        {
            builder.AppendLine($"NextStepHint: {MemorySanitizer.Sanitize(nextStepHint)}");
        }

        if (context.ExecutionMemoryContext?.HasResults == true)
        {
            var top = context.ExecutionMemoryContext.Results.Take(3)
                .Select(item => $"{item.MemoryRecord.Type}:{item.MemoryRecord.Summary}");
            builder.AppendLine($"RelatedMemory: {string.Join(" | ", top)}");
        }

        return builder.ToString().Trim();
    }

    private string FormatRelevantMemories(IReadOnlyList<MemorySearchResult> results)
    {
        if (results.Count == 0 || _settings.MaxInjectedMemoryCharacters == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var maxChars = Math.Max(0, _settings.MaxInjectedMemoryCharacters);
        var truncated = false;
        foreach (var result in results)
        {
            var record = result.MemoryRecord;
            var block =
                "Relevant Memory:\n" +
                $"- Type: {record.Type}\n" +
                $"- Scope: {record.Scope}\n" +
                $"- Summary: {TrimSingleLine(record.Summary, 220)}\n" +
                $"- Why relevant: {result.MatchReason} (score {result.FinalScore:F2})\n" +
                $"- Confidence: {record.Confidence:F2}\n" +
                $"- Created: {record.CreatedAtUtc:O}\n" +
                $"- Last used: {(record.LastAccessedAtUtc?.ToString("O") ?? "never")}\n" +
                $"- Source: {record.Source}\n";

            if (maxChars > 0 && builder.Length + block.Length > maxChars)
            {
                truncated = true;
                break;
            }

            builder.Append(block);
        }

        if (truncated)
        {
            LogDebug($"memory context truncated at {_settings.MaxInjectedMemoryCharacters} characters");
        }

        return builder.ToString().Trim();
    }

    private static string BuildEmbeddingText(string summary, string text)
    {
        var sanitizedSummary = MemorySanitizer.Sanitize(summary);
        var sanitizedText = MemorySanitizer.Sanitize(text);
        var combined = $"{sanitizedSummary}\n{sanitizedText}".Trim();
        return combined.Length <= 4000 ? combined : combined[..4000];
    }

    private static string TrimSingleLine(string? value, int maxLength)
    {
        var normalized = value?.Replace('\r', ' ').Replace('\n', ' ').Trim() ?? string.Empty;
        return normalized.Length <= maxLength ? normalized : normalized[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static string? ExtractPayloadValue(object? payload, string propertyName)
    {
        if (payload is null)
        {
            return null;
        }

        var property = payload.GetType().GetProperty(propertyName);
        var value = property?.GetValue(payload)?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsTransientSuccessNoise(ToolExecutionContext context, ToolExecutionResult result, string summary, string detail)
    {
        if (!result.Success)
        {
            return false;
        }

        if (result.MutatedState || result.Payload is not null)
        {
            return false;
        }

        if (context.Route.Intent is not AdminIntentType.StatusCheck)
        {
            return false;
        }

        var normalized = (result.Message ?? string.Empty).Trim();
        if (normalized.Length <= 8)
        {
            return true;
        }

        return !MemorySanitizer.LooksUseful(summary, detail);
    }

    private void LogDebug(string message)
    {
        if (_settings.DebugLoggingEnabled)
        {
            Console.WriteLine($"[memory] {message}");
        }
    }
}
