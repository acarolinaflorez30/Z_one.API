using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ZOne.Api.Infrastructure;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is ApiException api)
        {
            var problem = new ProblemDetails { Status = api.Status, Title = api.Message };
            problem.Extensions["code"] = api.Code;
            if (api.Errors is not null) problem.Extensions["errors"] = api.Errors;

            context.Response.StatusCode = api.Status;
            await context.Response.WriteAsJsonAsync(problem, cancellationToken);
            return true;
        }

        logger.LogError(exception, "Error no controlado en {Method} {Path}", context.Request.Method, context.Request.Path);

        var generic = new ProblemDetails { Status = 500, Title = "Ocurrió un error inesperado." };
        generic.Extensions["code"] = "error_interno";
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(generic, cancellationToken);
        return true;
    }
}
