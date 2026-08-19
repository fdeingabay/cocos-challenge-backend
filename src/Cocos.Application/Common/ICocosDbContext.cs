using Cocos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Cocos.Application.Common;

/// <summary>
/// El DbContext declarado en la capa que lo consume, para no invertir la dependencia
/// (Application no puede referenciar Infrastructure).
///
/// Esto NO es un Repository ni un Unit of Work: no hay una coleccion por agregado, no se
/// encapsulan queries y no hay un wrapper de Begin/Commit propio. Es el mismo DbContext de
/// EF Core -- que ya ES la unidad de trabajo -- expuesto tal cual, incluido el acceso a la
/// conexion para las consultas que se resuelven mejor en SQL directo.
/// </summary>
public interface ICocosDbContext
{
    DbSet<Order> Orders { get; }
    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
