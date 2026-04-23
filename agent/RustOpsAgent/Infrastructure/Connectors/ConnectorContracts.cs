namespace RustOpsAgent.Infrastructure.Connectors;

internal sealed record ConnectorLogRecord(
    string Connector,
    string Source,
    DateTime TimestampUtc,
    string Level,
    string Message,
    string Raw);

internal sealed record ConnectorFetchResult(
    string Connector,
    bool Success,
    string Summary,
    IReadOnlyList<ConnectorLogRecord> Records);

internal sealed record ConnectorHealthStatus(
    string Connector,
    bool Enabled,
    bool Healthy,
    string Message,
    DateTime CheckedAtUtc);

internal interface IConnectorLogSource
{
    string Name { get; }
    bool Enabled { get; }
    Task<ConnectorFetchResult> FetchRecentLogsAsync(CancellationToken cancellationToken);
    Task<ConnectorHealthStatus> GetStatusAsync(CancellationToken cancellationToken);
}
