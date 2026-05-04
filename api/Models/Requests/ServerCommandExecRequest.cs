namespace RusticalandOPS.Api.Models.Requests;

public sealed class ServerCommandExecRequest
{
    public string Command { get; set; } = string.Empty;
    public int WaitMs { get; set; } = 2500;
    public int MaxLines { get; set; } = 120;
    public int MaxBytes { get; set; } = 128 * 1024;
}
