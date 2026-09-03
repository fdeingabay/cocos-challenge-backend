using Cocos.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace Cocos.Api.Infrastructure;

public static class ResultExtensions
{
    /// <summary>
    /// Traduce un Error de negocio al status HTTP que le corresponde: 400, 404 o 409.
    ///
    /// Los rechazos por fondos o tenencia insuficiente no pasan por aca: son un resultado
    /// exitoso, y devuelven 201 con status REJECTED.
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
