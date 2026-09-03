namespace Cocos.Domain.Entities;

/// <summary>
/// Una fila por usuario, con el unico propósito de ser el punto de serializacion de la cuenta.
/// Cash y tenencia son agregados sobre "orders", no columnas: dos transacciones concurrentes
/// leen el mismo disponible e insertan filas distintas sin pisarse, asi que ni REPEATABLE READ
/// evita que la suma termine en negativo (write skew). Bloquear esta fila con
/// SELECT ... FOR UPDATE convierte ese conflicto sobre un agregado en un conflicto sobre una
/// fila concreta, que Postgres si sabe resolver.
/// </summary>
public sealed class UserAccount
{
    private UserAccount() { } // EF Core

    public int UserId { get; private set; }
}
