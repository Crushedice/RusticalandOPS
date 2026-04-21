using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Infrastructure;

internal sealed class RustOpsApiClient : IDisposable
{
    private readonly HttpClient _http;

    public RustOpsApiClient(ApiSettings settings)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/")
        };

        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey);
    }

    public async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path.TrimStart('/'), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"API GET {path} failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
        }

        return JsonDocument.Parse(body);
    }

    public async Task<JsonDocument> PostAsync(string path, object? payload, CancellationToken cancellationToken)
    {
        var json = payload is null ? "{}" : JsonSerializer.Serialize(payload, JsonDefaults.Default);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(path.TrimStart('/'), content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"API POST {path} failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
        }

        return JsonDocument.Parse(body);
    }

    public void Dispose() => _http.Dispose();
}