namespace RusticalandOPS.Api.Models.Requests;

public sealed class PluginInstallRequest
{
    public string PluginName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
}
