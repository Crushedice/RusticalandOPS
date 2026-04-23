using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Infrastructure.Connectors;
using RustOpsAgent.Infrastructure.Memory;

namespace RustOpsAgent.Domains.Integrations;

internal sealed class ConnectorStatusToolHandler : IToolHandler
{
    private readonly IReadOnlyList<IConnectorLogSource> _connectors;

    public ConnectorStatusToolHandler(IEnumerable<IConnectorLogSource> connectors)
    {
        _connectors = connectors.ToList();
    }

    public string Name => "integrations.connector.status";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.StatusCheck, AdminIntentType.Troubleshooting };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (_connectors.Count == 0)
        {
            return new ToolExecutionResult(false, "No connector modules were configured.", null, false, "no_connectors");
        }

        var statuses = await Task.WhenAll(_connectors.Select(connector => connector.GetStatusAsync(cancellationToken)));
        var summary = string.Join(" | ", statuses.Select(status =>
            $"{status.Connector}:{(status.Enabled ? (status.Healthy ? "ok" : "degraded") : "disabled")} ({status.Message})"));

        return new ToolExecutionResult(
            true,
            $"Connector status: {summary}",
            null,
            false,
            Payload: statuses);
    }
}

internal sealed class ConnectorLogsToolHandler : IToolHandler
{
    private readonly IReadOnlyList<IConnectorLogSource> _connectors;
    private readonly NeoCortexStore _memory;

    public ConnectorLogsToolHandler(IEnumerable<IConnectorLogSource> connectors, NeoCortexStore memory)
    {
        _connectors = connectors.ToList();
        _memory = memory;
    }

    public string Name => "integrations.logs.inspect";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.Troubleshooting, AdminIntentType.StatusCheck };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var requestedSources = ExtractSources(context.Message);
        var connectors = _connectors
            .Where(connector => connector.Enabled)
            .Where(connector => requestedSources.Count == 0 || requestedSources.Contains(connector.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var shouldFetchNow =
            context.Message.Contains("sync", StringComparison.OrdinalIgnoreCase) ||
            context.Message.Contains("fetch", StringComparison.OrdinalIgnoreCase) ||
            context.Message.Contains("pull", StringComparison.OrdinalIgnoreCase) ||
            context.Message.Contains("refresh", StringComparison.OrdinalIgnoreCase) ||
            context.Message.Contains("latest", StringComparison.OrdinalIgnoreCase);

        var knowledge = _memory.LoadLogs();
        if (shouldFetchNow && connectors.Count > 0)
        {
            foreach (var connector in connectors)
            {
                var fetch = await connector.FetchRecentLogsAsync(cancellationToken);
                if (!fetch.Success || fetch.Records.Count == 0)
                {
                    continue;
                }

                IngestRecords(knowledge, fetch.Records);
            }

            knowledge.RecentEntries = knowledge.RecentEntries
                .OrderBy(entry => entry.CapturedAtUtc)
                .TakeLast(800)
                .ToList();
            _memory.SaveLogs(knowledge);
        }

        var filtered = knowledge.RecentEntries
            .Where(entry => requestedSources.Count == 0 || requestedSources.Contains(entry.Source ?? entry.ServerName, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.CapturedAtUtc)
            .Take(80)
            .ToList();

        if (filtered.Count == 0)
        {
            return new ToolExecutionResult(
                true,
                "No ingested logs are available yet. Upload logs from the web console or request a connector sync.",
                null,
                false);
        }

        var severe = filtered
            .Where(entry => entry.Importance >= 2)
            .Take(8)
            .Select(entry => $"{entry.Source ?? entry.ServerName} [{entry.Level ?? "info"}] {entry.Line}")
            .ToList();

        var message = severe.Count > 0
            ? $"Recent high-signal logs: {string.Join(" | ", severe)}"
            : $"Recent logs were ingested ({filtered.Count} entries) but no high-signal patterns were found.";

        var selected = filtered.FirstOrDefault();
        return new ToolExecutionResult(true, message, selected?.Connector ?? selected?.Source, false);
    }

    private static HashSet<string> ExtractSources(string message)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (message.Contains("autotask", StringComparison.OrdinalIgnoreCase))
        {
            result.Add("autotask");
        }

        if (message.Contains("datto", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("rmm", StringComparison.OrdinalIgnoreCase))
        {
            result.Add("datto-rmm");
        }

        return result;
    }

    private static void IngestRecords(LogKnowledgeState knowledge, IEnumerable<ConnectorLogRecord> records)
    {
        var seen = new HashSet<string>(knowledge.RecentEntries
            .OrderByDescending(entry => entry.CapturedAtUtc)
            .Take(1200)
            .Select(entry => $"{entry.Source}|{entry.CapturedAtUtc:O}|{entry.Line}"), StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            var key = $"{record.Source}|{record.TimestampUtc:O}|{record.Message}";
            if (!seen.Add(key))
            {
                continue;
            }

            knowledge.RecentEntries.Add(new LogObservation
            {
                ServerName = record.Connector,
                Source = record.Source,
                Connector = record.Connector,
                Level = record.Level,
                Line = record.Message,
                Importance = ScoreImportance(record.Message, record.Level, knowledge.ImportanceRules),
                CapturedAtUtc = record.TimestampUtc
            });
        }
    }

    private static int ScoreImportance(string line, string? level, IEnumerable<string> dynamicRules)
    {
        var normalized = (line ?? string.Empty).ToLowerInvariant();
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

        if (normalized.Contains("warn") || normalized.Contains("timeout") || normalized.Contains("disconnect"))
        {
            return 2;
        }

        return dynamicRules.Any(rule => normalized.Contains(rule, StringComparison.OrdinalIgnoreCase)) ? 2 : 1;
    }
}

internal sealed class AgentChatToolHandler : IToolHandler
{
    public string Name => "agent.chat.reply";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.Chat, AdminIntentType.Clarification };

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var reply = context.Route.Intent switch
        {
            AdminIntentType.Clarification => context.Route.ClarificationQuestion ?? "Please clarify whether you want connector status, log analysis, or a configuration action.",
            _ => "Ready. I can check Autotask and Datto RMM connector status, ingest logs, and analyze recent high-signal events."
        };

        return Task.FromResult(new ToolExecutionResult(true, reply, context.SelectionState.LastServerName));
    }
}
