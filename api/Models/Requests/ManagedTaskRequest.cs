namespace RusticalandOPS.Api.Models.Requests;

public sealed class ManagedTaskRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Schedule { get; set; }
    public DateTime? OnceAtUtc { get; set; }
    public string Command { get; set; } = string.Empty;
}
