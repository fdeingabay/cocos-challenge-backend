using Cocos.Application.Features.Orders.ExpireOrders;
using Wolverine;

namespace Cocos.Api.Infrastructure;

/// <summary>
/// Dispara periodicamente el barrido de ordenes vencidas. El intervalo es corto a proposito:
/// el handler es idempotente y barato, asi que conviene equivocarse por correr de mas.
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

        // PeriodicTimer con TimeProvider: en los tests el reloj se adelanta a mano en vez
        // de dormir de verdad.
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
                    logger.LogInformation("Se vencieron {Count} ordenes.", result.ExpiredCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Apagado normal del host. No es un error: se captura de forma especifica
                // y solo cuando la cancelacion es efectivamente la nuestra.
                break;
            }
            catch (Exception ex)
            {
                // Un fallo puntual no puede matar el servicio: el barrido es idempotente,
                // asi que el proximo tick reintenta sin efectos duplicados.
                logger.LogError(ex, "Fallo el barrido de ordenes vencidas. Reintenta en {Interval}.", interval);
            }
        }
    }
}
