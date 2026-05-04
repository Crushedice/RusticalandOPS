namespace RusticalandOPS.Api.Models.Shared;

public sealed record RemoteServerEntry(
    string Name,
    string DisplayName,
    string RconIp,
    int RconPort,
    string RconPassword,
    int GamePort = 0,
    string AgentBaseUrl = "",
    string AgentApiKey = "",
    string AgentServerName = "");

public sealed class RemoteAgentStatus
{
    public string ServerName { get; set; } = string.Empty;
    public string AgentBaseUrl { get; set; } = string.Empty;
    public bool IsReachable { get; set; }
    public bool IsAuthValid { get; set; }
    public DateTime? LastHealthCheckAtUtc { get; set; }
    public int? LastHealthCheckLatencyMs { get; set; }
    public string? LastError { get; set; }
    public int FailureCount { get; set; }
    public int SuccessCount { get; set; }
}
