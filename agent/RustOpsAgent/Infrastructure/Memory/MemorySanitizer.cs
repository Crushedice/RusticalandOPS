using System.Text.RegularExpressions;

namespace RustOpsAgent.Infrastructure.Memory;

internal static partial class MemorySanitizer
{
    private static readonly Regex[] SecretPatterns =
    {
        new(@"(?im)\b(api[_-]?key|token|password|passwd|pwd|secret)\b\s*[:=]\s*[""']?([^\s;,""']+)", RegexOptions.Compiled),
        new(@"(?im)\b(bearer)\s+[a-z0-9\-_\.=]+", RegexOptions.Compiled),
        new(@"(?im)\b(cookie)\s*[:=]\s*([^\r\n;]+)", RegexOptions.Compiled),
        new(@"(?im)\b(connection\s*string)\s*[:=]\s*([^\r\n]+)", RegexOptions.Compiled),
        new(@"(?im)\b(user\s*id|uid|username)\s*=\s*[^;]+;\s*(password|pwd)\s*=\s*[^;]+", RegexOptions.Compiled),
        new(@"(?im)://[^/\s:@]+:([^@\s/]+)@", RegexOptions.Compiled)
    };

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var value = text;
        foreach (var pattern in SecretPatterns)
        {
            value = pattern.Replace(value, match =>
            {
                if (match.Value.Contains("://", StringComparison.Ordinal))
                {
                    return Regex.Replace(match.Value, @":([^@\s/]+)@", ":<redacted>@", RegexOptions.Compiled);
                }

                var separatorIndex = match.Value.IndexOfAny(new[] { ':', '=' });
                if (separatorIndex < 0)
                {
                    return "<redacted>";
                }

                return $"{match.Value[..(separatorIndex + 1)]} <redacted>";
            });
        }

        return value.Trim();
    }

    public static bool LooksUseful(string? summary, string? text)
    {
        var s = summary?.Trim() ?? string.Empty;
        var t = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(s) && string.IsNullOrWhiteSpace(t))
        {
            return false;
        }

        var combined = $"{s} {t}".Trim();
        if (combined.Length < 16)
        {
            return false;
        }

        var lowered = combined.ToLowerInvariant();
        return !(lowered is "ok" or "ready" or "done" or "thanks" or "thank you");
    }
}
