using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed class FocusedNetworkInspector
{
    private static readonly HashSet<string> AllowedInterfaces = new(StringComparer.OrdinalIgnoreCase) { "eth0", "wt1", "wg1" };

    public bool IsAllowedInterface(string? name) =>
        !string.IsNullOrWhiteSpace(name) && AllowedInterfaces.Contains(name);

    public JsonArray FilterInterfaces(JsonElement interfaces)
    {
        var result = new JsonArray();
        foreach (var iface in interfaces.EnumerateArray())
        {
            if (!iface.TryGetProperty("name", out var nameNode))
                continue;

            var name = nameNode.GetString();
            if (IsAllowedInterface(name))
                result.Add(JsonNode.Parse(iface.GetRawText()));
        }

        return result;
    }
}
