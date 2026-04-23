using Microsoft.SemanticKernel;
using System.Text.Json;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Core.Interaction;
using RustOpsAgent.Domains.Rust;
using RustOpsAgent.Infrastructure;
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
    private readonly RustOpsApiClient _api;
    private readonly Kernel? _kernel;
    private readonly Dictionary<string, long> _logOffsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _observationFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastObservationAtUtc = DateTime.MinValue;
    private volatile bool _stop;

    public AgentRuntime(
        AgentConfig config,
        IIntentClassifier classifier,
        IActionExecutor executor,
        IResponseComposer composer,
        NeoCortexStore neoCortex,
        LegacyAgentStateStore legacyState,
        IGitOpsService gitOps,
        RustOpsApiClient api,
        Kernel? kernel)
    {
        _config = config;
        _classifier = classifier;
        _executor = executor;
        _composer = composer;
        _neoCortex = neoCortex;
        _legacyState = legacyState;
        _gitOps = gitOps;
        _api = api;
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

            var chatFiles = Directory.Exists(_config.Inbox.ChatInboxPath)
                ? Directory.GetFiles(_config.Inbox.ChatInboxPath, "*.json").Length
                : 0;
            if (chatFiles > 0 || tick % 5 == 0)
                Console.WriteLine($"[agent] Tick {tick}: chat-inbox={chatFiles} file(s)");

            await ProcessChatInboxAsync(cancellationToken);
            await ObserveServersAsync(cancellationToken);
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
                return;
            try
            {
                var payload = JsonSerializer.Deserialize<FeedbackInboxItem>(await File.ReadAllTextAsync(file, cancellationToken), JsonDefaults.Default);
                if (payload is null)
                {
                    continue;
                }

                _legacyState.RecordFeedback(payload.AdminId, payload.ActionId, payload.Verdict, payload.Note, payload.ServerName);

                // Allow admins to teach the log filter using partial-match directives.
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
                return;
            try
            {
                var payload = JsonSerializer.Deserialize<DecisionInboxItem>(await File.ReadAllTextAsync(file, cancellationToken), JsonDefaults.Default);
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

    private async Task ProcessChatInboxAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_config.Inbox.ChatInboxPath))
        {
            return;
        }

        foreach (var file in EnumerateInboxFiles(_config.Inbox.ChatInboxPath))
        {
            if (_stop || cancellationToken.IsCancellationRequested)
                return;

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
                var state = selection.Conversations.FirstOrDefault(c => string.Equals(c.AdminId, item.AdminId, StringComparison.OrdinalIgnoreCase));
                if (state is null)
                {
                    state = new ConversationSelectionState { AdminId = item.AdminId };
                    selection.Conversations.Add(state);
                }

                var route = await _classifier.ClassifyAsync(item.Message, state, cancellationToken);
                Console.WriteLine($"[chat] {item.AdminId}: intent={route.Intent} target={route.TargetRef ?? "?"} llm={route.ClassifierSource}");
                RecordIntentRoutingInteraction(item.Message, route);
                var context = new ToolExecutionContext(item.AdminId, item.Message, route, state, DateTime.UtcNow);
                var result = await _executor.ExecuteAsync(context, cancellationToken);
                var composedReply = await _composer.ComposeAsync(context, result, cancellationToken);
                Console.WriteLine($"[chat] compose source={composedReply.Source} llm_ok={composedReply.LlmSucceeded}");
                RecordLlmInteraction(
                    composedReply.Type,
                    composedReply.LlmAttempted,
                    composedReply.LlmSucceeded,
                    item.Message,
                    composedReply.ResponsePreview ?? composedReply.Message,
                    composedReply.Source);
                var reply = composedReply.Message;

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
                    _legacyState.RecordIncident(result.SelectedServer ?? route.Slots.ServerName, result.ErrorCode ?? "execution_failure", result.Message);

                    if (_config.GitOps.Enabled && _config.GitOps.AllowPush)
                    {
                        _ = TryProposeIncidentBranchAsync(incident, cancellationToken);
                    }
                }

                WriteOutbox(item.AdminId, reply, actionId, result.SelectedServer ?? route.Slots.ServerName);
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

    private async Task ObserveServersAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _config.Monitor.PollSeconds));
        if (DateTime.UtcNow - _lastObservationAtUtc < interval)
        {
            return;
        }

        _lastObservationAtUtc = DateTime.UtcNow;

        List<string> servers;
        try
        {
            using var list = await _api.GetAsync("/servers", cancellationToken);
            servers = list.RootElement.ValueKind == JsonValueKind.Array
                ? list.RootElement.EnumerateArray()
                    .Where(node => node.ValueKind == JsonValueKind.String)
                    .Select(node => node.GetString())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
        }
        catch (Exception ex)
        {
            _legacyState.RecordAgentError($"server observation failed: {ex.Message}");
            Console.WriteLine($"[observe] Failed to list servers: {ex.Message}");
            return;
        }

        Console.WriteLine($"[observe] Scanning {servers.Count} server(s)...");

        foreach (var server in servers)
        {
            if (_stop || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await ObserveServerHealthAsync(server, cancellationToken);
                await ObserveServerLogsAsync(server, cancellationToken);
            }
            catch (Exception ex)
            {
                _legacyState.RecordAgentError($"observation failed for {server}: {ex.Message}");
                Console.WriteLine($"[observe] Error observing {server}: {ex.Message}");
            }
        }
    }

    private async Task ObserveServerHealthAsync(string server, CancellationToken cancellationToken)
    {
        using var health = await _api.GetAsync($"/servers/{Uri.EscapeDataString(server)}/health", cancellationToken);
        var root = health.RootElement;
        var state = ReadHealthState(root);
        var recentErrors = root.TryGetProperty("recentErrors", out var errorsNode) && errorsNode.ValueKind == JsonValueKind.Array
            ? errorsNode.EnumerateArray().Select(item => item.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).Take(3).ToList()
            : new List<string>();

        if (recentErrors.Count == 0)
        {
            Console.WriteLine($"[observe] {server}: {state}, no recent errors.");
            return;
        }

        Console.WriteLine($"[observe] {server}: {state}, {recentErrors.Count} error(s) — queuing LLM analysis.");
        var fingerprint = $"{state}|{string.Join("|", recentErrors)}";
        if (_observationFingerprints.TryGetValue($"{server}:health", out var previous) &&
            string.Equals(previous, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _observationFingerprints[$"{server}:health"] = fingerprint;
        var analysis = await AnalyzeObservationWithLlmAsync(server, state, recentErrors, cancellationToken);
        Console.WriteLine($"[observe] {server}: LLM analysis source={analysis.Source} summary={analysis.Summary}");
        RecordLlmInteraction(
            "observation-analysis",
            analysis.LlmAttempted,
            analysis.LlmSucceeded,
            $"{server} health observation",
            analysis.Summary,
            analysis.Source);
        var summary = analysis.Summary;
        _legacyState.RecordIncident(server, "health_observation", summary);
        await _neoCortex.RecordIncidentAsync(new EvolutionIncidentRecord
        {
            Request = $"observe health {server}",
            IntendedOutcome = "continuous_observation",
            FailureReason = summary,
            MissingCapability = analysis.MissingCapability,
            RecurrencePrevention = analysis.RecurrencePrevention,
            Classification = analysis.Classification,
            Timestamp = DateTime.UtcNow,
            Resolved = false
        }, cancellationToken);
    }

    private async Task ObserveServerLogsAsync(string server, CancellationToken cancellationToken)
    {
        _logOffsets.TryGetValue(server, out var offset);
        var path = $"/servers/{Uri.EscapeDataString(server)}/logs/read?offset={offset}&maxBytes=65536";
        using var logs = await _api.GetAsync(path, cancellationToken);
        var root = logs.RootElement;

        if (root.TryGetProperty("endOffset", out var endOffsetNode) && endOffsetNode.ValueKind == JsonValueKind.Number)
        {
            _logOffsets[server] = endOffsetNode.GetInt64();
        }

        var knowledge = _neoCortex.LoadLogs();
        var changed = false;
        if (root.TryGetProperty("entries", out var entriesNode) && entriesNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entriesNode.EnumerateArray())
            {
                var line = entry.TryGetProperty("message", out var messageNode) ? messageNode.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var lowered = line.ToLowerInvariant();
                if (knowledge.IgnorePatterns.Any(pattern => lowered.Contains(pattern.ToLowerInvariant(), StringComparison.Ordinal)))
                {
                    continue;
                }

                var importance = ScoreImportance(lowered, knowledge.ImportanceRules);
                knowledge.RecentEntries.Add(new LogObservation
                {
                    ServerName = server,
                    Line = line,
                    Importance = importance,
                    CapturedAtUtc = DateTime.UtcNow
                });
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        var newHighImportance = knowledge.RecentEntries
            .Where(e => e.ServerName.Equals(server, StringComparison.OrdinalIgnoreCase) && e.Importance >= 3)
            .TakeLast(3)
            .Select(e => e.Line)
            .ToList();
        if (newHighImportance.Count > 0)
        {
            Console.WriteLine($"[observe] {server}: {newHighImportance.Count} high-importance log line(s): {string.Join(" | ", newHighImportance.Select(l => l.Length > 80 ? l[..80] + "..." : l))}");
        }

        knowledge.RecentEntries = knowledge.RecentEntries.TakeLast(400).ToList();
        _neoCortex.SaveLogs(knowledge);
    }

    private static string ReadHealthState(JsonElement root)
    {
        if (root.TryGetProperty("status", out var statusNode) &&
            statusNode.ValueKind == JsonValueKind.Object &&
            statusNode.TryGetProperty("state", out var nestedState))
        {
            return nestedState.GetString() ?? "unknown";
        }

        return root.TryGetProperty("state", out var stateNode)
            ? stateNode.GetString() ?? "unknown"
            : "unknown";
    }

    private static int ScoreImportance(string line, IEnumerable<string> dynamicRules)
    {
        if (line.Contains("exception") || line.Contains("failed") || line.Contains("error"))
        {
            return 3;
        }

        if (line.Contains("warn") || line.Contains("disconnect"))
        {
            return 2;
        }

        return dynamicRules.Any(rule => line.Contains(rule, StringComparison.OrdinalIgnoreCase)) ? 2 : 1;
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
        string server,
        string state,
        IReadOnlyList<string> recentErrors,
        CancellationToken cancellationToken)
    {
        var fallbackSummary = $"{server} is {state}. Recent errors: {string.Join(" | ", recentErrors)}";
        if (_kernel is null || !_config.Llm.Enabled || !_config.Llm.UseForRecommendations)
        {
            return new ObservationAnalysis(
                fallbackSummary,
                "health_observation",
                "health_issue_detected",
                "Review recent errors and server health details.",
                false,
                false,
                _kernel is null ? "template_no_kernel" : "template_llm_disabled");
        }

        var prompt = $$"""
You are analyzing Rust server health events for recurring operational issues.
Return strict JSON only with keys:
summary, classification, missingCapability, recurrencePrevention

Constraints:
- classification: short snake_case (2-5 words)
- missingCapability: short snake_case (2-5 words)
- summary: one sentence
- recurrencePrevention: one sentence with concrete operator action
- do not invent data

Server: {{server}}
State: {{state}}
RecentErrors:
{{string.Join("\n", recentErrors)}}
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
                    "health_observation",
                    "health_issue_detected",
                    "Review recent errors and server health details.",
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
                SanitizeToken(classification, "health_observation"),
                SanitizeToken(missing, "health_issue_detected"),
                string.IsNullOrWhiteSpace(prevention) ? "Review recent errors and server health details." : prevention!,
                true,
                true,
                "llm");
        }
        catch (Exception ex)
        {
            return new ObservationAnalysis(
                fallbackSummary,
                "health_observation",
                "health_issue_detected",
                "Review recent errors and server health details.",
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
        if (string.IsNullOrWhiteSpace(token))
        {
            return fallback;
        }

        return token;
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
        try { File.Delete(path); }
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
