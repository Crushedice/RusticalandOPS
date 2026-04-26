using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Sentry;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Core.Interaction;
using RustOpsAgent.Domains.Rust.Rcon;
using RustOpsAgent.Infrastructure;
using RustOpsAgent.Infrastructure.Memory;

namespace RustOpsAgent.Domains.Rust;

internal sealed class RustStatusToolHandler : IToolHandler
{
    private readonly RustOpsApiClient _api;

    public RustStatusToolHandler(RustOpsApiClient api)
    {
        _api = api;
    }

    public string Name => "rust.status.check";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.StatusCheck, AdminIntentType.Chat, AdminIntentType.Troubleshooting };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var knownServers = await RustToolHelper.GetKnownServersAsync(_api, cancellationToken);
        var scope = RustToolHelper.ResolveServerScope(context, knownServers, allowPluralDefaultAll: true);
        if (scope.Servers.Count == 0)
        {
            var clarification = RustToolHelper.BuildScopeClarificationQuestion(
                context.Route.Intent,
                knownServers,
                allowAllServers: true);
            return new ToolExecutionResult(
                false,
                clarification,
                context.SelectionState.LastServerName,
                false,
                "clarification_required",
                SelectedServers: context.SelectionState.LastResolvedServers,
                ScopeKind: ServerScopeKind.Unspecified);
        }

        var results = new List<AggregateStatusServerResult>();
        foreach (var server in scope.Servers)
        {
            results.Add(await CheckServerStatusAsync(server, cancellationToken));
        }

        var successful = results.Where(result => result.CheckSucceeded).ToList();
        var failedServers = results
            .Where(result => !result.CheckSucceeded)
            .Select(result => result.Server)
            .ToList();
        var offlineServers = successful
            .Where(result => !result.Online)
            .Select(result => result.Server)
            .ToList();
        var onlineCount = successful.Count(result => result.Online);

        if (scope.ScopeKind == ServerScopeKind.Single && successful.Count == 1)
        {
            var single = successful[0];
            var singleMessage = single.RecentErrors is { Count: > 0 }
                ? $"{single.Server} is {single.State}. Recent errors: {string.Join(" | ", single.RecentErrors.Take(3))}"
                : $"{single.Server} is {single.State}. No recent errors were reported.";

            return new ToolExecutionResult(
                true,
                singleMessage,
                single.Server,
                false,
                Payload: single,
                SelectedServers: new[] { single.Server },
                ScopeKind: ServerScopeKind.Single);
        }

        var aggregatePayload = new AggregateStatusPayload(
            scope.ScopeKind == ServerScopeKind.Unspecified ? ServerScopeKind.Subset : scope.ScopeKind,
            scope.Servers,
            onlineCount,
            offlineServers,
            failedServers,
            results);

        var message = $"{onlineCount}/{scope.Servers.Count} servers are online.";
        if (offlineServers.Count > 0)
        {
            message += $" Offline: {string.Join(", ", offlineServers)}.";
        }

        if (failedServers.Count > 0)
        {
            message += $" Failed to check: {string.Join(", ", failedServers)}.";
        }

        return new ToolExecutionResult(
            successful.Count > 0,
            message,
            null,
            false,
            successful.Count > 0 ? null : "status_check_failed",
            aggregatePayload,
            scope.Servers,
            scope.ScopeKind);
    }

    private async Task<AggregateStatusServerResult> CheckServerStatusAsync(string server, CancellationToken cancellationToken)
    {
        try
        {
            using var health = await _api.GetAsync($"/servers/{Uri.EscapeDataString(server)}/health", cancellationToken);
            var root = health.RootElement;
            var state = ReadHealthState(root);
            var errors = root.TryGetProperty("recentErrors", out var errorsNode) && errorsNode.ValueKind == JsonValueKind.Array
                ? errorsNode.EnumerateArray().Select(item => item.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).Take(3).ToList()
                : new List<string>();

            return new AggregateStatusServerResult(
                server,
                state,
                IsOnlineState(state),
                true,
                null,
                errors);
        }
        catch (Exception ex)
        {
            return new AggregateStatusServerResult(
                server,
                "unknown",
                false,
                false,
                ex.Message,
                Array.Empty<string>());
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

    private static bool IsOnlineState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        return state.Equals("running", StringComparison.OrdinalIgnoreCase) ||
               state.Equals("online", StringComparison.OrdinalIgnoreCase) ||
               state.Equals("healthy", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class RustServerControlToolHandler : IToolHandler
{
    private readonly RustOpsApiClient _api;
    private readonly Action<string, string, string?>? _notifyAdmin; // (adminId, message, serverName)

    public RustServerControlToolHandler(RustOpsApiClient api, Action<string, string, string?>? notifyAdmin = null)
    {
        _api = api;
        _notifyAdmin = notifyAdmin;
    }

    public string Name => "rust.server.control";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.ServerControl };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var message = context.Message.ToLowerInvariant();

        // If admin is answering our pending restart-countdown clarification ("60", "300 seconds", "5 min"),
        // reconstruct the full restart intent from conversation state instead of falling through to /start.
        var pending = context.SelectionState.PendingClarification;
        if (pending is not null &&
            string.Equals(pending.Intent, "ServerControl", StringComparison.OrdinalIgnoreCase) &&
            pending.Question?.Contains("seconds", StringComparison.OrdinalIgnoreCase) == true &&
            !string.IsNullOrWhiteSpace(context.SelectionState.LastServerName))
        {
            var secs = TryExtractAnyNumber(message);
            if (secs.HasValue)
                return await ExecuteRestartAsync(context, context.SelectionState.LastServerName, secs.Value, cancellationToken);
        }

        var server = await RustToolHelper.ResolveServerAsync(_api, context, cancellationToken);
        if (string.IsNullOrWhiteSpace(server))
        {
            var knownServers = await RustToolHelper.GetKnownServersAsync(_api, cancellationToken);
            return new ToolExecutionResult(
                false,
                RustToolHelper.BuildScopeClarificationQuestion(AdminIntentType.ServerControl, knownServers, allowAllServers: false),
                null, false, "clarification_required",
                SelectedServers: context.SelectionState.LastResolvedServers,
                ScopeKind: ServerScopeKind.Unspecified);
        }

        if (message.Contains("restart"))
            return await HandleRestartAsync(context, server, cancellationToken);

        var endpoint = ResolveEndpoint(message);
        if (endpoint is null)
        {
            return new ToolExecutionResult(
                false,
                $"What should I do to {server}? Say start, stop, restart, kill, or update.",
                server, false, "clarification_required",
                SelectedServers: new[] { server },
                ScopeKind: ServerScopeKind.Single);
        }

        using var response = await _api.PostAsync(endpoint.Replace("{server}", Uri.EscapeDataString(server)), new { }, cancellationToken);
        return new ToolExecutionResult(true, $"Executed {endpoint.Split('/').Last()} for {server}.", server, true, Payload: response.RootElement.ToString());
    }

    private async Task<ToolExecutionResult> HandleRestartAsync(ToolExecutionContext context, string server, CancellationToken cancellationToken)
    {
        var seconds = ParseRestartSeconds(context.Message);
        if (seconds is null)
        {
            return new ToolExecutionResult(
                false,
                $"How many seconds should the countdown be before {server} restarts? (e.g., say 'restart {server} in 300 seconds')",
                server, false, "clarification_required",
                SelectedServers: context.SelectionState.LastResolvedServers,
                ScopeKind: ServerScopeKind.Single);
        }
        return await ExecuteRestartAsync(context, server, seconds.Value, cancellationToken);
    }

    private async Task<ToolExecutionResult> ExecuteRestartAsync(ToolExecutionContext context, string server, int seconds, CancellationToken cancellationToken)
    {

        // Send RCON restart command (Rust's graceful countdown shutdown).
        // If RCON is unavailable, fall back to the API restart endpoint which triggers rustmgr immediately.
        var rconSent = false;
        try
        {
            var rconResult = await RustDirectRconHelper.TryExecuteAsync(server, $"restart {seconds}", cancellationToken);
            rconSent = !string.IsNullOrWhiteSpace(rconResult);
        }
        catch (Exception ex)
        {
            RustOpsSentry.CaptureMessage(
                $"RCON restart command failed for '{server}', falling back to API.",
                "agent.server-control", Sentry.SentryLevel.Warning,
                extras: new Dictionary<string, object?> { ["server"] = server, ["exception"] = ex.Message });
        }

        if (!rconSent)
        {
            using var _ = await _api.PostAsync($"/servers/{Uri.EscapeDataString(server)}/restart", new { }, cancellationToken);
        }

        var method = rconSent ? $"RCON restart {seconds}s countdown" : "API restart (immediate)";
        var adminId = context.AdminId;
        var notifyAdmin = _notifyAdmin;
        var api = _api;
        var countdownSeconds = seconds;

        // Background task: monitor offline → online transition and notify the admin.
        _ = Task.Run(async () =>
        {
            await MonitorRestartAsync(server, countdownSeconds, adminId, notifyAdmin, api);
        });

        return new ToolExecutionResult(
            true,
            $"Restart initiated for {server} ({method}). I'll notify you when the server goes offline and again when it's back.",
            server, true,
            SelectedServers: new[] { server },
            ScopeKind: ServerScopeKind.Single);
    }

    private static async Task MonitorRestartAsync(
        string server, int countdownSeconds,
        string adminId, Action<string, string, string?>? notifyAdmin, RustOpsApiClient api)
    {
        // Wait until close to when the server should shut down, then start polling.
        var preWait = Math.Max(10, countdownSeconds - 15);
        await Task.Delay(TimeSpan.FromSeconds(preWait));

        // Phase 1: wait for the server to go offline (up to 5 min after the countdown expires).
        var offlineDeadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        var wentOffline = false;
        while (DateTime.UtcNow < offlineDeadline)
        {
            if (!await IsServerOnlineAsync(server, api))
            {
                wentOffline = true;
                notifyAdmin?.Invoke(adminId, $"[{server}] Server is now offline — waiting for the process to restart.", server);
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(12));
        }

        if (!wentOffline)
        {
            notifyAdmin?.Invoke(adminId, $"[{server}] Server did not appear to go offline within the expected window — please check manually.", server);
            return;
        }

        // Phase 2: wait for the server process to come back (rustmgr.sh handles the actual restart).
        // Rust servers have a bootstrapper phase followed by the long-lived server process; the
        // health endpoint going to "running" means the server is fully up and accepting connections.
        var onlineDeadline = DateTime.UtcNow + TimeSpan.FromMinutes(15);
        var processUpNotified = false;
        while (DateTime.UtcNow < onlineDeadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            var online = await IsServerOnlineAsync(server, api);

            // Detect the moment the process first comes up (may still be loading).
            if (!processUpNotified && await IsProcessUpAsync(server, api))
            {
                processUpNotified = true;
                notifyAdmin?.Invoke(adminId, $"[{server}] Server process is up — loading world, please wait.", server);
            }

            if (online)
            {
                notifyAdmin?.Invoke(adminId, $"[{server}] Server is back online and accepting connections. Restart complete.", server);
                return;
            }
        }

        notifyAdmin?.Invoke(adminId, $"[{server}] Server has been offline for 15 minutes and has not finished starting. Please investigate manually.", server);
    }

    private static async Task<bool> IsServerOnlineAsync(string server, RustOpsApiClient api)
    {
        try
        {
            using var health = await api.GetAsync($"/servers/{Uri.EscapeDataString(server)}/health", CancellationToken.None);
            var root = health.RootElement;
            var state = ExtractState(root);
            return state.Equals("running", StringComparison.OrdinalIgnoreCase) ||
                   state.Equals("online", StringComparison.OrdinalIgnoreCase) ||
                   state.Equals("healthy", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static async Task<bool> IsProcessUpAsync(string server, RustOpsApiClient api)
    {
        try
        {
            using var health = await api.GetAsync($"/servers/{Uri.EscapeDataString(server)}/health", CancellationToken.None);
            var root = health.RootElement;
            var state = ExtractState(root);
            return !string.Equals(state, "offline", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(state, "unknown", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(state);
        }
        catch { return false; }
    }

    private static string ExtractState(JsonElement root)
    {
        if (root.TryGetProperty("status", out var statusNode) &&
            statusNode.ValueKind == JsonValueKind.Object &&
            statusNode.TryGetProperty("state", out var nested))
            return nested.GetString() ?? string.Empty;
        return root.TryGetProperty("state", out var s) ? s.GetString() ?? string.Empty : string.Empty;
    }

    private static int? ParseRestartSeconds(string message)
    {
        // Patterns: "restart in 300 seconds", "restart 300s", "restart in 5 minutes", "restart 5min"
        var minuteMatch = Regex.Match(message, @"restart\s+(?:in\s+)?(\d+)\s*(?:minute|min|m)\b", RegexOptions.IgnoreCase);
        if (minuteMatch.Success && int.TryParse(minuteMatch.Groups[1].Value, out var mins))
            return mins * 60;

        var secondMatch = Regex.Match(message, @"restart\s+(?:in\s+)?(\d+)\s*(?:second|sec|s)?\b", RegexOptions.IgnoreCase);
        if (secondMatch.Success && int.TryParse(secondMatch.Groups[1].Value, out var secs) && secs > 0)
            return secs;

        return null;
    }

    // Returns null when the message doesn't match a known operation — caller must handle.
    private static string? ResolveEndpoint(string message)
    {
        if (message.Contains("kill")) return "/servers/{server}/kill";
        if (message.Contains("stop")) return "/servers/{server}/stop";
        if (message.Contains("update")) return "/servers/{server}/update";
        if (message.Contains("start")) return "/servers/{server}/start";
        return null;
    }

    // Extracts any number from the message, preferring minute-scaled values.
    private static int? TryExtractAnyNumber(string message)
    {
        var minMatch = Regex.Match(message, @"(\d+)\s*(?:minute|min)\b", RegexOptions.IgnoreCase);
        if (minMatch.Success && int.TryParse(minMatch.Groups[1].Value, out var mins) && mins > 0)
            return mins * 60;

        var numMatch = Regex.Match(message, @"\b(\d+)\b");
        if (numMatch.Success && int.TryParse(numMatch.Groups[1].Value, out var n) && n > 0)
            return n;

        return null;
    }
}

internal sealed class RustRconToolHandler : IToolHandler
{
    private readonly RustOpsApiClient _api;
    private readonly NeoCortexStore _memory;
    private readonly Core.Contracts.CommandExecutionSettings _cmdSettings;

    public RustRconToolHandler(RustOpsApiClient api, NeoCortexStore? memory = null, Core.Contracts.CommandExecutionSettings? cmdSettings = null)
    {
        _api = api;
        _memory = memory ?? new NeoCortexStore("data/NeoCortex", "data/agent-state.json");
        _cmdSettings = cmdSettings ?? new Core.Contracts.CommandExecutionSettings();
    }

    public string Name => "rust.rcon.command";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.RconCommand };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var server = await RustToolHelper.ResolveServerAsync(_api, context, cancellationToken);
        if (string.IsNullOrWhiteSpace(server))
        {
            var knownServers = await RustToolHelper.GetKnownServersAsync(_api, cancellationToken);
            return new ToolExecutionResult(
                false,
                RustToolHelper.BuildScopeClarificationQuestion(AdminIntentType.RconCommand, knownServers, allowAllServers: false),
                null, false, "clarification_required",
                SelectedServers: context.SelectionState.LastResolvedServers,
                ScopeKind: ServerScopeKind.Unspecified);
        }

        // If the admin asks for the live console/rolling log, return the RCON snapshot.
        var msgLower = context.Message.ToLowerInvariant();
        if (msgLower.Contains("console") || msgLower.Contains("rolling log") || msgLower.Contains("show log"))
        {
            var log = RustDirectRconHelper.GetRollingLog(server);
            if (log.Count == 0)
                return new ToolExecutionResult(true, $"No RCON console lines captured yet for {server}. The rolling log populates once the first RCON command is sent to that server.", server, false);
            var preview = string.Join('\n', log.TakeLast(40));
            return new ToolExecutionResult(true, $"RCON console ({log.Count} lines, last 40):\n{TruncateOutput(preview, 1200)}", server, false);
        }

        var command = context.Route.Slots.CommandText;
        if (string.IsNullOrWhiteSpace(command))
            command = ExtractCommandFromMessage(context.Message);

        if (string.IsNullOrWhiteSpace(command))
            return new ToolExecutionResult(false, "Which command should I run? Quote it or say 'run status'.", server, false, "clarification_required");

        // Normalize: take only the first token for policy checks (e.g. "oxide.reload MyPlugin" → "oxide.reload")
        var commandRoot = command.Split(' ', 2)[0].ToLowerInvariant();
        var policyCheck = CheckCommandPolicy(commandRoot);
        if (policyCheck is not null)
            return new ToolExecutionResult(false, policyCheck, server, false, "policy_blocked");

        string reply;
        bool succeeded;
        try
        {
            var directReply = await RustDirectRconHelper.TryExecuteAsync(server, command, cancellationToken);
            // null  = RCON unavailable (no config or connection failure) → fall back to API
            // ""    = RCON connected and executed; many Rust commands (say, kick) return empty — this is NOT a failure
            if (directReply is not null)
            {
                var endpoint = RustDirectRconHelper.GetSessionEndpoint(server) ?? "unknown";
                reply = string.IsNullOrWhiteSpace(directReply)
                    ? $"RCON {server} ({endpoint}): `{command}` executed — server returned no output. Run `status` on {server} to confirm RCON is reaching the correct server."
                    : $"RCON {server} ({endpoint}): {TruncateOutput(directReply)}";
                succeeded = true;
            }
            else
            {
                using var response = await _api.PostAsync($"/servers/{Uri.EscapeDataString(server)}/command/exec", new { command }, cancellationToken);
                reply = BuildApiFallbackReply(server, response.RootElement);
                succeeded = true;
            }
        }
        catch (Exception ex)
        {
            reply = $"Command failed on {server}: {ex.Message}";
            succeeded = false;
        }

        RecordCommandOutcome(commandRoot, succeeded);
        return new ToolExecutionResult(succeeded, reply, server, succeeded);
    }

    private string? CheckCommandPolicy(string commandRoot)
    {
        if (_cmdSettings.FreeMode)
            return null;

        var policy = _memory.LoadCommandPolicy();

        if (policy.Commands.TryGetValue(commandRoot, out var record))
        {
            if (record.RequiresApproval)
                return $"'{commandRoot}' has caused failures before and requires explicit admin approval to run.";
            if (record.AutoAllowed)
                return null;
        }

        // Check static allowList
        if (_cmdSettings.AllowList.Any(prefix => commandRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return null;

        return $"'{commandRoot}' is not on the allowed list. Run it via direct RCON, or say 'allow {commandRoot}' to add it.";
    }

    private void RecordCommandOutcome(string commandRoot, bool succeeded)
    {
        try
        {
            var policy = _memory.LoadCommandPolicy();
            if (!policy.Commands.TryGetValue(commandRoot, out var record))
            {
                record = new CommandRecord { Command = commandRoot };
                policy.Commands[commandRoot] = record;
            }

            record.LastUsedUtc = DateTime.UtcNow;
            if (succeeded)
            {
                record.SuccessCount++;
                if (record.SuccessCount >= _cmdSettings.AutoAllowAfterSuccesses && !record.RequiresApproval)
                    record.AutoAllowed = true;
            }
            else
            {
                record.FailCount++;
                if (record.FailCount >= _cmdSettings.RequireApprovalAfterFailures)
                    record.RequiresApproval = true;
            }

            _memory.SaveCommandPolicy(policy);
        }
        catch (Exception ex)
        {
            RustOpsSentry.CaptureException(ex, "Failed to record command policy outcome.", "agent.policy");
        }
    }

    private static string TruncateOutput(string output, int maxChars = 800)
    {
        output = output.Trim();
        return output.Length > maxChars ? output[..maxChars] + "…" : output;
    }

    internal static string BuildApiFallbackReply(string server, JsonElement root)
    {
        var apiReply = root.TryGetProperty("directReply", out var replyNode) && replyNode.ValueKind != JsonValueKind.Null
            ? replyNode.ToString()
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(apiReply))
        {
            return $"RCON {server}: {TruncateOutput(apiReply)}";
        }

        var command = root.TryGetProperty("command", out var commandNode) && commandNode.ValueKind == JsonValueKind.String
            ? commandNode.GetString()
            : null;
        var outputSummary = ExtractApiOutputSummary(root, command);
        return string.IsNullOrWhiteSpace(outputSummary)
            ? $"RCON {server}: command sent via API (no direct reply)."
            : $"RCON {server}: {TruncateOutput(outputSummary, 1200)}";
    }

    private static string? ExtractApiOutputSummary(JsonElement root, string? command)
    {
        if (!root.TryGetProperty("output", out var outputNode) || outputNode.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!outputNode.TryGetProperty("messages", out var messagesNode) || messagesNode.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var normalizedCommand = NormalizeWhitespace(command);
        var lines = messagesNode
            .EnumerateArray()
            .Select(message => message.ToString()?.Trim())
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Where(message => !LooksLikeRconCommandEcho(message!, normalizedCommand))
            .Take(6)
            .Cast<string>()
            .ToList();

        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private static bool LooksLikeRconCommandEcho(string message, string? normalizedCommand)
    {
        if (string.IsNullOrWhiteSpace(normalizedCommand))
            return false;

        var normalizedMessage = NormalizeWhitespace(message);
        if (string.IsNullOrWhiteSpace(normalizedMessage))
            return false;

        if (string.Equals(normalizedMessage, normalizedCommand, StringComparison.OrdinalIgnoreCase))
            return true;

        if (normalizedMessage.Contains("send(rcon):", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedMessage.EndsWith(normalizedCommand, StringComparison.OrdinalIgnoreCase);
        }

        var rconMarker = "[rcon]";
        var markerIndex = normalizedMessage.IndexOf(rconMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return false;

        var colonIndex = normalizedMessage.IndexOf(':', markerIndex + rconMarker.Length);
        if (colonIndex < 0 || colonIndex + 1 >= normalizedMessage.Length)
            return false;

        var trailing = NormalizeWhitespace(normalizedMessage[(colonIndex + 1)..]);
        return string.Equals(trailing, normalizedCommand, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Regex.Replace(value.Trim(), @"\s+", " ");
    }

    internal static string ExtractCommandFromMessage(string message)
    {
        var quoted = Regex.Match(message, "\"(?<cmd>.+?)\"");
        if (quoted.Success)
            return quoted.Groups["cmd"].Value.Trim();

        var lowered = message.ToLowerInvariant();
        foreach (var marker in new[] { "command", "rcon", "run", "execute", "send" })
        {
            var markerMatch = Regex.Match(lowered, $@"\b{Regex.Escape(marker)}\b");
            if (!markerMatch.Success)
                continue;

            var idx = markerMatch.Index;
            var candidate = message[(idx + marker.Length)..].Trim(' ', ':', '"', '\'', '.');
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return string.Empty;
    }
}

internal sealed class RustPlayerLookupToolHandler : IToolHandler
{
    private readonly RustOpsApiClient _api;

    public RustPlayerLookupToolHandler(RustOpsApiClient api)
    {
        _api = api;
    }

    public string Name => "rust.player.lookup";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.PlayerLookup };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var server = await RustToolHelper.ResolveServerAsync(_api, context, cancellationToken);
        if (string.IsNullOrWhiteSpace(server))
        {
            var knownServers = await RustToolHelper.GetKnownServersAsync(_api, cancellationToken);
            return new ToolExecutionResult(
                false,
                RustToolHelper.BuildScopeClarificationQuestion(AdminIntentType.PlayerLookup, knownServers, allowAllServers: false),
                null,
                false,
                "clarification_required",
                SelectedServers: context.SelectionState.LastResolvedServers,
                ScopeKind: ServerScopeKind.Unspecified);
        }

        var command = context.Message.Contains("ban", StringComparison.OrdinalIgnoreCase)
            ? "bans"
            : "playerlist";
        var directReply = await RustDirectRconHelper.TryExecuteAsync(server, command, cancellationToken);
        if (!string.IsNullOrWhiteSpace(directReply))
        {
            var payload = TryExtractStructuredReply(directReply);
            if (!string.IsNullOrWhiteSpace(payload))
            {
                return new ToolExecutionResult(true, payload, server, false, Payload: payload);
            }
        }

        var endpoint = context.Message.Contains("ban", StringComparison.OrdinalIgnoreCase)
            ? $"/servers/{Uri.EscapeDataString(server)}/bans"
            : $"/servers/{Uri.EscapeDataString(server)}/players";

        using var response = await _api.GetAsync(endpoint, cancellationToken);
        return new ToolExecutionResult(true, response.RootElement.ToString(), server, false, Payload: response.RootElement.ToString());
    }

    private static string? TryExtractStructuredReply(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return reply[start..(end + 1)];
        }

        start = reply.IndexOf('[');
        end = reply.LastIndexOf(']');
        return start >= 0 && end > start
            ? reply[start..(end + 1)]
            : null;
    }
}

internal sealed class RustLogsToolHandler : IToolHandler
{
    private readonly RustOpsApiClient _api;
    private readonly NeoCortexStore _memory;

    public RustLogsToolHandler(RustOpsApiClient api, NeoCortexStore memory)
    {
        _api = api;
        _memory = memory;
    }

    public string Name => "rust.logs.inspect";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.Troubleshooting, AdminIntentType.StatusCheck };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var server = await RustToolHelper.ResolveServerAsync(_api, context, cancellationToken);
        if (string.IsNullOrWhiteSpace(server))
        {
            var knownServers = await RustToolHelper.GetKnownServersAsync(_api, cancellationToken);
            return new ToolExecutionResult(
                false,
                RustToolHelper.BuildScopeClarificationQuestion(AdminIntentType.StatusCheck, knownServers, allowAllServers: true),
                null,
                false,
                "clarification_required",
                SelectedServers: context.SelectionState.LastResolvedServers,
                ScopeKind: ServerScopeKind.Unspecified);
        }

        using var logs = await _api.GetAsync($"/servers/{Uri.EscapeDataString(server)}/logs/tail?lines=120", cancellationToken);

        var lineItems = new List<string>();
        if (logs.RootElement.TryGetProperty("lines", out var linesNode) && linesNode.ValueKind == JsonValueKind.Array)
        {
            lineItems.AddRange(linesNode.EnumerateArray().Select(item => item.ToString()));
        }
        else
        {
            lineItems.AddRange(logs.RootElement.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var knowledge = _memory.LoadLogs();
        foreach (var line in lineItems.TakeLast(80))
        {
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
        }

        knowledge.RecentEntries = knowledge.RecentEntries.TakeLast(300).ToList();
        _memory.SaveLogs(knowledge);

        var high = knowledge.RecentEntries
            .Where(e => e.ServerName.Equals(server, StringComparison.OrdinalIgnoreCase) && e.Importance >= 2)
            .TakeLast(6)
            .Select(e => e.Line)
            .ToList();

        var message = high.Count > 0
            ? $"High-importance log lines for {server}: {string.Join(" | ", high)}"
            : $"No high-importance log lines detected for {server}.";

        return new ToolExecutionResult(true, message, server, false);
    }

    private static int ScoreImportance(string line, IEnumerable<string> dynamicRules)
    {
        var normalizedLine = line.ToLowerInvariant();
        if (normalizedLine.Contains("exception") || normalizedLine.Contains("failed") || normalizedLine.Contains("error")) return 3;
        if (normalizedLine.Contains("warn") || normalizedLine.Contains("disconnect")) return 2;
        if (dynamicRules.Any(rule => normalizedLine.Contains(rule, StringComparison.OrdinalIgnoreCase))) return 2;
        return 1;
    }
}

internal sealed class RustPluginToolHandler : IToolHandler
{
    private readonly RustOpsApiClient _api;
    private readonly Core.Contracts.PluginUpdateSettings _settings;
    private static readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "RustOpsAgent/1.0 (server-ops-bot)" } }
    };

    public RustPluginToolHandler(RustOpsApiClient api, Core.Contracts.PluginUpdateSettings? settings = null)
    {
        _api = api;
        _settings = settings ?? new Core.Contracts.PluginUpdateSettings();
    }

    public string Name => "rust.plugins.verify";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.Troubleshooting, AdminIntentType.StatusCheck };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var server = await RustToolHelper.ResolveServerAsync(_api, context, cancellationToken);
        if (string.IsNullOrWhiteSpace(server))
        {
            var knownServers = await RustToolHelper.GetKnownServersAsync(_api, cancellationToken);
            return new ToolExecutionResult(
                false,
                RustToolHelper.BuildScopeClarificationQuestion(AdminIntentType.StatusCheck, knownServers, allowAllServers: true),
                null, false, "clarification_required",
                SelectedServers: context.SelectionState.LastResolvedServers,
                ScopeKind: ServerScopeKind.Unspecified);
        }

        var msgLow = context.Message.ToLowerInvariant();
        var wantsCompileCheck = msgLow.Contains("error") || msgLow.Contains("fail") || msgLow.Contains("compile")
                                || msgLow.Contains("issue") || msgLow.Contains("broken") || msgLow.Contains("check");
        var wantsInstall = msgLow.Contains("install") || msgLow.Contains("update") || msgLow.Contains("download");

        // --- Step 1: RCON oxide.plugins for live compile status --------------------------------
        if (wantsCompileCheck)
        {
            var rconOutput = await RustDirectRconHelper.TryExecuteAsync(server, "oxide.plugins", cancellationToken);
            if (!string.IsNullOrWhiteSpace(rconOutput))
            {
                var lines = rconOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var failed = lines.Where(l => l.Contains("failed to compile", StringComparison.OrdinalIgnoreCase)).ToList();
                if (failed.Count > 0)
                {
                    return new ToolExecutionResult(true,
                        $"[{server}] {failed.Count} plugin(s) failed to compile:\n{string.Join('\n', failed)}",
                        server, false);
                }

                var totalLoaded = lines.Count(l => l.TrimStart().StartsWith('[') || Regex.IsMatch(l, @"^\s*\w.*\(\d+ms\)"));
                var summary = $"[{server}] All plugins loaded OK ({totalLoaded} reported). No compile errors.";
                if (!wantsInstall)
                    return new ToolExecutionResult(true, summary, server, false);
            }
        }

        // --- Step 2: oxide/validate for file-level info + uMod version check ----------------
        JsonDocument? validate = null;
        try
        {
            validate = await _api.GetAsync($"/servers/{Uri.EscapeDataString(server)}/oxide/validate", cancellationToken);
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(false,
                $"Could not reach oxide/validate for {server}: {ex.Message}",
                server, false, "api_error");
        }

        using (validate)
        {
            var updateMessages = new List<string>();
            var pendingDownloads = new List<(string slug, string latestVersion, string downloadUrl)>();

            if (validate.RootElement.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array)
            {
                // Show any file-level issues from the validator
                var fileIssues = plugins.EnumerateArray()
                    .Where(p => p.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                    .Select(p => $"{(p.TryGetProperty("pluginName", out var n) ? n.GetString() : "?")}: {(p.TryGetProperty("message", out var m) ? m.GetString() : "validation error")}")
                    .ToList();

                foreach (var plugin in plugins.EnumerateArray().Take(20))
                {
                    var pluginName = plugin.TryGetProperty("pluginName", out var nameNode) ? nameNode.GetString() : null;
                    var pluginSlug = plugin.TryGetProperty("pluginSlug", out var slugNode) ? slugNode.GetString() : null;
                    var pluginVersion = plugin.TryGetProperty("pluginVersion", out var versionNode) ? versionNode.GetString() : null;
                    var query = !string.IsNullOrWhiteSpace(pluginSlug) ? pluginSlug : pluginName;
                    if (string.IsNullOrWhiteSpace(query)) continue;

                    var searchUrl = string.Format(_settings.SearchUrlTemplate, Uri.EscapeDataString(query), _settings.SearchFilter);
                    try
                    {
                        using var httpResponse = await _http.GetAsync(searchUrl, cancellationToken);
                        if (!httpResponse.IsSuccessStatusCode)
                        {
                            updateMessages.Add($"{query}: uMod returned {(int)httpResponse.StatusCode}");
                            continue;
                        }

                        using var responseDoc = await JsonDocument.ParseAsync(
                            await httpResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

                        if (!responseDoc.RootElement.TryGetProperty("data", out var data)
                            || data.ValueKind != JsonValueKind.Array
                            || data.GetArrayLength() == 0)
                        {
                            updateMessages.Add($"{query}: not found on uMod");
                            continue;
                        }

                        var entry = data[0];
                        var latest = entry.TryGetProperty("latest_release_version", out var latestNode) ? latestNode.GetString() : null;
                        var downloadUrl = entry.TryGetProperty("download_url", out var dlNode) ? dlNode.GetString() : null;

                        if (string.IsNullOrWhiteSpace(latest) || string.Equals(latest, pluginVersion, StringComparison.OrdinalIgnoreCase))
                            updateMessages.Add($"{query}: up to date ({pluginVersion ?? "?"})");
                        else
                        {
                            updateMessages.Add($"{query}: {pluginVersion ?? "?"} → {latest}");
                            if (!string.IsNullOrWhiteSpace(downloadUrl))
                                pendingDownloads.Add((query, latest, downloadUrl));
                        }
                    }
                    catch (Exception ex)
                    {
                        updateMessages.Add($"{query}: check failed ({ex.Message.Split('\n')[0]})");
                    }
                }

                if (fileIssues.Count > 0)
                    updateMessages.Insert(0, $"FILE ISSUES: {string.Join(" | ", fileIssues)}");
            }

            var versionSummary = updateMessages.Count > 0
                ? string.Join(" | ", updateMessages)
                : "No plugin files found at the configured oxide path.";

            if (wantsInstall && _settings.DownloadEnabled && pendingDownloads.Count > 0)
            {
                var staged = await StageDownloadsAsync(server, pendingDownloads, cancellationToken);
                return new ToolExecutionResult(true, $"Plugin check for {server}: {versionSummary}\n{staged}", server, false);
            }

            var actionHint = pendingDownloads.Count > 0 && _settings.DownloadEnabled
                ? $" ({pendingDownloads.Count} update(s) available — say 'install updates' to apply)"
                : string.Empty;

            return new ToolExecutionResult(true, $"Plugin check for {server}: {versionSummary}{actionHint}", server, false);
        }
    }

    private async Task<string> StageDownloadsAsync(
        string server,
        IReadOnlyList<(string slug, string version, string url)> downloads,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_settings.StagingPath);
        var staged = new List<string>();
        var failed = new List<string>();

        foreach (var (slug, version, url) in downloads)
        {
            try
            {
                var fileName = $"{slug}.cs";
                var dest = Path.Combine(_settings.StagingPath, fileName);
                var bytes = await _http.GetByteArrayAsync(url, cancellationToken);
                await File.WriteAllBytesAsync(dest, bytes, cancellationToken);
                staged.Add($"{slug} v{version}");
                Console.WriteLine($"[plugins] Staged {fileName} ({bytes.Length} bytes)");
            }
            catch (Exception ex)
            {
                failed.Add(slug);
                Console.WriteLine($"[plugins] Download failed for {slug}: {ex.Message}");
            }
        }

        var result = staged.Count > 0 ? $"Staged: {string.Join(", ", staged)}." : string.Empty;
        if (failed.Count > 0)
            result += $" Failed: {string.Join(", ", failed)}.";
        return result.Trim();
    }
}

internal sealed class RustNetworkToolHandler : IToolHandler
{
    private readonly RustOpsApiClient _api;
    private readonly IReadOnlyList<string> _trackedInterfaces;

    public RustNetworkToolHandler(RustOpsApiClient api, IReadOnlyList<string>? trackedInterfaces = null)
    {
        _api = api;
        _trackedInterfaces = trackedInterfaces ?? new[] { "eth0", "wt1", "wg1" };
    }

    public string Name => "rust.network.inspect";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.StatusCheck, AdminIntentType.Troubleshooting };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        using var response = await _api.GetAsync("/host/network/summary", cancellationToken);

        var selected = new List<string>();
        if (response.RootElement.TryGetProperty("interfaces", out var interfaces) && interfaces.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in interfaces.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null || !_trackedInterfaces.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;

                var rx = item.TryGetProperty("rxRateMiBps", out var rxNode) ? rxNode.ToString() : "0";
                var tx = item.TryGetProperty("txRateMiBps", out var txNode) ? txNode.ToString() : "0";
                selected.Add($"{name}: rx={rx}MiB/s tx={tx}MiB/s");
            }
        }

        var ifaceList = string.Join(", ", _trackedInterfaces);
        var message = selected.Count == 0
            ? $"No tracked interfaces ({ifaceList}) found in current network sample."
            : string.Join(" | ", selected);

        return new ToolExecutionResult(true, message);
    }
}

internal sealed class RustServerManagementToolHandler : IToolHandler
{
    private readonly RustOpsApiClient _api;

    public RustServerManagementToolHandler(RustOpsApiClient api) => _api = api;

    public string Name => "rust.server.management";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.ServerManagement };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var msg = context.Message.ToLowerInvariant();

        if (IsRemoveIntent(msg))
            return await HandleRemoveAsync(context, cancellationToken);

        if (IsTestIntent(msg))
            return await HandleTestAsync(context, cancellationToken);

        if (IsListIntent(msg))
            return await HandleListAsync(cancellationToken);

        if (IsProvisionIntent(msg))
            return await HandleProvisionAsync(context, cancellationToken);

        // Default: add / register
        return await HandleAddAsync(context, cancellationToken);
    }

    private static bool IsRemoveIntent(string msg) =>
        msg.Contains("remove") || msg.Contains("delete") || msg.Contains("unregister");

    private static bool IsTestIntent(string msg) =>
        msg.Contains("test") && (msg.Contains("connection") || msg.Contains("rcon") || msg.Contains("connect"));

    private static bool IsListIntent(string msg) =>
        (msg.Contains("list") || msg.Contains("show") || msg.Contains("what server")) &&
        (msg.Contains("remote") || msg.Contains("server"));

    private static bool IsProvisionIntent(string msg) =>
        msg.Contains("provision") || msg.Contains("local server") || msg.Contains("new local");

    private async Task<ToolExecutionResult> HandleListAsync(CancellationToken ct)
    {
        using var response = await _api.GetAsync("/servers/remote/list", ct);
        var root = response.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            return new ToolExecutionResult(true, "No remote RCON servers registered.");

        var lines = root.EnumerateArray()
            .Select(s =>
            {
                var name = s.TryGetProperty("name", out var n) ? n.GetString() : "?";
                var display = s.TryGetProperty("displayName", out var d) ? d.GetString() : name;
                var ip = s.TryGetProperty("rconIp", out var ip_) ? ip_.GetString() : "?";
                var port = s.TryGetProperty("rconPort", out var p) ? p.GetInt32().ToString() : "?";
                return $"• {display} ({name}) → {ip}:{port}";
            })
            .ToList();

        return new ToolExecutionResult(true, $"Remote RCON servers ({lines.Count}):\n{string.Join('\n', lines)}");
    }

    private async Task<ToolExecutionResult> HandleAddAsync(ToolExecutionContext context, CancellationToken ct)
    {
        var parsed = ParseServerSpec(context.Message);
        if (parsed is null)
            return new ToolExecutionResult(false,
                "To add a remote server I need: name, RCON IP/host, port, and password. " +
                "Say something like: \"add remote server MyServer at 1.2.3.4:28016 password abc123\"",
                null, false, "clarification_required");

        var body = new
        {
            name = parsed.Name,
            displayName = parsed.DisplayName ?? parsed.Name,
            rconIp = parsed.Host,
            rconPort = parsed.Port,
            rconPassword = parsed.Password,
            gamePort = parsed.GamePort
        };

        try
        {
            using var response = await _api.PostAsync("/servers/remote", body, ct);
            return new ToolExecutionResult(true,
                $"Remote server '{parsed.Name}' registered at {parsed.Host}:{parsed.Port}. " +
                $"Run `test rcon connection for {parsed.Name}` to verify connectivity.",
                parsed.Name, true);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate_name", StringComparison.OrdinalIgnoreCase))
        {
            return new ToolExecutionResult(false, $"A server named '{parsed.Name}' already exists.", null, false, "duplicate");
        }
    }

    private async Task<ToolExecutionResult> HandleRemoveAsync(ToolExecutionContext context, CancellationToken ct)
    {
        var name = context.Route.Slots.ServerName
            ?? context.SelectionState.LastServerName
            ?? ExtractQuotedOrLastWord(context.Message);

        if (string.IsNullOrWhiteSpace(name))
            return new ToolExecutionResult(false, "Which server should I remove? Say its name.", null, false, "clarification_required");

        try
        {
            using var _ = await _api.DeleteAsync($"/servers/remote/{Uri.EscapeDataString(name)}", ct);
            return new ToolExecutionResult(true, $"Remote server '{name}' removed.", name, true);
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(false, $"Could not remove '{name}': {ex.Message}", name, false, "api_error");
        }
    }

    private async Task<ToolExecutionResult> HandleTestAsync(ToolExecutionContext context, CancellationToken ct)
    {
        var name = context.Route.Slots.ServerName
            ?? context.SelectionState.LastServerName
            ?? ExtractQuotedOrLastWord(context.Message);

        if (string.IsNullOrWhiteSpace(name))
            return new ToolExecutionResult(false, "Which server should I test? Say its name.", null, false, "clarification_required");

        try
        {
            using var response = await _api.PostAsync($"/servers/remote/{Uri.EscapeDataString(name)}/test", new { }, ct);
            var root = response.RootElement;
            var ok = root.TryGetProperty("ok", out var okNode) && okNode.ValueKind == JsonValueKind.True;
            var msg = root.TryGetProperty("message", out var msgNode) ? msgNode.GetString() : null;
            var latencyMs = root.TryGetProperty("latencyMs", out var lat) ? lat.GetInt32() : (int?)null;
            var suffix = latencyMs.HasValue ? $" ({latencyMs}ms)" : string.Empty;
            return new ToolExecutionResult(ok,
                ok ? $"RCON connection to '{name}' successful{suffix}. {msg}" : $"RCON test failed for '{name}': {msg}",
                name, false);
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(false, $"RCON test for '{name}' failed: {ex.Message}", name, false, "rcon_error");
        }
    }

    private async Task<ToolExecutionResult> HandleProvisionAsync(ToolExecutionContext context, CancellationToken ct)
    {
        return new ToolExecutionResult(false,
            "Provisioning a local server requires a full config (ports, seed, worldsize, server directory, etc.). " +
            "Use the web dashboard's Server Management tab to fill in the form, or say what you need and I'll guide you through it.",
            null, false, "clarification_required");
    }

    private static RemoteServerSpec? ParseServerSpec(string message)
    {
        var nameMatch = Regex.Match(message, @"(?:server|add|register|connect)\s+([A-Za-z0-9_\-\.]+)", RegexOptions.IgnoreCase);
        var atMatch = Regex.Match(message, @"(?:at|to|host|ip)\s+([\w\.\-]+)(?::(\d+))?", RegexOptions.IgnoreCase);
        var portMatch = Regex.Match(message, @"port\s+(\d+)", RegexOptions.IgnoreCase);
        var passMatch = Regex.Match(message, @"(?:password|pass|pw|rconpassword)\s+(\S+)", RegexOptions.IgnoreCase);

        var name = nameMatch.Success ? nameMatch.Groups[1].Value : null;
        var host = atMatch.Success ? atMatch.Groups[1].Value : null;
        var portText = atMatch.Success && atMatch.Groups[2].Success
            ? atMatch.Groups[2].Value
            : portMatch.Success ? portMatch.Groups[1].Value : null;
        var password = passMatch.Success ? passMatch.Groups[1].Value : null;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host) ||
            !int.TryParse(portText, out var port) || string.IsNullOrWhiteSpace(password))
            return null;

        return new RemoteServerSpec(name, null, host, port, password, 0);
    }

    private static string? ExtractQuotedOrLastWord(string message)
    {
        var quoted = Regex.Match(message, @"""([^""]+)""");
        if (quoted.Success) return quoted.Groups[1].Value.Trim();
        var words = message.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 ? words[^1] : null;
    }

    private sealed record RemoteServerSpec(string Name, string? DisplayName, string Host, int Port, string Password, int GamePort);
}

internal static class RustToolHelper
{
    public static async Task<string?> ResolveServerAsync(RustOpsApiClient api, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var knownServers = await GetKnownServersAsync(api, cancellationToken);
        var scope = ResolveServerScope(
            context,
            knownServers,
            allowPluralDefaultAll: false);
        return scope.Servers.Count == 1
            ? scope.Servers[0]
            : null;
    }

    public static ScopeResolution ResolveServerScope(
        ToolExecutionContext context,
        IReadOnlyList<string> knownServers,
        bool allowPluralDefaultAll)
    {
        return ServerScopeResolver.Resolve(
            context.Message,
            knownServers,
            context.SelectionState,
            context.Route.Slots.ScopeKind,
            context.Route.Slots.ServerNames,
            context.Route.Slots.ServerName,
            allowPluralDefaultAll,
            allowLastScopeFallback: true);
    }

    public static string BuildScopeClarificationQuestion(
        AdminIntentType intent,
        IReadOnlyList<string> knownServers,
        bool allowAllServers)
    {
        var known = knownServers.Count == 0
            ? "No configured servers are currently available."
            : $"Known servers: {string.Join(", ", knownServers)}.";

        if (!allowAllServers)
        {
            return intent switch
            {
                AdminIntentType.ServerControl => $"Which single server should I target? {known}",
                AdminIntentType.RconCommand => $"Which server should receive this command? {known}",
                AdminIntentType.PlayerLookup => $"Which server should I query for players? {known}",
                _ => $"Which server should I use? {known}"
            };
        }

        return $"Which server should I check? You can name one server or say 'all servers'. {known}";
    }

    public static async Task<List<string>> GetKnownServersAsync(RustOpsApiClient api, CancellationToken cancellationToken)
    {
        try
        {
            using var list = await api.GetAsync("/servers", cancellationToken);
            var knownServers = ParseKnownServers(list.RootElement).ToList();
            if (knownServers.Count > 0)
            {
                return knownServers;
            }

            using var summary = await api.GetAsync("/servers/summary", cancellationToken);
            return ParseSummaryServers(summary.RootElement).ToList();
        }
        catch (Exception ex)
        {
            RustOpsSentry.CaptureException(
                ex,
                "Failed to retrieve known server list from API.",
                "agent.api");
            return new List<string>();
        }
    }

    internal static IReadOnlyList<string> ParseKnownServers(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (var node in root.EnumerateArray())
        {
            if (node.ValueKind == JsonValueKind.String)
            {
                var value = node.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    names.Add(value.Trim());
                }
                continue;
            }

            if (node.ValueKind == JsonValueKind.Object &&
                node.TryGetProperty("name", out var nameNode) &&
                nameNode.ValueKind == JsonValueKind.String)
            {
                var value = nameNode.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    names.Add(value.Trim());
                }
            }
        }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ParseSummaryServers(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return root.EnumerateArray()
            .Where(node => node.ValueKind == JsonValueKind.Object &&
                           node.TryGetProperty("name", out var nameNode) &&
                           nameNode.ValueKind == JsonValueKind.String)
            .Select(node => node.GetProperty("name").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

internal static class RustDirectRconHelper
{
    private static readonly ConcurrentDictionary<string, PersistentRconSession> Sessions = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> GetRollingLog(string server)
    {
        if (Sessions.TryGetValue(server, out var session))
            return session.Snapshot();
        return Array.Empty<string>();
    }

    public static string? GetSessionEndpoint(string server)
    {
        return Sessions.TryGetValue(server, out var session) ? session.ConnectionEndpoint : null;
    }

    public static async Task<string?> TryExecuteAsync(string server, string command, CancellationToken cancellationToken)
    {
        try
        {
            var connection = LoadConnection(server);
            if (connection is null)
            {
                return null;
            }

            var session = GetOrCreateSession(server, connection.Value.Uri, connection.Value.Password);
            return await session.SendCommandAsync(command, cancellationToken);
        }
        catch (Exception ex)
        {
            RustOpsSentry.AddBreadcrumb(
                $"Direct RCON failed for '{server}', falling back to API. command={command} error={ex.Message}",
                "agent.rcon");
            return null;
        }
    }

    private static PersistentRconSession GetOrCreateSession(string server, Uri uri, string password)
    {
        while (true)
        {
            if (Sessions.TryGetValue(server, out var existing))
            {
                if (existing.Matches(uri, password))
                {
                    return existing;
                }

                if (Sessions.TryRemove(server, out var stale))
                {
                    _ = stale.DisposeAsync().AsTask();
                }
            }

            var created = new PersistentRconSession(uri, password);
            if (Sessions.TryAdd(server, created))
            {
                return created;
            }

            _ = created.DisposeAsync().AsTask();
        }
    }

    private static (Uri Uri, string Password)? LoadConnection(string server)
    {
        var configRoot = Environment.GetEnvironmentVariable("RUSTMGR_CONFIG");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "rustmgr", "config")
                : "/opt/rust-manager/config";
        }
        var configPath = Path.Combine(configRoot, $"{server}.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        using var cfg = JsonDocument.Parse(File.ReadAllText(configPath));
        return LoadConnectionFromConfig(cfg.RootElement);
    }

    internal static (Uri Uri, string Password)? LoadConnectionFromConfig(JsonElement root)
    {
        var additionalArgs = ReadConfigValue(root, "additionalArgs");

        // rcon.ip is the explicit RCON bind address. server.ip is the player-facing bind address
        // (often "0.0.0.0" or a public interface) and is NOT a valid RCON connection target.
        var host =
            ReadConfigValue(root, "rcon.ip") ??
            ReadArgValue(additionalArgs, "rcon.ip") ??
            "127.0.0.1";

        // rcon.web is always enabled — WebRCON is the only supported transport.
        var portText = ReadConfigValue(root, "rcon.port") ?? ReadArgValue(additionalArgs, "rcon.port");
        var password = ReadConfigValue(root, "rcon.password") ?? ReadArgValue(additionalArgs, "rcon.password");
        if (!int.TryParse(portText, out var port) || port <= 0 || port > 65535 || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var encodedPassword = Uri.EscapeDataString(password);
        return (new Uri($"ws://{host.Trim().Trim('\"')}:{port}/{encodedPassword}"), password);
    }

    private static string? ReadConfigValue(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var node))
        {
            return null;
        }

        return node.ValueKind switch
        {
            JsonValueKind.String => node.GetString(),
            JsonValueKind.Number => node.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => node.ToString()
        };
    }

    private static string? ReadArgValue(string? additionalArgs, string key)
    {
        if (string.IsNullOrWhiteSpace(additionalArgs))
        {
            return null;
        }

        var pattern = $@"(?:^|\s)\+{Regex.Escape(key)}\s+(?:""(?<v>[^""]+)""|(?<v>\S+))";
        var match = Regex.Match(additionalArgs, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value : null;
    }
}
