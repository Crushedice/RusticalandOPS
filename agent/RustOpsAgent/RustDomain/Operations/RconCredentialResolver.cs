using System.Text.Json;

internal static class RconCredentialResolver
{
    public static (Uri? uri, string? password) Resolve(string serverName)
    {
        var path = $"/opt/rust-manager/config/{serverName}.json";
        if (!File.Exists(path))
            return (null, null);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var host = root.TryGetProperty("rconHost", out var hostNode) && hostNode.ValueKind == JsonValueKind.String
            ? hostNode.GetString()
            : "127.0.0.1";

        var port = root.TryGetProperty("rconPort", out var portNode) && portNode.ValueKind == JsonValueKind.Number
            ? portNode.GetInt32()
            : 28016;

        var password = root.TryGetProperty("rconPassword", out var passwordNode) && passwordNode.ValueKind == JsonValueKind.String
            ? passwordNode.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(host))
            return (null, null);

        return (new Uri($"ws://{host}:{port}/"), password);
    }
}
