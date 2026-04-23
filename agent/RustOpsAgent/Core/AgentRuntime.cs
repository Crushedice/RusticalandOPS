using Microsoft.SemanticKernel;
using System.Text.Json;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Core.Interaction;
using RustOpsAgent.Infrastructure;
using RustOpsAgent.Infrastructure.Connectors;
using RustOpsAgent.Infrastructure.GitOps;
using RustOpsAgent.Infrastructure.Memory;

namespace RustOpsAgent.Core;

internal sealed class AgentRuntime
{
    private readonly AgentConfig _config;
    private readonly IIntentClassifier _classifier;
    private readonly IActionExecutor _executor;
    private readonly IResponseComposer _composer;
    private readonly NeoCortexStore _neoCortex;
    private readonly LegacyAgentStateStore _legacyState;
    private readonly IGitOpsService _gitOps;
    private readonly IReadOnlyList<IConnectorLogSource> _connectors;
    private readonly Kernel? _kernel;
    private readonly Dictionary<string, string> _observationFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastConnectorObservationAtUtc = DateTime.MinValue;
    private volatile bool _stop;

    public AgentRuntime(
        AgentConfig config,
        IIntentClassifier classifier,
        IActionExecutor executor,
        IResponseComposer composer,
        NeoCortexStore neoCortex,
        LegacyAgentStateStore legacyState,
        IGitOpsService gitOps,
        IReadOnlyList<IConnectorLogSource> connectors,
        Kernel? kernel)
    {
        _config = config;
        _classifier = classifier;
        _executor = executor;
        _composer = composer;
        _neoCortex = neoCortex;
        _legacyState = legacyState;
        _gitOps = gitOps;
        _connectors = connectors;
        _kernel = kernel;
    }

    public void RequestStop() => _stop = true;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[agent] Runtime started.");
        var tick = 0;
        while (!_stop && !cancellationToken.IsCancellationRequested)
        {
            _legacyState.UpdateRuntimeStatus(_config.Llm);
            await ProcessFeedbackInboxAsync(cancellationToken);
            await ProcessDecisionInboxAsync(cancellationToken);
            await ProcessLogInboxAsync(cancellationToken);

            var chatFiles = Directory.Exists(_config.Inbox.ChatInboxPath)
                ? Directory.GetFiles(_config.Inbox.ChatInboxPath, "*.json").Length
                : 0;
            var logFiles = Directory.Exists(_config.Inbox.LogInboxPath)
                ? Directory.GetFiles(_config.Inbox.LogInboxPath, "*.json").Length
                : 0;

            if (chatFiles > 0 || logFiles > 0 || tick % 5 == 0)
            {
                Console.WriteLine($"[agent] Tick {tick}: chat-inbox={chatFiles} file(s), log-inbox={logFiles} file(s)");
            }

            await ProcessChatInboxAsync(cancellationToken);
            await ObserveConnectorLogsAsync(cancellationToken);
            _legacyState.Save();

            tick++;
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _config.Monitor.PollSeconds)), cancellationToken);
        }

        Console.WriteLine("[agent] Runtime stopped.");
    }

    private async Task ProcessFeedbackInboxAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_config.Inbox.FeedbackInboxPath))
        {
            return;
        }

        foreach (var file in EnumerateInboxFiles(_config.Inbox.FeedbackInboxPath))
        {
            if (_stop || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var payload = JsonSerializer.Deserialize<FeedbackInboxItem>(
                    await File.ReadAllTextAsync(file, cancellationToken),
                    JsonDefaults.Default);
                if (payload is null)
                {
                    continue;
                }

                _legacyState.RecordFeedback(payload.AdminId, payload.ActionId, payload.Verdict, payload.Note, payload.ServerName);

                var note = payload.Note ?? payload.Preference;
                if (!string.IsNullOrWhiteSpace(note) && note.StartsWith("ignore ", StringComparison.OrdinalIgnoreCase))
                {
                    var pattern = note["ignore ".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(pattern))
                    {
                        var ignore = _neoCortex.LoadIgnoreFeedback();
                        if (!ignore.PartialMatches.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                        {
                            ignore.PartialMatches.Add(pattern);
                            _neoCortex.SaveIgnoreFeedback(ignore);
                        }

                        var logs = _neoCortex.LoadLogs();
                        if (!logs.IgnorePatterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                        {
                            logs.IgnorePatterns.Add(pattern);
                            _neoCortex.SaveLogs(logs);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(payload.AdminId))
                {
                    WriteOutbox(payload.AdminId!, "Feedback received and applied.", payload.ActionId, payload.ServerName);
                }
            }
            catch (Exception ex)
            {
                _legacyState.RecordAgentError($"feedback inbox processing failed: {ex.Message}");
                RustOpsSentry.CaptureException(
                    ex,
                    "Feedback inbox processing failed.",
                    "agent.inbox",
                    tags: new Dictionary<string, string?> { ["inbox.kind"] = "feedback" },
                    extras: new Dictionary<string, object?> { ["file"] = file });
            }
            finally
            {
                TryDelete(file);
            }
        }
    }

    private async Task ProcessDecisionInboxAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_config.Inbox.DecisionInboxPath))
        {
            return;
        }

        foreach (var file in EnumerateInboxFiles(_config.Inbox.DecisionInboxPath))
        {
            if (_stop || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var payload = JsonSerializer.Deserialize<DecisionInboxItem>(
                    await File.ReadAllTextAsync(file, cancellationToken),
                    JsonDefaults.Default);
                if (payload is null)
                {
                    continue;
                }

                _legacyState.RecordFeedback(payload.AdminId, payload.ActionId, payload.Decision, payload.Note, null);
                if (!string.IsNullOrWhiteSpace(payload.AdminId))
                {
                    var action = string.IsNullOrWhiteSpace(payload.ActionId) ? "(unknown action)" : payload.ActionId;
                    var decision = string.IsNullOrWhiteSpace(payload.Decision) ? "noted" : payload.Decision;
                    WriteOutbox(payload.AdminId!, $"Decision received for {action}: {decision}.", payload.ActionId, null);
                }
            }
            catch (Exception ex)
            {
                _legacyState.RecordAgentError($"decision inbox processing failed: {ex.Message}");
                RustOpsSentry.CaptureException(
                    ex,
                    "Decision inbox processing failed.",
                    "agent.inbox",
                    tags: new Dictionary<string, string?> { ["inbox.kind"] = "decision" },
                    extras: new Dictionary<string, object?> { ["file"] = file });
            }
            finally
            {
                TryDelete(file);
            }
        }
    }

    private async Task ProcessLogInboxAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_config.Inbox.LogInboxPath))
        {
            return;
        }

        foreach (var file in EnumerateInboxFiles(_config.Inbox.LogInboxPath))
        {
            if (_stop || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            LogIngestInboxItem? payload = null;
            try
            {
                payload = JsonSerializer.Deserialize<LogIngestInboxItem>(
                    await File.ReadAllTextAsync(file, cancellationToken),
                    JsonDefaults.Default);
                if (payload is null)
                {
                    continue;
                }

                var source = string.IsNullOrWhiteSpace(payload.Source) ? "manual" : payload.Source.Trim();
                var connector = string.IsNullOrWhiteSpace(payload.Connector) ? source : payload.Connector.Trim();
                var logs = _neoCortex.LoadLogs();
                var added = 0;

                if (payload.Lines.Count > 0)
                {
                    foreach (var line in payload.Lines.Where(line => !string.IsNullOrWhiteSpace(line.Message)))
                    {
                        if (!TryRegisterObservationFingerprint(connector, line.Message))
                        {
                            continue;
                        }

                        var importance = ScoreImportance(line.Message, line.Level, logs.ImportanceRules);
                        logs.RecentEntries.Add(new LogObservation
                        {
                            ServerName = connector,
                            Source = source,
                            Connector = connector,
                            Level = line.Level,
                            Line = line.Message,
                            Importance = importance,
                            CapturedAtUtc = line.TimestampUtc ?? DateTime.UtcNow
                        });
                        added++;
                    }
                }

                if (!string.IsNullOrWhiteSpace(payload.Content))
                {
                    foreach (var line in payload.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!TryRegisterObservationFingerprint(connector, line))
                        {
                            continue;
                        }

                        var importance = ScoreImportance(line, null, logs.ImportanceRules);
                        logs.RecentEntries.Add(new LogObservation
                        {
                            ServerName = connector,
                            Source = source,
                            Connector = connector,
                            Line = line,
                            Importance = importance,
                            CapturedAtUtc = DateTime.UtcNow
                        });
                        added++;
                    }
                }

                if (added == 0)
                {
                    continue;
                }

                logs.RecentEntries = logs.RecentEntries
                    .OrderBy(entry => entry.CapturedAtUtc)
                    .TakeLast(1200)
                    .ToList();
                _neoCortex.SaveLogs(logs);

                var latest = logs.RecentEntries
                    .Where(entry => string.Equals(entry.Connector, connector, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(entry => entry.CapturedAtUtc)
                    .Take(25)
                    .ToList();

                var highSignal = latest
                    .Where(entry => entry.Importance >= 2)
                    .Take(6)
                    .Select(entry => $"[{entry.Level ?? "info"}] {entry.Line}")
                    .ToList();

                var summary = highSignal.Count > 0
                    ? $"Ingested {added} log lines from {source}. High-signal lines: {string.Join(" | ", highSignal)}"
                    : $"Ingested {added} log lines from {source}. No high-signal patterns were detected.";

                var analysis = await AnalyzeObservationWithLlmAsync(source, latest.Select(entry => entry.Line).Take(12).ToList(), cancellationToken);
                RecordLlmInteraction(
                    "log-ingest-analysis",
                    analysis.LlmAttempted,
                    analysis.LlmSucceeded,
                    $"log ingest {source}",
                    analysis.Summary,
                    analysis.Source);

                _legacyState.RecordAction(
                    payload.RequestId ?? payload.Id,
                    "log_ingest",
                    true,
                    summary,
                    connector,
                    payload.Channel ?? "web");

                if (!string.IsNullOrWhiteSpace(payload.AdminId))
                {
                    WriteOutbox(payload.AdminId, $"{summary} {analysis.Summary}", payload.RequestId ?? payload.Id, connector);
                }
            }
            catch (Exception ex)
            {
                _legacyState.RecordAgentError($"log inbox processing failed: {ex.Message}");
                _legacyState.RecordIncident(payload?.Connector ?? payload?.Source, "log_ingest_error", ex.Message);
                RustOpsSentry.CaptureException(
                    ex,
                    "Log inbox processing failed.",
                    "agent.inbox",
                    tags: new Dictionary<string, string?> { ["inbox.kind"] = "log" },
                    extras: new Dictionary<string, object?> { ["file"] = file });
            }
            finally
            {
                TryDelete(file);
            }
        }
    }

    private async Task ProcessChatInboxAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_config.Inbox.ChatInboxPath))
        {
            return;
        }

        foreach (var file in EnumerateInboxFiles(_config.Inbox.ChatInboxPath))
        {
            if (_stop || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ChatInboxItem? item = null;
            try
            {
                item = JsonSerializer.Deserialize<ChatInboxItem>(File.ReadAllText(file), JsonDefaults.Default);
                if (item is null || string.IsNullOrWhiteSpace(item.Message))
                {
                    continue;
                }

                var actionId = string.IsNullOrWhiteSpace(item.RequestId) ? item.Id : item.RequestId!;
                var selection = _neoCortex.LoadSelection();
                var state = selection.Conversations.FirstOrDefault(conversation =>
                    string.Equals(conversation.AdminId, item.AdminId, StringComparison.OrdinalIgnoreCase));
                if (state is null)
                {
                    state = new ConversationSelectionState { AdminId = item.AdminId };
                    selection.Conversations.Add(state);
                }

                var route = await _classifier.ClassifyAsync(item.Message, state, cancellationToken);
                RecordIntentRoutingInteraction(item.Message, route);

                var context = new ToolExecutionContext(item.AdminId, item.Message, route, state, DateTime.UtcNow);
                var result = await _executor.ExecuteAsync(context, cancellationToken);
                var composedReply = await _composer.ComposeAsync(context, result, cancellationToken);
                RecordLlmInteraction(
                    composedReply.Type,
                    composedReply.LlmAttempted,
                    composedReply.LlmSucceeded,
                    item.Message,
                    composedReply.ResponsePreview ?? composedReply.Message,
                    composedReply.Source);

                UpdateSelectionState(state, route, result);
                _neoCortex.SaveSelection(selection);

                var operations = _neoCortex.LoadOperations();
                operations.RuntimeStatus = new RuntimeStatus
                {
                    LlmEnabled = _config.Llm.Enabled,
                    LlmProvider = _config.Llm.Provider,
                    LastLlmInteractionAtUtc = operations.LlmInteractions.FirstOrDefault()?.AtUtc,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                operations.RecentActions.Add(new ActionRecord
                {
                    Intent = route.Intent.ToString(),
                    Result = result.Success ? "success" : (result.ErrorCode ?? "failed"),
                    ServerName = result.SelectedServer ?? route.Slots.ServerName,
                    TimestampUtc = DateTime.UtcNow
                });
                operations.RecentActions = operations.RecentActions.TakeLast(100).ToList();
                _neoCortex.SaveOperations(operations);

                _legacyState.RecordAction(
                    actionId,
                    route.Intent.ToString().ToLowerInvariant(),
                    result.Success,
                    result.Message,
                    result.SelectedServer ?? route.Slots.ServerName,
                    "chat");

                if (!result.Success)
                {
                    var incident = new EvolutionIncidentRecord
                    {
                        Request = item.Message,
                        IntendedOutcome = route.Intent.ToString(),
                        FailureReason = result.Message,
                        MissingCapability = result.ErrorCode ?? "unknown",
                        RecurrencePrevention = "Improve handler coverage and routing slots.",
                        Classification = result.ErrorCode ?? "execution_failure",
                        Timestamp = DateTime.UtcNow,
                        Resolved = false
                    };
                    await _neoCortex.RecordIncidentAsync(incident, cancellationToken);
                    _legacyState.RecordIncident(
                        result.SelectedServer ?? route.Slots.ServerName,
                        result.ErrorCode ?? "execution_failure",
                        result.Message);

                    if (_config.GitOps.Enabled && _config.GitOps.AllowPush)
                    {
                        _ = TryProposeIncidentBranchAsync(incident, cancellationToken);
                    }
                }

                WriteOutbox(item.AdminId, composedReply.Message, actionId, result.SelectedServer ?? route.Slots.ServerName);
            }
            catch (Exception ex)
            {
                await _neoCortex.RecordIncidentAsync(new EvolutionIncidentRecord
                {
                    Request = item?.Message ?? "<invalid inbox item>",
                    IntendedOutcome = "processing",
                    FailureReason = ex.Message,
                    MissingCapability = "processing_error",
                    RecurrencePrevention = "Validate chat inbox payloads before processing.",
                    Classification = "processing_error",
                    Timestamp = DateTime.UtcNow,
                    Resolved = false
                }, cancellationToken);

                _legacyState.RecordAgentError(ex.Message);
                _legacyState.RecordIncident(null, "processing_error", ex.Message);
                RustOpsSentry.CaptureException(
                    ex,
                    "Chat inbox processing failed.",
                    "agent.inbox",
                    tags: new Dictionary<string, string?> { ["inbox.kind"] = "chat" },
                    extras: new Dictionary<string, object?>
                    {
                        ["file"] = file,
                        ["adminId"] = item?.AdminId,
                        ["requestId"] = item?.RequestId,
                        ["message"] = item?.Message
                    });

                WriteOutbox(item?.AdminId ?? "admin", $"Failed to process request: {ex.Message}", item?.RequestId ?? item?.Id, null);
            }
            finally
            {
                TryDelete(file);
            }
        }
    }

    private async Task ObserveConnectorLogsAsync(CancellationToken cancellationToken)
    {
        var intervalSeconds = Math.Max(10, _config.Integrations.PollSeconds);
        if (DateTime.UtcNow - _lastConnectorObservationAtUtc < TimeSpan.FromSeconds(intervalSeconds))
        {
            return;
        }

        _lastConnectorObservationAtUtc = DateTime.UtcNow;
        var activeConnectors = _connectors.Where(connector => connector.Enabled).ToList();
        if (activeConnectors.Count == 0)
        {
            return;
        }

        foreach (var connector in activeConnectors)
        {
            if (_stop || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var fetched = await connector.FetchRecentLogsAsync(cancellationToken);
                if (!fetched.Success)
                {
                    _legacyState.RecordAgentError($"{connector.Name} sync failed: {fetched.Summary}");
                    continue;
                }

                if (fetched.Records.Count == 0)
                {
                    continue;
                }

                var logs = _neoCortex.LoadLogs();
                var added = 0;
                foreach (var record in fetched.Records.Take(Math.Max(10, _config.Integrations.MaxLogsPerPoll)))
                {
                    if (!TryRegisterObservationFingerprint(record.Connector, record.Message))
                    {
                        continue;
                    }

                    var importance = ScoreImportance(record.Message, record.Level, logs.ImportanceRules);
                    logs.RecentEntries.Add(new LogObservation
                    {
                        ServerName = record.Connector,
                        Source = record.Source,
                        Connector = record.Connector,
                        Level = record.Level,
                        Line = record.Message,
                        Importance = importance,
                        CapturedAtUtc = record.TimestampUtc
                    });
                    added++;
                }

                if (added == 0)
                {
                    continue;
                }

                logs.RecentEntries = logs.RecentEntries
                    .OrderBy(entry => entry.CapturedAtUtc)
                    .TakeLast(1200)
                    .ToList();
                _neoCortex.SaveLogs(logs);

                _legacyState.RecordAction(
                    Guid.NewGuid().ToString("N"),
                    "connector_log_poll",
                    true,
                    $"{connector.Name}: {added} log entries ingested.",
                    connector.Name,
                    "polling");

                var important = logs.RecentEntries
                    .Where(entry =>
                        string.Equals(entry.Connector, connector.Name, StringComparison.OrdinalIgnoreCase) &&
                        entry.Importance >= 2)
                    .OrderByDescending(entry => entry.CapturedAtUtc)
                    .Take(6)
                    .Select(entry => entry.Line)
                    .ToList();
                if (important.Count == 0)
                {
                    continue;
                }

                var analysis = await AnalyzeObservationWithLlmAsync(connector.Name, important, cancellationToken);
                RecordLlmInteraction(
                    "connector-observation-analysis",
                    analysis.LlmAttempted,
                    analysis.LlmSucceeded,
                    $"{connector.Name} logs",
                    analysis.Summary,
                    analysis.Source);

                _legacyState.RecordIncident(connector.Name, analysis.Classification, analysis.Summary);
                await _neoCortex.RecordIncidentAsync(new EvolutionIncidentRecord
                {
                    Request = $"observe logs {connector.Name}",
                    IntendedOutcome = "continuous_observation",
                    FailureReason = analysis.Summary,
                    MissingCapability = analysis.MissingCapability,
                    RecurrencePrevention = analysis.RecurrencePrevention,
                    Classification = analysis.Classification,
                    Timestamp = DateTime.UtcNow,
                    Resolved = false
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _legacyState.RecordAgentError($"connector observation failed for {connector.Name}: {ex.Message}");
            }
        }
    }

    private void RecordIntentRoutingInteraction(string message, AdminIntentRoute route)
    {
        RecordLlmInteraction(
            route.LlmAttempted ? "intent-routing" : "intent-routing-fallback",
            route.LlmAttempted,
            route.LlmSucceeded,
            message,
            $"intent={route.Intent} target={route.TargetRef ?? "n/a"} source={route.ClassifierSource}",
            route.ClassifierSource);
    }

    private static int ScoreImportance(string line, string? level, IEnumerable<string> dynamicRules)
    {
        var normalized = line.ToLowerInvariant();
        var severity = (level ?? string.Empty).ToLowerInvariant();

        if (severity is "critical" or "fatal" or "error")
        {
            return 3;
        }

        if (severity is "warn" or "warning")
        {
            return 2;
        }

        if (normalized.Contains("exception") || normalized.Contains("failed") || normalized.Contains("error"))
        {
            return 3;
        }

        if (normalized.Contains("warn") || normalized.Contains("disconnect") || normalized.Contains("timeout"))
        {
            return 2;
        }

        return dynamicRules.Any(rule => normalized.Contains(rule, StringComparison.OrdinalIgnoreCase)) ? 2 : 1;
    }

    private bool TryRegisterObservationFingerprint(string source, string line)
    {
        var fingerprint = $"{source}:{TrimSingleLine(line, 220)}";
        if (_observationFingerprints.ContainsKey(fingerprint))
        {
            return false;
        }

        _observationFingerprints[fingerprint] = DateTime.UtcNow.ToString("O");
        if (_observationFingerprints.Count > 2500)
        {
            var oldest = _observationFingerprints
                .OrderBy(kvp => kvp.Value, StringComparer.Ordinal)
                .Take(400)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in oldest)
            {
                _observationFingerprints.Remove(key);
            }
        }

        return true;
    }

    private static string TrimSingleLine(string input, int maxLength)
    {
        var singleLine = input.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maxLength
            ? singleLine
            : $"{singleLine[..Math.Max(0, maxLength - 3)]}...";
    }

    private void RecordLlmInteraction(string type, bool llmAttempted, bool llmSucceeded, string? context, string? responsePreview, string? source)
    {
        var operations = _neoCortex.LoadOperations();
        operations.LlmInteractions.Add(new LlmInteractionRecord
        {
            AtUtc = DateTime.UtcNow,
            Type = type,
            Model = llmAttempted ? _config.Llm.Model : null,
            Success = llmSucceeded,
            Context = TrimSingleLine(context ?? string.Empty, 180),
            ResponsePreview = TrimSingleLine($"{responsePreview ?? string.Empty} {(string.IsNullOrWhiteSpace(source) ? string.Empty : $"[{source}]")}".Trim(), 220)
        });
        operations.LlmInteractions = operations.LlmInteractions
            .OrderByDescending(item => item.AtUtc)
            .Take(80)
            .ToList();
        operations.RuntimeStatus ??= new RuntimeStatus();
        operations.RuntimeStatus.LlmEnabled = _config.Llm.Enabled;
        operations.RuntimeStatus.LlmProvider = _config.Llm.Provider;
        operations.RuntimeStatus.LastLlmInteractionAtUtc = operations.LlmInteractions.FirstOrDefault()?.AtUtc;
        operations.RuntimeStatus.UpdatedAtUtc = DateTime.UtcNow;
        _neoCortex.SaveOperations(operations);
    }

    private async Task<ObservationAnalysis> AnalyzeObservationWithLlmAsync(
        string source,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken)
    {
        var fallbackSummary = $"{source} reported operational signals in recent logs.";
        if (_kernel is null || !_config.Llm.Enabled || !_config.Llm.UseForRecommendations)
        {
            return new ObservationAnalysis(
                fallbackSummary,
                "connector_observation",
                "log_signal_detected",
                "Review the latest connector events and confirm whether operator action is required.",
                false,
                false,
                _kernel is null ? "template_no_kernel" : "template_llm_disabled");
        }

        var prompt = $$"""
You are analyzing operational logs from MSP tools.
Return strict JSON only with keys:
summary, classification, missingCapability, recurrencePrevention

Constraints:
- classification: short snake_case (2-5 words)
- missingCapability: short snake_case (2-5 words)
- summary: one sentence
- recurrencePrevention: one sentence with concrete operator action
- do not invent data

Source: {{source}}
RecentSignals:
{{string.Join("\n", lines.Take(12))}}
""";

        try
        {
            var response = await _kernel.InvokePromptAsync(prompt, cancellationToken: cancellationToken);
            var raw = response.GetValue<string>() ?? string.Empty;
            var json = TryExtractJson(raw);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new ObservationAnalysis(
                    fallbackSummary,
                    "connector_observation",
                    "log_signal_detected",
                    "Review the latest connector events and confirm whether operator action is required.",
                    true,
                    false,
                    "llm_parse_failure");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var summary = root.TryGetProperty("summary", out var summaryNode) ? summaryNode.GetString() : null;
            var classification = root.TryGetProperty("classification", out var classNode) ? classNode.GetString() : null;
            var missing = root.TryGetProperty("missingCapability", out var missingNode) ? missingNode.GetString() : null;
            var prevention = root.TryGetProperty("recurrencePrevention", out var preventionNode) ? preventionNode.GetString() : null;

            return new ObservationAnalysis(
                string.IsNullOrWhiteSpace(summary) ? fallbackSummary : summary!,
                SanitizeToken(classification, "connector_observation"),
                SanitizeToken(missing, "log_signal_detected"),
                string.IsNullOrWhiteSpace(prevention) ? "Review the latest connector events and confirm whether operator action is required." : prevention!,
                true,
                true,
                "llm");
        }
        catch (Exception ex)
        {
            return new ObservationAnalysis(
                fallbackSummary,
                "connector_observation",
                "log_signal_detected",
                "Review the latest connector events and confirm whether operator action is required.",
                true,
                false,
                $"llm_error:{ex.GetType().Name}");
        }
    }

    private static string? TryExtractJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return raw[start..(end + 1)];
    }

    private static string SanitizeToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var token = new string(value.Trim().ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-').ToArray());
        return string.IsNullOrWhiteSpace(token) ? fallback : token;
    }

    private sealed record ObservationAnalysis(
        string Summary,
        string Classification,
        string MissingCapability,
        string RecurrencePrevention,
        bool LlmAttempted,
        bool LlmSucceeded,
        string Source);

    private async Task TryProposeIncidentBranchAsync(EvolutionIncidentRecord incident, CancellationToken cancellationToken)
    {
        try
        {
            var slug = $"incident-{incident.Classification}";
            var branch = await _gitOps.EnsureAgentBranchAsync(slug, cancellationToken);
            var title = $"[agent] Incident: {incident.Classification}";
            var body = $"Request: {incident.Request}\nFailure: {incident.FailureReason}\nMissing: {incident.MissingCapability}\nPrevention: {incident.RecurrencePrevention}";
            await _gitOps.CommitAsync($"incident: record {incident.Id}", cancellationToken);
            await _gitOps.PushAsync(branch, cancellationToken);
            await _gitOps.CreatePrAsync(branch, title, body, cancellationToken);
        }
        catch (Exception ex)
        {
            _legacyState.RecordAgentError($"GitOps incident PR failed: {ex.Message}");
            RustOpsSentry.CaptureException(
                ex,
                "GitOps incident PR creation failed.",
                "agent.gitops",
                extras: new Dictionary<string, object?>
                {
                    ["incidentId"] = incident.Id,
                    ["classification"] = incident.Classification,
                    ["request"] = incident.Request
                });
        }
    }

    private IEnumerable<string> EnumerateInboxFiles(string path)
    {
        return Directory
            .GetFiles(path, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void UpdateSelectionState(ConversationSelectionState state, AdminIntentRoute route, ToolExecutionResult result)
    {
        state.LastIntent = route.Intent.ToString();
        state.LastServerName = result.SelectedServer ?? route.Slots.ServerName ?? state.LastServerName;
        state.LastCommandText = route.Slots.CommandText ?? state.LastCommandText;
        state.LastTimeRange = route.Slots.TimeRange ?? state.LastTimeRange;
        state.UpdatedAtUtc = DateTime.UtcNow;
    }

    private void WriteOutbox(string adminId, string message, string? actionId, string? serverName)
    {
        var payload = new AdapterMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            AdminId = adminId,
            Kind = "chat-reply",
            Audience = "admins",
            TargetAdminId = adminId,
            ServerName = serverName ?? string.Empty,
            ActionId = actionId,
            Message = message,
            CreatedAtUtc = DateTime.UtcNow
        };

        Directory.CreateDirectory(_config.Outbox.MessageOutboxPath);
        var path = Path.Combine(_config.Outbox.MessageOutboxPath, $"{payload.CreatedAtUtc:yyyyMMddHHmmssfff}-chat-reply-{payload.Id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonDefaults.Default));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            RustOpsSentry.CaptureException(
                ex,
                "Failed to delete processed inbox/outbox file.",
                "agent.files",
                extras: new Dictionary<string, object?> { ["path"] = path });
        }
    }
}
