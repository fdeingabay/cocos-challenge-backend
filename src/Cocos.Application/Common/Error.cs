namespace Cocos.Application.Common;

/// <summary>
/// Clasificación del error, que la capa Api traduce a status HTTP (400 / 404 / 409).
///
/// No hay un tipo para "fondos insuficientes" a propósito: eso no es un error sino un resultado
/// de negocio válido, y produce unaóorden REJECTED persistida con su 201.
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
