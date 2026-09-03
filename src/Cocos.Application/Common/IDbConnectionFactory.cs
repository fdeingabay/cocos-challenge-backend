using System.Data.Common;

namespace Cocos.Application.Common;

/// <summary>
/// Conexión para el lado de LECTURA, con Dapper. Las consultas de portfolio y búsqueda son
/// agregaciones que EF materializaria peor y con mas ceremonia; Dapper las resuelve en una sola
/// query proyectando directo a records inmutables.
///
/// Sin excepciones: toda ESCRITURA pasa por el DbContext, que ya trae transacciones y tracking.
/// Un ExecuteAsync colgado de este factory es un bug.
/// </summary>
public interface IDbConnectionFactory
{
    Task<DbConnection> OpenAsync(CancellationToken cancellationToken);
}
