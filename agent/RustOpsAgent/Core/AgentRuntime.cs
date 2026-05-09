using Microsoft.SemanticKernel;
using Sentry;
using System.Text;
using System.Text.Json;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Core.Interaction;
using RustOpsAgent.Domains.Rust;
using RustOpsAgent.Domains.Rust.Rcon;
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
    private readonly ISemanticMemoryService _semanticMemory;
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
    private readonly Dictionary<string, int> _remoteServerLogOffsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _adminLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _staticIgnorePatterns;
    private readonly HashSet<string> _incidentPatterns;
    private readonly HashSet<string> _startupIgnorePatterns;
    private DateTime _logRulesLastWriteUtc = DateTime.MinValue;
    private string _lastDigestDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
    private DateTime _alertMutedUntilUtc = DateTime.MinValue;
    private DateTime _lastObservationAtUtc = DateTime.MinValue;
    private DateTime _lastIncidentReviewAtUtc = DateTime.MinValue;
    private DateTime _lastSentimentAnalysisAtUtc = DateTime.MinValue;
    private DateTime _lastClassifierEvolutionAtUtc = DateTime.MinValue;
    private DateTime _deepLlmUnauthorizedMutedUntilUtc = DateTime.MinValue;
    private DateTime _lastDeepLlmUnauthorizedNoticeUtc = DateTime.MinValue;
    private volatile bool _stop;
    private readonly Dictionary<string, DateTime> _standInLastProcessedAtUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _standInCooldownByServer = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _standInResponsesThisMinute = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _standInRateLimitWindowStart = DateTime.MinValue;
    private DateTime _lastPluginCheckAtUtc = DateTime.MinValue;
    private DateTime _lastForcedPollAtUtc = DateTime.MinValue;
    private readonly PlayerStore? _playerStore;

    public AgentRuntime(
        AgentConfig config,
        IIntentClassifier classifier,
        IActionExecutor executor,
        IResponseComposer composer,
        NeoCortexStore neoCortex,
        LegacyAgentStateStore legacyState,
        ISemanticMemoryService semanticMemory,
        IGitOpsService gitOps,
        AutoPullService autoPull,
        RustOpsApiClient api,
        Kernel? kernel,
        PlayerStore? playerStore = null)
    {
        _config = config;
        _classifier = classifier;
        _executor = executor;
        _composer = composer;
        _neoCortex = neoCortex;
        _legacyState = legacyState;
        _semanticMemory = semanticMemory;
        _gitOps = gitOps;
        _autoPull = autoPull;
        _api = api;
        _deepKernel = kernel;
        _playerStore = playerStore;
        (_staticIgnorePatterns, _incidentPatterns, _startupIgnorePatterns) = LoadStaticLogRules(config.Monitor.LogRulesPath);
        if (!string.IsNullOrWhiteSpace(config.Monitor.LogRulesPath) && File.Exists(config.Monitor.LogRulesPath))
        {
            try { _logRulesLastWriteUtc = File.GetLastWriteTimeUtc(config.Monitor.LogRulesPath); } catch { }
        }

        // Capture player chat from remote servers via direct RCON unsolicited messages.
        // Local servers still use the log-polling path; remote servers have no log file
        // accessible to the API, so RCON is the only source of their chat stream.
        if (_config.ConsoleMonitor.Enabled)
        {
            RustDirectRconHelper.UnsolicitedMessageReceived += OnRemoteRconUnsolicited;
        }
    }

    private DateTime _lastRemoteRconWarmupAtUtc = DateTime.MinValue;
    private readonly Dictionary<string, string> _notifiedPluginUpdatesHash = new(StringComparer.OrdinalIgnoreCase);

    // Periodically re-warm RCON sessions to remote servers. If a server was offline at agent
    // startup, was restarted, or its WebSocket got dropped, this re-establishes the chat
    // stream as soon as it's reachable again. Throttled to once per minute to avoid hammering
    // unreachable hosts.
    private async Task ReconnectRemoteRconSessionsAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _lastRemoteRconWarmupAtUtc < TimeSpan.FromMinutes(1))
            return;
        _lastRemoteRconWarmupAtUtc = DateTime.UtcNow;

        var servers = RustDirectRconHelper.GetAllKnownServerNames();
        if (servers.Count == 0)
            return;

        foreach (var server in servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await RustDirectRconHelper.WarmupAsync(server, cancellationToken);
            // Only log failures and recoveries, not "already connected" — that would spam the log.
            if (!outcome.Success)
                Console.WriteLine($"[agent] RCON reconnect {server}: {outcome.Message}");
        }
    }

    private void OnRemoteRconUnsolicited(string server, string rawMessage)
    {
        try
        {
            // Try to parse as a chat line first
            var parsed = TryParseChatLine(rawMessage);
            if (parsed is not null)
            {
                Console.WriteLine($"[chat] {server}: {parsed.Value.Player}: {parsed.Value.Message}");
                var playerChat = _neoCortex.LoadPlayerChat();
                RecordPlayerChat(playerChat, server, parsed.Value.Player, parsed.Value.Message, DateTime.UtcNow);
                _neoCortex.SavePlayerChat(playerChat);
                _playerStore?.RecordChat(parsed.Value.SteamId, parsed.Value.Player, server, parsed.Value.Message);
                return; // Chat lines don't also go to console error tracking
            }

            // Try to parse as a player join/auth/disconnect line
            TryRecordPlayerEventFromLogLine(rawMessage, server);

            // Record non-chat lines to console monitor (errors, warnings, etc.)
            if (!_config.ConsoleMonitor.Enabled)
                return;

            var consoleMonitor = _neoCortex.LoadConsoleMonitor();
            if (!consoleMonitor.Servers.TryGetValue(server, out var serverConsole))
            {
                serverConsole = new ServerConsoleState();
                consoleMonitor.Servers[server] = serverConsole;
            }

            var consoleSignalLine = ExtractConsoleSignalLine(rawMessage);
            var category = consoleSignalLine is null ? "info" : ClassifyConsoleLine(consoleSignalLine.ToLowerInvariant());

            if (category is "error" or "warning")
            {
                if (IsPurePlayerConnectionNoise(consoleSignalLine!))
                    return;

                var key = NormalizeErrorKey(consoleSignalLine!);
                var existing = serverConsole.RecentErrors
                    .FirstOrDefault(e => string.Equals(e.Message, key, StringComparison.OrdinalIgnoreCase));

                if (existing is not null)
                {
                    existing.Count++;
                    existing.LastSeenAtUtc = DateTime.UtcNow;
                    if (string.IsNullOrWhiteSpace(existing.SampleLine) || IsBetterConsoleSample(consoleSignalLine!, existing.SampleLine))
                    {
                        existing.SampleLine = TrimSingleLine(consoleSignalLine!, 600);
                    }
                }
                else
                {
                    existing = new ConsoleErrorEntry
                    {
                        Message = key,
                        SampleLine = TrimSingleLine(consoleSignalLine!, 600),
                        Category = category,
                        FirstSeenAtUtc = DateTime.UtcNow,
                        LastSeenAtUtc = DateTime.UtcNow
                    };
                    serverConsole.RecentErrors.Add(existing);
                }

                serverConsole.TotalErrorsIngested++;
                serverConsole.ErrorCountSinceLastAlert++;

                // Trim recent errors to a reasonable size
                serverConsole.RecentErrors = serverConsole.RecentErrors
                    .OrderByDescending(e => e.LastSeenAtUtc)
                    .Take(100)
                    .ToList();

                consoleMonitor.UpdatedAtUtc = DateTime.UtcNow;
                _neoCortex.SaveConsoleMonitor(consoleMonitor);
            }
            else if (category is "info" or "debug")
            {
                // Track repeating info/debug messages
                var key = NormalizeErrorKey(rawMessage);
                serverConsole.RepeatingMessages.TryGetValue(key, out var cnt);
                serverConsole.RepeatingMessages[key] = cnt + 1;

                // Keep repeating map bounded
                if (serverConsole.RepeatingMessages.Count > 200)
                {
                    var oldestKey = serverConsole.RepeatingMessages.OrderBy(kv => kv.Value).First().Key;
                    serverConsole.RepeatingMessages.Remove(oldestKey);
                }

                consoleMonitor.UpdatedAtUtc = DateTime.UtcNow;
                _neoCortex.SaveConsoleMonitor(consoleMonitor);
            }
        }
        catch (Exception ex)
        {
            // Never let a chat-capture or console-monitor failure affect the RCON receive loop.
            RustOpsSentry.CaptureException(ex, "Remote RCON message processing failed.", "agent.chat-monitor",
                extras: new Dictionary<string, object?> { ["server"] = server });
        }
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
                await SafeExecuteAsync(() => ProcessPluginChatInboxAsync(cancellationToken), "plugin-chat-processing", cancellationToken);
                await SafeExecuteAsync(() => ObserveServersAsync(cancellationToken), "server-observation", cancellationToken);
                await SafeExecuteAsync(() => ReconnectRemoteRconSessionsAsync(cancellationToken), "remote-rcon-reconnect", cancellationToken);
                await SafeExecuteAsync(() => ReviewIncidentsAsync(cancellationToken), "incident-review", cancellationToken);
                await SafeExecuteAsync(() => AnalyzePlayerSentimentAsync(cancellationToken), "sentiment-analysis", cancellationToken);
                await SafeExecuteAsync(() => ProcessPlayerChatForStandInAsync(cancellationToken), "stand-in-admin", cancellationToken);
                await SafeExecuteAsync(() => CheckPluginUpdatesAsync(cancellationToken), "plugin-check", cancellationToken);
                await SafeExecuteAsync(() => EvolveClassifierAsync(cancellationToken), "classifier-evolution", cancellationToken);
                await SafeExecuteAsync(() => PollForcedListAsync(cancellationToken), "forced-list-poll", cancellationToken);
                await SafeExecuteAsync(() => MaybeReloadStaticLogRulesAsync(cancellationToken), "log-rules-reload", cancellationToken);

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
                if (!string.IsNullOrWhiteSpace(note))
                {
                    await _semanticMemory.RecordUserInstructionAsync(payload.AdminId, payload.ServerName, note, cancellationToken);
                }

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

                        // Backfill: drop already-tracked entries that match the new pattern.
                        SweepTrackedLinesForPatterns(new[] { pattern });
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

    internal async Task ProcessSingleChatMessageAsync(string file, ChatInboxItem? item, CancellationToken cancellationToken)
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

            if (route.Intent == AdminIntentType.RconCommand && string.IsNullOrWhiteSpace(route.Slots.CommandText))
            {
                var extractedCommand = RustRconToolHandler.ExtractCommandFromMessage(item.Message);
                if (!string.IsNullOrWhiteSpace(extractedCommand))
                {
                    route = route with
                    {
                        Slots = route.Slots with { CommandText = extractedCommand },
                        ClassifierSource = route.ClassifierSource + "+rcon-command-extract"
                    };
                    Console.WriteLine($"[chat] Extracted RCON command from message: '{extractedCommand}'");
                }
            }

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
                (route.Intent == AdminIntentType.Clarification ||
                 route.Intent == AdminIntentType.Chat ||
                 (!string.IsNullOrWhiteSpace(route.Slots.ServerName) && string.IsNullOrWhiteSpace(route.Slots.CommandText))) &&
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
                state.PendingClarification = null;
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
            var context = new ToolExecutionContext(item.AdminId, item.Message, route, state, DateTime.UtcNow, route.PlanningMemoryContext ?? WorkflowMemoryContext.Empty, null);
            LogMemoryDebug("runtime execution recall invoked");
            var executionMemory = await _semanticMemory.RecallForExecutionAsync(context, cancellationToken);
            executionMemory = executionMemory with { RetrievalOrigin = "runtime" };
            context = context with { ExecutionMemoryContext = executionMemory };
            LogMemoryDebug(executionMemory.RetrievalSkipped
                ? $"runtime execution recall skipped: {executionMemory.SkipReason}"
                : $"runtime execution recall completed: {executionMemory.Results.Count} result(s)");
            var result = await _executor.ExecuteAsync(context, cancellationToken);
            LogMemoryDebug("post-action writeback invoked");
            await _semanticMemory.RecordActionOutcomeAsync(context, result, cancellationToken);
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

    private async Task ProcessPluginChatInboxAsync(CancellationToken cancellationToken)
    {
        var inboxPath = _config.Inbox.PluginChatInboxPath;
        if (!Directory.Exists(inboxPath))
            return;

        var playerChat = _neoCortex.LoadPlayerChat();
        var changed = false;

        foreach (var file in EnumerateInboxFiles(inboxPath))
        {
            if (_stop || cancellationToken.IsCancellationRequested)
                break;
            try
            {
                var text = await File.ReadAllTextAsync(file, cancellationToken);
                var item = JsonSerializer.Deserialize<PluginChatInboxItem>(text, JsonDefaults.Default);
                if (item is null || string.IsNullOrWhiteSpace(item.Message) || string.IsNullOrWhiteSpace(item.Server))
                    continue;

                var playerName = !string.IsNullOrWhiteSpace(item.Username)
                    ? item.Username
                    : item.SteamId ?? "unknown";

                Console.WriteLine($"[plugin-chat] {item.Server}: {playerName}: {item.Message}");
                RecordPlayerChat(playerChat, item.Server, playerName, item.Message, DateTime.UtcNow);
                if (!string.IsNullOrWhiteSpace(item.SteamId))
                    _playerStore?.RecordChat(item.SteamId, item.Username ?? string.Empty, item.Server, item.Message);
                changed = true;
            }
            catch (Exception ex)
            {
                RustOpsSentry.CaptureException(ex, "Plugin chat inbox processing failed.", "agent.plugin-chat",
                    extras: new Dictionary<string, object?> { ["file"] = file });
            }
            finally
            {
                TryDelete(file);
            }
        }

        if (changed)
            _neoCortex.SavePlayerChat(playerChat);
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
                {
                    await ObserveServerHealthAsync(server, cancellationToken);
                    await ObserveServerLogsAsync(server, cancellationToken);
                }
                else
                {
                    // Remote server — actively poll RCON rolling log for chat/console.
                    // (Unsolicited messages are also captured via event handler, but this ensures no messages are missed.)
                    var rconEndpoint = RustDirectRconHelper.GetSessionEndpoint(server);
                    if (rconEndpoint is not null)
                    {
                        await ObserveRemoteServerRconLogsAsync(server, cancellationToken);
                    }
                    else
                    {
                        Console.WriteLine($"[observe] {server}: remote — RCON not connected");
                    }
                }
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
        await _semanticMemory.RecordServerFactAsync(
            server,
            $"{server} health observation: {summary}",
            $"State: {state}\nRecentErrors: {string.Join(" | ", recentErrors)}\nClassification: {analysis.Classification}\nMitigation: {analysis.RecurrencePrevention}",
            new[] { "health-observation", analysis.Classification, state },
            cancellationToken);
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

    // Extracts (playerName, steamId, chatMessage) from a Rust chat log line, or returns null if not a chat line.
    private static (string Player, string SteamId, string Message)? TryParseChatLine(string line)
    {
        if (!ChatLineRegex.IsMatch(line))
            return null;

        var chatIdx = line.IndexOf("[Chat]", StringComparison.OrdinalIgnoreCase);
        if (chatIdx < 0)
            return null;

        var after = line[(chatIdx + 6)..].TrimStart();

        // WebRCON sends chat as a JSON object: {"Username":"...","Message":"...","UserId":"...","Channel":0,...}
        if (after.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(after);
                var root = doc.RootElement;
                var username = root.TryGetProperty("Username", out var uEl) ? uEl.GetString() : null;
                var chatMsg = root.TryGetProperty("Message", out var mEl) ? mEl.GetString() : null;
                var steamId = root.TryGetProperty("UserId", out var sEl) ? sEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(username) && chatMsg is not null)
                    return (username.Trim(), (steamId ?? string.Empty).Trim(), chatMsg.Trim());
            }
            catch { /* fall through to text-format parse */ }
        }

        // Log-file / old RCON format: PlayerName[steamId64]: message  OR  PlayerName: message
        var colonIdx = after.IndexOf(':');
        if (colonIdx <= 0)
            return null;

        var nameRaw = after[..colonIdx];
        var bracketIdx = nameRaw.IndexOf('[');
        var playerName = (bracketIdx > 0 ? nameRaw[..bracketIdx] : nameRaw).Trim();
        var chatMessage = after[(colonIdx + 1)..].Trim();
        var sidFromBracket = string.Empty;
        if (bracketIdx > 0)
        {
            var closeIdx = nameRaw.IndexOf(']', bracketIdx);
            if (closeIdx > bracketIdx)
                sidFromBracket = nameRaw[(bracketIdx + 1)..closeIdx].Trim();
        }

        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(chatMessage))
            return null;

        // Reject non-player entries: names with commas (plugin lists), newlines, brackets, or excessive length.
        if (playerName.Contains(',') || playerName.Contains('\n') ||
            playerName.Contains('[') || playerName.Contains(']') ||
            playerName.Length > 60)
            return null;

        return (playerName, sidFromBracket, chatMessage);
    }

    // Matches join lines: "<name> with steamid <sid> joined from ip <ip>:<port>"
    private static readonly System.Text.RegularExpressions.Regex JoinLineRegex = new(
        @"^(?<name>.+?)\s+with steamid\s+(?<sid>7656\d{13})\s+joined from ip\s+(?<ip>[^\s:]+)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Matches "<ip>:<port>/<sid>/<name>" prefix (auth, kick, disconnect, etc.)
    private static readonly System.Text.RegularExpressions.Regex AuthOrLeaveRegex = new(
        @"^(?<ip>\d{1,3}(?:\.\d{1,3}){3}):\d+/(?<sid>7656\d{13})/(?<name>[^\s]+)\s",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private void TryRecordPlayerEventFromLogLine(string line, string server)
    {
        if (_playerStore is null) return;
        var join = JoinLineRegex.Match(line);
        if (join.Success)
        {
            _playerStore.RecordSighting(
                join.Groups["sid"].Value,
                join.Groups["name"].Value,
                server,
                join.Groups["ip"].Value,
                startsSession: true);
            return;
        }

        var auth = AuthOrLeaveRegex.Match(line);
        if (auth.Success)
        {
            _playerStore.RecordSighting(
                auth.Groups["sid"].Value,
                auth.Groups["name"].Value,
                server,
                auth.Groups["ip"].Value,
                startsSession: false);
        }
    }

    private void RecordPlayerChat(PlayerChatKnowledge chat, string server, string player, string message, DateTime capturedAtUtc)
    {
        var isAdminCall = TryClassifyAdminCall(message, out var callType);
        chat.RecentMessages.Add(new PlayerChatEntry
        {
            ServerName = server,
            PlayerName = player,
            Message = message,
            CapturedAtUtc = capturedAtUtc,
            IsAdminCall = isAdminCall
        });
        chat.RecentMessages = chat.RecentMessages
            .TakeLast(_config.ConsoleMonitor.MaxChatMessages)
            .ToList();

        if (!chat.PerServerVolume.TryGetValue(server, out var volume))
        {
            volume = new ServerChatVolume { ServerName = server };
            chat.PerServerVolume[server] = volume;
        }

        var todayUtc = capturedAtUtc.Date;
        if (volume.LastMessageAtUtc?.Date != todayUtc)
            volume.TodayMessageCount = 0;

        volume.TotalMessageCount++;
        volume.TodayMessageCount++;
        volume.LastMessageAtUtc = capturedAtUtc;
        volume.ActivePlayerCount = chat.RecentMessages
            .Where(m => m.CapturedAtUtc.Date == todayUtc && m.ServerName.Equals(server, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.PlayerName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (isAdminCall)
        {
            chat.AdminCalls.Add(new AdminCallEvent
            {
                ServerName = server,
                PlayerName = player,
                Message = message,
                CallType = callType,
                CapturedAtUtc = capturedAtUtc,
                Acknowledged = false
            });
            chat.AdminCalls = chat.AdminCalls
                .OrderBy(e => e.CapturedAtUtc)
                .TakeLast(_config.ConsoleMonitor.MaxAdminCalls)
                .ToList();
        }
    }

    // Patterns are anchored on word boundaries so that substrings like "esp" inside
    // "respawn" or "admin" inside a plugin name don't false-trigger. Keep these in sync
    // with LooksLikeAdminCall in api/Program.cs.
    private static readonly System.Text.RegularExpressions.Regex AdminCallCheaterRegex = new(
        @"\b(cheater|cheating|hacker|hacking|aimbot|aimlock|wallhack|wallhacking|esp|scripting|script user)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex AdminCallAdminRegex = new(
        @"(?:^|\s)@?(admin|admins|moderator|moderators|staff|owner)\b|\b(any|some|an)\s+(admin|mod|moderator|staff)\b|\b(admin|mod|moderator)\s+(here|on|please|pls|plz|help|needed)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex AdminCallHelpRegex = new(
        @"\b(stuck|glitched|bugged|frozen|broken|softlocked)\b|\b(not\s+working|doesn'?t\s+work|won'?t\s+load|can'?t\s+(?:join|connect|spawn|move|build|craft|open))\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex AdminCallReportRegex = new(
        @"\b(grief(?:ing|ed)?|toxic|racist|racism|harass(?:ing|ment)?|insult(?:ing)?|slur|slurs)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool TryClassifyAdminCall(string message, out string callType)
    {
        callType = string.Empty;
        var text = (message ?? string.Empty).Trim();
        // Require at least a couple of words; single-word interjections are too noisy.
        if (text.Length < 6) return false;

        if (AdminCallCheaterRegex.IsMatch(text)) { callType = "cheater-report"; return true; }
        if (AdminCallAdminRegex.IsMatch(text))   { callType = "admin-request"; return true; }
        if (AdminCallReportRegex.IsMatch(text))  { callType = "player-report"; return true; }
        if (AdminCallHelpRegex.IsMatch(text))    { callType = "help-request"; return true; }
        return false;
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

        var newHighImportanceLines = new List<string>();
        var newConsoleMemoryCandidates = new List<string>();
        var uncertainReviewPrompts = new List<(string Key, string Line)>();

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
                    RecordPlayerChat(playerChat, server, parsed.Value.Player, parsed.Value.Message, DateTime.UtcNow);
                    _playerStore?.RecordChat(parsed.Value.SteamId, parsed.Value.Player, server, parsed.Value.Message);
                    chatChanged = true;
                    continue; // chat lines don't also go to the error console
                }

                // --- Player join/auth/disconnect stream ---
                TryRecordPlayerEventFromLogLine(line, server);

                // --- Console error/warning stream ---
                if (consoleMonitor is not null)
                {
                    var consoleSignalLine = ExtractConsoleSignalLine(line);
                    var category = consoleSignalLine is null ? "info" : ClassifyConsoleLine(consoleSignalLine.ToLowerInvariant());
                    if (category is "error" or "warning")
                    {
                        if (IsPurePlayerConnectionNoise(consoleSignalLine!))
                        {
                            continue;
                        }

                        var key = NormalizeErrorKey(consoleSignalLine!);
                        var existing = serverConsole.RecentErrors
                            .FirstOrDefault(e => string.Equals(e.Message, key, StringComparison.OrdinalIgnoreCase));
                        if (existing is not null)
                        {
                            existing.Count++;
                            existing.LastSeenAtUtc = DateTime.UtcNow;
                            if (string.IsNullOrWhiteSpace(existing.SampleLine) || IsBetterConsoleSample(consoleSignalLine!, existing.SampleLine))
                            {
                                existing.SampleLine = TrimSingleLine(consoleSignalLine!, 600);
                            }
                        }
                        else
                        {
                            existing = new ConsoleErrorEntry
                            {
                                Message = key,
                                SampleLine = TrimSingleLine(consoleSignalLine!, 600),
                                Category = category,
                                FirstSeenAtUtc = DateTime.UtcNow,
                                LastSeenAtUtc = DateTime.UtcNow
                            };
                            serverConsole.RecentErrors.Add(existing);
                        }
                        serverConsole.TotalErrorsIngested++;
                        serverConsole.ErrorCountSinceLastAlert++;
                        consoleChanged = true;
                        newConsoleMemoryCandidates.Add(consoleSignalLine!);
                        if (ShouldAskAdminToReviewConsoleLine(consoleSignalLine!, category) && existing.ReviewPromptedAtUtc is null)
                        {
                            existing.ReviewPromptedAtUtc = DateTime.UtcNow;
                            uncertainReviewPrompts.Add((key, consoleSignalLine!));
                        }

                        // Track compile/oxide errors for learning seeding
                        var signalLowered = consoleSignalLine!.ToLowerInvariant();
                        if (signalLowered.Contains("oxide") || signalLowered.Contains("compil") || signalLowered.Contains("error while"))
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
                if (importance >= 3)
                    newHighImportanceLines.Add(line);
                logChanged = true;
            }
        }

        // Trim and persist console monitor
        if (consoleChanged && consoleMonitor is not null)
        {
            serverConsole.RecentErrors = serverConsole.RecentErrors
                .OrderByDescending(e => e.LastSeenAtUtc)
                .Take(_config.ConsoleMonitor.MaxConsoleErrors)
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
                var alertCount = serverConsole.ErrorCountSinceLastAlert;
                serverConsole.LastAlertAtUtc = DateTime.UtcNow;
                serverConsole.ErrorCountSinceLastAlert = 0;
                if (DateTime.UtcNow > _alertMutedUntilUtc)
                {
                    Console.WriteLine($"[console] ESCALATE {server}: {alertCount} errors since last alert.");
                    var topErrors = serverConsole.RecentErrors
                        .OrderByDescending(e => e.Count)
                        .Select(e => new { Entry = e, Line = TryFormatConsoleAlertLine(e) })
                        .Where(item => !string.IsNullOrWhiteSpace(item.Line))
                        .Take(3)
                        .Select(item => $"  • {item.Line} ({item.Entry.Count}x)")
                        .ToList();
                    var alertMsg = $"[{server}] {alertCount} console errors since last alert." +
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

        if (_config.Memory.WriteEnabled)
        {
            await RecordConsoleMonitorCandidatesAsync(server, newConsoleMemoryCandidates, uncertainReviewPrompts);
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

        // Write newly observed high-importance lines to semantic memory.
        if (_config.Memory.WriteEnabled)
        {
            foreach (var logLine in newHighImportanceLines)
            {
                var summary = $"[{server}] {TrimSingleLine(logLine, 80)}";
                var detail = $"Server: {server}\nLog: {logLine}";
                _ = _semanticMemory.RecordServerFactAsync(server, summary, detail,
                    new[] { "log", "high-importance", server.ToLowerInvariant() }, CancellationToken.None);
            }
        }

        knowledge.RecentEntries = knowledge.RecentEntries.TakeLast(400).ToList();
        _neoCortex.SaveLogs(knowledge);
    }

    private static string ClassifyConsoleLine(string lowered)
    {
        if (IsPurePlayerConnectionNoise(lowered))
            return "info";
        if (lowered.Contains("exception") || lowered.Contains("fatal") ||
            (lowered.Contains("crash") && !lowered.Contains("silent-crashes")) ||
            (lowered.Contains("error") && !lowered.Contains("errorcorrection")))
            return "error";
        if (lowered.Contains("warn"))
            return "warning";
        if (lowered.Contains("debug") || lowered.Contains("[d]"))
            return "debug";
        return "info";
    }

    private async Task ObserveRemoteServerRconLogsAsync(string server, CancellationToken cancellationToken)
    {
        // Actively poll RCON rolling log for remote servers. This complements the event-driven
        // unsolicited message handler by ensuring no chat/console output is missed.
        try
        {
            var rollingLog = RustDirectRconHelper.GetRollingLog(server);
            if (rollingLog.Count == 0)
                return;

            var playerChat = _neoCortex.LoadPlayerChat();
            var consoleMonitor = _neoCortex.LoadConsoleMonitor();
            if (!consoleMonitor.Servers.TryGetValue(server, out var serverConsole))
            {
                serverConsole = new ServerConsoleState();
                consoleMonitor.Servers[server] = serverConsole;
            }

            var chatChanged = false;
            var consoleChanged = false;
            var startOffset = _remoteServerLogOffsets.TryGetValue(server, out var lastOffset) ? lastOffset : 0;

            for (var i = Math.Max(0, startOffset); i < rollingLog.Count; i++)
            {
                var line = rollingLog[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var lowered = line.ToLowerInvariant();

                // Skip initialization markers
                if (lowered.Contains("steamserver initialized", StringComparison.OrdinalIgnoreCase))
                {
                    _serverInitialized[server] = true;
                    continue;
                }

                // Detect server restart
                if (lowered.Contains("initializing steam") || lowered.StartsWith("oxide version"))
                {
                    _serverInitialized[server] = false;
                    continue;
                }

                // Only process after server initialization
                if (!_serverInitialized.TryGetValue(server, out var initialized) || !initialized)
                    continue;

                // --- Player chat stream ---
                var parsed = TryParseChatLine(line);
                if (parsed.HasValue)
                {
                    RecordPlayerChat(playerChat, server, parsed.Value.Player, parsed.Value.Message, DateTime.UtcNow);
                    _playerStore?.RecordChat(parsed.Value.SteamId, parsed.Value.Player, server, parsed.Value.Message);
                    chatChanged = true;
                    continue;
                }

                // --- Player join/auth/disconnect stream ---
                TryRecordPlayerEventFromLogLine(line, server);

                // --- Console error/warning stream ---
                var consoleSignalLine = ExtractConsoleSignalLine(line);
                var category = consoleSignalLine is null ? "info" : ClassifyConsoleLine(consoleSignalLine.ToLowerInvariant());
                if (category is "error" or "warning")
                {
                    if (IsPurePlayerConnectionNoise(consoleSignalLine!))
                        continue;

                    var key = NormalizeErrorKey(consoleSignalLine!);
                    var existing = serverConsole.RecentErrors
                        .FirstOrDefault(e => string.Equals(e.Message, key, StringComparison.OrdinalIgnoreCase));

                    if (existing is not null)
                    {
                        existing.Count++;
                        existing.LastSeenAtUtc = DateTime.UtcNow;
                        if (string.IsNullOrWhiteSpace(existing.SampleLine) || IsBetterConsoleSample(consoleSignalLine!, existing.SampleLine))
                        {
                            existing.SampleLine = TrimSingleLine(consoleSignalLine!, 600);
                        }
                    }
                    else
                    {
                        existing = new ConsoleErrorEntry
                        {
                            Message = key,
                            SampleLine = TrimSingleLine(consoleSignalLine!, 600),
                            Category = category,
                            FirstSeenAtUtc = DateTime.UtcNow,
                            LastSeenAtUtc = DateTime.UtcNow
                        };
                        serverConsole.RecentErrors.Add(existing);
                    }
                    serverConsole.TotalErrorsIngested++;
                    serverConsole.ErrorCountSinceLastAlert++;
                    consoleChanged = true;
                }
            }

            // Update offset to resume from next new line
            _remoteServerLogOffsets[server] = rollingLog.Count;

            // Persist changes
            if (chatChanged)
            {
                _neoCortex.SavePlayerChat(playerChat);
            }

            if (consoleChanged)
            {
                serverConsole.RecentErrors = serverConsole.RecentErrors
                    .OrderByDescending(e => e.LastSeenAtUtc)
                    .Take(_config.ConsoleMonitor.MaxConsoleErrors)
                    .ToList();
                _neoCortex.SaveConsoleMonitor(consoleMonitor);
                Console.WriteLine($"[observe] {server}: remote RCON log processed (chat={playerChat.RecentMessages.Count} messages, console={serverConsole.RecentErrors.Count} errors)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[observe] {server}: failed to process remote RCON log: {ex.Message}");
        }
    }

    private async Task RecordConsoleMonitorCandidatesAsync(
        string server,
        IReadOnlyList<string> newConsoleMemoryCandidates,
        IReadOnlyList<(string Key, string Line)> uncertainReviewPrompts)
    {
        foreach (var logLine in newConsoleMemoryCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var summary = $"[{server}] console monitor: {TrimSingleLine(logLine, 120)}";
            var detail = $"Server: {server}\nLog: {logLine}";
            await _semanticMemory.RecordServerFactAsync(server, summary, detail,
                new[] { "console-monitor", "log", server.ToLowerInvariant() }, CancellationToken.None);
        }

        foreach (var (_, logLine) in uncertainReviewPrompts.Take(3))
        {
            var prompt =
                $"[{server}] I noticed a possible console issue but I am not confident it is a real error. " +
                $"I saved it as pending memory for admin review.\n" +
                $"Line: {TrimSingleLine(logLine, 500)}\n" +
                "Reply with `memory pending`, then `memory approve <id>` or `memory reject <id>`.";
            BroadcastOutbox(prompt, server);
        }
    }

    private static string NormalizeErrorKey(string line)
    {
        // Strip timestamps and volatile parts to group repeated messages.
        var normalized = System.Text.RegularExpressions.Regex.Replace(line, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", "<ts>");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\d{2}:\d{2}:\d{2}", "<time>");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b\d{1,3}(?:\.\d{1,3}){3}:\d+\b", "<ip>:<port>");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b\d{1,3}(?:\.\d{1,3}){3}\b", "<ip>");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b7656\d{13}\b", "<steamid>");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b\d+\b", "<n>");
        return normalized.Length > 240 ? normalized[..240] : normalized;
    }

    private static string? ExtractConsoleSignalLine(string line)
    {
        var lines = line
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        foreach (var candidate in lines)
        {
            if (IsPurePlayerConnectionNoise(candidate) || IsMundaneConsoleLine(candidate))
            {
                continue;
            }

            var lowered = candidate.ToLowerInvariant();
            if (lowered.Contains("exception", StringComparison.Ordinal) ||
                lowered.Contains("failed to call hook", StringComparison.Ordinal) ||
                lowered.Contains("failed to send notification", StringComparison.Ordinal) ||
                lowered.Contains("http error", StringComparison.Ordinal) ||
                lowered.Contains("curl error", StringComparison.Ordinal) ||
                lowered.Contains("unable to connect", StringComparison.Ordinal) ||
                lowered.Contains("connection timed out", StringComparison.Ordinal) ||
                lowered.Contains("connection reset by peer", StringComparison.Ordinal) ||
                lowered.Contains("fallback handler could not load library", StringComparison.Ordinal) ||
                lowered.Contains("response status code does not indicate success", StringComparison.Ordinal) ||
                lowered.StartsWith("error:", StringComparison.Ordinal) ||
                lowered.Contains(" error:", StringComparison.Ordinal) ||
                lowered.Contains("failed:", StringComparison.Ordinal) ||
                lowered.Contains("fatal", StringComparison.Ordinal) ||
                (lowered.Contains("crash", StringComparison.Ordinal) && !lowered.Contains("silent-crashes", StringComparison.Ordinal)))
            {
                return candidate;
            }
        }

        var firstNonNoise = lines.FirstOrDefault(candidate => !IsPurePlayerConnectionNoise(candidate) && !IsMundaneConsoleLine(candidate));
        if (firstNonNoise is null)
        {
            return null;
        }

        var category = ClassifyConsoleLine(firstNonNoise.ToLowerInvariant());
        return category is "error" or "warning" ? firstNonNoise : null;
    }

    private static bool IsMundaneConsoleLine(string line)
    {
        var lowered = line.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lowered))
        {
            return true;
        }

        if (lowered.StartsWith("mono path[", StringComparison.Ordinal) ||
            lowered.StartsWith("mono config path", StringComparison.Ordinal) ||
            lowered.StartsWith("preloaded ", StringComparison.Ordinal) ||
            lowered.StartsWith("shutdown handler:", StringComparison.Ordinal) ||
            lowered.StartsWith("command line:", StringComparison.Ordinal) ||
            lowered.StartsWith("system", StringComparison.Ordinal) ||
            lowered.StartsWith("cpu", StringComparison.Ordinal) ||
            lowered.StartsWith("gpu", StringComparison.Ordinal) ||
            lowered.StartsWith("setup unity update hooks", StringComparison.Ordinal) ||
            lowered.StartsWith("server config loaded", StringComparison.Ordinal) ||
            lowered.StartsWith("running server/", StringComparison.Ordinal) ||
            lowered.StartsWith("manifest ", StringComparison.Ordinal) ||
            lowered.StartsWith("loading shared/", StringComparison.Ordinal) ||
            lowered.StartsWith("saved ", StringComparison.Ordinal) ||
            lowered.StartsWith("saving complete", StringComparison.Ordinal))
        {
            return true;
        }

        var mundaneFragments = new[]
        {
            "[empty low fps]",
            "was killed by",
            "has spawned",
            "calling 'onserversave'",
            "calling 'onplayerconnected'",
            "server is empty",
            "server is no longer empty",
            "setting fps limit",
            "checking for new steam item definitions",
            "unloaded plugin ",
            "loaded plugin ",
            "[better chat]"
        };

        if (mundaneFragments.Any(fragment => lowered.Contains(fragment, StringComparison.Ordinal)))
        {
            return true;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d{1,3}(?:\.\d{1,3}){3}:\d+/\d+/.+\sdisconnecting:\s(?:disconnect|closing)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool IsPurePlayerConnectionNoise(string line)
    {
        var lowered = line.ToLowerInvariant();
        var looksLikePlayerConnection =
            lowered.Contains("joined from ip", StringComparison.Ordinal) ||
            lowered.Contains("networkid", StringComparison.Ordinal) ||
            System.Text.RegularExpressions.Regex.IsMatch(line, @"\bjoined\s+\[(windows|linux|osx|mac|unknown)/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!looksLikePlayerConnection)
        {
            return false;
        }

        return !HasHighConfidenceConsoleErrorSignal(lowered);
    }

    private static bool HasHighConfidenceConsoleErrorSignal(string lowered) =>
        lowered.Contains("exception", StringComparison.Ordinal) ||
        lowered.Contains("fatal", StringComparison.Ordinal) ||
        lowered.Contains("crash", StringComparison.Ordinal) ||
        lowered.Contains("stack trace", StringComparison.Ordinal);

    private static bool ShouldAskAdminToReviewConsoleLine(string line, string category)
    {
        var lowered = line.ToLowerInvariant();
        if (IsPurePlayerConnectionNoise(line))
        {
            return false;
        }

        if (HasHighConfidenceConsoleErrorSignal(lowered))
        {
            return false;
        }

        return category == "warning" ||
               lowered.Contains("unable to connect", StringComparison.Ordinal) ||
               lowered.Contains("access denied", StringComparison.Ordinal) ||
               lowered.Contains("error:", StringComparison.Ordinal) ||
               lowered.Contains("failed:", StringComparison.Ordinal) ||
               lowered.Contains("disconnect", StringComparison.Ordinal);
    }

    private static bool IsBetterConsoleSample(string candidate, string current)
    {
        var candidateLowered = candidate.ToLowerInvariant();
        var currentLowered = current.ToLowerInvariant();
        if (HasHighConfidenceConsoleErrorSignal(candidateLowered) && !HasHighConfidenceConsoleErrorSignal(currentLowered))
        {
            return true;
        }

        return candidate.Length > current.Length && candidate.Length <= 600;
    }

    private static string FormatConsoleAlertLine(ConsoleErrorEntry entry)
    {
        return TryFormatConsoleAlertLine(entry) ?? TrimSingleLine(entry.Message, 600);
    }

    private static string? TryFormatConsoleAlertLine(ConsoleErrorEntry entry)
    {
        var sample = string.IsNullOrWhiteSpace(entry.SampleLine) ? entry.Message : entry.SampleLine;
        if (!string.IsNullOrWhiteSpace(entry.SampleLine))
        {
            return TrimSingleLine(sample, 600);
        }

        var signal = ExtractConsoleSignalLine(sample);
        return string.IsNullOrWhiteSpace(signal) ? null : TrimSingleLine(signal, 600);
    }

    private async Task CheckPluginUpdatesAsync(CancellationToken cancellationToken)
    {
        if (!_config.PluginUpdates.Enabled)
            return;

        var interval = TimeSpan.FromMinutes(Math.Max(5, _config.PluginUpdates.CheckIntervalMinutes));
        if (DateTime.UtcNow - _lastPluginCheckAtUtc < interval)
            return;

        _lastPluginCheckAtUtc = DateTime.UtcNow;

        List<string> servers;
        try
        {
            using var list = await _api.GetAsync("/servers", cancellationToken);
            servers = list.RootElement.ValueKind == JsonValueKind.Array
                ? list.RootElement.EnumerateArray()
                    .Select(n => n.ValueKind == JsonValueKind.String
                        ? n.GetString()
                        : n.TryGetProperty("name", out var nm) ? nm.GetString() : null)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugin-check] Failed to list servers: {ex.Message}");
            return;
        }

        foreach (var server in servers)
        {
            if (_stop || cancellationToken.IsCancellationRequested)
                return;

            try
            {
                using var checkDoc = await _api.GetAsync($"/servers/{Uri.EscapeDataString(server)}/plugins/updates", cancellationToken);
                if (!checkDoc.RootElement.TryGetProperty("updates", out var updatesArray) || updatesArray.ValueKind != JsonValueKind.Array)
                    continue;

                var available = new List<(string Plugin, string Current, string Latest, string? DownloadUrl)>();
                foreach (var entry in updatesArray.EnumerateArray())
                {
                    var state = entry.TryGetProperty("state", out var sv) ? sv.GetString() : null;
                    if (state != "update_available") continue;
                    var plugin = entry.TryGetProperty("plugin", out var pn) ? pn.GetString() ?? string.Empty : string.Empty;
                    var current = entry.TryGetProperty("current", out var cv) ? cv.GetString() : null;
                    var latest = entry.TryGetProperty("latest", out var lv) ? lv.GetString() : null;
                    var downloadUrl = entry.TryGetProperty("downloadUrl", out var du) ? du.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(plugin))
                        available.Add((plugin, current ?? "?", latest ?? "?", downloadUrl));
                }

                if (available.Count == 0)
                {
                    _notifiedPluginUpdatesHash.Remove(server);
                    Console.WriteLine($"[plugin-check] {server}: all plugins up to date.");
                    continue;
                }

                var summary = string.Join(", ", available.Select(u => $"{u.Plugin} {u.Current} → {u.Latest}"));
                var currentHash = ComputeHash(summary);
                var hasNotified = _notifiedPluginUpdatesHash.TryGetValue(server, out var previousHash) && previousHash == currentHash;

                var msg = $"[{server}] Plugin updates available ({available.Count}): {summary}";
                Console.WriteLine($"[plugin-check] {msg}");
                if (_config.PluginUpdates.NotifyAdmins && !hasNotified)
                {
                    BroadcastOutbox(msg, server);
                    _notifiedPluginUpdatesHash[server] = currentHash;
                }
                if (_config.Memory.WriteEnabled)
                    _ = _semanticMemory.RecordServerFactAsync(server,
                        $"Plugin updates available on {server}: {available.Count} plugin(s)",
                        msg, new[] { "plugins", "updates", server.ToLowerInvariant() },
                        CancellationToken.None);

                if (!_config.PluginUpdates.DownloadEnabled)
                    continue;

                var installed = new List<string>();
                var failed = new List<string>();
                foreach (var (plugin, _, latest, downloadUrl) in available)
                {
                    if (string.IsNullOrWhiteSpace(downloadUrl))
                    {
                        failed.Add($"{plugin} (no download URL)");
                        continue;
                    }

                    try
                    {
                        using var installDoc = await _api.PostAsync(
                            $"/servers/{Uri.EscapeDataString(server)}/plugins/install",
                            new { pluginName = plugin, downloadUrl },
                            cancellationToken);
                        installed.Add($"{plugin} v{latest}");
                        Console.WriteLine($"[plugin-check] {server}: installed {plugin} v{latest}");
                    }
                    catch (Exception ex)
                    {
                        failed.Add(plugin);
                        Console.WriteLine($"[plugin-check] {server}: failed to install {plugin}: {ex.Message}");
                    }
                }

                if (installed.Count > 0 || failed.Count > 0)
                {
                    var installMsg = installed.Count > 0 ? $"Installed: {string.Join(", ", installed)}." : string.Empty;
                    if (failed.Count > 0) installMsg += $" Failed: {string.Join(", ", failed)}.";
                    var fullMsg = $"[{server}] Plugin auto-update: {installMsg.Trim()}";
                    if (_config.PluginUpdates.NotifyAdmins)
                        BroadcastOutbox(fullMsg, server);
                    Console.WriteLine($"[plugin-check] {fullMsg}");
                    if (_config.Memory.WriteEnabled && installed.Count > 0)
                        _ = _semanticMemory.RecordServerFactAsync(server,
                            $"Plugins auto-updated on {server}: {string.Join(", ", installed)}",
                            fullMsg, new[] { "plugins", "installed", server.ToLowerInvariant() },
                            CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[plugin-check] {server}: check failed — {ex.Message}");
            }
        }
    }

    private async Task ProcessPlayerChatForStandInAsync(CancellationToken cancellationToken)
    {
        if (!_config.StandInAdmin.Enabled || _deepKernel is null || !_config.Llm.Enabled)
            return;

        var chat = _neoCortex.LoadPlayerChat();
        if (chat.RecentMessages.Count == 0)
            return;

        var now = DateTime.UtcNow;
        if ((now - _standInRateLimitWindowStart).TotalMinutes >= 1)
        {
            _standInRateLimitWindowStart = now;
            _standInResponsesThisMinute.Clear();
        }

        var agentName = _config.StandInAdmin.AgentNameInGame;

        var serverGroups = chat.RecentMessages
            .GroupBy(m => m.ServerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in serverGroups)
        {
            var server = group.Key;

            if (_config.StandInAdmin.AllowedServers.Count > 0 &&
                !_config.StandInAdmin.AllowedServers.Any(s => string.Equals(s, server, StringComparison.OrdinalIgnoreCase)))
                continue;

            _standInResponsesThisMinute.TryGetValue(server, out var responsesThisMinute);
            if (responsesThisMinute >= _config.StandInAdmin.MaxResponsesPerMinute)
                continue;

            _standInLastProcessedAtUtc.TryGetValue(server, out var lastProcessed);
            var pending = group
                .Where(m => m.CapturedAtUtc > lastProcessed)
                .OrderBy(m => m.CapturedAtUtc)
                .ToList();

            if (pending.Count == 0)
                continue;

            _standInLastProcessedAtUtc[server] = pending.Last().CapturedAtUtc;

            if (_standInCooldownByServer.TryGetValue(server, out var cooldownUntil) && now < cooldownUntil)
                continue;

            foreach (var msg in pending)
            {
                if (string.Equals(msg.PlayerName, agentName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var shouldRespond =
                    (_config.StandInAdmin.RespondToMentions &&
                        msg.Message.Contains(agentName, StringComparison.OrdinalIgnoreCase)) ||
                    (_config.StandInAdmin.RespondToQuestions &&
                        msg.Message.TrimEnd().EndsWith('?') && msg.Message.Length > 10);

                if (!shouldRespond)
                    continue;

                _standInResponsesThisMinute.TryGetValue(server, out var rateCount);
                if (rateCount >= _config.StandInAdmin.MaxResponsesPerMinute)
                    break;

                try
                {
                    var memoryContext = string.Empty;
                    if (_config.Memory.SearchEnabled)
                    {
                        var memResults = await _semanticMemory.SearchAsync(msg.Message, 4, cancellationToken);
                        if (memResults.Count > 0)
                            memoryContext = "\nRelevant server context:\n" + string.Join("\n", memResults.Select(r => $"- {r.MemoryRecord.Summary}"));
                    }

                    var systemPrompt = _config.StandInAdmin.SystemPrompt
                        ?? $"You are {agentName}, the automated admin assistant for this Rust game server. Answer briefly and helpfully. Never impersonate other players. If you don't know, say so. Keep responses under 140 characters.";

                    var prompt = $"{systemPrompt}{memoryContext}\n\nPlayer '{msg.PlayerName}' says: {msg.Message}\n\nReply as {agentName} (max 140 chars, no quotes):";

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(30));

                    var response = await _deepKernel.InvokePromptAsync(prompt, cancellationToken: cts.Token);
                    var reply = (response.GetValue<string>() ?? string.Empty).Trim()
                        .Replace('\n', ' ').Replace('\r', ' ');
                    if (reply.Length > 140)
                        reply = reply[..137] + "...";

                    if (string.IsNullOrWhiteSpace(reply))
                        continue;

                    var sayCommand = $"say [{agentName}] {reply}";
                    using var sayResponse = await _api.PostAsync(
                        $"/servers/{Uri.EscapeDataString(server)}/command/exec",
                        new { command = sayCommand },
                        cancellationToken);

                    Console.WriteLine($"[stand-in] {server}: Responded to {msg.PlayerName}: {reply}");
                    _standInResponsesThisMinute[server] = rateCount + 1;
                    _standInCooldownByServer[server] = now.AddSeconds(_config.StandInAdmin.ResponseCooldownSeconds);

                    if (_config.Memory.WriteEnabled)
                    {
                        var summary = $"[{server}] Stand-in admin responded to {msg.PlayerName}";
                        var detail = $"Server: {server}\nPlayer: {msg.PlayerName}\nMessage: {msg.Message}\nReply: {reply}";
                        _ = _semanticMemory.RecordServerFactAsync(server, summary, detail,
                            new[] { "stand-in-admin", "player-interaction", server.ToLowerInvariant(), msg.PlayerName.ToLowerInvariant() },
                            CancellationToken.None);
                    }

                    break; // one response per tick per server
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"[stand-in] {server}: Response timed out.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[stand-in] {server}: Failed to respond to {msg.PlayerName}: {ex.Message}");
                }
            }
        }
    }

    private string _lastSentimentSkipReason = string.Empty;
    private async Task AnalyzePlayerSentimentAsync(CancellationToken cancellationToken)
    {
        if (!_config.ConsoleMonitor.Enabled)
        {
            ReportSentimentSkip("console-monitor disabled");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(10, _config.ConsoleMonitor.SentimentAnalysisIntervalMinutes));
        if (DateTime.UtcNow - _lastSentimentAnalysisAtUtc < interval)
            return; // intentional silent skip — interval gate fires constantly

        var chat = _neoCortex.LoadPlayerChat();
        if (chat.RecentMessages.Count < 5)
        {
            ReportSentimentSkip($"only {chat.RecentMessages.Count} chat msgs (<5)");
            return;
        }

        if (_deepKernel is null)
        {
            ReportSentimentSkip("deep LLM kernel not configured");
            return;
        }

        if (!CanUseDeepLlm("sentiment"))
        {
            // CanUseDeepLlm already logs its own reason for the 401-mute case; otherwise:
            if (!_config.Llm.Enabled) ReportSentimentSkip("llm.enabled=false");
            else if (!_config.Llm.UseForRecommendations) ReportSentimentSkip("llm.useForRecommendations=false");
            return;
        }

        // Only commit the timestamp once we've actually decided to run the analysis,
        // otherwise transient skip-reasons would push the next attempt out by a full interval.
        _lastSentimentAnalysisAtUtc = DateTime.UtcNow;

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

            if (_config.Memory.WriteEnabled && chat.SentimentSummary is not null)
            {
                var themes = chat.KeyThemes.Count > 0 ? string.Join(", ", chat.KeyThemes) : "none";
                var feedback = chat.ConstructiveFeedback.Count > 0 ? string.Join("; ", chat.ConstructiveFeedback) : "none";
                var sentimentSummary = $"Player sentiment: {chat.SentimentLabel} ({chat.SentimentScore:F1}/10) — {TrimSingleLine(chat.SentimentSummary, 80)}";
                var sentimentDetail = $"Score: {chat.SentimentScore:F1}/10\nLabel: {chat.SentimentLabel}\nSummary: {chat.SentimentSummary}\nThemes: {themes}\nFeedback: {feedback}";
                var sentimentTags = new List<string> { "sentiment", "player-chat", chat.SentimentLabel ?? "unknown" };
                _ = _semanticMemory.RecordReflectionAsync(sentimentSummary, sentimentDetail, sentimentTags, CancellationToken.None);
            }

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

    private Task MaybeReloadStaticLogRulesAsync(CancellationToken cancellationToken)
    {
        var path = _config.Monitor.LogRulesPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Task.CompletedTask;

        DateTime mtime;
        try { mtime = File.GetLastWriteTimeUtc(path); }
        catch { return Task.CompletedTask; }

        if (mtime <= _logRulesLastWriteUtc) return Task.CompletedTask;
        _logRulesLastWriteUtc = mtime;

        var (ignore, incident, startup) = LoadStaticLogRules(path);
        var newIgnore = ignore.Except(_staticIgnorePatterns, StringComparer.OrdinalIgnoreCase).ToList();

        _staticIgnorePatterns.Clear(); foreach (var p in ignore) _staticIgnorePatterns.Add(p);
        _incidentPatterns.Clear();     foreach (var p in incident) _incidentPatterns.Add(p);
        _startupIgnorePatterns.Clear();foreach (var p in startup) _startupIgnorePatterns.Add(p);

        Console.WriteLine($"[log-rules] reloaded ({_staticIgnorePatterns.Count} ignore, {_incidentPatterns.Count} incident, {_startupIgnorePatterns.Count} startup-ignore).");

        if (newIgnore.Count > 0)
        {
            SweepTrackedLinesForPatterns(newIgnore);
        }
        return Task.CompletedTask;
    }

    // Drop already-recorded console errors / admin calls / log entries that match newly-added
    // ignore patterns. Called after a static-rule reload or after admin "ignore X" feedback.
    private void SweepTrackedLinesForPatterns(IReadOnlyCollection<string> patterns)
    {
        if (patterns.Count == 0) return;
        var lowered = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.ToLowerInvariant())
            .ToList();
        if (lowered.Count == 0) return;

        bool MatchesAny(string text)
        {
            var t = text?.ToLowerInvariant() ?? string.Empty;
            return lowered.Any(p => t.Contains(p, StringComparison.Ordinal));
        }

        try
        {
            var consoleMonitor = _neoCortex.LoadConsoleMonitor();
            var consoleChanged = false;
            foreach (var (server, state) in consoleMonitor.Servers)
            {
                var beforeErr = state.RecentErrors.Count;
                state.RecentErrors = state.RecentErrors
                    .Where(e => !MatchesAny(e.SampleLine ?? string.Empty) && !MatchesAny(e.Message ?? string.Empty))
                    .ToList();
                if (state.RecentErrors.Count != beforeErr) consoleChanged = true;

                var beforeRep = state.RepeatingMessages.Count;
                foreach (var key in state.RepeatingMessages.Keys.Where(MatchesAny).ToList())
                    state.RepeatingMessages.Remove(key);
                if (state.RepeatingMessages.Count != beforeRep) consoleChanged = true;

                if (consoleChanged) Console.WriteLine($"[log-rules] swept {server}: errors→{state.RecentErrors.Count}, repeating→{state.RepeatingMessages.Count}");
            }
            if (consoleChanged)
            {
                consoleMonitor.UpdatedAtUtc = DateTime.UtcNow;
                _neoCortex.SaveConsoleMonitor(consoleMonitor);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[log-rules] sweep console failed: {ex.Message}"); }

        try
        {
            var logs = _neoCortex.LoadLogs();
            var beforeLog = logs.RecentEntries.Count;
            logs.RecentEntries = logs.RecentEntries.Where(e => !MatchesAny(e.Line ?? string.Empty)).ToList();
            if (logs.RecentEntries.Count != beforeLog)
            {
                _neoCortex.SaveLogs(logs);
                Console.WriteLine($"[log-rules] swept logs: entries→{logs.RecentEntries.Count}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[log-rules] sweep logs failed: {ex.Message}"); }
    }

    private void ReportSentimentSkip(string reason)
    {
        if (string.Equals(reason, _lastSentimentSkipReason, StringComparison.Ordinal)) return;
        _lastSentimentSkipReason = reason;
        Console.WriteLine($"[sentiment] skipping: {reason}");
    }

    private async Task PollForcedListAsync(CancellationToken cancellationToken)
    {
        if (_playerStore is null) return;
        if (DateTime.UtcNow - _lastForcedPollAtUtc < TimeSpan.FromMinutes(5)) return;
        _lastForcedPollAtUtc = DateTime.UtcNow;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var resp = await http.GetAsync("http://apps.rusticaland.net:8853/all", cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[forced] poll skipped: HTTP {(int)resp.StatusCode}");
                return;
            }
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body)) return;

            var entries = ParseForcedListPayload(body);
            if (entries.Count == 0)
            {
                Console.WriteLine("[forced] poll returned no entries.");
                return;
            }
            _playerStore.ApplyForcedList(entries);
            Console.WriteLine($"[forced] applied {entries.Count} forced entries.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"[forced] poll failed: {ex.Message}");
        }
    }

    // The actual /all endpoint returns an array of forced players, e.g.:
    //   [{ "CurTimestamp": "Yes", "usedIP": null, "userID": 76561199645683644, "userName": "hophop" }]
    // Presence in the array implies forced=true (entries that aren't forced just don't appear).
    // userID is a JSON number, not a string. CurTimestamp == "Yes" means forced but not yet
    // logged in via the launcher — still forced from our perspective.
    //
    // Also tolerated for resilience: { "<sid>": true|... } object map and string-array shapes.
    private static List<ForcedListEntry> ParseForcedListPayload(string body)
    {
        var result = new List<ForcedListEntry>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (IsSteamId(prop.Name) && ReadForcedBool(prop.Value))
                        result.Add(new ForcedListEntry(prop.Name, null, null));
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in root.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String)
                    {
                        var sid = entry.GetString();
                        if (sid is not null && IsSteamId(sid))
                            result.Add(new ForcedListEntry(sid, null, null));
                    }
                    else if (entry.ValueKind == JsonValueKind.Object)
                    {
                        string? sid = null;
                        string? name = null;
                        string? ip = null;
                        foreach (var prop in entry.EnumerateObject())
                        {
                            if (sid is null && IsIdField(prop.Name))
                                sid = ReadFlexibleString(prop.Value);
                            else if (name is null && IsNameField(prop.Name))
                                name = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                            else if (ip is null && IsIpField(prop.Name))
                                ip = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                        }
                        if (sid is not null && IsSteamId(sid))
                            result.Add(new ForcedListEntry(sid, name, ip));
                    }
                }
            }
        }
        catch (JsonException) { /* tolerate malformed responses */ }
        return result;

        static bool IsSteamId(string s) =>
            s.Length == 17 && s.StartsWith("7656", StringComparison.Ordinal) && s.All(char.IsDigit);

        static bool IsIdField(string name) =>
            name.Equals("userID", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("userId", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("steamId", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("steam_id", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("id", StringComparison.OrdinalIgnoreCase);

        static bool IsNameField(string name) =>
            name.Equals("userName", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("displayName", StringComparison.OrdinalIgnoreCase);

        static bool IsIpField(string name) =>
            name.Equals("usedIP", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ip", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("lastIp", StringComparison.OrdinalIgnoreCase);

        static string? ReadFlexibleString(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null
        };

        static bool ReadForcedBool(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => el.TryGetInt32(out var n) && n != 0,
            JsonValueKind.String => !string.Equals(el.GetString(), "0", StringComparison.Ordinal)
                                    && !string.Equals(el.GetString(), "false", StringComparison.OrdinalIgnoreCase)
                                    && !string.IsNullOrWhiteSpace(el.GetString()),
            _ => true
        };
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
        var repeatedFailureContext = string.Empty;
        try
        {
            var repeatedFailures = await _semanticMemory.ListRepeatedFailuresAsync(2, cancellationToken);
            if (repeatedFailures.Count > 0)
            {
                repeatedFailureContext = string.Join("\n", repeatedFailures.Take(5).Select(group =>
                    $"- {group.Count()}x {group.Key}: {group.First().Summary}"));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[review] Repeated failure lookup skipped: {ex.Message}");
        }

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

Repeated failure memory clusters:
{{(string.IsNullOrWhiteSpace(repeatedFailureContext) ? "none" : repeatedFailureContext)}}
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
            if (!string.IsNullOrWhiteSpace(summary) || !string.IsNullOrWhiteSpace(mitigation))
            {
                await _semanticMemory.RecordReflectionAsync(
                    summary ?? "Incident review reflection",
                    $"Pattern: {pattern}\nMitigation: {mitigation}\nConfigSuggestion: {config}",
                    new[] { "incident-review", pattern ?? "unknown" },
                    cancellationToken);
            }

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
            await _gitOps.PushAsync(branch, cancellationToken);
            var prUrl = await _gitOps.CreatePrAsync(
                branch,
                $"[agent] Incident Review: {pattern}",
                $"**Pattern:** {pattern}\n\n**Summary:** {summary ?? "See review file."}\n\n**Mitigation:** {mitigation ?? "Review handler coverage."}",
                cancellationToken);
            await _gitOps.CheckoutMainAsync(cancellationToken);
            Console.WriteLine($"[review] Review PR opened for pattern '{pattern}': {prUrl}");
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
            var body = $"**Request:** {incident.Request}\n**Failure:** {incident.FailureReason}\n**Missing:** {incident.MissingCapability}\n**Prevention:** {incident.RecurrencePrevention}";
            await _gitOps.CommitAsync($"incident: record {incident.Id}", cancellationToken);
            await _gitOps.PushAsync(branch, cancellationToken);
            await _gitOps.CreatePrAsync(branch, title, body, cancellationToken);
            await _gitOps.CheckoutMainAsync(cancellationToken);
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
        if (state.RecentMessages.Count > 20)
            state.RecentMessages = state.RecentMessages.TakeLast(20).ToList();
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

        var commandText = route.Slots.CommandText;
        if (string.IsNullOrWhiteSpace(commandText) && route.Intent == AdminIntentType.RconCommand)
        {
            commandText = RustRconToolHandler.ExtractCommandFromMessage(message);
        }

        if (!string.IsNullOrWhiteSpace(commandText))
        {
            state.LastCommandText = commandText;
        }

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

    private void LogMemoryDebug(string message)
    {
        if (_config.Memory.DebugLoggingEnabled)
        {
            Console.WriteLine($"[memory] {message}");
        }
    }

    private static string ComputeHash(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }
}
