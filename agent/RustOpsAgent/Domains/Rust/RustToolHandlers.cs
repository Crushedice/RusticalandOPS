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

    public RustServerControlToolHandler(RustOpsApiClient api)
    {
        _api = api;
    }

    public string Name => "rust.server.control";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.ServerControl };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var message = context.Message.ToLowerInvariant();
        var server = await RustToolHelper.ResolveServerAsync(_api, context, cancellationToken);
        if (string.IsNullOrWhiteSpace(server))
        {
            var knownServers = await RustToolHelper.GetKnownServersAsync(_api, cancellationToken);
            return new ToolExecutionResult(
                false,
                RustToolHelper.BuildScopeClarificationQuestion(AdminIntentType.ServerControl, knownServers, allowAllServers: false),
                null,
                false,
                "clarification_required",
                SelectedServers: context.SelectionState.LastResolvedServers,
                ScopeKind: ServerScopeKind.Unspecified);
        }

        if (message.Contains("countdown") || message.Contains("in 3") || message.Contains("in three") || message.Contains("3 min"))
        {
            // Fire-and-forget: do not block inbox processing for 3 minutes.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var _ = await _api.PostAsync($"/servers/{Uri.EscapeDataString(server)}/command", new { command = "say Server restart in 3 minutes" }, CancellationToken.None);
                    await Task.Delay(TimeSpan.FromMinutes(3), CancellationToken.None);
                    using var __ = await _api.PostAsync($"/servers/{Uri.EscapeDataString(server)}/restart", new { }, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    RustOpsSentry.CaptureException(
                        ex,
                        $"Scheduled restart countdown failed for server '{server}'.",
                        "agent.server-control",
                        extras: new Dictionary<string, object?> { ["server"] = server });
                }
            });
            return new ToolExecutionResult(true, $"Restart countdown started for {server}. Server will restart in ~3 minutes.", server, true);
        }

        var endpoint = ResolveEndpoint(message);
        using var response = await _api.PostAsync(endpoint.Replace("{server}", Uri.EscapeDataString(server)), new { }, cancellationToken);
        return new ToolExecutionResult(true, $"Executed {endpoint.Split('/').Last()} for {server}.", server, true, Payload: response.RootElement.ToString());
    }

    private static string ResolveEndpoint(string message)
    {
        if (message.Contains("kill")) return "/servers/{server}/kill";
        if (message.Contains("stop")) return "/servers/{server}/stop";
        if (message.Contains("restart")) return "/servers/{server}/restart";
        if (message.Contains("update")) return "/servers/{server}/update";
        return "/servers/{server}/start";
    }
}

internal sealed class RustRconToolHandler : IToolHandler
{
    private readonly RustOpsApiClient _api;

    public RustRconToolHandler(RustOpsApiClient api)
    {
        _api = api;
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
                null,
                false,
                "clarification_required",
                SelectedServers: context.SelectionState.LastResolvedServers,
                ScopeKind: ServerScopeKind.Unspecified);
        }

        var command = context.Route.Slots.CommandText;
        if (string.IsNullOrWhiteSpace(command))
        {
            command = ExtractCommand(context.Message);
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return new ToolExecutionResult(false, "Command text is required.", server, false, "clarification_required");
        }

        var directReply = await RustDirectRconHelper.TryExecuteAsync(server, command, cancellationToken);
        if (!string.IsNullOrWhiteSpace(directReply))
        {
            return new ToolExecutionResult(true, $"RCON on {server}: {directReply}", server, true);
        }

        using var response = await _api.PostAsync($"/servers/{Uri.EscapeDataString(server)}/command/exec", new { command }, cancellationToken);
        var root = response.RootElement;
        var apiReply = root.TryGetProperty("directReply", out var replyNode) ? replyNode.ToString() : "command sent";
        return new ToolExecutionResult(true, $"RCON on {server}: {apiReply}", server, true, Payload: root.ToString());
    }

    private static string ExtractCommand(string message)
    {
        var quoted = Regex.Match(message, "\"(?<cmd>.+?)\"");
        if (quoted.Success)
        {
            return quoted.Groups["cmd"].Value.Trim();
        }

        var lowered = message.ToLowerInvariant();
        var markers = new[] { "command", "rcon", "run", "execute", "send" };
        foreach (var marker in markers)
        {
            var idx = lowered.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }

            var candidate = message[(idx + marker.Length)..].Trim(' ', ':', '"', '\'', '.');
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
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
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public RustPluginToolHandler(RustOpsApiClient api)
    {
        _api = api;
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
                null,
                false,
                "clarification_required",
                SelectedServers: context.SelectionState.LastResolvedServers,
                ScopeKind: ServerScopeKind.Unspecified);
        }

        using var validate = await _api.GetAsync($"/servers/{Uri.EscapeDataString(server)}/oxide/validate", cancellationToken);

        var updateMessages = new List<string>();
        if (validate.RootElement.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array)
        {
            foreach (var plugin in plugins.EnumerateArray().Take(12))
            {
                var pluginName = plugin.TryGetProperty("pluginName", out var nameNode) ? nameNode.GetString() : null;
                var pluginSlug = plugin.TryGetProperty("pluginSlug", out var slugNode) ? slugNode.GetString() : null;
                var pluginVersion = plugin.TryGetProperty("pluginVersion", out var versionNode) ? versionNode.GetString() : null;
                var query = !string.IsNullOrWhiteSpace(pluginSlug) ? pluginSlug : pluginName;
                if (string.IsNullOrWhiteSpace(query))
                {
                    continue;
                }

                var escaped = Uri.EscapeDataString(query);
                var url = $"https://umod.org/plugins/search.json?query={escaped}&page=1&sort=title&sortdir=asc&filter=rust";
                try
                {
                    using var stream = await _http.GetStreamAsync(url, cancellationToken);
                    using var responseDoc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    if (!responseDoc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                    {
                        updateMessages.Add($"{query}: non-uMod or not found (informational)");
                        continue;
                    }

                    var latest = data[0].TryGetProperty("latest_release_version", out var latestNode) ? latestNode.GetString() : null;
                    if (string.IsNullOrWhiteSpace(latest) || string.Equals(latest, pluginVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        updateMessages.Add($"{query}: up to date");
                    }
                    else
                    {
                        updateMessages.Add($"{query}: update {pluginVersion ?? "unknown"} -> {latest}");
                    }
                }
                catch
                {
                    updateMessages.Add($"{query}: update check unavailable");
                }
            }
        }

        var summary = updateMessages.Count > 0
            ? string.Join(" | ", updateMessages.Take(8))
            : "No plugin update metadata available.";

        return new ToolExecutionResult(true, $"Plugin validation for {server} complete. {summary}", server, false);
    }
}

internal sealed class RustNetworkToolHandler : IToolHandler
{
    private readonly RustOpsApiClient _api;

    public RustNetworkToolHandler(RustOpsApiClient api)
    {
        _api = api;
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
                if (name is not ("eth0" or "wt1" or "wg1"))
                {
                    continue;
                }

                var rx = item.TryGetProperty("rxRateMiBps", out var rxNode) ? rxNode.ToString() : "0";
                var tx = item.TryGetProperty("txRateMiBps", out var txNode) ? txNode.ToString() : "0";
                selected.Add($"{name}: rx={rx}MiB/s tx={tx}MiB/s");
            }
        }

        var message = selected.Count == 0
            ? "No matching interfaces (eth0, wt1, wg1) were available in current network sample."
            : string.Join(" | ", selected);

        return new ToolExecutionResult(true, message);
    }
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
            RustOpsSentry.CaptureMessage(
                $"Direct RCON failed for '{server}'.",
                "agent.rcon",
                SentryLevel.Warning,
                extras: new Dictionary<string, object?>
                {
                    ["server"] = server,
                    ["command"] = command,
                    ["exception"] = ex.Message
                });
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
        var configRoot = Environment.GetEnvironmentVariable("RUSTMGR_CONFIG") ?? "/opt/rust-manager/config";
        var configPath = Path.Combine(configRoot, $"{server}.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        using var cfg = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = cfg.RootElement;
        var port = root.TryGetProperty("rcon.port", out var portNode) && portNode.ValueKind == JsonValueKind.Number
            ? portNode.GetInt32()
            : 0;
        var password = root.TryGetProperty("rcon.password", out var passwordNode) && passwordNode.ValueKind == JsonValueKind.String
            ? passwordNode.GetString()
            : null;

        if (port <= 0 || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var encodedPassword = Uri.EscapeDataString(password);
        return (new Uri($"ws://127.0.0.1:{port}/{encodedPassword}"), password);
    }
}
