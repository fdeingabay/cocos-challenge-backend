using Cocos.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace Cocos.Api.Infrastructure;

public static class ResultExtensions
{
    /// <summary>
    /// Traduce un Error de negocio al status HTTP correspondiente.
    /// Los rechazos de orden por fondos/tenencia insuficiente NO pasan por aca:
    /// son un resultado exitoso que devuelve 201 con status REJECTED.
    /// </summary>
    public static ActionResult ToProblem(this Error error)
    {
        var problem = new ProblemDetails
        {
            Title = error.Message,
            Status = error.Type switch
            {
                ErrorType.NotFound => StatusCodes.Status404NotFound,
                ErrorType.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest
            },
            Extensions = { ["code"] = error.Code }
        };

        return new ObjectResult(problem) { StatusCode = problem.Status };
    }
}
