using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Cocos.Api.Infrastructure;

/// <summary>
/// Manejo global de errores, para no tener try/catch repartido por los handlers.
///
/// Solo llegan aca los fallos inesperados: el flujo de negocio viaja por Result y lo traduce
/// ResultExtensions.ToProblem.
/// </summary>
internal sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // La cancelación se trata aparte y no como una excepcion génerica: que el cliente corte
        // la conexión no es un error 500 del servidor, y loguearlo como tal ensucia la senal de
        // los errores de verdad.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation(
                "Request cancelado por el cliente: {Method} {Path}",
                httpContext.Request.Method,
                httpContext.Request.Path);

            // 499 Client Closed Request. Sin body: el cliente ya no esta del otro lado.
            httpContext.Response.StatusCode = 499;
            return true;
        }

        logger.LogError(
            exception,
            "Error no controlado procesando {Method} {Path}",
            httpContext.Request.Method,
            httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
                Title = "Ocurrio un error procesando la solicitud.",
                Status = StatusCodes.Status500InternalServerError
            }
        });
    }
}
