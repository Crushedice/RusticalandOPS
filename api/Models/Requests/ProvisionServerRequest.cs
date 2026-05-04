namespace RusticalandOPS.Api.Models.Requests;

using RusticalandOPS.Api.Models.Shared;

public sealed class ProvisionServerRequest
{
    public string Name { get; set; } = string.Empty;
    public bool CreateDirectories { get; set; }
    public ServerConfig? Config { get; set; }
}
