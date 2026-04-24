using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Infrastructure.Connectors;

internal sealed class ITGlueConnector : ApiConnectorBase
{
    public ITGlueConnector(ApiConnectorSettings settings) : base(settings)
    {
    }

    public override string Name => "itglue";

    protected override void ApplyAuthHeaders(HttpRequestMessage request)
    {
        base.ApplyAuthHeaders(request);

        // IT Glue requires x-api-key header
        if (!string.IsNullOrWhiteSpace(Settings.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", Settings.ApiKey!.Trim());
        }
    }
}
