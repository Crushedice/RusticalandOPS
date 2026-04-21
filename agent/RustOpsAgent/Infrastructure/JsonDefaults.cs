using System.Text.Json;

namespace RustOpsAgent.Infrastructure;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}