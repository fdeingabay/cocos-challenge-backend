namespace Cocos.Application.Common;

/// <summary>
/// Clasificacion del error, usada por la capa Api para elegir el status HTTP.
/// Deliberadamente NO existe un tipo para "fondos insuficientes": ese no es un error,
/// es un resultado de negocio valido que produce una orden REJECTED persistida.
/// </summary>
public enum ErrorType
{
    Validation,
    NotFound,
    Conflict
}

public sealed record Error(string Code, string Message, ErrorType Type)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Validation);

    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);
    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);
    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);
}
