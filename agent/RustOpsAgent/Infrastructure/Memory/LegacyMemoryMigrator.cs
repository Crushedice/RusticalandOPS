using System.Text.Json;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Infrastructure.Memory;

internal sealed class LegacyMemoryMigrator
{
    private readonly string _legacyStatePath;
    private readonly string _neoCortexRoot;

    public LegacyMemoryMigrator(string legacyStatePath, string neoCortexRoot)
    {
        _legacyStatePath = legacyStatePath;
        _neoCortexRoot = neoCortexRoot;
    }

    public async Task<MemoryMigrationReport> MigrateAsync(
        Func<MemoryRecord, Task<MemoryImportDisposition>> importAsync,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var report = new MemoryMigrationReport { DryRun = dryRun };
        var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = GetMigrationFiles().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        report.FilesScanned.AddRange(files);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                var batch = await ExtractRecordsAsync(file, cancellationToken);
                report.MalformedEntries += batch.MalformedEntries;
                report.OtherErrors += batch.OtherErrors;
                report.SkipReasons.AddRange(batch.Issues);

                foreach (var record in batch.Records)
                {
                    report.RecordsDiscovered++;

                    try
                    {
                        record.Normalize();
                        if (!seenHashes.Add(record.ContentHash))
                        {
                            report.DuplicatesRemoved++;
                            report.RecordsSkipped++;
                            report.SkipReasons.Add($"duplicate_in_run:{record.Metadata.GetValueOrDefault("originalPath", file)}:{record.ContentHash[..Math.Min(12, record.ContentHash.Length)]}");
                            continue;
                        }

                        var disposition = dryRun ? MemoryImportDisposition.Imported : await importAsync(record);
                        switch (disposition)
                        {
                            case MemoryImportDisposition.Imported:
                                report.RecordsImported++;
                                break;
                            case MemoryImportDisposition.Duplicate:
                                report.DuplicatesRemoved++;
                                report.RecordsSkipped++;
                                break;
                            case MemoryImportDisposition.EmbeddingFailure:
                                report.EmbeddingFailures++;
                                report.RecordsSkipped++;
                                break;
                            case MemoryImportDisposition.Invalid:
                                report.MalformedEntries++;
                                report.RecordsSkipped++;
                                break;
                            default:
                                report.RecordsSkipped++;
                                break;
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or JsonException)
                    {
                        report.MalformedEntries++;
                        report.RecordsSkipped++;
                        report.SkipReasons.Add($"invalid_record:{file}:{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[memory] migration failed for {file}: {ex.Message}");
                report.OtherErrors++;
                report.SkipReasons.Add($"file_error:{file}:{ex.Message}");
            }
        }

        return report;
    }

    private IEnumerable<string> GetMigrationFiles()
    {
        yield return _legacyStatePath;
        if (!Directory.Exists(_neoCortexRoot))
        {
            yield break;
        }

        foreach (var file in Directory.GetFiles(_neoCortexRoot, "*.json", SearchOption.AllDirectories))
        {
            yield return file;
        }

        foreach (var file in Directory.GetFiles(_neoCortexRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            yield return file;
        }
    }

    private async Task<LegacyExtractionBatch> ExtractRecordsAsync(string file, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(file);
        if (fileName.Equals("agent-state.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("legacy-state.json", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, cancellationToken));
            return new LegacyExtractionBatch(ExtractLegacyState(file, doc.RootElement));
        }

        if (file.EndsWith("incidents.jsonl", StringComparison.OrdinalIgnoreCase))
        {
            var records = new List<MemoryRecord>();
            var issues = new List<string>();
            var malformedEntries = 0;
            var lines = await File.ReadAllLinesAsync(file, cancellationToken);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    records.Add(ImportedRecord(
                        file,
                        MemoryRecordType.Failure,
                        MemoryScope.Project,
                        $"Imported incident: {ReadString(doc.RootElement, "classification")}",
                        $"Request: {ReadString(doc.RootElement, "request")}\nFailure: {ReadString(doc.RootElement, "failureReason")}\nMissingCapability: {ReadString(doc.RootElement, "missingCapability")}\nRecurrencePrevention: {ReadString(doc.RootElement, "recurrencePrevention")}",
                        new[] { "legacy-import", "incident", ReadString(doc.RootElement, "classification") },
                        null,
                        0.82,
                        0.82));
                }
                catch (Exception ex)
                {
                    malformedEntries++;
                    issues.Add($"malformed_jsonl:{file}:line={index + 1}:{ex.Message}");
                }
            }

            return new LegacyExtractionBatch(records, malformedEntries: malformedEntries, issues: issues);
        }

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, cancellationToken));
        var root = json.RootElement;
        if (file.EndsWith("active-state.json", StringComparison.OrdinalIgnoreCase))
        {
            return new LegacyExtractionBatch(ExtractActiveState(file, root));
        }

        if (file.EndsWith("command-policy.json", StringComparison.OrdinalIgnoreCase))
        {
            return new LegacyExtractionBatch(ExtractCommandPolicy(file, root));
        }

        if (file.EndsWith("log-knowledge.json", StringComparison.OrdinalIgnoreCase))
        {
            return new LegacyExtractionBatch(ExtractLogKnowledge(file, root));
        }

        if (file.EndsWith("monitor.json", StringComparison.OrdinalIgnoreCase))
        {
            return new LegacyExtractionBatch(ExtractConsoleMonitor(file, root));
        }

        if (file.EndsWith("session-state.json", StringComparison.OrdinalIgnoreCase))
        {
            return new LegacyExtractionBatch(ExtractSelectionState(file, root));
        }

        if (file.EndsWith("knowledge.json", StringComparison.OrdinalIgnoreCase) &&
            file.Contains($"{Path.DirectorySeparatorChar}classifier{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return new LegacyExtractionBatch(ExtractClassifierKnowledge(file, root));
        }

        if (file.EndsWith("knowledge.json", StringComparison.OrdinalIgnoreCase) &&
            file.Contains($"{Path.DirectorySeparatorChar}chat{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return new LegacyExtractionBatch(ExtractPlayerChatKnowledge(file, root));
        }

        return new LegacyExtractionBatch(Array.Empty<MemoryRecord>());
    }

    private static IReadOnlyList<MemoryRecord> ExtractLegacyState(string file, JsonElement root)
    {
        var results = new List<MemoryRecord>();
        if (root.TryGetProperty("actionHistory", out var actions) && actions.ValueKind == JsonValueKind.Array)
        {
            foreach (var action in actions.EnumerateArray())
            {
                var summary = ReadString(action, "summary");
                if (!MemorySanitizer.LooksUseful(summary, summary))
                {
                    continue;
                }

                var success = action.TryGetProperty("success", out var successNode) && successNode.ValueKind == JsonValueKind.True;
                var server = ReadString(action, "serverName");
                results.Add(ImportedRecord(
                    file,
                    success ? MemoryRecordType.Procedure : MemoryRecordType.Failure,
                    string.IsNullOrWhiteSpace(server) ? MemoryScope.Project : MemoryScope.Server,
                    Trim(summary, 160),
                    $"ActionType: {ReadString(action, "actionType")}\nSummary: {summary}",
                    new[] { "legacy-import", success ? "success" : "failure" },
                    server is null ? null : new[] { server },
                    success ? 0.55 : 0.78,
                    0.74));
            }
        }

        if (root.TryGetProperty("feedbackHistory", out var feedback) && feedback.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in feedback.EnumerateArray())
            {
                var note = ReadString(entry, "note");
                if (string.IsNullOrWhiteSpace(note))
                {
                    continue;
                }

                var server = ReadString(entry, "serverName");
                var adminId = ReadString(entry, "adminId");
                results.Add(ImportedRecord(
                    file,
                    MemoryRecordType.UserInstruction,
                    string.IsNullOrWhiteSpace(server) ? MemoryScope.User : MemoryScope.Server,
                    Trim(note, 160),
                    $"Imported admin feedback.\nNote: {note}",
                    new[] { "legacy-import", "feedback" },
                    new[] { adminId, server }.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>(),
                    0.72,
                    0.7));
            }
        }

        return results;
    }

    private static IReadOnlyList<MemoryRecord> ExtractActiveState(string file, JsonElement root)
    {
        var results = new List<MemoryRecord>();
        if (!root.TryGetProperty("recentActions", out var recentActions) || recentActions.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var action in recentActions.EnumerateArray())
        {
            var result = ReadString(action, "result") ?? string.Empty;
            if (!MemorySanitizer.LooksUseful(result, result))
            {
                continue;
            }

            var server = ReadString(action, "serverName");
            results.Add(ImportedRecord(
                file,
                result.Contains("success", StringComparison.OrdinalIgnoreCase) ? MemoryRecordType.Procedure : MemoryRecordType.ToolObservation,
                string.IsNullOrWhiteSpace(server) ? MemoryScope.Project : MemoryScope.Server,
                Trim(result, 160),
                $"Intent: {ReadString(action, "intent")}\nResult: {result}",
                new[] { "legacy-import", "active-state" },
                server is null ? null : new[] { server },
                0.48,
                0.6));
        }

        return results;
    }

    private static IReadOnlyList<MemoryRecord> ExtractCommandPolicy(string file, JsonElement root)
    {
        var results = new List<MemoryRecord>();
        if (!root.TryGetProperty("commands", out var commandsNode) || commandsNode.ValueKind != JsonValueKind.Object)
        {
            return results;
        }

        foreach (var command in commandsNode.EnumerateObject())
        {
            var successCount = command.Value.TryGetProperty("successCount", out var successNode) ? successNode.GetInt32() : 0;
            var failCount = command.Value.TryGetProperty("failCount", out var failNode) ? failNode.GetInt32() : 0;
            if (successCount == 0 && failCount == 0)
            {
                continue;
            }

            results.Add(ImportedRecord(
                file,
                failCount > successCount ? MemoryRecordType.Failure : MemoryRecordType.Procedure,
                MemoryScope.Tool,
                failCount > successCount
                    ? $"Command `{command.Name}` often fails and may require approval."
                    : $"Command `{command.Name}` has succeeded repeatedly and is auto-allowed.",
                $"Command: {command.Name}\nSuccessCount: {successCount}\nFailCount: {failCount}",
                new[] { "legacy-import", "command-policy", $"command:{command.Name}" },
                new[] { command.Name },
                failCount > successCount ? 0.76 : 0.64,
                0.78,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["actionFingerprint"] = $"rcon|{command.Name}"
                }));
        }

        return results;
    }

    private static IReadOnlyList<MemoryRecord> ExtractLogKnowledge(string file, JsonElement root)
    {
        var results = new List<MemoryRecord>();
        if (root.TryGetProperty("importanceRules", out var rulesNode) && rulesNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var ruleNode in rulesNode.EnumerateArray())
            {
                var rule = ruleNode.GetString();
                if (string.IsNullOrWhiteSpace(rule))
                {
                    continue;
                }

                results.Add(ImportedRecord(
                    file,
                    MemoryRecordType.Procedure,
                    MemoryScope.Tool,
                    $"Important log rule: {rule}",
                    $"Imported log importance rule.\nRule: {rule}",
                    new[] { "legacy-import", "log-rule" },
                    null,
                    0.58,
                    0.7));
            }
        }

        if (root.TryGetProperty("recentEntries", out var recentEntries) && recentEntries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in recentEntries.EnumerateArray().TakeLast(50))
            {
                var line = ReadString(entry, "line");
                var server = ReadString(entry, "serverName");
                var importance = entry.TryGetProperty("importance", out var importanceNode) ? importanceNode.GetInt32() : 0;
                if (string.IsNullOrWhiteSpace(line) || importance < 2)
                {
                    continue;
                }

                results.Add(ImportedRecord(
                    file,
                    MemoryRecordType.ToolObservation,
                    string.IsNullOrWhiteSpace(server) ? MemoryScope.Project : MemoryScope.Server,
                    Trim(line, 160),
                    $"Server: {server}\nLine: {line}",
                    new[] { "legacy-import", "log-observation" },
                    server is null ? null : new[] { server },
                    Math.Min(1.0, importance / 5.0),
                    0.7));
            }
        }

        return results;
    }

    private static IReadOnlyList<MemoryRecord> ExtractConsoleMonitor(string file, JsonElement root)
    {
        var results = new List<MemoryRecord>();
        if (!root.TryGetProperty("servers", out var serversNode) || serversNode.ValueKind != JsonValueKind.Object)
        {
            return results;
        }

        foreach (var serverEntry in serversNode.EnumerateObject())
        {
            if (!serverEntry.Value.TryGetProperty("recentErrors", out var errorsNode) || errorsNode.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var error in errorsNode.EnumerateArray())
            {
                var message = ReadString(error, "message");
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                results.Add(ImportedRecord(
                    file,
                    MemoryRecordType.Failure,
                    MemoryScope.Server,
                    Trim(message, 160),
                    $"Server: {serverEntry.Name}\nMessage: {message}\nCount: {(error.TryGetProperty("count", out var countNode) ? countNode.GetInt32() : 1)}",
                    new[] { "legacy-import", "console-error", $"server:{serverEntry.Name}" },
                    new[] { serverEntry.Name },
                    0.8,
                    0.75));
            }
        }

        return results;
    }

    private static IReadOnlyList<MemoryRecord> ExtractSelectionState(string file, JsonElement root)
    {
        var results = new List<MemoryRecord>();
        if (!root.TryGetProperty("conversations", out var conversationsNode) || conversationsNode.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var conversation in conversationsNode.EnumerateArray())
        {
            var adminId = ReadString(conversation, "adminId");
            var lastServer = ReadString(conversation, "lastServerName");
            if (string.IsNullOrWhiteSpace(adminId) || string.IsNullOrWhiteSpace(lastServer))
            {
                continue;
            }

            results.Add(ImportedRecord(
                file,
                MemoryRecordType.UserInstruction,
                MemoryScope.User,
                $"Admin {adminId} last targeted server {lastServer}.",
                $"AdminId: {adminId}\nLastServerName: {lastServer}",
                new[] { "legacy-import", "selection-state" },
                new[] { adminId, lastServer },
                0.4,
                0.55,
                expiryUtc: DateTime.UtcNow.AddDays(7)));
        }

        return results;
    }

    private static IReadOnlyList<MemoryRecord> ExtractClassifierKnowledge(string file, JsonElement root)
    {
        var results = new List<MemoryRecord>();
        if (!root.TryGetProperty("learnedRules", out var rulesNode) || rulesNode.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var rule in rulesNode.EnumerateArray())
        {
            var ruleText = ReadString(rule, "rule");
            if (string.IsNullOrWhiteSpace(ruleText))
            {
                continue;
            }

            results.Add(ImportedRecord(
                file,
                MemoryRecordType.Reflection,
                MemoryScope.Project,
                Trim(ruleText, 160),
                $"Rule: {ruleText}\nRationale: {ReadString(rule, "rationale")}",
                new[] { "legacy-import", "classifier" },
                null,
                0.66,
                0.74));
        }

        return results;
    }

    private static IReadOnlyList<MemoryRecord> ExtractPlayerChatKnowledge(string file, JsonElement root)
    {
        var results = new List<MemoryRecord>();
        if (!root.TryGetProperty("constructiveFeedback", out var feedbackNode) || feedbackNode.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var feedback in feedbackNode.EnumerateArray())
        {
            var value = feedback.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            results.Add(ImportedRecord(
                file,
                MemoryRecordType.ToolObservation,
                MemoryScope.Project,
                Trim(value, 160),
                $"Imported player chat constructive feedback.\n{value}",
                new[] { "legacy-import", "player-feedback" },
                null,
                0.46,
                0.6));
        }

        return results;
    }

    private static MemoryRecord ImportedRecord(
        string file,
        MemoryRecordType type,
        MemoryScope scope,
        string summary,
        string detail,
        IEnumerable<string?> tags,
        IEnumerable<string>? relatedIds,
        double importance,
        double confidence,
        Dictionary<string, string>? metadata = null,
        DateTime? expiryUtc = null)
    {
        var record = new MemoryRecord
        {
            Type = type,
            Scope = scope,
            Source = MemorySource.ManualImport,
            Summary = MemorySanitizer.Sanitize(summary),
            Text = MemorySanitizer.Sanitize(detail),
            Tags = tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RelatedEntityIds = relatedIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>(),
            Importance = importance,
            Confidence = confidence,
            ExpiryUtc = expiryUtc,
            Metadata = new Dictionary<string, string>(metadata ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
            {
                ["originalPath"] = file
            }
        };

        return record;
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var node) && node.ValueKind != JsonValueKind.Null ? node.ToString() : null;

    private static string Trim(string? value, int maxLength)
    {
        var normalized = value?.Replace('\r', ' ').Replace('\n', ' ').Trim() ?? string.Empty;
        return normalized.Length <= maxLength ? normalized : normalized[..Math.Max(0, maxLength - 3)] + "...";
    }

    private sealed class LegacyExtractionBatch
    {
        public LegacyExtractionBatch(
            IReadOnlyList<MemoryRecord> records,
            int malformedEntries = 0,
            int otherErrors = 0,
            IReadOnlyList<string>? issues = null)
        {
            Records = records;
            MalformedEntries = malformedEntries;
            OtherErrors = otherErrors;
            Issues = issues ?? Array.Empty<string>();
        }

        public IReadOnlyList<MemoryRecord> Records { get; }
        public int MalformedEntries { get; }
        public int OtherErrors { get; }
        public IReadOnlyList<string> Issues { get; }
    }
}
