using Microsoft.SemanticKernel;
using System.Text.Json;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Core.Interaction;
using RustOpsAgent.Domains.Rust;
using RustOpsAgent.Infrastructure;
using RustOpsAgent.Infrastructure.GitOps;
using RustOpsAgent.Infrastructure.Memory;
using AutoPullService = RustOpsAgent.Infrastructure.AutoPullService;

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
    private readonly AutoPullService _autoPull;
    private readonly RustOpsApiClient _api;
    private readonly Kernel? _kernel;
    private readonly Dictionary<string, long> _logOffsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _observationFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _adminLocks = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastObservationAtUtc = DateTime.MinValue;
    private DateTime _lastIncidentReviewAtUtc = DateTime.MinValue;
    private volatile bool _stop;

    public AgentRuntime(
        AgentConfig config,
        IIntentClassifier classifier,
        IActionExecutor executor,
        IResponseComposer composer,
        NeoCortexStore neoCortex,
        LegacyAgentStateStore legacyState,
        IGitOpsService gitOps,
        AutoPullService autoPull,
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
        _autoPull = autoPull;
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

            // Feedback and decision are independent — run them concurrently.
            await Task.WhenAll(
                ProcessFeedbackInboxAsync(cancellationToken),
                ProcessDecisionInboxAsync(cancellationToken));

            await _autoPull.TickAsync(cancellationToken);

            var chatFiles = Directory.Exists(_config.Inbox.ChatInboxPath)
                ? Directory.GetFiles(_config.Inbox.ChatInboxPath, "*.json").Length
                : 0;
            if (chatFiles > 0 || tick % 5 == 0)
                Console.WriteLine($"[agent] Tick {tick}: chat-inbox={chatFiles} file(s)");

            await ProcessChatInboxAsync(cancellationToken);
            await ObserveServersAsync(cancellationToken);
            await ReviewIncidentsAsync(cancellationToken);
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
            return;

        var files = EnumerateInboxFiles(_config.Inbox.ChatInboxPath).ToArray();
        if (files.Length == 0) return;

        // Group messages by adminId so each admin's messages stay ordered,
        // but different admins are processed concurrently.
        var groups = files
            .Select(f =>
            {
                try
                {
                    var item = JsonSerializer.Deserialize<ChatInboxItem>(File.ReadAllText(f), JsonDefaults.Default);
                    return (file: f, item);
                }
                catch { return (file: f, item: (ChatInboxItem?)null); }
            })
            .GroupBy(x => x.item?.AdminId ?? "__invalid__", StringComparer.OrdinalIgnoreCase)
            .ToList();

        await Task.WhenAll(groups.Select(group => ProcessAdminChatGroupAsync(group, cancellationToken)));
    }

    private async Task ProcessAdminChatGroupAsync(
        IEnumerable<(string file, ChatInboxItem? item)> group,
        CancellationToken cancellationToken)
    {
        var adminId = group.First().item?.AdminId ?? "__invalid__";
        var adminLock = _adminLocks.GetOrAdd(adminId, _ => new SemaphoreSlim(1, 1));
        await adminLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var (file, item) in group)
            {
                if (_stop || cancellationToken.IsCancellationRequested) return;
                await ProcessSingleChatMessageAsync(file, item, cancellationToken);
            }
        }
        finally
        {
            adminLock.Release();
        }
    }

    private async Task ProcessSingleChatMessageAsync(string file, ChatInboxItem? item, CancellationToken cancellationToken)
    {
        try
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Message))
                return;

            // Detect inline importance rule directives before routing.
            var quickReply = TryHandleLearnDirective(item.Message);
            if (quickReply is not null)
            {
                WriteOutbox(item.AdminId, quickReply, item.RequestId ?? item.Id, null);
                return;
            }

            var actionId = string.IsNullOrWhiteSpace(item.RequestId) ? item.Id : item.RequestId!;
            var selection = _neoCortex.LoadSelection();
            var state = selection.Conversations.FirstOrDefault(c =>
                string.Equals(c.AdminId, item.AdminId, StringComparison.OrdinalIgnoreCase));
            if (state is null)
            {
                state = new ConversationSelectionState { AdminId = item.AdminId };
                selection.Conversations.Add(state);
            }

            var knownServers = await RustToolHelper.GetKnownServersAsync(_api, cancellationToken);
            var route = await _classifier.ClassifyAsync(item.Message, state, knownServers, cancellationToken);

            // If there's an outstanding clarification and the classifier didn't find a new intent,
            // re-route to the handler that originally asked the question so it can process the answer.
            var pending = state.PendingClarification;
            if (pending is not null &&
                (route.Intent == AdminIntentType.Clarification || route.Intent == AdminIntentType.Chat) &&
                Enum.TryParse<AdminIntentType>(pending.Intent, true, out var pendingIntent) &&
                pendingIntent != AdminIntentType.Clarification)
            {
                var serverName = route.Slots.ServerName ?? state.LastServerName;
                var overriddenSlots = route.Slots with { ServerName = serverName };
                route = route with
                {
                    Intent = pendingIntent,
                    Slots = overriddenSlots,
                    TargetRef = route.TargetRef ?? pending.TargetRef,
                    ClassifierSource = route.ClassifierSource + "+pending-clarification-override"
                };
                Console.WriteLine($"[chat] Overriding intent to {pendingIntent} (pending clarification answer)");
            }

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

            UpdateSelectionState(state, item.Message, route, result);
            RecordConversationTurn(state, item.Message, reply);
            _neoCortex.SaveSelection(selection);
            var primaryServer = ResolvePrimaryServer(result, route);

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
                ServerName = primaryServer,
                TimestampUtc = DateTime.UtcNow
            });
            operations.RecentActions = operations.RecentActions.TakeLast(100).ToList();
            _neoCortex.SaveOperations(operations);

            _legacyState.RecordAction(
                actionId,
                route.Intent.ToString().ToLowerInvariant(),
                result.Success,
                result.Message,
                primaryServer,
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
                _legacyState.RecordIncident(primaryServer, result.ErrorCode ?? "execution_failure", result.Message);

                if (_config.GitOps.Enabled && _config.GitOps.AllowPush)
                    _ = TryProposeIncidentBranchAsync(incident, cancellationToken);
            }

            WriteOutbox(item.AdminId, reply, actionId, primaryServer);
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
                    ["message"] = item?.Message
                });
            WriteOutbox(item?.AdminId ?? "admin",
                $"Something went wrong processing your request. Check the logs.",
                item?.RequestId ?? item?.Id, null);
        }
        finally
        {
            TryDelete(file);
        }
    }

    private string? TryHandleLearnDirective(string message)
    {
        var lowered = message.Trim().ToLowerInvariant();

        // "importance +pattern" — add importance rule
        if (lowered.StartsWith("importance +", StringComparison.Ordinal))
        {
            var pattern = message["importance +".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                var logs = _neoCortex.LoadLogs();
                if (!logs.ImportanceRules.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                {
                    logs.ImportanceRules.Add(pattern);
                    _neoCortex.SaveLogs(logs);
                }
                return $"Got it — I'll flag any log line matching \"{pattern}\" as high-importance from now on.";
            }
        }

        // "importance -pattern" — remove importance rule
        if (lowered.StartsWith("importance -", StringComparison.Ordinal))
        {
            var pattern = message["importance -".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                var logs = _neoCortex.LoadLogs();
                var removed = logs.ImportanceRules.RemoveAll(r =>
                    r.Equals(pattern, StringComparison.OrdinalIgnoreCase));
                _neoCortex.SaveLogs(logs);
                return removed > 0
                    ? $"Removed \"{pattern}\" from importance rules."
                    : $"No importance rule matched \"{pattern}\".";
            }
        }

        // "importance list" — show current rules
        if (lowered is "importance list" or "importance rules" or "list importance rules")
        {
            var logs = _neoCortex.LoadLogs();
            if (logs.ImportanceRules.Count == 0)
                return "No custom importance rules set. Defaults: exception/failed/error = high, warn/disconnect = medium.";
            return $"Custom importance rules: {string.Join(", ", logs.ImportanceRules.Select(r => $"\"{r}\""))}.";
        }

        return null;
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

    private async Task ReviewIncidentsAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(5, _config.Monitor.IncidentReviewIntervalMinutes));
        if (DateTime.UtcNow - _lastIncidentReviewAtUtc < interval)
            return;

        _lastIncidentReviewAtUtc = DateTime.UtcNow;

        EvolutionReviewResult review;
        try
        {
            review = await _neoCortex.ReviewAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[review] Failed to load incidents: {ex.Message}");
            return;
        }

        if (review.OpenIncidents.Count == 0)
        {
            Console.WriteLine("[review] No open incidents.");
            return;
        }

        Console.WriteLine($"[review] {review.OpenIncidents.Count} open incident(s) — analysing with LLM.");

        if (_kernel is null || !_config.Llm.Enabled || !_config.Llm.UseForRecommendations)
        {
            Console.WriteLine("[review] LLM disabled — skipping trend analysis.");
            return;
        }

        var top = review.OpenIncidents.Take(10).ToList();
        var incidentSummary = string.Join("\n", top.Select((i, idx) =>
            $"{idx + 1}. [{i.Classification}] {i.Request} → {i.FailureReason}"));

        var prompt = $$"""
You are reviewing recurring operational failures for a Rust server agent.
Identify patterns, propose concrete mitigations, and list any configuration changes.

Return strict JSON only with keys:
trendSummary, topPattern, proposedMitigation, configSuggestion

Constraints:
- trendSummary: 1-2 sentences
- topPattern: short snake_case label
- proposedMitigation: one concrete sentence
- configSuggestion: one sentence or null

Open incidents (newest first):
{{incidentSummary}}
""";

        try
        {
            var response = await _kernel.InvokePromptAsync(prompt, cancellationToken: cancellationToken);
            var raw = response.GetValue<string>() ?? string.Empty;
            var json = TryExtractJson(raw);
            if (json is null)
            {
                Console.WriteLine("[review] LLM returned unparseable trend analysis.");
                return;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var summary = root.TryGetProperty("trendSummary", out var s) ? s.GetString() : null;
            var pattern = root.TryGetProperty("topPattern", out var p) ? p.GetString() : null;
            var mitigation = root.TryGetProperty("proposedMitigation", out var m) ? m.GetString() : null;
            var config = root.TryGetProperty("configSuggestion", out var c) ? c.GetString() : null;

            Console.WriteLine($"[review] Trend: {summary}");
            Console.WriteLine($"[review] Pattern: {pattern} | Mitigation: {mitigation}");

            RecordLlmInteraction(
                "incident-review",
                true, true,
                $"{top.Count} open incidents",
                summary,
                "llm");

            // Propose a GitOps branch with the review findings when push is enabled.
            if (_config.GitOps.Enabled && _config.GitOps.AllowPush && !string.IsNullOrWhiteSpace(pattern))
            {
                _ = TryProposeReviewBranchAsync(pattern!, summary, mitigation, config, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[review] LLM trend analysis failed: {ex.Message}");
            RustOpsSentry.CaptureException(ex, "Incident review LLM analysis failed.", "agent.review");
        }
    }

    private async Task TryProposeReviewBranchAsync(
        string pattern, string? summary, string? mitigation, string? configSuggestion,
        CancellationToken cancellationToken)
    {
        try
        {
            var slug = $"review-{SanitizeToken(pattern, "incident")}";
            var branch = await _gitOps.EnsureAgentBranchAsync(slug, cancellationToken);

            var reviewNote = $"""
# Incident Trend Review — {DateTime.UtcNow:yyyy-MM-dd}

## Summary
{summary ?? "See open incidents."}

## Top Pattern
{pattern}

## Proposed Mitigation
{mitigation ?? "Review handler coverage."}

## Config Suggestion
{configSuggestion ?? "None identified."}
""";
            var reviewPath = Path.Combine(_config.GitOps.RepoPath, "agent-reviews", $"{DateTime.UtcNow:yyyyMMdd}-{slug}.md");
            Directory.CreateDirectory(Path.GetDirectoryName(reviewPath)!);
            await File.WriteAllTextAsync(reviewPath, reviewNote, cancellationToken);

            await _gitOps.CommitAsync($"agent: incident review {DateTime.UtcNow:yyyyMMdd} pattern={pattern}", cancellationToken);
            await _gitOps.PushAsync(branch, cancellationToken);
            await _gitOps.CreatePrAsync(branch, $"[agent] Incident review: {pattern}", reviewNote, cancellationToken);
            Console.WriteLine($"[review] PR proposed for pattern '{pattern}' on branch {branch}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[review] GitOps PR for review failed: {ex.Message}");
            RustOpsSentry.CaptureException(ex, "Review GitOps PR failed.", "agent.review");
        }
    }

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

    private static void RecordConversationTurn(ConversationSelectionState state, string userMessage, string agentReply)
    {
        state.RecentMessages.Add(new ConversationMessage { Role = "user", Text = userMessage, AtUtc = DateTime.UtcNow });
        state.RecentMessages.Add(new ConversationMessage { Role = "assistant", Text = agentReply, AtUtc = DateTime.UtcNow });
        if (state.RecentMessages.Count > 12)
            state.RecentMessages = state.RecentMessages.TakeLast(12).ToList();
    }

    private void UpdateSelectionState(ConversationSelectionState state, string message, AdminIntentRoute route, ToolExecutionResult result)
    {
        state.LastIntent = route.Intent.ToString();
        var resolvedServers = (result.SelectedServers ?? route.Slots.ServerNames ?? Array.Empty<string>())
            .Where(server => !string.IsNullOrWhiteSpace(server))
            .Select(server => server.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var primaryServer = ResolvePrimaryServer(result, route);
        if (resolvedServers.Count == 0 && !string.IsNullOrWhiteSpace(primaryServer))
        {
            resolvedServers.Add(primaryServer!);
        }

        if (resolvedServers.Count > 0)
        {
            state.LastResolvedServers = resolvedServers;
            state.LastServerName = resolvedServers[0];
            state.LastScopeKind = result.ScopeKind != ServerScopeKind.Unspecified
                ? result.ScopeKind
                : route.Slots.ScopeKind != ServerScopeKind.Unspecified
                    ? route.Slots.ScopeKind
                    : resolvedServers.Count == 1
                        ? ServerScopeKind.Single
                        : ServerScopeKind.Subset;
        }

        state.LastCommandText = route.Slots.CommandText ?? state.LastCommandText;
        state.LastTimeRange = route.Slots.TimeRange ?? state.LastTimeRange;
        state.LastUserMessageSummary = TrimSingleLine(message, 180);
        if (string.Equals(result.ErrorCode, "clarification_required", StringComparison.OrdinalIgnoreCase))
        {
            state.PendingClarification = new ConversationPendingClarification
            {
                Intent = route.Intent.ToString(),
                TargetRef = route.TargetRef,
                Question = result.Message,
                ScopeKind = route.Slots.ScopeKind,
                AskedAtUtc = DateTime.UtcNow
            };
        }
        else if (route.Intent != AdminIntentType.Clarification)
        {
            state.PendingClarification = null;
        }

        state.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static string? ResolvePrimaryServer(ToolExecutionResult result, AdminIntentRoute route)
    {
        if (!string.IsNullOrWhiteSpace(result.SelectedServer))
        {
            return result.SelectedServer;
        }

        if (result.SelectedServers is { Count: > 0 })
        {
            return result.SelectedServers[0];
        }

        if (!string.IsNullOrWhiteSpace(route.Slots.ServerName))
        {
            return route.Slots.ServerName;
        }

        if (route.Slots.ServerNames is { Count: > 0 })
        {
            return route.Slots.ServerNames[0];
        }

        return null;
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
