using Cocos.Application.Features.Orders.ExpireOrders;
using Wolverine;

namespace Cocos.Api.Infrastructure;

/// <summary>
/// Dispara cada tanto el barrido de órdenes vencidas. El intervalo es corto a propósito: el
/// barrido es idempotente y barato, asi que conviene equivocarse por correr de mas -- cada
/// minuto que una orden vencida sigue viva es plata reservada que el usuario no puede usar.
/// </summary>
internal sealed class OrderExpirationService(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<OrderExpirationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = configuration.GetValue<TimeSpan?>("Orders:ExpirationCheckInterval")
                       ?? TimeSpan.FromMinutes(5);

        // PeriodicTimer con TimeProvider: en los tests el reloj se adelanta a mano en vez de
        // dormir de verdad.
        using var timer = new PeriodicTimer(interval, timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;

                await using var scope = serviceProvider.CreateAsyncScope();
                var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

                var result = await bus.InvokeAsync<ExpireOrdersResponse>(
                    new ExpireOrdersCommand(), stoppingToken);

                if (result.ExpiredCount > 0)
                    logger.LogInformation("Se vencieron {Count} órdenes.", result.ExpiredCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Apagado normal del host. No es un error, y el filtro asegura que solo se
                // capture cuando la cancelación es efectivamente la nuestra.
                break;
            }
            catch (Exception ex)
            {
                // Un fallo puntual no puede matar el servicio: como el barrido es idempotente,
                // el proximo tick reintenta sin duplicar nada.
                logger.LogError(ex, "Fallo el barrido de órdenes vencidas. Reintenta en {Interval}.", interval);
            }
        }
    }
}
