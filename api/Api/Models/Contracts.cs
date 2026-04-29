using System.Text.Json.Serialization;

namespace rustmgrapi.Api.Models;

public sealed record ApiError(string Code, string Message);

public sealed class ServerCommandExecRequest
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;
}

public sealed class ServerConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("server.hostname")] public string ServerHostname { get; set; } = string.Empty;
    [JsonPropertyName("server.identity")] public string ServerIdentity { get; set; } = string.Empty;
    [JsonPropertyName("server.port")] public int ServerPort { get; set; }
    [JsonPropertyName("rcon.port")] public int RconPort { get; set; }
    [JsonPropertyName("app.port")] public int AppPort { get; set; }
    [JsonPropertyName("server.worldsize")] public int ServerWorldSize { get; set; }
    [JsonPropertyName("server.seed")] public int ServerSeed { get; set; }
    [JsonPropertyName("server.maxplayers")] public int ServerMaxPlayers { get; set; }
    [JsonPropertyName("server.level")] public string ServerLevel { get; set; } = "Procedural Map";
    [JsonPropertyName("rcon.password")] public string RconPassword { get; set; } = string.Empty;
    [JsonPropertyName("additionalArgs")] public string AdditionalArgs { get; set; } = string.Empty;
    [JsonPropertyName("serverDir")] public string ServerDir { get; set; } = string.Empty;
    [JsonPropertyName("logFile")] public string LogFile { get; set; } = "Log.txt";
}

public sealed class PluginInstallRequest
{
    [JsonPropertyName("pluginName")]
    public string PluginName { get; set; } = string.Empty;
    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;
}

public sealed class HostInterfaceCounter
{
    public string Name { get; set; } = string.Empty;
    public string? OperState { get; set; }
    public int? SpeedMbps { get; set; }
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }
    public double? RxRateMiBps { get; set; }
    public double? TxRateMiBps { get; set; }
    public double? CombinedRateMbps { get; set; }
}
