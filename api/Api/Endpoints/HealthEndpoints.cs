using rustmgrapi.Api.Services;

namespace rustmgrapi.Api.Endpoints;

internal static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app, RuntimePaths paths)
    {
        app.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            utc = DateTime.UtcNow,
            paths.ConfigDir,
            paths.RuntimeDir,
            paths.TasksDir
        }));
    }
}
