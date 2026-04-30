using System.Text;
using System.Text.RegularExpressions;
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
                MemoryRecordType.Reflection,
                MemoryRecordType.Exception,
                MemoryRecordType.ServerConvar,
                MemoryRecordType.ServerCommand,
                MemoryRecordType.PluginSummary
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
                MemoryRecordType.Reflection,
                MemoryRecordType.Exception,
                MemoryRecordType.ServerConvar,
                MemoryRecordType.ServerCommand,
                MemoryRecordType.PluginSummary
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

        if (ShouldSkipActionOutcome(context, result, summary, detail))
        {
            LogDebug("write skipped: low-value action outcome");
            return;
        }

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

        var serverFact = ClassifyServerFact(serverName, summary, detail, tags);
        if (serverFact is null)
        {
            LogDebug("server fact skipped: low-value or noisy observation");
            return;
        }

        var record = new MemoryRecord
        {
            Type = serverFact.Type,
            Scope = serverFact.Scope,
            Source = serverFact.Source,
            ApprovalState = serverFact.ApprovalState,
            Summary = serverFact.Summary,
            Text = serverFact.Text,
            Tags = serverFact.Tags,
            RelatedEntityIds = serverFact.RelatedEntityIds,
            Importance = serverFact.Importance,
            Confidence = serverFact.Confidence,
            ExpiryUtc = serverFact.ExpiryUtc,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["classifier"] = serverFact.Reason
            }
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
            ApprovalState = input.ApprovalState,
            Title = input.Title,
            SourcePath = input.SourcePath,
            SourceHash = input.SourceHash,
            ChunkIndex = input.ChunkIndex,
            Category = input.Category,
            LastVerifiedUtc = input.LastVerifiedUtc,
            Metadata = input.Metadata
        };

        await TryStoreRecordAsync(record, cancellationToken, allowMissingEmbeddings: false);
        return record;
    }

    public Task<MemoryImportDisposition> ImportRecordAsync(MemoryRecord record, CancellationToken cancellationToken) =>
        TryStoreRecordAsync(record, cancellationToken, allowMissingEmbeddings: false);

    public Task<IReadOnlyList<MemoryRecord>> ListPendingAsync(int maxResults, CancellationToken cancellationToken) =>
        _store.ListByApprovalStateAsync(MemoryApprovalState.Pending, maxResults, cancellationToken);

    public async Task<bool> SetApprovalStateAsync(string id, MemoryApprovalState approvalState, CancellationToken cancellationToken)
    {
        var record = await _store.GetByIdAsync(id, cancellationToken);
        if (record is null)
        {
            return false;
        }

        record.ApprovalState = approvalState;
        record.UpdatedAtUtc = DateTime.UtcNow;
        record.Normalize();
        await _store.UpsertAsync(record, cancellationToken);
        return true;
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

        if (string.IsNullOrWhiteSpace(query))
        {
            LogDebug($"{origin} recall skipped: empty query");
            return new WorkflowMemoryContext { Query = query, RetrievalSkipped = true, SkipReason = "empty query", RetrievalOrigin = origin };
        }

        float[]? embedding = null;
        string? embeddingModel = null;
        if (_embeddingProvider is null)
        {
            LogDebug($"{origin} recall using keyword fallback: embedding provider unavailable");
        }
        else
        {
            try
            {
                embedding = await _embeddingProvider.GenerateEmbeddingAsync(MemorySanitizer.Sanitize(query), cancellationToken);
                embeddingModel = _embeddingProvider.ModelName;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[memory] embedding unavailable for {origin}; using keyword fallback: {ex.Message}");
            }
        }

        try
        {
            var request = new MemorySearchRequest
            {
                Query = query,
                QueryEmbedding = embedding,
                QueryEmbeddingModel = embeddingModel,
                Scope = scope,
                Types = types?.ToList(),
                RelatedEntityIds = relatedIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                MaxResults = maxResultsOverride ?? _settings.MaxRetrievedMemoriesPerStep,
                MinSimilarity = minSimilarityOverride ?? (embedding is null ? Math.Min(_settings.SimilarityThreshold, 0.35) : _settings.SimilarityThreshold),
                MinConfidence = _settings.MinimumRecallConfidence,
                IncludeExpired = false
            };

            var results = await _store.SearchAsync(request, cancellationToken);
            foreach (var result in results)
            {
                await _store.MarkAccessedAsync(result.MemoryRecord.Id, cancellationToken);
            }

            var mode = embedding is null ? "keyword" : "semantic";
            LogDebug($"{origin} {mode} recall query=\"{query}\" retrieved={results.Count} top={string.Join(", ", results.Take(3).Select(r => $"{r.MemoryRecord.Type}:{r.FinalScore:F2}"))}");

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

            var policyDisposition = ApplyMemoryWritePolicy(record);
            if (policyDisposition is not null)
            {
                LogDebug($"write skipped: {policyDisposition}");
                return policyDisposition.Value;
            }

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

    private static bool ShouldSkipActionOutcome(ToolExecutionContext context, ToolExecutionResult result, string summary, string detail)
    {
        if (!result.Success)
        {
            return false;
        }

        if (result.MutatedState)
        {
            return false;
        }

        if (context.Route.Intent is AdminIntentType.Chat or AdminIntentType.Clarification)
        {
            return true;
        }

        if (context.Route.Intent is AdminIntentType.StatusCheck &&
            !LooksLikeExceptionOrError(detail).HasSignal)
        {
            return true;
        }

        var combined = $"{summary}\n{detail}";
        return LooksLikeMundaneConsoleNoise(combined);
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

    private static MemoryImportDisposition? ApplyMemoryWritePolicy(MemoryRecord record)
    {
        if (!MemorySanitizer.LooksUseful(record.Summary, record.Text))
        {
            return MemoryImportDisposition.Skipped;
        }

        if (record.Type == MemoryRecordType.ServerState && LooksLikeRawConsoleOutput(record.Text))
        {
            var signal = LooksLikeExceptionOrError(record.Text);
            if (signal.Confidence >= 0.78)
            {
                PromoteExceptionRecord(record, signal);
            }
            else if (LooksLikeMundaneConsoleNoise(record.Text))
            {
                return MemoryImportDisposition.Skipped;
            }
            else
            {
                record.Type = MemoryRecordType.ToolObservation;
                record.Source = MemorySource.LogClassifier;
                record.ApprovalState = MemoryApprovalState.Pending;
                record.Confidence = Math.Min(record.Confidence, 0.68);
                record.ExpiryUtc ??= DateTime.UtcNow.AddDays(14);
                record.Metadata["approvalReason"] = "raw_console_output_requires_admin_review";
            }
        }

        if (record.Type == MemoryRecordType.Exception)
        {
            var signal = LooksLikeExceptionOrError(record.Text);
            if (!signal.HasSignal)
            {
                record.ApprovalState = MemoryApprovalState.Pending;
                record.Confidence = Math.Min(record.Confidence, 0.68);
                record.Metadata["approvalReason"] = "exception_classification_uncertain";
            }
            else if (record.Confidence < 0.82)
            {
                record.ApprovalState = MemoryApprovalState.Pending;
                record.Metadata["approvalReason"] = "exception_confidence_below_auto_approval";
            }
        }

        if (record.Type is MemoryRecordType.ServerConvar or MemoryRecordType.ServerCommand)
        {
            record.Scope = MemoryScope.Global;
            if (record.Source == MemorySource.AgentAction || record.Source == MemorySource.AdminCommand)
            {
                record.Source = MemorySource.ServerCatalog;
            }

            record.ApprovalState = record.Confidence >= 0.9
                ? MemoryApprovalState.Active
                : MemoryApprovalState.Pending;
        }

        if (record.Type == MemoryRecordType.PluginSummary)
        {
            record.Source = MemorySource.PluginSummary;
            record.ApprovalState = MemoryApprovalState.Active;
        }

        return null;
    }

    private static ServerFactClassification? ClassifyServerFact(
        string serverName,
        string summary,
        string detail,
        IReadOnlyList<string> inputTags)
    {
        var sanitizedSummary = TrimSingleLine(MemorySanitizer.Sanitize(summary), 180);
        var sanitizedDetail = MemorySanitizer.Sanitize(detail);
        var tags = inputTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Append($"server:{serverName}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var combined = $"{sanitizedSummary}\n{sanitizedDetail}";
        var loweredTags = tags.Select(tag => tag.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (loweredTags.Contains("server-catalog") || loweredTags.Contains("server-knowledge"))
        {
            var isConvar = loweredTags.Contains("convar") || loweredTags.Contains("variable");
            var isCommand = loweredTags.Contains("command");
            if (isConvar || isCommand)
            {
                return new ServerFactClassification(
                    isConvar ? MemoryRecordType.ServerConvar : MemoryRecordType.ServerCommand,
                    MemoryScope.Global,
                    MemorySource.ServerCatalog,
                    MemoryApprovalState.Active,
                    sanitizedSummary,
                    sanitizedDetail,
                    tags,
                    BuildRelatedIds(serverName, tags),
                    0.9,
                    0.96,
                    null,
                    "server_catalog");
            }
        }

        var signal = LooksLikeExceptionOrError(combined);
        if (signal.HasSignal)
        {
            if (signal.Confidence < 0.78)
            {
                return new ServerFactClassification(
                    MemoryRecordType.ToolObservation,
                    MemoryScope.Server,
                    MemorySource.LogClassifier,
                    MemoryApprovalState.Pending,
                    sanitizedSummary,
                    TrimMultiline(sanitizedDetail, 1200),
                    tags.Concat(new[] { "needs-admin-approval", signal.SignalTag }).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    BuildRelatedIds(serverName, tags),
                    0.5,
                    signal.Confidence,
                    DateTime.UtcNow.AddDays(14),
                    "possible_error_needs_admin_approval");
            }

            var exceptionText = ExtractExceptionSnippet(sanitizedDetail);
            return new ServerFactClassification(
                MemoryRecordType.Exception,
                MemoryScope.Server,
                MemorySource.LogClassifier,
                signal.Confidence >= 0.82 ? MemoryApprovalState.Active : MemoryApprovalState.Pending,
                BuildExceptionSummary(serverName, sanitizedSummary, exceptionText),
                exceptionText,
                tags.Concat(new[] { "exception", signal.SignalTag }).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                BuildRelatedIds(serverName, tags),
                signal.Confidence >= 0.82 ? 0.84 : 0.66,
                signal.Confidence,
                null,
                signal.Confidence >= 0.82 ? "exception_high_confidence" : "exception_needs_admin_approval");
        }

        if (LooksLikeRawConsoleOutput(combined))
        {
            if (LooksLikeMundaneConsoleNoise(combined))
            {
                return null;
            }

            return new ServerFactClassification(
                MemoryRecordType.ToolObservation,
                MemoryScope.Server,
                MemorySource.LogClassifier,
                MemoryApprovalState.Pending,
                sanitizedSummary,
                TrimMultiline(sanitizedDetail, 1200),
                tags.Append("needs-admin-approval").Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                BuildRelatedIds(serverName, tags),
                0.45,
                0.62,
                DateTime.UtcNow.AddDays(14),
                "raw_log_uncertain_pending");
        }

        var source = loweredTags.Contains("config-scan")
            ? MemorySource.ConfigScan
            : loweredTags.Contains("logs") || loweredTags.Contains("log") || loweredTags.Contains("health-observation")
                ? MemorySource.LogClassifier
                : MemorySource.AgentAction;
        var type = loweredTags.Contains("config") || loweredTags.Contains("status") || loweredTags.Contains("lifecycle")
            ? MemoryRecordType.ServerState
            : MemoryRecordType.ToolObservation;

        return new ServerFactClassification(
            type,
            MemoryScope.Server,
            source,
            MemoryApprovalState.Active,
            sanitizedSummary,
            sanitizedDetail,
            tags,
            BuildRelatedIds(serverName, tags),
            source == MemorySource.LogClassifier ? 0.62 : 0.72,
            source == MemorySource.LogClassifier ? 0.78 : 0.84,
            source == MemorySource.LogClassifier ? DateTime.UtcNow.AddDays(14) : DateTime.UtcNow.AddDays(30),
            "server_fact");
    }

    private static void PromoteExceptionRecord(MemoryRecord record, ErrorSignal signal)
    {
        record.Type = MemoryRecordType.Exception;
        record.Source = MemorySource.LogClassifier;
        record.Text = ExtractExceptionSnippet(record.Text);
        record.Summary = BuildExceptionSummary(
            record.RelatedEntityIds.FirstOrDefault() ?? "server",
            record.Summary,
            record.Text);
        record.Tags = record.Tags.Concat(new[] { "exception", signal.SignalTag }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        record.Confidence = Math.Max(record.Confidence, signal.Confidence);
        record.Importance = Math.Max(record.Importance, signal.Confidence >= 0.82 ? 0.84 : 0.66);
        record.ApprovalState = signal.Confidence >= 0.82 ? MemoryApprovalState.Active : MemoryApprovalState.Pending;
        if (record.ApprovalState == MemoryApprovalState.Pending)
        {
            record.Metadata["approvalReason"] = "exception_confidence_below_auto_approval";
        }
    }

    private static List<string> BuildRelatedIds(string serverName, IEnumerable<string> tags)
    {
        return tags
            .Concat(new[] { serverName })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildExceptionSummary(string serverName, string fallbackSummary, string exceptionText)
    {
        var firstSignalLine = exceptionText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => LooksLikeExceptionOrError(line).HasSignal)
            ?? fallbackSummary;
        return TrimSingleLine($"Exception on {serverName}: {firstSignalLine}", 180);
    }

    private static string ExtractExceptionSnippet(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.TrimEntries);
        var start = Array.FindIndex(lines, line => LooksLikeExceptionOrError(line).HasSignal);
        if (start < 0)
        {
            return TrimMultiline(text, 1200);
        }

        var selected = new List<string>();
        for (var i = start; i < lines.Length && selected.Count < 14; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                if (selected.Count > 0)
                {
                    break;
                }

                continue;
            }

            var isStackFrame = Regex.IsMatch(line, @"^\s*at\s+[\w\.`]+\.", RegexOptions.IgnoreCase);
            var isContinuation = line.StartsWith("---", StringComparison.Ordinal) ||
                                 line.StartsWith("Caused by", StringComparison.OrdinalIgnoreCase) ||
                                 line.Contains(" in <", StringComparison.Ordinal);
            if (selected.Count > 0 &&
                !isStackFrame &&
                !isContinuation &&
                !LooksLikeExceptionOrError(line).HasSignal)
            {
                break;
            }

            selected.Add(line);
        }

        return TrimMultiline(string.Join('\n', selected), 1800);
    }

    private static ErrorSignal LooksLikeExceptionOrError(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ErrorSignal.None;
        }

        var lowered = text.ToLowerInvariant();
        var hasExceptionType = Regex.IsMatch(text, @"\b[A-Za-z_][A-Za-z0-9_.]*Exception\b", RegexOptions.IgnoreCase);
        var hasStackFrame = Regex.IsMatch(text, @"(?m)^\s*at\s+[\w\.`]+\.", RegexOptions.IgnoreCase);
        if (hasExceptionType && hasStackFrame)
        {
            return new ErrorSignal(true, 0.94, "exception-stack");
        }

        if (hasExceptionType || lowered.Contains("exception:", StringComparison.Ordinal))
        {
            return new ErrorSignal(true, 0.88, "exception");
        }

        if (lowered.Contains("failed to compile", StringComparison.Ordinal) ||
            lowered.Contains("fatal", StringComparison.Ordinal) ||
            lowered.Contains("crash", StringComparison.Ordinal))
        {
            return new ErrorSignal(true, 0.86, "error");
        }

        if (lowered.Contains("unable to connect", StringComparison.Ordinal) ||
            lowered.Contains("access denied", StringComparison.Ordinal) ||
            lowered.Contains("error:", StringComparison.Ordinal) ||
            lowered.Contains("failed:", StringComparison.Ordinal))
        {
            return new ErrorSignal(true, 0.78, "possible-error");
        }

        if (lowered.Contains("warn", StringComparison.Ordinal) ||
            lowered.Contains("disconnect", StringComparison.Ordinal))
        {
            return new ErrorSignal(true, 0.62, "warning");
        }

        return ErrorSignal.None;
    }

    private static bool LooksLikeRawConsoleOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lowered = text.ToLowerInvariant();
        return lowered.Contains("\nlog:", StringComparison.Ordinal) ||
               lowered.Contains("joined from ip", StringComparison.Ordinal) ||
               lowered.Contains("networkid", StringComparison.Ordinal) ||
               lowered.Contains("[chat]", StringComparison.Ordinal) ||
               Regex.IsMatch(text, @"(?m)^\s*at\s+[\w\.`]+\.", RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeMundaneConsoleNoise(string text)
    {
        var lowered = text.ToLowerInvariant();
        if (LooksLikeExceptionOrError(text).Confidence >= 0.78)
        {
            return false;
        }

        var mundaneSignals = new[]
        {
            "joined from ip",
            "networkid",
            "calling 'onplayerconnected'",
            "[garbage collect]",
            "[empty low fps]",
            "server is no longer empty",
            "setting fps limit"
        };

        return mundaneSignals.Any(signal => lowered.Contains(signal, StringComparison.Ordinal));
    }

    private static string TrimMultiline(string? value, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maxLength ? normalized : normalized[..Math.Max(0, maxLength - 3)] + "...";
    }

    private void LogDebug(string message)
    {
        if (_settings.DebugLoggingEnabled)
        {
            Console.WriteLine($"[memory] {message}");
        }
    }

    private sealed record ServerFactClassification(
        MemoryRecordType Type,
        MemoryScope Scope,
        MemorySource Source,
        MemoryApprovalState ApprovalState,
        string Summary,
        string Text,
        List<string> Tags,
        List<string> RelatedEntityIds,
        double Importance,
        double Confidence,
        DateTime? ExpiryUtc,
        string Reason);

    private readonly record struct ErrorSignal(bool HasSignal, double Confidence, string SignalTag)
    {
        public static ErrorSignal None => new(false, 0.0, string.Empty);
    }
}
