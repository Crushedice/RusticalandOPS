using System.Text.Json;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Domains.Rust;

/// <summary>
/// Handles web lookup requests for uMod plugin docs, Rust convar info, and Oxide API references.
/// Uses DuckDuckGo Instant Answer API, which does not require an API key.
/// </summary>
internal sealed class WebSearchToolHandler : IToolHandler
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "User-Agent", "RustOpsAgent/1.0" } }
    };

    public string Name => "web.search";

    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[]
    {
        AdminIntentType.Chat,
        AdminIntentType.Troubleshooting,
        AdminIntentType.RconCommand
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var query = ExtractSearchQuery(context.Message);
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolExecutionResult(false, "No search query could be extracted.", null, false, "no_query");
        }

        var scopedQuery = ShouldScopeToRust(context.Message, query)
            ? $"site:umod.org OR site:wiki.facepunch.com {query}"
            : query;

        try
        {
            var encoded = Uri.EscapeDataString(scopedQuery);
            var url = $"https://api.duckduckgo.com/?q={encoded}&format=json&no_html=1&skip_disambig=1";
            var response = await _http.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(response);

            var abstractText = doc.RootElement.TryGetProperty("Abstract", out var absNode) ? absNode.GetString() : null;
            var abstractSource = doc.RootElement.TryGetProperty("AbstractSource", out var srcNode) ? srcNode.GetString() : null;
            var abstractUrl = doc.RootElement.TryGetProperty("AbstractURL", out var urlNode) ? urlNode.GetString() : null;

            var relatedTopics = doc.RootElement.TryGetProperty("RelatedTopics", out var rtNode) && rtNode.ValueKind == JsonValueKind.Array
                ? rtNode.EnumerateArray()
                    .Take(3)
                    .Where(t => t.TryGetProperty("Text", out _))
                    .Select(t => t.GetProperty("Text").GetString())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList()
                : new List<string?>();

            if (string.IsNullOrWhiteSpace(abstractText) && relatedTopics.Count == 0)
            {
                return new ToolExecutionResult(false,
                    $"No results found for: {query}. Try rephrasing or check umod.org directly.",
                    null, false, "no_results");
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(abstractText))
            {
                parts.Add(abstractText!);
                if (!string.IsNullOrWhiteSpace(abstractSource))
                {
                    parts.Add($"Source: {abstractSource} - {abstractUrl}");
                }
            }

            if (relatedTopics.Count > 0)
            {
                parts.AddRange(relatedTopics.Where(t => t is not null).Select(t => $"- {t}"));
            }

            var result = string.Join("\n", parts);
            return new ToolExecutionResult(true, result, null, false, Payload: new { query, result, source = abstractUrl });
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(false, $"Web search failed: {ex.Message}", null, false, "search_error");
        }
    }

    private static string ExtractSearchQuery(string message)
    {
        var prefixes = new[] { "search for", "search", "look up", "lookup", "find", "what is", "how does", "tell me about", "docs for", "documentation for" };
        var trimmed = message.Trim();
        var lowered = trimmed.ToLowerInvariant();
        foreach (var prefix in prefixes)
        {
            if (lowered.StartsWith(prefix, StringComparison.Ordinal))
            {
                return trimmed[prefix.Length..].Trim();
            }
        }

        return trimmed;
    }

    private static bool ShouldScopeToRust(string message, string query)
    {
        var lower = $"{message} {query}".ToLowerInvariant();
        return lower.Contains("plugin") || lower.Contains("oxide") || lower.Contains("umod")
            || lower.Contains("rust server") || lower.Contains("convar") || lower.Contains("permission");
    }
}
