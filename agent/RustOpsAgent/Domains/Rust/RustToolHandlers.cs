using System.Text.Json;
using System.Text.RegularExpressions;
using RustOpsAgent.Core.Contracts;
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
        var server = await RustToolHelper.ResolveServerAsync(_api, context, cancellationToken);
        if (string.IsNullOrWhiteSpace(server))
        {
            var knownServers = await RustToolHelper.GetKnownServersAsync(_api, cancellationToken);
            var list = knownServers.Count > 0 ? string.Join(", ", knownServers.Take(10)) : "none";
            return new ToolExecutionResult(true, $"Known servers: {list}");
        }

        using var health = await _api.GetAsync($"/servers/{Uri.EscapeDataString(server)}/health", cancellationToken);
        var root = health.RootElement;
        var state = root.TryGetProperty("state", out var stateNode) ? stateNode.GetString() : "unknown";
        var errors = root.TryGetProperty("recentErrors", out var errorsNode) && errorsNode.ValueKind == JsonValueKind.Array
            ? errorsNode.EnumerateArray().Select(e => e.ToString()).Take(3).ToList()
            : new List<string>();

        var msg = errors.Count > 0
            ? $"{server} is {state}. Recent errors: {string.Join(" | ", errors)}"
            : $"{server} is {state}. No recent errors were reported.";

        return new ToolExecutionResult(true, msg, server, false, Payload: root.ToString());
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
            return new ToolExecutionResult(false, "Server name is required for server control actions.", null, false, "clarification_required");
        }

        if (message.Contains("countdown") || message.Contains("in 3") || message.Contains("in three") || message.Contains("3 min"))
        {
            await _api.PostAsync($"/servers/{Uri.EscapeDataString(server)}/command", new { command = "say Server restart in 3 minutes" }, cancellationToken);
            await Task.Delay(TimeSpan.FromMinutes(3), cancellationToken);
            await _api.PostAsync($"/servers/{Uri.EscapeDataString(server)}/restart", new { }, cancellationToken);
            return new ToolExecutionResult(true, $"Restart countdown executed for {server} with minimum 3 minutes.", server, true);
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
    private readonly RconRollingLogMonitor _rconLogMonitor = new();

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
            return new ToolExecutionResult(false, "Server name is required for RCON commands.", null, false, "clarification_required");
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

        var directReply = await TryExecuteDirectRconAsync(server, command, cancellationToken);
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

    private async Task<string?> TryExecuteDirectRconAsync(string server, string command, CancellationToken cancellationToken)
    {
        try
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
            var uri = new Uri($"ws://127.0.0.1:{port}/{encodedPassword}");

            await using IRconClient client = new RustRconClient();
            _rconLogMonitor.Attach(client);
            await client.ConnectAsync(uri, password, cancellationToken);
            return await client.SendCommandAsync(command, cancellationToken);
        }
        catch
        {
            return null;
        }
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
            return new ToolExecutionResult(false, "Server name is required for player lookup.", null, false, "clarification_required");
        }

        var endpoint = context.Message.Contains("ban", StringComparison.OrdinalIgnoreCase)
            ? $"/servers/{Uri.EscapeDataString(server)}/bans"
            : $"/servers/{Uri.EscapeDataString(server)}/players";

        using var response = await _api.GetAsync(endpoint, cancellationToken);
        return new ToolExecutionResult(true, response.RootElement.ToString(), server, false, Payload: response.RootElement.ToString());
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
            return new ToolExecutionResult(false, "Server name is required for log inspection.", null, false, "clarification_required");
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
        if (line.Contains("exception") || line.Contains("failed") || line.Contains("error")) return 3;
        if (line.Contains("warn") || line.Contains("disconnect")) return 2;
        if (dynamicRules.Any(rule => line.Contains(rule, StringComparison.OrdinalIgnoreCase))) return 2;
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
            return new ToolExecutionResult(false, "Server name is required for plugin checks.", null, false, "clarification_required");
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
        if (!string.IsNullOrWhiteSpace(context.Route.Slots.ServerName))
            return context.Route.Slots.ServerName;

        if (ShouldUseLastServer(context.Message) && !string.IsNullOrWhiteSpace(context.SelectionState.LastServerName))
            return context.SelectionState.LastServerName;

        var knownServers = await GetKnownServersAsync(api, cancellationToken);
        if (knownServers.Count == 0)
            return context.SelectionState.LastServerName;

        var lowered = context.Message.ToLowerInvariant();
        foreach (var server in knownServers)
        {
            if (lowered.Contains(server.ToLowerInvariant(), StringComparison.Ordinal))
            {
                return server;
            }
        }

        return knownServers.Count == 1 ? knownServers[0] : context.SelectionState.LastServerName;
    }

    public static async Task<List<string>> GetKnownServersAsync(RustOpsApiClient api, CancellationToken cancellationToken)
    {
        try
        {
            using var list = await api.GetAsync("/servers", cancellationToken);
            return list.RootElement.ValueKind == JsonValueKind.Array
                ? list.RootElement.EnumerateArray()
                    .Where(node => node.ValueKind == JsonValueKind.String)
                    .Select(node => node.GetString())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static bool ShouldUseLastServer(string message)
    {
        var lowered = message.ToLowerInvariant();
        return lowered.Contains("that one") || lowered.Contains("same server") || lowered.Contains("again") || lowered.Contains("it ");
    }
}
