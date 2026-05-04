namespace RusticalandOPS.Api.Models.Shared;

public sealed record RconConnectionInfo(string Host, ushort Port, string Password, bool WebRconEnabled);
