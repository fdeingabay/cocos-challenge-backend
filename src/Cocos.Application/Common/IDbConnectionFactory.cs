using System.Data.Common;

namespace Cocos.Application.Common;

/// <summary>
/// Conexion para el lado de LECTURA (Dapper). Las consultas de portfolio y busqueda son
/// agregaciones y proyecciones que EF materializaria peor y con mas ceremonia; Dapper las
/// resuelve en una sola query proyectando directo a records inmutables.
/// El lado de ESCRITURA usa el DbContext, que ya trae transacciones y tracking.
/// </summary>
public interface IDbConnectionFactory
{
    Task<DbConnection> OpenAsync(CancellationToken cancellationToken);
}
