namespace Cocos.Application.Common;

/// <summary>
/// Result Pattern: el flujo de negocio no usa excepciones. Una excepcion en este codebase es un
/// fallo genuinamente inesperado, no una regla de negocio que no se cumplio.
///
/// El constructor si lanza <see cref="InvalidOperationException"/> ante un Result mal formado
/// -- exitoso con error, o fallido sin el -- porque eso no es un desenlace del negocio sino un
/// error de programacion.
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None)
            throw new InvalidOperationException("Un Result exitoso no puede llevar un error.");
        if (!isSuccess && error == Error.None)
            throw new InvalidOperationException("Un Result fallido necesita un error.");

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);
    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);
}

public class Result<TValue> : Result
{
    private readonly TValue? _value;

    protected internal Result(TValue? value, bool isSuccess, Error error) : base(isSuccess, error)
        => _value = value;

    /// <summary>
    /// El valor del resultado exitoso. Si el Result es fallido lanza
    /// <see cref="InvalidOperationException"/>: hay que preguntar por IsSuccess antes.
    /// </summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("No se puede leer el Value de un Result fallido.");

    public static implicit operator Result<TValue>(TValue value) => Success(value);
    public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);
}
