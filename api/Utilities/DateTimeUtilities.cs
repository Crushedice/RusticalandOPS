namespace RusticalandOPS.Api.Utilities;

using System.Globalization;

public static class DateTimeUtilities
{
    public static DateTime? TryParseLogTimestamp(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var match = System.Text.RegularExpressions.Regex.Match(line, @"\[(\d{2}/\d{2}/\d{4}\s+\d{2}:\d{2}:\d{2})\]");
        if (!match.Success)
            return null;

        var ts = match.Groups[1].Value;
        return DateTime.TryParseExact(ts, "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
    }

    public static string DetectLogLevel(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return "info";

        var upper = line.ToUpperInvariant();
        if (upper.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) return "error";
        if (upper.Contains("WARN", StringComparison.OrdinalIgnoreCase)) return "warn";
        if (upper.Contains("DEBUG", StringComparison.OrdinalIgnoreCase)) return "debug";
        if (upper.Contains("FATAL", StringComparison.OrdinalIgnoreCase)) return "fatal";

        return "info";
    }
}
