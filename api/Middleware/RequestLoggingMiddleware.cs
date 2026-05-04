namespace RusticalandOPS.Api.Middleware;

public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;

    public RequestLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        RustOpsSentry.AddBreadcrumb($"{context.Request.Method} {context.Request.Path}", "http.request");
        await _next(context);
    }
}
