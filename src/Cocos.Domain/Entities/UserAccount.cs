namespace Cocos.Domain.Entities;

/// <summary>
/// Una fila por usuario cuyo unico proposito es ser el punto de serializacion de la cuenta.
/// El cash y la tenencia son agregados sobre "orders", no columnas: dos transacciones
/// concurrentes pueden leer el mismo disponible e insertar filas distintas sin colisionar,
/// asi que ni REPEATABLE READ evita que la suma quede en negativo (write skew).
/// Bloquear esta fila con SELECT ... FOR UPDATE convierte ese conflicto sobre un agregado
/// en un conflicto sobre una fila concreta, que Postgres si sabe resolver.
/// </summary>
public sealed class UserAccount
{
    private UserAccount() { } // EF Core

    public int UserId { get; private set; }
}
