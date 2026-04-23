using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Infrastructure.Connectors;

internal abstract class ApiConnectorBase : IConnectorLogSource, IDisposable
{
    private static readonly string[] CollectionPropertyCandidates =
    {
        "logs", "data", "items", "results", "events", "entries", "alerts", "auditLogs"
    };

    private readonly HttpClient _http;
    protected readonly ApiConnectorSettings Settings;

    protected ApiConnectorBase(ApiConnectorSettings settings)
    {
        Settings = settings;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public abstract string Name { get; }
    public bool Enabled => Settings.Enabled;

    public async Task<ConnectorFetchResult> FetchRecentLogsAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return new ConnectorFetchResult(Name, false, "Connector disabled.", Array.Empty<ConnectorLogRecord>());
        }

        if (!TryBuildUri(Settings.LogsEndpointPath, out var uri, out var error))
        {
            return new ConnectorFetchResult(Name, false, error, Array.Empty<ConnectorLogRecord>());
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyAuthHeaders(request);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ConnectorFetchResult(
                    Name,
                    false,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    Array.Empty<ConnectorLogRecord>());
            }

            var records = ParseLogRecords(body);
            return new ConnectorFetchResult(
                Name,
                true,
                records.Count == 0 ? "Connected, no recent logs." : $"Fetched {records.Count} log entries.",
                records);
        }
        catch (Exception ex)
        {
            return new ConnectorFetchResult(Name, false, ex.Message, Array.Empty<ConnectorLogRecord>());
        }
    }

    public async Task<ConnectorHealthStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return new ConnectorHealthStatus(Name, false, false, "Disabled", DateTime.UtcNow);
        }

        if (!TryBuildUri(Settings.StatusEndpointPath, out var uri, out var error))
        {
            return new ConnectorHealthStatus(Name, true, false, error, DateTime.UtcNow);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyAuthHeaders(request);
            using var response = await _http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? new ConnectorHealthStatus(Name, true, true, "Reachable", DateTime.UtcNow)
                : new ConnectorHealthStatus(Name, true, false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            return new ConnectorHealthStatus(Name, true, false, ex.Message, DateTime.UtcNow);
        }
    }

    protected virtual void ApplyAuthHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(Settings.AccessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.AccessToken!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(Settings.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", Settings.ApiKey!.Trim());
        }
    }

    private bool TryBuildUri(string endpointPath, out Uri? uri, out string error)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(Settings.BaseUrl) || RustOpsEnv.HasUnresolvedPlaceholder(Settings.BaseUrl))
        {
            error = "Base URL is missing or unresolved.";
            return false;
        }

        if (!Uri.TryCreate(Settings.BaseUrl.TrimEnd('/'), UriKind.Absolute, out var baseUri))
        {
            error = "Base URL is invalid.";
            return false;
        }

        var path = string.IsNullOrWhiteSpace(endpointPath) ? "/" : endpointPath.Trim();
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        uri = new Uri(baseUri, path);
        error = string.Empty;
        return true;
    }

    private IReadOnlyList<ConnectorLogRecord> ParseLogRecords(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Array.Empty<ConnectorLogRecord>();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var records = new List<ConnectorLogRecord>();
            foreach (var node in EnumerateLogNodes(doc.RootElement))
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var message = ReadStringAny(node, "message", "summary", "description", "details", "text", "content", "title");
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                var level = ReadStringAny(node, "severity", "level", "status", "type") ?? "info";
                var source = ReadStringAny(node, "source", "device", "resource", "resourceName", "hostname", "site", "company") ?? Name;
                var timestamp = ReadDateTimeAny(node, "timestamp", "timestampUtc", "createdAt", "created", "time", "date")
                    ?? DateTime.UtcNow;

                records.Add(new ConnectorLogRecord(
                    Name,
                    source,
                    timestamp,
                    level,
                    CollapseWhitespace(message),
                    node.ToString()));
            }

            return records;
        }
        catch
        {
            return body
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(400)
                .Select(line => new ConnectorLogRecord(Name, Name, DateTime.UtcNow, "info", CollapseWhitespace(line), line))
                .ToList();
        }
    }

    private static IEnumerable<JsonElement> EnumerateLogNodes(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var propertyName in CollectionPropertyCandidates)
        {
            if (!root.TryGetProperty(propertyName, out var candidate) || candidate.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in candidate.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        yield return root;
    }

    private static string? ReadStringAny(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static DateTime? ReadDateTimeAny(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), out var parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
            {
                if (epoch > 10_000_000_000)
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime;
                }

                return DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
            }
        }

        return null;
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var ch in value)
        {
            var currentIsWhitespace = char.IsWhiteSpace(ch);
            if (currentIsWhitespace)
            {
                if (previousWasWhitespace)
                {
                    continue;
                }

                builder.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            builder.Append(ch);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    public void Dispose() => _http.Dispose();
}
