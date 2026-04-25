using rustmgrapi.Api.Models;
using rustmgrapi.Api.Services;

namespace rustmgrapi.Api.Endpoints;

internal static class ServerEndpoints
{
    public static void MapServerEndpoints(this IEndpointRouteBuilder app, RustManagerService rust, RconService rcon, PluginService plugins)
    {
        app.MapGet("/servers", async () => Results.Ok(await rust.ListServersAsync()));

        app.MapGet("/servers/summary", async () =>
        {
            var servers = await rust.ListServersAsync();
            var rows = new List<object>();
            foreach (var server in servers)
            {
                rows.Add(await rust.GetStatusAsync(server));
            }

            return Results.Ok(new { count = rows.Count, servers = rows });
        });

        app.MapGet("/servers/{server}/status", async (string server) =>
        {
            if (!rust.IsKnownServer(server)) return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));
            return Results.Ok(await rust.GetStatusAsync(server));
        });

        app.MapGet("/servers/{server}/health", async (string server) =>
        {
            if (!rust.IsKnownServer(server)) return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));
            return Results.Ok(await rust.ReadHealthAsync(server));
        });

        app.MapPost("/servers/{server}/start", async (string server) =>
        {
            if (!rust.IsKnownServer(server)) return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));
            return Results.Ok(await rust.LifecycleAsync(server, "start"));
        });

        app.MapPost("/servers/{server}/stop", async (string server) =>
        {
            if (!rust.IsKnownServer(server)) return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));
            return Results.Ok(await rust.LifecycleAsync(server, "stop"));
        });

        app.MapPost("/servers/{server}/restart", async (string server) =>
        {
            if (!rust.IsKnownServer(server)) return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));
            return Results.Ok(await rust.LifecycleAsync(server, "restart"));
        });

        app.MapPost("/servers/{server}/kill", async (string server) =>
        {
            if (!rust.IsKnownServer(server)) return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));
            return Results.Ok(await rust.KillAsync(server));
        });

        app.MapPost("/servers/{server}/update", async (string server) =>
        {
            if (!rust.IsKnownServer(server)) return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));
            return Results.Ok(await rust.UpdateAsync(server));
        });

        app.MapGet("/servers/{server}/logs/tail", async (string server, int? lines) =>
        {
            if (!rust.IsKnownServer(server)) return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));
            return Results.Ok(await rust.ReadLogsTailAsync(server, lines ?? 120));
        });

        app.MapPost("/servers/{server}/command/exec", async (string server, ServerCommandExecRequest request, CancellationToken cancellationToken) =>
        {
            if (!rust.IsKnownServer(server)) return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));
            if (string.IsNullOrWhiteSpace(request.Command)) return Results.BadRequest(new ApiError("invalid_request", "command is required"));
            return Results.Ok(await rcon.ExecuteAsync(server, request.Command));
        });

        app.MapPost("/servers/{server}/command", async (string server, ServerCommandExecRequest request, CancellationToken cancellationToken) =>
        {
            if (!rust.IsKnownServer(server)) return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));
            if (string.IsNullOrWhiteSpace(request.Command)) return Results.BadRequest(new ApiError("invalid_request", "command is required"));
            return Results.Ok(await rcon.ExecuteAsync(server, request.Command));
        });

        app.MapGet("/servers/{server}/players", async (string server, CancellationToken cancellationToken) =>
        {
            if (!rust.IsKnownServer(server)) return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));
            return Results.Ok(await rcon.ExecuteAsync(server, "playerlist"));
        });

        app.MapGet("/servers/{server}/bans", async (string server, CancellationToken cancellationToken) =>
        {
            if (!rust.IsKnownServer(server)) return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));
            return Results.Ok(await rcon.ExecuteAsync(server, "bans"));
        });

        app.MapGet("/servers/{server}/oxide/validate", (string server) =>
        {
            if (!rust.IsKnownServer(server)) return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));
            return Results.Ok(plugins.ValidateOxide(server));
        });

        app.MapGet("/servers/{server}/plugins/updates", async (string server, CancellationToken cancellationToken) =>
        {
            if (!rust.IsKnownServer(server)) return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));
            return Results.Ok(await plugins.CheckUpdatesAsync(server, cancellationToken));
        });
    }
}
