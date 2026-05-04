namespace RusticalandOPS.Api.Middleware;

public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;

    public ErrorHandlingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            RustOpsSentry.CaptureException(
                ex,
                $"Unhandled HTTP pipeline exception for {context.Request.Method} {context.Request.Path}.",
                "http.request",
                tags: new Dictionary<string, string?>
                {
                    ["http.method"] = context.Request.Method,
                    ["http.path"] = context.Request.Path.Value ?? "/"
                },
                extras: new Dictionary<string, object?>
                {
                    ["queryString"] = context.Request.QueryString.Value ?? string.Empty,
                    ["traceIdentifier"] = context.TraceIdentifier
                });
            throw;
        }
    }
}
