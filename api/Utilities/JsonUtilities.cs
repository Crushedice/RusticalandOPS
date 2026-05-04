namespace RusticalandOPS.Api.Utilities;

using System.Text.Json;
using System.Text.RegularExpressions;
using RusticalandOPS.Api.Models.Responses;
using RusticalandOPS.Api.Models.Shared;

public static class JsonUtilities
{
    public static string? TryExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return doc.RootElement.GetRawText();
            }
            catch { }
        }

        var match = Regex.Match(trimmed, @"\{.*\}", RegexOptions.Singleline);
        if (match.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(match.Value);
                return doc.RootElement.GetRawText();
            }
            catch { }
        }

        return null;
    }

    public static string? PrettyJson(string? jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return null;
        }
    }

    public static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    public static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value))
                return value;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }

    public static bool? TryGetBool(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var prop)
            ? prop.ValueKind == JsonValueKind.True ? true : prop.ValueKind == JsonValueKind.False ? false : null
            : null;
    }

    public static string? TryGetStringAny(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }

        return null;
    }

    public static int? TryGetIntAny(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value))
                    return value;
                if (property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), out var parsed))
                    return parsed;
            }
        }

        return null;
    }

    public static double? TryGetDoubleAny(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var value))
                    return value;
                if (property.Value.ValueKind == JsonValueKind.String && double.TryParse(property.Value.GetString(), out var parsed))
                    return parsed;
            }
        }

        return null;
    }

    public static ServerStatusResponse ParseStatus(string server, string? payload)
    {
        if (payload is null)
            return new ServerStatusResponse { Name = server, State = "unknown" };

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            return new ServerStatusResponse
            {
                Name = server,
                State = TryGetStringAny(root, "state", "status") ?? "unknown",
                Online = TryGetBool(root, "online") ?? false,
                Raw = payload
            };
        }
        catch
        {
            return new ServerStatusResponse { Name = server, State = "unknown" };
        }
    }
}
