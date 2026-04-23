using System.Net.Http.Headers;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Infrastructure.Connectors;

internal sealed class AutotaskConnector : ApiConnectorBase
{
    public AutotaskConnector(ApiConnectorSettings settings) : base(settings)
    {
    }

    public override string Name => "autotask";

    protected override void ApplyAuthHeaders(HttpRequestMessage request)
    {
        base.ApplyAuthHeaders(request);

        if (!string.IsNullOrWhiteSpace(Settings.IntegrationCode))
        {
            request.Headers.TryAddWithoutValidation("ApiIntegrationcode", Settings.IntegrationCode!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(Settings.Username))
        {
            request.Headers.TryAddWithoutValidation("UserName", Settings.Username!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(Settings.ApiSecret))
        {
            request.Headers.TryAddWithoutValidation("Secret", Settings.ApiSecret!.Trim());
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
