using Cocos.Application.Common;
using Cocos.Application.Features.Orders.CancelOrder;
using Cocos.Application.Features.Orders.ExpireOrders;
using Cocos.Application.Features.Orders.SubmitOrder;
using Cocos.Infrastructure.Orders;
using Cocos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cocos.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        // Un único NpgsqlDataSource compartido: concentra el pool y evita que cada componente
        // arme su conexión con una cadena distinta.
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());

        services.AddDbContext<CocosDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));

        services.AddScoped<IDbConnectionFactory, NpgsqlConnectionFactory>();
        // Mapeo de tipo directo y no un lambda: Wolverine genera el código de invocación de los
        // handlers en compilación, y un factory opaco lo obliga a caer en service location.
        services.AddScoped<ICocosDbContext, CocosDbContext>();

        // Ambos toman el ICocosDbContext scoped: la MISMA conexión y la misma transacción que
        // el handler. Contra IDbConnectionFactory abririan otra sesión de Postgres y leerian el
        // disponible fuera del lock.
        services.AddScoped<IAccountLedger, AccountLedger>();
        services.AddScoped<IInstrumentReader, InstrumentReader>();

        // Cancelar no toma el lock de cuenta: su conflicto vive en una fila concreta y lo
        // resuelve la sentencia condicional.
        services.AddScoped<IOrderBook, OrderBook>();

        // El vencimiento tampoco: su criterio se evalúa al escribir, en un único statement.
        services.AddScoped<IOpenOrders, OpenOrders>();

        return services;
    }
}
