using Microsoft.SemanticKernel;
using Sentry;
using System.Text;
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
    private readonly Kernel? _deepKernel;
    private readonly Dictionary<string, long> _logOffsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _observationFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _appliedAffinityPids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _compileErrorCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _serverInitialized = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _remoteServers = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _adminLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _staticIgnorePatterns;
    private readonly HashSet<string> _incidentPatterns;
    private readonly HashSet<string> _startupIgnorePatterns;
    private string _lastDigestDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
    private DateTime _alertMutedUntilUtc = DateTime.MinValue;
    private DateTime _lastObservationAtUtc = DateTime.MinValue;
    private DateTime _lastIncidentReviewAtUtc = DateTime.MinValue;
    private DateTime _lastSentimentAnalysisAtUtc = DateTime.MinValue;
    private DateTime _lastClassifierEvolutionAtUtc = DateTime.MinValue;
    private DateTime _deepLlmUnauthorizedMutedUntilUtc = DateTime.MinValue;
    private DateTime _lastDeepLlmUnauthorizedNoticeUtc = DateTime.MinValue;
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
        _deepKernel = kernel;
        (_staticIgnorePatterns, _incidentPatterns, _startupIgnorePatterns) = LoadStaticLogRules(config.Monitor.LogRulesPath);
    }

    public void RequestStop() => _stop = true;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[agent] Runtime started.");
        var tick = 0;
        while (!_stop && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                _legacyState.UpdateRuntimeStatus(_config.Llm);

                // Feedback and decision are independent — run them concurrently.
                await SafeExecuteAsync(() => Task.WhenAll(
                    ProcessFeedbackInboxAsync(cancellationToken),
                    ProcessDecisionInboxAsync(cancellationToken)), "inbox-processing", cancellationToken);

                await SafeExecuteAsync(() => _autoPull.TickAsync(cancellationToken), "auto-pull", cancellationToken);

                var chatFiles = Directory.Exists(_config.Inbox.ChatInboxPath)
                    ? Directory.GetFiles(_config.Inbox.ChatInboxPath, "*.json").Length
                    : 0;
                if (chatFiles > 0 || tick % 5 == 0)
                    Console.WriteLine($"[agent] Tick {tick}: chat-inbox={chatFiles} file(s)");

                await SafeExecuteAsync(() => ProcessChatInboxAsync(cancellationToken), "chat-processing", cancellationToken);
                await SafeExecuteAsync(() => ObserveServersAsync(cancellationToken), "server-observation", cancellationToken);
                await SafeExecuteAsync(() => ReviewIncidentsAsync(cancellationToken), "incident-review", cancellationToken);
                await SafeExecuteAsync(() => AnalyzePlayerSentimentAsync(cancellationToken), "sentiment-analysis", cancellationToken);
                await SafeExecuteAsync(() => EvolveClassifierAsync(cancellationToken), "classifier-evolution", cancellationToken);

                _legacyState.Save();
                tick++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[agent] Unexpected error in main loop tick {tick}: {ex.Message}");
                RustOpsSentry.CaptureException(ex, "Agent main loop exception.", "agent.runtime",
                    extras: new Dictionary<string, object?> { ["tick"] = tick });
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _config.Monitor.PollSeconds)), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
        Console.WriteLine("[agent] Runtime stopped.");
    }

    private async Task SafeExecuteAsync(Func<Task> taskFactory, string taskName, CancellationToken cancellationToken)
    {
        try
        {
            await taskFactory();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[agent] Task '{taskName}' failed: {ex.Message}");
            RustOpsSentry.CaptureException(ex, $"Agent task failed: {taskName}", "agent.task",
                extras: new Dictionary<string, object?> { ["taskName"] = taskName });
        }
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

                var note = payload.Note ?? payload.Preference;

                // Allow admins to teach the log filter using partial-match directives.
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

                // Record bad-verdict feedback as a misclassification candidate for classifier evolution.
                var isBadVerdict = string.Equals(payload.Verdict, "bad", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(payload.Verdict, "note", StringComparison.OrdinalIgnoreCase);
                if (isBadVerdict && !string.IsNullOrWhiteSpace(note))
                {
                    var detectedIntent = TryFindRecentDetectedIntent();
                    var knowledge = _neoCortex.LoadClassifierKnowledge();
                    knowledge.PendingMisclassifications.Add(new MisclassificationRecord
                    {
                        AdminId = payload.AdminId,
                        FeedbackNote = note,
                        DetectedIntent = detectedIntent,
                        CapturedAtUtc = DateTime.UtcNow
                    });
                    knowledge.PendingMisclassifications = knowledge.PendingMisclassifications.TakeLast(50).ToList();
                    _neoCortex.SaveClassifierKnowledge(knowledge);
                    Console.WriteLine($"[evolution] Misclassification queued: \"{note}\" (detected={detectedIntent})");
                }

                var ackMessage = isBadVerdict && !string.IsNullOrWhiteSpace(note)
                    ? "Got it. I've noted the correction and will refine my intent understanding during the next learning cycle."
                    : "Feedback received and applied.";

                if (!string.IsNullOrWhiteSpace(payload.AdminId))
                {
                    WriteOutbox(payload.AdminId!, ackMessage, payload.ActionId, payload.ServerName);
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

            // Detect inline learn directives and agent self-control before routing.
            var quickReply = TryHandleLearnDirective(item.Message)
                ?? await TryHandleAgentControlAsync(item.Message, cancellationToken);
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
            // BUT: skip this if the message contains explicit intent signals (pull, git, rebuild, etc.)
            // indicating a new request, not a clarification answer.
            var pending = state.PendingClarification;
            var messageLowered = item.Message.ToLowerInvariant();
            var hasExplicitNewIntent = messageLowered.Contains("pull", StringComparison.Ordinal) ||
                                       messageLowered.Contains("git", StringComparison.Ordinal) ||
                                       messageLowered.Contains("rebuild", StringComparison.Ordinal) ||
                                       messageLowered.Contains("restart", StringComparison.Ordinal) ||
                                       messageLowered.Contains("start ", StringComparison.Ordinal) ||
                                       messageLowered.Contains("stop", StringComparison.Ordinal) ||
                                       messageLowered.Contains("kill", StringComparison.Ordinal) ||
                                       messageLowered.Contains("update", StringComparison.Ordinal) ||
                                       messageLowered.Contains("wipe", StringComparison.Ordinal);

            if (pending is not null &&
                !hasExplicitNewIntent &&
                (route.Intent == AdminIntentType.Clarification || route.Intent == AdminIntentType.Chat) &&
                Enum.TryParse<AdminIntentType>(pending.Intent, true, out var pendingIntent) &&
                pendingIntent != AdminIntentType.Clarification)
            {
                var serverName = route.Slots.ServerName ?? state.LastServerName;
                // Carry forward the last known command so a server-clarification answer
                // doesn't lose the command context (e.g. "modded" after "which server?").
                var commandText = route.Slots.CommandText ?? state.LastCommandText;
                var overriddenSlots = route.Slots with { ServerName = serverName, CommandText = commandText };
                route = route with
                {
                    Intent = pendingIntent,
                    Slots = overriddenSlots,
                    TargetRef = route.TargetRef ?? pending.TargetRef,
                    ClassifierSource = route.ClassifierSource + "+pending-clarification-override"
                };
                Console.WriteLine($"[chat] Overriding intent to {pendingIntent} (pending clarification answer)");
            }

            // Inject previous command when user says "repeat" / "execute previous" / etc.
            if (IsRepeatPreviousCommand(item.Message) &&
                route.Intent == AdminIntentType.RconCommand &&
                string.IsNullOrWhiteSpace(route.Slots.CommandText) &&
                !string.IsNullOrWhiteSpace(state.LastCommandText))
            {
                var server = route.Slots.ServerName ?? state.LastServerName;
                route = route with
                {
                    Slots = route.Slots with
                    {
                        CommandText = state.LastCommandText,
                        ServerName = server,
                        ScopeKind = server is not null ? ServerScopeKind.Single : ServerScopeKind.Unspecified
                    },
                    NeedsClarification = string.IsNullOrWhiteSpace(server),
                    ClassifierSource = route.ClassifierSource + "+repeat-previous"
                };
                Console.WriteLine($"[chat] Injecting previous command '{state.LastCommandText}' (repeat-previous)");
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
            if (route.Intent is not (AdminIntentType.Chat or AdminIntentType.Clarification))
            {
                operations.RecentActions.Add(new ActionRecord
                {
                    Intent = route.Intent.ToString(),
                    Result = result.Success ? "success" : (result.ErrorCode ?? "failed"),
                    ServerName = primaryServer,
                    TimestampUtc = DateTime.UtcNow
                });
                operations.RecentActions = operations.RecentActions.TakeLast(100).ToList();
            }
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

        // "ignore <pattern>" / "ignore line <pattern>" — add pattern to dynamic log ignore list
        if (lowered.StartsWith("ignore ", StringComparison.Ordinal))
        {
            var pattern = message["ignore ".Length..].Trim();
            // Strip optional "line " or "lines " prefix for natural phrasing
            if (pattern.StartsWith("line ", StringComparison.OrdinalIgnoreCase))
                pattern = pattern["line ".Length..].Trim();
            else if (pattern.StartsWith("lines ", StringComparison.OrdinalIgnoreCase))
                pattern = pattern["lines ".Length..].Trim();

            if (!string.IsNullOrWhiteSpace(pattern) && pattern.Length >= 3)
            {
                var logs = _neoCortex.LoadLogs();
                var already = logs.IgnorePatterns.Contains(pattern, StringComparer.OrdinalIgnoreCase);
                if (!already)
                {
                    logs.IgnorePatterns.Add(pattern);
                    _neoCortex.SaveLogs(logs);

                    // Also clean existing entries that match the new ignore pattern
                    var removed = logs.RecentEntries.RemoveAll(e => e.Line.Contains(pattern, StringComparison.OrdinalIgnoreCase));
                    if (removed > 0) _neoCortex.SaveLogs(logs);

                    // Sync to ignore feedback store too
                    var feedback = _neoCortex.LoadIgnoreFeedback();
                    if (!feedback.PartialMatches.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                    {
                        feedback.PartialMatches.Add(pattern);
                        _neoCortex.SaveIgnoreFeedback(feedback);
                    }
                }

                return already
                    ? $"Already ignoring lines containing \"{pattern}\"."
                    : $"Got it — I'll ignore log lines containing \"{pattern}\" from now on.";
            }
        }

        // "unignore <pattern>" / "stop ignoring <pattern>" — remove from dynamic ignore list
        if (lowered.StartsWith("unignore ", StringComparison.Ordinal) ||
            lowered.StartsWith("stop ignoring ", StringComparison.Ordinal))
        {
            var prefix = lowered.StartsWith("unignore ") ? "unignore " : "stop ignoring ";
            var pattern = message[prefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                var logs = _neoCortex.LoadLogs();
                var removed = logs.IgnorePatterns.RemoveAll(p => p.Equals(pattern, StringComparison.OrdinalIgnoreCase));
                _neoCortex.SaveLogs(logs);
                return removed > 0
                    ? $"Removed ignore rule for \"{pattern}\"."
                    : $"No ignore rule matched \"{pattern}\".";
            }
        }

        // "mute alerts" / "silence alerts" / "no alerts" — suppress broadcast alerts for 1 hour
        if (lowered is "mute alerts" or "silence alerts" or "no alerts" or "stop alerting" or "stop spamming")
        {
            _alertMutedUntilUtc = DateTime.UtcNow.AddHours(1);
            return $"Alert broadcasts muted for 1 hour (until {_alertMutedUntilUtc:HH:mm} UTC). Say \"unmute alerts\" to re-enable.";
        }

        // "mute alerts <N>h" — mute for N hours
        if (lowered.StartsWith("mute alerts ", StringComparison.Ordinal) || lowered.StartsWith("silence alerts ", StringComparison.Ordinal))
        {
            var prefix = lowered.StartsWith("mute alerts ") ? "mute alerts " : "silence alerts ";
            var rest = lowered[prefix.Length..].Trim().TrimEnd('h').Trim();
            if (int.TryParse(rest, out var hours) && hours is >= 1 and <= 48)
            {
                _alertMutedUntilUtc = DateTime.UtcNow.AddHours(hours);
                return $"Alert broadcasts muted for {hours}h (until {_alertMutedUntilUtc:HH:mm} UTC).";
            }
        }

        // "unmute alerts" / "resume alerts" — re-enable broadcasts
        if (lowered is "unmute alerts" or "resume alerts" or "alerts on" or "start alerting")
        {
            _alertMutedUntilUtc = DateTime.MinValue;
            return "Alert broadcasts re-enabled.";
        }

        // "allow <command>" — add command to RCON allowlist
        if (lowered.StartsWith("allow ", StringComparison.Ordinal))
        {
            var commandRoot = message["allow ".Length..].Trim().ToLowerInvariant().Split(' ')[0].Trim('\'', '"', '`');
            if (!string.IsNullOrWhiteSpace(commandRoot) && !commandRoot.Contains(' '))
            {
                var policy = _neoCortex.LoadCommandPolicy();
                if (!policy.Commands.TryGetValue(commandRoot, out var record))
                {
                    record = new CommandRecord { Command = commandRoot };
                    policy.Commands[commandRoot] = record;
                }
                record.AutoAllowed = true;
                record.RequiresApproval = false;
                _neoCortex.SaveCommandPolicy(policy);
                return $"Got it — '{commandRoot}' is now allowed. Try your command again.";
            }
        }

        // "deny <command>" / "block <command>" — flag as requiring approval
        if (lowered.StartsWith("deny ", StringComparison.Ordinal) || lowered.StartsWith("block ", StringComparison.Ordinal))
        {
            var prefix = lowered.StartsWith("deny ") ? "deny " : "block ";
            var commandRoot = message[prefix.Length..].Trim().ToLowerInvariant().Split(' ')[0].Trim('\'', '"', '`');
            if (!string.IsNullOrWhiteSpace(commandRoot) && !commandRoot.Contains(' '))
            {
                var policy = _neoCortex.LoadCommandPolicy();
                if (!policy.Commands.TryGetValue(commandRoot, out var record))
                {
                    record = new CommandRecord { Command = commandRoot };
                    policy.Commands[commandRoot] = record;
                }
                record.RequiresApproval = true;
                record.AutoAllowed = false;
                _neoCortex.SaveCommandPolicy(policy);
                return $"Got it — '{commandRoot}' now requires explicit approval before running.";
            }
        }

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

        // Daily log digest: archive yesterday's entries at UTC midnight and start fresh.
        var todayDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (todayDate != _lastDigestDate)
        {
            _neoCortex.ArchiveAndResetLogs(_lastDigestDate);
            _lastDigestDate = todayDate;
            Console.WriteLine($"[observe] Daily log digest archived for {_lastDigestDate}. RecentEntries reset.");
        }

        List<string> servers;
        try
        {
            using var list = await _api.GetAsync("/servers", cancellationToken);
            _remoteServers.Clear();
            servers = list.RootElement.ValueKind == JsonValueKind.Array
                ? list.RootElement.EnumerateArray()
                    .Select(node =>
                    {
                        // API returns either string (legacy) or {name, configExists, remote} objects
                        string? name = null;
                        bool isRemote = false;
                        if (node.ValueKind == JsonValueKind.String)
                        {
                            name = node.GetString();
                        }
                        else if (node.ValueKind == JsonValueKind.Object)
                        {
                            if (node.TryGetProperty("name", out var nameNode))
                                name = nameNode.GetString();
                            if (node.TryGetProperty("remote", out var remoteNode) && remoteNode.ValueKind == JsonValueKind.True)
                                isRemote = true;
                        }
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            if (isRemote)
                                _remoteServers.Add(name!);
                            return name;
                        }
                        return null;
                    })
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
                if (!_remoteServers.Contains(server))
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

        // Apply CPU affinity when configured and server is running with a fresh PID.
        if (_config.CpuAffinity.Enabled &&
            _config.CpuAffinity.Servers.TryGetValue(server, out var cpuList) &&
            root.TryGetProperty("status", out var statusNode) &&
            statusNode.TryGetProperty("pid", out var pidNode) &&
            pidNode.ValueKind == JsonValueKind.Number)
        {
            var pid = pidNode.GetInt32();
            if (pid > 0 && (!_appliedAffinityPids.TryGetValue(server, out var lastPid) || lastPid != pid))
            {
                await TryApplyAffinityAsync(server, pid, cpuList, cancellationToken);
                _appliedAffinityPids[server] = pid;
            }
        }
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

    // Rust dedicated server chat format: HH:MM:SS [Chat] PlayerName[SteamID64]: message
    private static readonly System.Text.RegularExpressions.Regex ChatLineRegex = new(
        @"\[Chat\]",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // Extracts (playerName, chatMessage) from a Rust chat log line, or returns null if not a chat line.
    private static (string Player, string Message)? TryParseChatLine(string line)
    {
        if (!ChatLineRegex.IsMatch(line))
            return null;

        // Format: ... [Chat] PlayerName[steamId]: message
        var chatIdx = line.IndexOf("[Chat]", StringComparison.OrdinalIgnoreCase);
        if (chatIdx < 0)
            return null;

        var after = line[(chatIdx + 6)..].TrimStart();
        // PlayerName[steamId64]: message  OR  PlayerName: message
        var colonIdx = after.IndexOf(':');
        if (colonIdx <= 0)
            return null;

        var nameRaw = after[..colonIdx];
        var bracketIdx = nameRaw.IndexOf('[');
        var playerName = (bracketIdx > 0 ? nameRaw[..bracketIdx] : nameRaw).Trim();
        var chatMessage = after[(colonIdx + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(chatMessage))
            return null;

        // Reject non-player entries: names with commas (plugin lists), newlines, brackets, or excessive length.
        if (playerName.Contains(',') || playerName.Contains('\n') ||
            playerName.Contains('[') || playerName.Contains(']') ||
            playerName.Length > 60)
            return null;

        return (playerName, chatMessage);
    }

    private async Task ObserveServerLogsAsync(string server, CancellationToken cancellationToken)
    {
        var isFirstScan = !_logOffsets.ContainsKey(server);
        _logOffsets.TryGetValue(server, out var offset);
        var path = $"/servers/{Uri.EscapeDataString(server)}/logs/read?offset={offset}&maxBytes=65536";
        using var logs = await _api.GetAsync(path, cancellationToken);
        var root = logs.RootElement;

        long newEndOffset = offset;
        if (root.TryGetProperty("endOffset", out var endOffsetNode) && endOffsetNode.ValueKind == JsonValueKind.Number)
        {
            newEndOffset = endOffsetNode.GetInt64();
        }

        // On first scan after (re)start, skip all historical log content.
        // Seek to the current end so we only process new lines going forward.
        if (isFirstScan)
        {
            _logOffsets[server] = newEndOffset;
            _serverInitialized[server] = true;
            Console.WriteLine($"[observe] {server}: First scan — seeking to log offset {newEndOffset}, monitoring active.");
            return;
        }

        _logOffsets[server] = newEndOffset;

        var knowledge = _neoCortex.LoadLogs();
        var consoleMonitor = _config.ConsoleMonitor.Enabled ? _neoCortex.LoadConsoleMonitor() : null;
        var playerChat = _config.ConsoleMonitor.Enabled ? _neoCortex.LoadPlayerChat() : null;

        var logChanged = false;
        var consoleChanged = false;
        var chatChanged = false;

        if (!consoleMonitor!.Servers.TryGetValue(server, out var serverConsole))
        {
            serverConsole = new ServerConsoleState();
            consoleMonitor.Servers[server] = serverConsole;
            consoleChanged = true; // new server entry — persist so dashboard sees it immediately
        }

        if (root.TryGetProperty("entries", out var entriesNode) && entriesNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entriesNode.EnumerateArray())
            {
                var line = entry.TryGetProperty("message", out var messageNode) ? messageNode.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var lowered = line.ToLowerInvariant();

                // Apply static IgnoreContains rules from agent-log-rules.json first.
                if (_staticIgnorePatterns.Any(p => lowered.Contains(p.ToLowerInvariant(), StringComparison.Ordinal)))
                    continue;

                // Skip all noise before the server fully initializes.
                if (line.Contains("SteamServer Initialized", StringComparison.OrdinalIgnoreCase))
                {
                    _serverInitialized[server] = true;
                    Console.WriteLine($"[observe] {server}: SteamServer initialized — log monitoring active.");
                    continue; // skip the init line itself; it's just a marker
                }

                // During startup (before SteamServer Initialized), also apply StartupIgnoreContains patterns.
                if (!_serverInitialized.TryGetValue(server, out var initialized) || !initialized)
                {
                    if (_startupIgnorePatterns.Any(p => lowered.Contains(p.ToLowerInvariant(), StringComparison.Ordinal)))
                        continue;
                    // Still skip lines that aren't startup-noise but aren't real events yet.
                    continue;
                }

                // Detect server restart: Unity/Steam init lines only appear at the start of a new boot.
                if (initialized && (lowered.Contains("initializing steam") || lowered.StartsWith("oxide version")))
                {
                    _serverInitialized[server] = false;
                    Console.WriteLine($"[observe] {server}: Server restart detected — waiting for SteamServer Initialized.");
                    continue;
                }

                // --- Player chat stream ---
                var parsed = TryParseChatLine(line);
                if (parsed.HasValue && playerChat is not null)
                {
                    playerChat.RecentMessages.Add(new PlayerChatEntry
                    {
                        ServerName = server,
                        PlayerName = parsed.Value.Player,
                        Message = parsed.Value.Message,
                        CapturedAtUtc = DateTime.UtcNow
                    });
                    chatChanged = true;
                    continue; // chat lines don't also go to the error console
                }

                // --- Console error/warning stream ---
                if (consoleMonitor is not null)
                {
                    var category = ClassifyConsoleLine(lowered);
                    if (category is "error" or "warning")
                    {
                        var key = NormalizeErrorKey(line);
                        var existing = serverConsole.RecentErrors
                            .FirstOrDefault(e => string.Equals(e.Message, key, StringComparison.OrdinalIgnoreCase));
                        if (existing is not null)
                        {
                            existing.Count++;
                            existing.LastSeenAtUtc = DateTime.UtcNow;
                        }
                        else
                        {
                            serverConsole.RecentErrors.Add(new ConsoleErrorEntry
                            {
                                Message = key,
                                Category = category,
                                FirstSeenAtUtc = DateTime.UtcNow,
                                LastSeenAtUtc = DateTime.UtcNow
                            });
                        }
                        serverConsole.TotalErrorsIngested++;
                        serverConsole.ErrorCountSinceLastAlert++;
                        consoleChanged = true;

                        // Track compile/oxide errors for learning seeding
                        if (lowered.Contains("oxide") || lowered.Contains("compil") || lowered.Contains("error while"))
                        {
                            _compileErrorCounts.TryGetValue(server, out var ceCount);
                            _compileErrorCounts[server] = ceCount + 1;
                            if (_compileErrorCounts[server] >= _config.ConsoleMonitor.CompileErrorSeedThreshold)
                            {
                                _compileErrorCounts[server] = 0;
                                var ck = _neoCortex.LoadClassifierKnowledge();
                                ck.PendingMisclassifications.Add(new MisclassificationRecord
                                {
                                    FeedbackNote = $"Repeated plugin/oxide compilation errors observed on server '{server}'. Queries about errors on this server likely relate to plugin compilation.",
                                    DetectedIntent = "observation",
                                    CapturedAtUtc = DateTime.UtcNow
                                });
                                ck.PendingMisclassifications = ck.PendingMisclassifications.TakeLast(50).ToList();
                                _neoCortex.SaveClassifierKnowledge(ck);
                                Console.WriteLine($"[evolution] Compile/oxide observation seeded for '{server}'.");
                            }
                        }
                    }

                    // Track repeating info/debug messages
                    if (category is "info" or "debug")
                    {
                        var key = NormalizeErrorKey(line);
                        serverConsole.RepeatingMessages.TryGetValue(key, out var cnt);
                        serverConsole.RepeatingMessages[key] = cnt + 1;
                        if (cnt + 1 == _config.ConsoleMonitor.RepeatThreshold)
                        {
                            Console.WriteLine($"[console] {server}: repeating message ({cnt + 1}x): {(key.Length > 80 ? key[..80] + "..." : key)}");
                            consoleChanged = true;
                        }
                    }
                }

                // --- Legacy log knowledge stream ---
                // Apply dynamic ignore patterns (set by admin "ignore X" directives).
                if (knowledge.IgnorePatterns.Any(pattern => lowered.Contains(pattern.ToLowerInvariant(), StringComparison.Ordinal)))
                    continue;

                var importance = ScoreImportance(lowered, knowledge.ImportanceRules);

                // Also promote IncidentContains matches from agent-log-rules.json to high importance.
                if (importance < 3 && _incidentPatterns.Any(p => lowered.Contains(p.ToLowerInvariant(), StringComparison.Ordinal)))
                    importance = 3;

                // Only save meaningful entries (warnings/errors/incidents) — skip pure info noise.
                if (importance < 2)
                    continue;

                knowledge.RecentEntries.Add(new LogObservation
                {
                    ServerName = server,
                    Line = line,
                    Importance = importance,
                    CapturedAtUtc = DateTime.UtcNow
                });
                logChanged = true;
            }
        }

        // Trim and persist console monitor
        if (consoleChanged && consoleMonitor is not null)
        {
            serverConsole.RecentErrors = serverConsole.RecentErrors
                .OrderByDescending(e => e.LastSeenAtUtc)
                .Take(100)
                .ToList();

            // Keep repeating map bounded
            if (serverConsole.RepeatingMessages.Count > 200)
            {
                var top = serverConsole.RepeatingMessages
                    .OrderByDescending(kv => kv.Value)
                    .Take(100)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                serverConsole.RepeatingMessages = top;
            }

            // Escalate if error count exceeds threshold, unless alerts are muted.
            if (serverConsole.ErrorCountSinceLastAlert >= _config.ConsoleMonitor.ErrorEscalationThreshold)
            {
                serverConsole.LastAlertAtUtc = DateTime.UtcNow;
                serverConsole.ErrorCountSinceLastAlert = 0;
                if (DateTime.UtcNow > _alertMutedUntilUtc)
                {
                    Console.WriteLine($"[console] ESCALATE {server}: {serverConsole.ErrorCountSinceLastAlert} errors since last alert.");
                    var topErrors = serverConsole.RecentErrors
                        .OrderByDescending(e => e.Count)
                        .Take(3)
                        .Select(e => $"  • {e.Message[..(Math.Min(e.Message.Length, 60))]} ({e.Count}x)")
                        .ToList();
                    var alertMsg = $"[{server}] {serverConsole.ErrorCountSinceLastAlert} console errors since last alert." +
                        (topErrors.Count > 0 ? "\nTop errors:\n" + string.Join("\n", topErrors) : string.Empty);
                    BroadcastOutbox(alertMsg, server);
                }
                else
                {
                    Console.WriteLine($"[console] {server}: escalation suppressed (alerts muted until {_alertMutedUntilUtc:HH:mm} UTC).");
                }
            }

            consoleMonitor.UpdatedAtUtc = DateTime.UtcNow;
            _neoCortex.SaveConsoleMonitor(consoleMonitor);
        }

        // Trim and persist player chat
        if (chatChanged && playerChat is not null)
        {
            playerChat.RecentMessages = playerChat.RecentMessages
                .TakeLast(_config.ConsoleMonitor.MaxChatMessages)
                .ToList();
            _neoCortex.SavePlayerChat(playerChat);
        }

        if (!logChanged)
            return;

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

    private static string ClassifyConsoleLine(string lowered)
    {
        if (lowered.Contains("exception") || lowered.Contains("fatal") || lowered.Contains("crash") ||
            (lowered.Contains("error") && !lowered.Contains("errorcorrection")))
            return "error";
        if (lowered.Contains("warn"))
            return "warning";
        if (lowered.Contains("debug") || lowered.Contains("[d]"))
            return "debug";
        return "info";
    }

    private static string NormalizeErrorKey(string line)
    {
        // Strip timestamps and volatile parts to group repeated messages.
        var normalized = System.Text.RegularExpressions.Regex.Replace(line, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", "<ts>");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\d{2}:\d{2}:\d{2}", "<time>");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b\d+\b", "<n>");
        return normalized.Length > 200 ? normalized[..200] : normalized;
    }

    private async Task AnalyzePlayerSentimentAsync(CancellationToken cancellationToken)
    {
        if (!_config.ConsoleMonitor.Enabled)
            return;

        var interval = TimeSpan.FromMinutes(Math.Max(10, _config.ConsoleMonitor.SentimentAnalysisIntervalMinutes));
        if (DateTime.UtcNow - _lastSentimentAnalysisAtUtc < interval)
            return;

        _lastSentimentAnalysisAtUtc = DateTime.UtcNow;

        var chat = _neoCortex.LoadPlayerChat();
        if (chat.RecentMessages.Count < 5)
            return;

        if (!CanUseDeepLlm("sentiment"))
            return;

        var recent = chat.RecentMessages.TakeLast(50).ToList();
        var chatText = string.Join("\n", recent.Select(m => $"[{m.ServerName}] {m.PlayerName}: {m.Message}"));

        var prompt = $"""
You are analysing player chat from a Rust game server community.
Return strict JSON only with keys:
sentimentScore, sentimentLabel, sentimentSummary, keyThemes, constructiveFeedback

Constraints:
- sentimentScore: number 0.0–10.0 (0=very negative, 5=neutral, 10=very positive)
- sentimentLabel: "positive" | "neutral" | "negative" | "mixed"
- sentimentSummary: 1-2 sentences describing overall player mood
- keyThemes: array of up to 5 short strings (topics players talk about)
- constructiveFeedback: array of up to 5 short strings (actionable feedback from players)

Recent player chat (newest last):
{chatText}
""";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(180));

            var response = await _deepKernel!.InvokePromptAsync(prompt, cancellationToken: cts.Token);
            var raw = response.GetValue<string>() ?? string.Empty;
            var json = TryExtractJson(raw);
            if (json is null) return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            chat.SentimentScore = root.TryGetProperty("sentimentScore", out var sc) && sc.ValueKind == JsonValueKind.Number
                ? sc.GetDouble() : null;
            chat.SentimentLabel = root.TryGetProperty("sentimentLabel", out var sl) ? sl.GetString() : null;
            chat.SentimentSummary = root.TryGetProperty("sentimentSummary", out var ss) ? ss.GetString() : null;

            chat.KeyThemes = root.TryGetProperty("keyThemes", out var kt) && kt.ValueKind == JsonValueKind.Array
                ? kt.EnumerateArray().Select(t => t.GetString() ?? string.Empty).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
                : new List<string>();

            chat.ConstructiveFeedback = root.TryGetProperty("constructiveFeedback", out var cf) && cf.ValueKind == JsonValueKind.Array
                ? cf.EnumerateArray().Select(t => t.GetString() ?? string.Empty).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
                : new List<string>();

            chat.AnalysedAtUtc = DateTime.UtcNow;
            _neoCortex.SavePlayerChat(chat);

            Console.WriteLine($"[sentiment] Score={chat.SentimentScore:F1} Label={chat.SentimentLabel} — {chat.SentimentSummary}");

            RecordLlmInteraction("player-sentiment", true, true, $"{recent.Count} chat messages", chat.SentimentSummary, "llm");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[sentiment] Analysis timed out (180s limit).");
        }
        catch (Exception ex)
        {
            if (HandleDeepLlmUnauthorized(ex, "sentiment", "Player sentiment analysis"))
                return;
            Console.WriteLine($"[sentiment] Analysis failed: {ex.Message}");
        }
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
        if (!CanUseDeepLlm("observe", emitSkipLog: false))
        {
            return new ObservationAnalysis(
                fallbackSummary,
                "health_observation",
                "health_issue_detected",
                "Review recent errors and server health details.",
                false,
                false,
                "template_llm_unavailable");
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
            var response = await _deepKernel!.InvokePromptAsync(prompt, cancellationToken: cancellationToken);
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
            if (HandleDeepLlmUnauthorized(ex, "observe", "Health observation analysis"))
            {
                return new ObservationAnalysis(
                    fallbackSummary,
                    "health_observation",
                    "health_issue_detected",
                    "Review recent errors and server health details.",
                    true,
                    false,
                    "llm_unauthorized");
            }

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
        if (start < 0) return null;

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < raw.Length; i++)
        {
            char c = raw[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return raw[start..(i + 1)]; }
        }

        return null;
    }

    private bool CanUseDeepLlm(string scope, bool emitSkipLog = true)
    {
        if (_deepKernel is null || !_config.Llm.Enabled || !_config.Llm.UseForRecommendations)
            return false;

        var nowUtc = DateTime.UtcNow;
        if (nowUtc < _deepLlmUnauthorizedMutedUntilUtc)
        {
            if (emitSkipLog && nowUtc - _lastDeepLlmUnauthorizedNoticeUtc >= TimeSpan.FromMinutes(5))
            {
                _lastDeepLlmUnauthorizedNoticeUtc = nowUtc;
                Console.WriteLine($"[{scope}] Deep LLM temporarily disabled after 401 Unauthorized. Retry after {_deepLlmUnauthorizedMutedUntilUtc:O}.");
            }

            return false;
        }

        return true;
    }

    private bool HandleDeepLlmUnauthorized(Exception ex, string scope, string operation)
    {
        if (!IsUnauthorizedLlmException(ex))
            return false;

        _deepLlmUnauthorizedMutedUntilUtc = DateTime.UtcNow.AddMinutes(30);
        _lastDeepLlmUnauthorizedNoticeUtc = DateTime.UtcNow;
        Console.WriteLine($"[{scope}] Deep LLM returned 401 Unauthorized. Pausing deep LLM tasks for 30 minutes; verify llm.apiKey/baseUrl/model.");
        RustOpsSentry.CaptureMessage(
            "Deep LLM request unauthorized; pausing deep LLM tasks.",
            "agent.llm",
            SentryLevel.Warning,
            extras: new Dictionary<string, object?>
            {
                ["scope"] = scope,
                ["operation"] = operation,
                ["resumeAtUtc"] = _deepLlmUnauthorizedMutedUntilUtc.ToString("O"),
                ["error"] = ex.Message
            });
        return true;
    }

    private static bool IsUnauthorizedLlmException(Exception ex)
    {
        var text = ex.ToString();
        return text.Contains("401 (Unauthorized)", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Status: 401", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
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

    private string TryFindRecentDetectedIntent()
    {
        try
        {
            var ops = _neoCortex.LoadOperations();
            var recent = ops.LlmInteractions
                .Where(i => i.Type is "intent-routing" or "intent-routing-fallback")
                .OrderByDescending(i => i.AtUtc)
                .FirstOrDefault();
            return recent?.ResponsePreview ?? "unknown";
        }
        catch { return "unknown"; }
    }

    private async Task EvolveClassifierAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(10, _config.Monitor.ClassifierEvolutionIntervalMinutes));
        if (DateTime.UtcNow - _lastClassifierEvolutionAtUtc < interval)
            return;

        _lastClassifierEvolutionAtUtc = DateTime.UtcNow;

        var knowledge = _neoCortex.LoadClassifierKnowledge();
        var pending = knowledge.PendingMisclassifications.Where(m => !m.Processed).ToList();

        if (pending.Count == 0)
        {
            Console.WriteLine("[evolution] No pending misclassifications to learn from.");
            return;
        }

        if (!CanUseDeepLlm("evolution"))
        {
            Console.WriteLine("[evolution] Deep LLM unavailable — skipping classifier evolution.");
            return;
        }

        Console.WriteLine($"[evolution] Synthesizing classifier rules from {pending.Count} correction(s).");

        var corrections = string.Join("\n", pending.Select((m, i) =>
            $"{i + 1}. Note: \"{m.FeedbackNote}\" | Prior detected intent: {m.DetectedIntent}"));

        var existingRulesText = knowledge.LearnedRules.Count > 0
            ? "Existing learned rules (do not duplicate):\n" + string.Join("\n", knowledge.LearnedRules.Select(r => $"- {r.Rule}"))
            : "No existing learned rules.";

        var prompt = $$"""
You are improving an intent classifier for a Rust game server operations bot.
Admin corrections indicate the bot misunderstood their intent.
Derive concise routing rules from these corrections.

Return strict JSON: { "rules": [ { "rule": "...", "rationale": "..." } ] }
- "rule": One concise routing rule in plain English, e.g. "compile errors / compilation → intent=troubleshooting, target=rust.plugins.verify"
- "rationale": One-sentence explanation
- Return only NEW rules not already covered by existing ones
- Return an empty "rules" array if corrections relate to execution failures or general dissatisfaction, not misrouting
- Maximum 5 new rules per cycle

{{existingRulesText}}

Admin corrections:
{{corrections}}
""";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(180));

            var response = await _deepKernel!.InvokePromptAsync(prompt, cancellationToken: cts.Token);
            var raw = response.GetValue<string>() ?? string.Empty;
            var json = TryExtractJson(raw);
            if (json is null)
            {
                Console.WriteLine("[evolution] LLM returned unparseable rule synthesis.");
                return;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("rules", out var rulesNode) || rulesNode.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine("[evolution] No rules array in LLM response.");
                return;
            }

            var newRules = new List<LearnedClassifierRule>();
            foreach (var ruleNode in rulesNode.EnumerateArray())
            {
                var rule = ruleNode.TryGetProperty("rule", out var r) ? r.GetString() : null;
                var rationale = ruleNode.TryGetProperty("rationale", out var rat) ? rat.GetString() : null;
                if (!string.IsNullOrWhiteSpace(rule))
                {
                    newRules.Add(new LearnedClassifierRule
                    {
                        Rule = rule!,
                        Rationale = rationale ?? string.Empty,
                        LearnedAtUtc = DateTime.UtcNow
                    });
                }
            }

            foreach (var m in pending)
                m.Processed = true;

            knowledge.LearnedRules.AddRange(newRules);
            knowledge.LearnedRules = knowledge.LearnedRules.TakeLast(50).ToList();
            knowledge.LastEvolutionAtUtc = DateTime.UtcNow;
            knowledge.EvolutionCycleCount++;
            _neoCortex.SaveClassifierKnowledge(knowledge);

            Console.WriteLine($"[evolution] Cycle #{knowledge.EvolutionCycleCount}: learned {newRules.Count} new rule(s). Total: {knowledge.LearnedRules.Count}.");
            foreach (var r in newRules)
                Console.WriteLine($"[evolution]   + {r.Rule}");

            RecordLlmInteraction("classifier-evolution", true, true,
                $"{pending.Count} correction(s)",
                $"{newRules.Count} rule(s) synthesized",
                "deep-llm");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[evolution] Classifier evolution timed out (180s limit).");
        }
        catch (Exception ex)
        {
            if (HandleDeepLlmUnauthorized(ex, "evolution", "Classifier evolution"))
            {
                RecordLlmInteraction("classifier-evolution", true, false, $"{pending.Count} correction(s)", "401 unauthorized", "deep-llm-unauthorized");
                return;
            }

            Console.WriteLine($"[evolution] Classifier evolution failed: {ex.Message}");
            RustOpsSentry.CaptureException(ex, "Classifier evolution LLM failed.", "agent.evolution");
        }
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

        if (!CanUseDeepLlm("review"))
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(180));

            var response = await _deepKernel!.InvokePromptAsync(prompt, cancellationToken: cts.Token);
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
        catch (OperationCanceledException)
        {
            Console.WriteLine("[review] LLM trend analysis timed out (180s limit).");
            RustOpsSentry.CaptureMessage("Incident review LLM timeout", "agent.review", SentryLevel.Warning);
        }
        catch (Exception ex)
        {
            if (HandleDeepLlmUnauthorized(ex, "review", "Incident trend analysis"))
                return;

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
            Console.WriteLine($"[review] Review committed locally for pattern '{pattern}' on branch {branch}.");
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
        Directory.CreateDirectory(_config.Outbox.MessageOutboxPath);
        var chunks = ChunkMessage(message, 3500);
        var baseTime = DateTime.UtcNow;

        foreach (var (index, chunk) in chunks.Select((c, i) => (i, c)))
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
                Message = chunk,
                CreatedAtUtc = baseTime.AddMilliseconds(index * 100)
            };

            var path = Path.Combine(_config.Outbox.MessageOutboxPath, $"{payload.CreatedAtUtc:yyyyMMddHHmmssfff}-chat-reply-{payload.Id}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonDefaults.Default));
        }
    }

    private static IEnumerable<string> ChunkMessage(string text, int limitChars = 3500)
    {
        if (text.Length <= limitChars)
        {
            yield return text;
            yield break;
        }

        var normalized = text.Replace("\r", string.Empty);
        var lines = normalized.Split('\n');
        var buffer = new StringBuilder();

        foreach (var line in lines)
        {
            var candidate = buffer.Length == 0 ? line : $"{buffer}\n{line}";
            if (candidate.Length <= limitChars)
            {
                buffer = new StringBuilder(candidate);
            }
            else
            {
                if (buffer.Length > 0)
                    yield return buffer.ToString();
                buffer = new StringBuilder(line);
            }
        }

        if (buffer.Length > 0)
            yield return buffer.ToString();
    }

    private void BroadcastOutbox(string message, string? serverName)
    {
        var payload = new AdapterMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = "chat-reply",
            Audience = "admins",
            TargetAdminId = null,
            ServerName = serverName ?? string.Empty,
            Message = message,
            CreatedAtUtc = DateTime.UtcNow
        };

        Directory.CreateDirectory(_config.Outbox.MessageOutboxPath);
        var path = Path.Combine(_config.Outbox.MessageOutboxPath, $"{payload.CreatedAtUtc:yyyyMMddHHmmssfff}-chat-reply-{payload.Id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonDefaults.Default));
    }

    private static async Task TryApplyAffinityAsync(string server, int pid, string cpuList, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "taskset",
                Arguments = $"-cp {cpuList} {pid}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                await proc.WaitForExitAsync(ct);
                Console.WriteLine($"[affinity] {server} pid={pid} cores={cpuList} exit={proc.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[affinity] Could not set affinity for {server} pid={pid}: {ex.Message}");
        }
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

    private static (HashSet<string> ignore, HashSet<string> incident, HashSet<string> startupIgnore) LoadStaticLogRules(string? path)
    {
        var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incident = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var startupIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return (ignore, incident, startupIgnore);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            AddStrings(root, "IgnoreContains", ignore);
            AddStrings(root, "IncidentContains", incident);
            AddStrings(root, "StartupIgnoreContains", startupIgnore);
            Console.WriteLine($"[agent] Log rules loaded from '{path}': {ignore.Count} ignore, {incident.Count} incident, {startupIgnore.Count} startup-ignore.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[agent] WARNING: Failed to load log rules from '{path}': {ex.Message}");
        }

        return (ignore, incident, startupIgnore);
    }

    private static void AddStrings(JsonElement root, string key, HashSet<string> target)
    {
        if (root.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    target.Add(s);
            }
        }
    }

    // Detects messages where the user wants to repeat the last RCON command.
    private static bool IsRepeatPreviousCommand(string message)
    {
        var lowered = message.ToLowerInvariant();
        return lowered.Contains("previous") ||
               lowered.Contains("repeat") ||
               lowered.Contains("same command") ||
               (lowered.Contains("run") && lowered.Contains("again")) ||
               (lowered.Contains("execute") && (lowered.Contains("that") || lowered.Contains("it")));
    }

    // Detects messages directed at the agent itself rather than a Rust server.
    private static readonly string[] AgentSelfPatterns =
    {
        "restart yourself", "restart the agent", "restart rustops", "restart the bot",
        "pull and restart", "pull from source", "autopull", "pull yourself",
        "update yourself", "update the agent", "update rustops"
    };

    private async Task<string?> TryHandleAgentControlAsync(string message, CancellationToken ct)
    {
        var lowered = message.ToLowerInvariant();
        if (!AgentSelfPatterns.Any(p => lowered.Contains(p, StringComparison.Ordinal)))
            return null;

        Console.WriteLine($"[agent-ctrl] Detected self-control directive: {message}");
        var status = await _autoPull.TriggerAsync(ct);
        return status.Phase switch
        {
            "updated" => "Pull complete — agent is restarting now.",
            "up-to-date" => "Already up to date. No restart needed.",
            "error" => $"Pull failed: {status.Error}",
            _ => "Pull cycle started."
        };
    }
}
