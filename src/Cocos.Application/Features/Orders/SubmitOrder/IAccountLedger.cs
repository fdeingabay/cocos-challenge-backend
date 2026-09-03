using Cocos.Domain;
using Cocos.Domain.Entities;

namespace Cocos.Application.Features.Orders.SubmitOrder;

/// <summary>
/// Punto de entrada al estado de una cuenta. Lo unico que ofrece es tomar el lock: no hay forma
/// de leer el disponible de un usuario sin antes serializar su cuenta.
/// </summary>
public interface IAccountLedger
{
    /// <summary>
    /// Abre la transacción y toma el lock de la cuenta. Devuelve null si el usuario no existe.
    ///
    /// El lock es la pieza que sostiene el invariante: el conflicto entre dos órdenes
    /// concurrentes vive en una SUMA y no en una fila, asi que Postgres no puede verlo. Dos
    /// transacciones leen el mismo disponible, insertan filas distintas, no se pisan y ambas
    /// commitean (write skew); ni REPEATABLE READ lo evita. Bloquear la fila de user_accounts
    /// materializa ese conflicto, y para eso existe esa tabla.
    /// </summary>
    Task<IAccountLock?> LockAsync(int userId, CancellationToken cancellationToken);
}

/// <summary>
/// La cuenta del usuario, tomada en exclusiva. El lock dura exactamente lo que dura este objeto:
/// mientras exista, ninguna otra orden del mismo usuario lee ni escribe su disponible.
///
/// Que todas las lecturas de estado cuelguen de aca no es cosmetico: vuelve imposible consultar
/// el disponible fuera del lock, que es el error que rompe el sistema.
///
/// No es un Unit of Work. No trackea entidades -- de eso ya se ocupa el DbContext -- ni abstrae
/// la persistencia: modela un SELECT ... FOR UPDATE, que es algo que el DbContext no representa.
/// Si nadie commitea, el Dispose revierte.
/// </summary>
public interface IAccountLock : IAsyncDisposable
{
    /// <summary>
    /// La orden ya creada con esta clave, si existe. Se consulta DENTRO del lock: dos reintentos
    /// simultaneos se serializan y el segundo ve la orden del primero en vez de duplicarla.
    /// </summary>
    Task<SubmitOrderResponse?> FindOrderByKeyAsync(string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Lo que la cuenta puede comprometer para este pedido, ya neto de reserva. Consulta solo el
    /// recurso que el pedido consume: pesos si compra, nominales si vende.
    /// </summary>
    Task<AccountAvailability> GetAvailabilityAsync(OrderRequest request, CancellationToken cancellationToken);

    /// <summary>Suma la orden al trabajo pendiente. No la persiste todavia.</summary>
    void Place(Order order);

    /// <summary>
    /// Persiste y libera el lock.
    ///
    /// No recibe CancellationToken a propósito: que el cliente corte la conexión no puede dejar
    /// una orden aplicada a medias. La firma lo impide en vez de confiarlo a un comentario.
    /// </summary>
    Task CommitAsync();
}
