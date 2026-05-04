namespace RusticalandOPS.Api.Utilities;

public static class StringUtilities
{
    public static string BuildQueryString(IReadOnlyDictionary<string, string?> values)
    {
        var parts = new List<string>();
        foreach (var kvp in values)
        {
            if (string.IsNullOrWhiteSpace(kvp.Value))
                continue;
            parts.Add($"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");
        }
        return string.Join("&", parts);
    }

    public static string Escape(string value) => value.Replace("\"", "\\\"");

    public static string TailLines(string text, int lines)
    {
        if (lines <= 0) return text;
        var textLines = text.Split('\n');
        return string.Join('\n', textLines.TakeLast(lines));
    }
}
