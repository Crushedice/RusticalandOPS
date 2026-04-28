using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Infrastructure;

namespace RustOpsAgent.Infrastructure.Memory;

internal sealed class OpenAiCompatibleEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _endpoint;
    private readonly int _batchSize;

    public OpenAiCompatibleEmbeddingProvider(
        string baseUrl,
        string modelName,
        string? apiKey,
        bool requireApiKey,
        int timeoutSeconds,
        int batchSize,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Embedding base URL is required.");
        }

        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new InvalidOperationException("Embedding model name is required.");
        }

        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Embedding base URL must be an absolute URI.");
        }

        if (requireApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Embedding API key is required by configuration.");
        }

        _endpoint = $"{baseUri.ToString().TrimEnd('/')}/embeddings";
        ModelName = modelName.Trim();
        _batchSize = Math.Max(1, batchSize);
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds));
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Authorization = null;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
    }

    public string ModelName { get; }
    public int? Dimensions { get; private set; }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        var results = await GenerateEmbeddingsAsync(new[] { text }, cancellationToken);
        if (results.Count == 0)
        {
            throw new InvalidOperationException("Embedding response did not return any vectors.");
        }

        return results[0];
    }

    public async Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken)
    {
        var normalized = texts
            .Select(text => text?.Trim() ?? string.Empty)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        if (normalized.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var results = new List<float[]>(normalized.Count);
        foreach (var batch in Batch(normalized, _batchSize))
        {
            var embeddings = await SendBatchAsync(batch, cancellationToken);
            if (embeddings.Count != batch.Count)
            {
                throw new InvalidOperationException($"Embedding batch size mismatch. Sent {batch.Count} item(s), received {embeddings.Count} vector(s).");
            }

            results.AddRange(embeddings);
        }

        return results;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<IReadOnlyList<float[]>> SendBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                object input = texts.Count == 1 ? texts[0] : texts;
                var payload = JsonSerializer.Serialize(new
                {
                    model = ModelName,
                    input
                }, JsonDefaults.Default);

                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return ParseEmbeddings(body);
                }

                var message = BuildHttpFailureMessage(response.StatusCode, response.ReasonPhrase, body);
                if (!IsTransient(response.StatusCode) || attempt == maxAttempts)
                {
                    throw new InvalidOperationException(message);
                }

                lastError = new HttpRequestException(message);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException("Embedding request timed out.", ex);
                if (attempt == maxAttempts)
                {
                    break;
                }
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < maxAttempts)
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
        }

        throw new InvalidOperationException("Embedding request failed after retries.", lastError);
    }

    private IReadOnlyList<float[]> ParseEmbeddings(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Embedding response did not include a data array.");
        }

        var results = new List<float[]>();
        foreach (var item in dataNode.EnumerateArray().OrderBy(node =>
                     node.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number ? idx.GetInt32() : 0))
        {
            if (!item.TryGetProperty("embedding", out var embeddingNode) || embeddingNode.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Embedding response item did not include an embedding array.");
            }

            var vector = embeddingNode.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            if (vector.Length == 0)
            {
                throw new InvalidOperationException("Embedding response returned an empty vector.");
            }

            if (vector.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
            {
                throw new InvalidOperationException("Embedding response contained invalid numeric values.");
            }

            if (Dimensions is null)
            {
                Dimensions = vector.Length;
            }
            else if (Dimensions != vector.Length)
            {
                throw new InvalidOperationException(
                    $"Embedding dimension mismatch. Expected {Dimensions}, received {vector.Length}.");
            }

            results.Add(vector);
        }

        return results;
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TimeoutException ||
        ex is TaskCanceledException canceled && canceled.InnerException is TimeoutException;

    private static string BuildHttpFailureMessage(HttpStatusCode statusCode, string? reasonPhrase, string body)
    {
        var suffix = TrimError(body);
        return (int)statusCode switch
        {
            400 => $"Embedding request failed with HTTP 400. Check the model name and request shape. Body: {suffix}",
            401 => $"Embedding request failed with HTTP 401. Check embedding API key configuration. Body: {suffix}",
            404 => $"Embedding request failed with HTTP 404. Check the embedding endpoint URL or model availability. Body: {suffix}",
            _ => $"Embedding request failed ({(int)statusCode} {reasonPhrase}). Body: {suffix}"
        };
    }

    private static string TrimError(string? value)
    {
        var normalized = value?.Replace('\r', ' ').Replace('\n', ' ').Trim() ?? string.Empty;
        return normalized.Length <= 280 ? normalized : normalized[..280];
    }

    private static IEnumerable<IReadOnlyList<string>> Batch(IReadOnlyList<string> texts, int batchSize)
    {
        for (var i = 0; i < texts.Count; i += batchSize)
        {
            yield return texts.Skip(i).Take(batchSize).ToList();
        }
    }
}
