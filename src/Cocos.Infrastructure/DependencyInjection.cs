using Cocos.Application.Common;
using Cocos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cocos.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        // Un unico NpgsqlDataSource compartido: concentra el pool de conexiones y evita
        // que cada componente arme la suya con una cadena distinta.
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());

        services.AddDbContext<CocosDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));

        services.AddScoped<IDbConnectionFactory, NpgsqlConnectionFactory>();
        // Mapeo de tipo directo y no un lambda: Wolverine genera el codigo de invocacion de los
        // handlers en compilacion, y un factory opaco lo obliga a caer en service location.
        services.AddScoped<ICocosDbContext, CocosDbContext>();

        return services;
    }
}
