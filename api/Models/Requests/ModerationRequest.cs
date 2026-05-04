namespace RusticalandOPS.Api.Models.Requests;

public sealed class ModerationRequest
{
    public string SteamId { get; set; } = string.Empty;
    public string? Reason { get; set; }
}
