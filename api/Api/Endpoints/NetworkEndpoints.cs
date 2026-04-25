using rustmgrapi.Api.Services;

namespace rustmgrapi.Api.Endpoints;

internal static class NetworkEndpoints
{
    public static void MapNetworkEndpoints(this IEndpointRouteBuilder app, NetworkInspectionService network)
    {
        app.MapGet("/host/network/summary", () => Results.Ok(network.CaptureSummary()));
        app.MapGet("/host/network/interfaces", () => Results.Ok(network.CaptureSummary()));
    }
}
