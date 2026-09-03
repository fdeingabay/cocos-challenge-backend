using Cocos.Domain;

namespace Cocos.Application.Features.Orders.ExpireOrders;

/// <summary>
/// Las órdenes vivas del sistema -- de todos los usuarios -- vistas desde el barrido de
/// vencimiento. El nombre acota el subconjunto que el job toca: solo una orden viva puede vencer.
///
/// Es un port propio del slice y no el IOrderBook de la cancelación: el barrido nunca busca una
/// orden puntual, y arrastrar ese metodo acoplaria dos casos de uso por algo que uno no llama.
/// </summary>
public interface IOpenOrders
{
    /// <summary>
    /// Aplica el criterio y devuelve cuantas órdenes vencieron.
    ///
    /// Es idempotente por construccion: una segunda corrida devuelve 0, porque las que ya
    /// vencieron dejaron de estar vivas y el criterio no las alcanza. Por eso el job puede correr
    /// en N instancias sin leader election ni claim -- la primera gana y las demas no hacen nada.
    ///
    /// No recibe CancellationToken: dejar el barrido por la mitad obliga a averiguar despues que
    /// quedo vencido y que no.
    /// </summary>
    Task<int> ApplyAsync(OrderExpiry expiry);
}
