using System.Net.Http.Headers;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Infrastructure.Connectors;

internal sealed class DattoRmmConnector : ApiConnectorBase
{
    public DattoRmmConnector(ApiConnectorSettings settings) : base(settings)
    {
    }

    public override string Name => "datto-rmm";

    protected override void ApplyAuthHeaders(HttpRequestMessage request)
    {
        base.ApplyAuthHeaders(request);

        if (!string.IsNullOrWhiteSpace(Settings.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", Settings.ApiKey!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(Settings.ApiSecret))
        {
            request.Headers.TryAddWithoutValidation("x-api-secret", Settings.ApiSecret!.Trim());
        }

        if (string.IsNullOrWhiteSpace(Settings.AccessToken) &&
            !string.IsNullOrWhiteSpace(Settings.Username) &&
            !string.IsNullOrWhiteSpace(Settings.Password))
        {
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{Settings.Username}:{Settings.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }
}
