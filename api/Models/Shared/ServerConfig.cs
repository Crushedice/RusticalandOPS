namespace RusticalandOPS.Api.Models.Shared;

using System.Text.Json.Serialization;

public sealed class ServerConfig
{
    [JsonPropertyName("name")]                        public string Name                        { get; set; } = string.Empty;
    [JsonPropertyName("server.hostname")]             public string ServerHostname              { get; set; } = string.Empty;
    [JsonPropertyName("server.description")]          public string ServerDescription           { get; set; } = string.Empty;
    [JsonPropertyName("server.url")]                  public string ServerUrl                   { get; set; } = string.Empty;
    [JsonPropertyName("server.logoimage")]            public string ServerLogoImage             { get; set; } = string.Empty;
    [JsonPropertyName("server.headerimage")]          public string ServerHeaderImage           { get; set; } = string.Empty;
    [JsonPropertyName("server.tags")]                 public string ServerTags                  { get; set; } = string.Empty;
    [JsonPropertyName("server.identity")]             public string ServerIdentity              { get; set; } = string.Empty;
    [JsonPropertyName("server.port")]                 public int    ServerPort                  { get; set; }
    [JsonPropertyName("rcon.port")]                   public int    RconPort                    { get; set; }
    [JsonPropertyName("app.port")]                    public int    AppPort                     { get; set; }
    [JsonPropertyName("server.worldsize")]            public int    ServerWorldSize             { get; set; }
    [JsonPropertyName("server.seed")]                 public int    ServerSeed                  { get; set; }
    [JsonPropertyName("server.maxplayers")]           public int    ServerMaxPlayers            { get; set; }
    [JsonPropertyName("server.level")]                public string ServerLevel                 { get; set; } = "Procedural Map";
    [JsonPropertyName("server.levelurl")]             public string ServerLevelUrl              { get; set; } = string.Empty;
    [JsonPropertyName("rcon.password")]               public string RconPassword                { get; set; } = string.Empty;
    [JsonPropertyName("server.reportsserverendpoint")]public string ServerReportsServerEndpoint { get; set; } = string.Empty;
    [JsonPropertyName("logFile")]                     public string LogFile                     { get; set; } = "Log.txt";
    [JsonPropertyName("server.encryption")]           public string ServerEncryption            { get; set; } = string.Empty;
    [JsonPropertyName("boombox.serverurllist")]       public string BoomboxServerUrlList        { get; set; } = string.Empty;
    [JsonPropertyName("additionalArgs")]              public string AdditionalArgs              { get; set; } = string.Empty;
    [JsonPropertyName("serverDir")]                   public string ServerDir                   { get; set; } = string.Empty;
    [JsonPropertyName("oxideDir")]                    public string OxideDir                    { get; set; } = string.Empty;
}
