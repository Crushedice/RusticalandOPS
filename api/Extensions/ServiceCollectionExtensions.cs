using RusticalandOPS.Api.Infrastructure.Configuration;
using RusticalandOPS.Api.Middleware;
using RusticalandOPS.Api.Utilities;
using Microsoft.AspNetCore.Http.Json;
using System.Text.Json.Serialization;

namespace RusticalandOPS.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRustOpsServices(this IServiceCollection services)
    {
        services.AddSingleton<RusticalandOPS.Api.Infrastructure.Configuration.IConfigurationProvider, RusticalandOPS.Api.Infrastructure.Configuration.ConfigurationProvider>();
        return services;
    }

    public static void UseRustOpsMiddleware(this WebApplication app)
    {
        app.UseMiddleware<ErrorHandlingMiddleware>();
        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseMiddleware<AuthenticationMiddleware>();
    }
}
